using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace Dropshot;

public class Config : BasePluginConfig
{
    [JsonPropertyName("Player speed")] public float Speed { get; set; } = 200;
    [JsonPropertyName("After shot delay")] public float Delay { get; set; } = 5;
    [JsonPropertyName("After jump delay")] public float JDelay { get; set; } = 2;
    [JsonPropertyName("Player on ground check")] public bool OnGroundCheck { get; set; } = true;
    [JsonPropertyName("Player vertical speed check (0 - disabled, 1 - less than in config, 2 - more than in config)")] public ushort VSpeedCheckMode { get; set; } = 0;
    [JsonPropertyName("Player vertical speed")] public float VSpeed { get; set; } = 0;
    [JsonPropertyName("Timer update interval")] public float TimerInterval { get; set; } = 0.1f;
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;
}

public class DropshotPlugin : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Dropshot";
    public override string ModuleVersion => "1.1.1";
    public override string ModuleAuthor => "Rexus Ohm";
    
    private MemoryFunctionWithReturn<CCSWeaponBaseGun, IntPtr, IntPtr, float>? _getInaccuracyFunc;
    
    private const int MaxPlayers = 65;
    private readonly float[] _lastJumpTimes = new float[MaxPlayers];
    private readonly float[] _lastShotTimes = new float[MaxPlayers];
    private readonly bool[] _lastSpreadStates = new bool[MaxPlayers];
    private readonly CCSPlayerController?[] _cachedControllers = new CCSPlayerController[MaxPlayers];
    
    private bool _isServerNoSpread;
    private Timer? _timer;
    
    public override void Load(bool hotReload)
    {
        // Инициализация функции через GameData
        // Плагин будет искать файл: addons/counterstrikesharp/gamedata/dropshot.json
        var signature = GameData.GetSignature("CCSWeaponBaseGun_GetInaccuracy");
        
        if (string.IsNullOrEmpty(signature))
        {
            Console.WriteLine($"{new string('*', 60)}\n[Dropshot error] Ошибка при инициализации: Сигнатура не найдена! \n{new string('*', 60)}");
            throw new Exception("Не удалось найти сигнатуру 'CCSWeaponBaseGun_GetInaccuracy' в gamedata!");
        }

        _getInaccuracyFunc = new MemoryFunctionWithReturn<CCSWeaponBaseGun, IntPtr, IntPtr, float>(signature);
        
        HookAll();
        CheckServerNoSpreadInitial();
        
        if (!_isServerNoSpread) StartTimer();
        
        if (hotReload)
        {
            InitializePlayerCache();
        }
    }
    
    public override void Unload(bool hotReload)
    {
        UnhookAll();
    }
    
    private void HookAll()
    {
        Console.WriteLine("[dropshot plugin] HookAll]");
        try
        {
            _getInaccuracyFunc?.Hook(ProcessShotPre, HookMode.Post);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{new string('*', 60)}\n[Dropshot error] Ошибка при инициализации: {ex}\n{new string('*', 60)}");
        }
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventServerCvar>(OnServerCvar);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
    }
    private void UnhookAll()
    {
        Console.WriteLine("[dropshot plugin] UnhookAll]");
        _getInaccuracyFunc?.Unhook(ProcessShotPre, HookMode.Post);

        DeregisterEventHandler<EventWeaponFire>(OnWeaponFire);
        DeregisterEventHandler<EventPlayerJump>(OnPlayerJump);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        DeregisterEventHandler<EventServerCvar>(OnServerCvar);
        RemoveListener<Listeners.OnMapStart>(OnMapStart);

        StopTimer();
        ClearPlayerCache();
    }
    
    private void StartTimer()
    {
        if (_timer != null) return;
        if (_isServerNoSpread) return;

        _timer = AddTimer(Config.TimerInterval, UpdatePlayerSpreadEffect, TimerFlags.REPEAT);
    }
    
    private void StopTimer()
    {
        if (_timer == null) return;
        _timer.Kill();
        _timer = null;
        
        ResetAllClientSpread();
    }
    
    private void InitializePlayerCache()
    {
        try
        {
            var players = Utilities.GetPlayers();
            foreach (var player in players)
            {
                if (player.IsValid && !player.IsBot && player.Slot >= 0 && player.Slot < MaxPlayers)
                {
                    AddPlayerToCache(player);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
    
    private void AddPlayerToCache(CCSPlayerController controller)
    {
        int slot = controller.Slot;
        if (slot < 0 || slot >= MaxPlayers) return;
        
        _cachedControllers[slot] = controller;
        
        if (Config.Debug)
        {
            Console.WriteLine($"[Dropshot] Added player {controller.PlayerName} (slot: {slot}) to cache");
        }
    }
    
    private void RemovePlayerFromCache(int slot)
    {
        if (slot < 0 || slot >= MaxPlayers) return;
        
        _cachedControllers[slot] = null;
        _lastJumpTimes[slot] = 0;
        _lastShotTimes[slot] = 0;
        _lastSpreadStates[slot] = false;
    }
    
    private void ClearPlayerCache()
    {
        for (int i = 0; i < MaxPlayers; i++)
        {
            RemovePlayerFromCache(i);
        }
    }
    
    private void CheckServerNoSpreadInitial()
    {
        var nospreadConVar = ConVar.Find("weapon_accuracy_nospread");
        if (nospreadConVar == null) return;
        bool isNoSpreadEnabled = nospreadConVar.GetPrimitiveValue<bool>();
        if (isNoSpreadEnabled != _isServerNoSpread)
        {
            _isServerNoSpread = isNoSpreadEnabled;
            if (!_isServerNoSpread)
            {
                StartTimer();
            }
            else
            {
                StopTimer();
            }
        }
    }
    
    private void UpdatePlayerSpreadEffect()
    {
        if (_isServerNoSpread) return;
        
        for (int slot = 0; slot < MaxPlayers; slot++)
        {
            var controller = _cachedControllers[slot];
            
            // Проверяем актуальность контроллера
            if (controller == null || !controller.IsValid)
            {
                RemovePlayerFromCache(slot);
                continue;
            }
            
            // Получаем павн напрямую из контроллера
            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;
            
            bool shouldRemoveSpread = ShouldRemoveSpread(controller, pawn, slot, isShot:false);
            
            if (shouldRemoveSpread != _lastSpreadStates[slot])
            {
                _lastSpreadStates[slot] = shouldRemoveSpread;
                controller.ReplicateConVar("weapon_accuracy_nospread", shouldRemoveSpread ? "true" : "false");
                
                if (Config.Debug)
                {
                    string status = shouldRemoveSpread ? "enabled" : "disabled";
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01" + $"No-spread {status} for {controller.PlayerName}");
                }
            }
        }
    }
    
    private void ResetAllClientSpread()
    {
        var stringValue = ConVar.Find("weapon_accuracy_nospread")?.StringValue ?? "false";
        
        for (int slot = 0; slot < MaxPlayers; slot++)
        {
            var controller = _cachedControllers[slot];
            if (controller != null && controller.IsValid)
            {
                controller.ReplicateConVar("weapon_accuracy_nospread", stringValue);
                _lastSpreadStates[slot] = false;
            }
        }
    }

    private HookResult ProcessShotPre(DynamicHook hook)
    {
        if (_isServerNoSpread) return HookResult.Continue;
        
        var weaponPtr = hook.GetParam<IntPtr>(0);
        if (weaponPtr == IntPtr.Zero)
            return HookResult.Continue;
        
        var weapon = new CBasePlayerWeapon(weaponPtr);
        
        var ownerHandle = weapon.OwnerEntity;
        if (!ownerHandle.IsValid)
            return HookResult.Continue;
        
        var owner = ownerHandle.Value;
        if (owner == null || !owner.IsValid)
            return HookResult.Continue;

        // Пытаемся преобразовать владельца к CCSPlayerPawn
        var playerPawn = owner.As<CCSPlayerPawn>();
        if (!playerPawn.IsValid)
            return HookResult.Continue;

        // Получаем контроллер игрока
        var controllerHandle = playerPawn.OriginalController;
        if (!controllerHandle.IsValid)
            return HookResult.Continue;

        var playerController = controllerHandle.Value;
        if (playerController == null || !playerController.IsValid || playerController.IsBot)
            return HookResult.Continue;
        
        if(Config.Debug)
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01" + $"GET_INACCURACY handled for weaponPtr=0x{weaponPtr.ToInt64():X} for player: {playerController.PlayerName}");
        
        int slot = playerController.Slot;
        if (slot >= 0 && slot < MaxPlayers && ShouldRemoveSpread(playerController, playerPawn, slot, isShot:true))
        {
            hook.SetReturn(0.0f);
            if (Config.Debug)
                Server.PrintToChatAll($" \x02[Dropshot Debug] \x04" + $" NOSPREAD APPLIED\x01" + $" for {playerController.PlayerName}");
        }

        return HookResult.Continue;
    }
    
    private bool ShouldRemoveSpread(CCSPlayerController controller, CCSPlayerPawn pawn, int slot, bool isShot = false)
    {
        // 1. Проверка на нахождение на земле
        if (Config.OnGroundCheck)
        {
            if (((PlayerFlags)pawn.Flags).HasFlag(PlayerFlags.FL_ONGROUND) || pawn.GroundEntity.IsValid)
            {
                if (Config.Debug && isShot)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} stands on the ground");
                return false;
            }
        }

        // 2. Проверка скорости
        float currentSpeed = MathF.Sqrt(pawn.AbsVelocity.X * pawn.AbsVelocity.X + pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);
        if (currentSpeed > Config.Speed)
        {
            if (Config.Debug && isShot)
                Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} speed too high: {currentSpeed:F1}/{Config.Speed}");
            return false;
        }

        float currentTime = Server.CurrentTime;

        // 3. Задержка после выстрела
        float lastShotTime = _lastShotTimes[slot];
        if (Config.Delay > 0 && lastShotTime > 0)
        {
            var timeSinceLastShot = currentTime - lastShotTime;
            if (timeSinceLastShot < Config.Delay)
            {
                if (Config.Debug && isShot)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} shot delay: {timeSinceLastShot:F2}/{Config.Delay}");
                return false;
            }
        }

        // 4. Задержка после прыжка
        float lastJumpTime = _lastJumpTimes[slot];
        if (Config.JDelay > 0 && lastJumpTime > 0)
        {
            var timeSinceLastJump = currentTime - lastJumpTime;
            if (timeSinceLastJump < Config.JDelay)
            {
                if (Config.Debug && isShot)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} jump delay: {timeSinceLastJump:F2}/{Config.JDelay}");
                return false;
            }
        }
        
        // 5. Проверка вертикальной скорости
        if (Config.VSpeedCheckMode != 0)
        {
            float verticalSpeed = pawn.AbsVelocity.Z;
            bool isSpeedInvalid = false;
            string condition = "";

            if (Config.VSpeedCheckMode == 1 && verticalSpeed < Config.VSpeed)
            {
                isSpeedInvalid = true;
                condition = "less";
            }
            else if (Config.VSpeedCheckMode == 2 && verticalSpeed > Config.VSpeed)
            {
                isSpeedInvalid = true;
                condition = "greater";
            }

            if (isSpeedInvalid)
            {
                if (Config.Debug && isShot)
                {
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} vertical speed {condition} than limit: {verticalSpeed:F1}/{Config.VSpeed}");
                }
                return false;
            }
        }
        
        return true;
    }
    
    private HookResult OnServerCvar(EventServerCvar @event, GameEventInfo info)
    {
        if (@event.Cvarname == "weapon_accuracy_nospread" && @event.Cvarvalue is "true" or "1")
        {
            _isServerNoSpread = true;
            Server.NextFrame(StopTimer);
        }
        else if (@event.Cvarname == "weapon_accuracy_nospread" && @event.Cvarvalue is "false" or "0")
        {
            _isServerNoSpread = false;
            Server.NextFrame(StartTimer);
        }
        return HookResult.Continue;
    }
    
    private void OnMapStart(string mapName)
    {
        Console.WriteLine($"[Dropshot] Map started: {mapName}");
        
        // Очищаем кэш при старте карты - игроки переподключатся
        ClearPlayerCache();
        
        CheckServerNoSpreadInitial();
    }
    
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && !player.IsBot)
        {
            AddPlayerToCache(player);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            RemovePlayerFromCache(player.Slot);
        }
        return HookResult.Continue;
    }
    
    private HookResult OnPlayerJump(EventPlayerJump handler, GameEventInfo info)
    {
        if (_isServerNoSpread) return HookResult.Continue;
        
        var player = handler.Userid;
        if (player != null && player.IsValid)
        {
            int slot = player.Slot;
            if (slot >= 0 && slot < MaxPlayers)
            {
                _lastJumpTimes[slot] = Server.CurrentTime;
            }
        }
        return HookResult.Continue;
    }
    
    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        if (_isServerNoSpread) return HookResult.Continue;
        
        var controller = @event.Userid;
        if (controller != null && controller.IsValid && !controller.IsBot)
        {
            int slot = controller.Slot;
            if (slot >= 0 && slot < MaxPlayers)
            {
                Server.NextFrame(() => {_lastShotTimes[slot] = Server.CurrentTime;});
            }
        }
        return HookResult.Continue;
    }
    
    public void OnConfigParsed(Config config)
    {
        if (config.Speed <= 0)
            config.Speed = 200;

        if (config.Delay < 0)
            config.Delay = 0;
        
        if (config.JDelay < 0)
            config.JDelay = 0;
            
        if (config.TimerInterval < 0.02f)
            config.TimerInterval = 0.1f;
        
        if (config.VSpeedCheckMode > 2)
            config.VSpeedCheckMode = 0;
        
        Config = config;
    }

    public required Config Config { get; set; }
}
