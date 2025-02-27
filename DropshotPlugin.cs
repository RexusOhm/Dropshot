using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace Dropshot;

public class Config : BasePluginConfig
{
    [JsonPropertyName("Player speed")] public float Speed { get; set; } = 200;
    [JsonPropertyName("After shot delay")] public float Delay { get; set; } = 5;
    [JsonPropertyName("After jump delay")] public float JDelay { get; set; } = 2;
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;
}

public class DropshotPlugin : BasePlugin, IPluginConfig<Config>
{
    public static readonly MemoryFunctionWithReturn<CBasePlayerWeapon, IntPtr, IntPtr, IntPtr, float, float> CBasePlayerWeapon_GetInaccuracy =
        new("55 48 89 E5 41 57 41 56 49 89 D6 41 55 41 54 49 89 FC 53 48 89 F3 48 83 EC 48 E8 ? ? ? ?");
    
    public override string ModuleName => "Dropshot Plugin";
    public override string ModuleVersion => "1.0.5";
    public override string ModuleAuthor => "Rexus Ohm";

    private readonly bool[] _nospreadEnabled = new bool[64];
    
    private readonly ConVar? _weaponaccuracynospread = ConVar.Find("weapon_accuracy_nospread");

    private readonly Dictionary<ulong, DateTime> _lastJumpTicks = new();
    
    private readonly Dictionary<ulong, DateTime> _lastShotTimes = new();
    
    private bool? _oldValue;
    
    private HookResult OnPlayerJump(EventPlayerJump handler, GameEventInfo info)
    {
        var player = handler.Userid;
        if (player == null || !player.IsValid) 
            return HookResult.Continue;
        
        _lastJumpTicks[player.SteamID] = DateTime.UtcNow;
        return HookResult.Continue;
    }
    
    public override void Load(bool hotReload)
    {
        CBasePlayerWeapon_GetInaccuracy.Hook(ProcessShotPre, HookMode.Pre);
        CBasePlayerWeapon_GetInaccuracy.Hook(ProcessShotPost, HookMode.Post);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        
        RegisterListener<Listeners.OnTick>(() =>
        {
            foreach (var player in Utilities.GetPlayers().Where(p => 
                         p.IsValid && 
                         p.PawnIsAlive && 
                         p.Pawn.Value != null))
            {
                UpdatePlayerSpread(player);
            }
        });
    }

    public override void Unload(bool hotReload)
    {
        CBasePlayerWeapon_GetInaccuracy.Unhook(ProcessShotPre, HookMode.Pre);
        CBasePlayerWeapon_GetInaccuracy.Unhook(ProcessShotPost, HookMode.Post);
    }

    private void UpdatePlayerSpread(CCSPlayerController player)
    {
        var pawn = player.Pawn.Value!;
        float speed = GetHorizontalSpeed(pawn);
        bool shouldEnable = speed <= Config.Speed;
        var oldValue = _nospreadEnabled[player.Slot];
        if(((PlayerFlags)player.Flags).HasFlag(PlayerFlags.FL_ONGROUND) || pawn.GroundEntity?.IsValid == true)
        {
            shouldEnable = false;
        }
        
        // Получаем SteamID
        var controller = pawn.Controller.Value?.As<CBasePlayerController>();
        ulong steamId = controller!.SteamID;
        
        // Проверяем задержку после последнего выстрела
        if (_lastShotTimes.TryGetValue(steamId, out DateTime lastShotTime))
        {
            double timeSinceLastShot = (DateTime.UtcNow - lastShotTime).TotalSeconds;
            if (timeSinceLastShot < Config.Delay)
            {
                shouldEnable = false;
            }
        }
        
        // Проверяем задержку после последнего прыжка
        if (_lastJumpTicks.TryGetValue(steamId, out DateTime lastJumpTime))
        {
            double timeSinceLastJump = (DateTime.UtcNow - lastJumpTime).TotalSeconds;
            if (timeSinceLastJump < Config.JDelay)
            {
                shouldEnable = false;
            }
        }

        if (oldValue != shouldEnable)
        {
            player.ReplicateConVar("weapon_accuracy_nospread", shouldEnable? "1" : "0");
            _nospreadEnabled[player.Slot] = shouldEnable;
            if(Config.Debug.Equals(true))
                Server.PrintToChatAll(shouldEnable? " \x02[Dropshot Debug] \x01NS enabled" : " \x02[Dropshot Debug] \x01NS disabled");
        }
    }
    
    public HookResult ProcessShotPre(DynamicHook hook)
    {
        if (_weaponaccuracynospread == null)
        {
            return HookResult.Continue;
        }
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01 1"); //logger before set
        CBasePlayerWeapon weapon = hook.GetParam<CBasePlayerWeapon>(0);
        var cBasePlayerPawn = weapon.OwnerEntity.Value?.As<CBasePlayerPawn>();
        if (cBasePlayerPawn == null)
            return HookResult.Continue;
        
        // Получаем SteamID
        var controller = cBasePlayerPawn.Controller.Value?.As<CBasePlayerController>();
        if (controller == null)
            return HookResult.Continue;
        ulong steamId = controller.SteamID;
        if (GetHorizontalSpeed(cBasePlayerPawn) > Config.Speed)
        {
            if (Config.Debug)
                Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} has a speed out of value: \n{GetHorizontalSpeed(cBasePlayerPawn)}/{Config.Speed} unit/s");
            return HookResult.Continue;
        }

        if (((PlayerFlags)cBasePlayerPawn.Flags).HasFlag(PlayerFlags.FL_ONGROUND) ||
            cBasePlayerPawn.GroundEntity?.IsValid == true)
        {
            if (Config.Debug)
                Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} stands on the ground");
            return HookResult.Continue;
        }
        
        // Проверяем задержку после последнего выстрела
        if (_lastShotTimes.TryGetValue(steamId, out DateTime lastShotTime))
        {
            double timeSinceLastShot = (DateTime.UtcNow - lastShotTime).TotalSeconds;
            if (timeSinceLastShot < Config.Delay)
            {
                if (Config.Debug)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} has delay after shot: \n{timeSinceLastShot}/{Config.Delay} sec");
                return HookResult.Continue;
            }
        }
        
        // Проверяем задержку после последнего прыжка
        if (_lastJumpTicks.TryGetValue(steamId, out DateTime lastJumpTime))
        {
            double timeSinceLastJump = (DateTime.UtcNow - lastJumpTime).TotalSeconds;
            if (timeSinceLastJump < Config.JDelay)
            {
                if (Config.Debug)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{controller.PlayerName} has delay after jump: \n{timeSinceLastJump}/{Config.JDelay} sec");
                return HookResult.Continue;
            }
        }
        
        _oldValue = _weaponaccuracynospread.GetPrimitiveValue<bool>();
        _weaponaccuracynospread.SetValue(true);
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01 2"); //logger after set
        
        // Обновляем время последнего выстрела
        _lastShotTimes[steamId] = DateTime.UtcNow;
        return HookResult.Continue;
    }

    public HookResult ProcessShotPost(DynamicHook hook)
    {
        if (_weaponaccuracynospread == null || !_oldValue.HasValue)
        {
            return HookResult.Continue;
        }
        
        _weaponaccuracynospread.SetValue(_oldValue.Value);
        
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01 3");
    
        return HookResult.Continue;
    }
    
    private float GetHorizontalSpeed(CBasePlayerPawn pawn)
    {
        Vector velocity = new Vector()
        {
            X=pawn.AbsVelocity.X,
            Y=pawn.AbsVelocity.Y
        };
        return velocity.Length();
    }
    
    // очистка данных
     private void OnMapStart(string mapName)
    {
        Array.Clear(_nospreadEnabled, 0, _nospreadEnabled.Length);
        _lastShotTimes.Clear();
        _lastJumpTicks.Clear();
    }
     
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
        {
            CleanupPlayerData(player.SteamID);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
        {
            CleanupPlayerData(player.SteamID);
        }
        return HookResult.Continue;
    }
    
    private void CleanupPlayerData(ulong steamId)
    {
        _lastShotTimes.Remove(steamId);
        _lastJumpTicks.Remove(steamId);
    }
    // конец очистки данных
    
    public void OnConfigParsed(Config config)
    {
        if (config.Speed <= 0)
        {
            config.Speed = 80;
        }

        if (config.Delay < 0)
        {
            config.Delay = 0;
        }
        
        if (config.JDelay < 0)
        {
            config.JDelay = 0;
        }
        Config = config;
    }

    public Config Config { get; set; }
}
