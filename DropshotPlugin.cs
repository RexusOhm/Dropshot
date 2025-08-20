using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Dropshot;

public class Config : BasePluginConfig
{
    [JsonPropertyName("Player speed")] public float Speed { get; set; } = 200;
    [JsonPropertyName("After shot delay")] public float ShotDelay { get; set; } = 5;
    [JsonPropertyName("After jump delay")] public float JumpDelay { get; set; } = 2;
    [JsonPropertyName("On ground checker")] public bool OnGroundChecker { get; set; } = true;
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;
}

public class DropshotPlugin : BasePlugin, IPluginConfig<Config>
{
    public static readonly MemoryFunctionWithReturn<CBasePlayerWeapon, IntPtr, IntPtr, IntPtr, float, float> CBasePlayerWeaponGetInaccuracy =
        new("55 48 89 E5 41 57 41 56 49 89 D6 41 55 49 89 F5 41 54 53 48 89 FB 48 83 EC ? E8");
                                                                                                                                                                                                                                                                                                                                          
    public override string ModuleName => "Dropshot";
    public override string ModuleVersion => "1.0.8";
    public override string ModuleAuthor => "Rexus Ohm";

    private readonly ConVar? _weaponaccuracynospread = ConVar.Find("weapon_accuracy_nospread");
    
    private readonly Dictionary<ulong, bool> _nospreadEnabled = new();
    private readonly Dictionary<ulong, float> _lastJumpTicks = new();
    private readonly Dictionary<ulong, float> _lastShotTimes = new();
    
    private bool? _oldValue;
    
    public override void Load(bool hotReload)
    {
        CBasePlayerWeaponGetInaccuracy.Hook(ProcessShotPre, HookMode.Pre);
        CBasePlayerWeaponGetInaccuracy.Hook(ProcessShotPost, HookMode.Post);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        try
        {
            AddTimer(0.1f, () =>
            {
                foreach (var player in Utilities.GetPlayers().Where(p => 
                             p.IsValid && p.PawnIsAlive && 
                             p.Pawn.Value is not null && !p.IsBot))
                {
                    UpdatePlayerSpread(player);
                }
            }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public override void Unload(bool hotReload)
    {
        CBasePlayerWeaponGetInaccuracy.Unhook(ProcessShotPre, HookMode.Pre);
        CBasePlayerWeaponGetInaccuracy.Unhook(ProcessShotPost, HookMode.Post);
    }

    private void UpdatePlayerSpread(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;
        float speed = GetHorizontalSpeed(pawn);
        bool shouldEnable = speed <= Config.Speed;
        if (!_nospreadEnabled.TryGetValue(player.SteamID, out bool oldValue))
        {
            oldValue = false;
            _nospreadEnabled[player.SteamID] = oldValue;
        }
        if(Config.OnGroundChecker && (((PlayerFlags)player.Flags).HasFlag(PlayerFlags.FL_ONGROUND) || player.GroundEntity?.IsValid == true))
        {
            shouldEnable = false;
        }

        // Проверяем задержку после последнего выстрела
        if (Config.ShotDelay > 0 && _lastShotTimes.TryGetValue(player.SteamID, out float lastShotTime))
        {
            var timeSinceLastShot = Server.CurrentTime - lastShotTime;
            if (timeSinceLastShot < Config.ShotDelay)
            {
                shouldEnable = false;
            }
        }
        
        // Проверяем задержку после последнего прыжка
        if (Config.JumpDelay > 0 && _lastJumpTicks.TryGetValue(player.SteamID, out float lastJumpTime))
        {
            var timeSinceLastJump = Server.CurrentTime - lastJumpTime;
            if (timeSinceLastJump < Config.JumpDelay)
            {
                shouldEnable = false;
            }
        }

        if (oldValue != shouldEnable)
        {
            player.ReplicateConVar("weapon_accuracy_nospread", shouldEnable? "1" : "0");
            _nospreadEnabled[player.SteamID] = shouldEnable;
            if(Config.Debug.Equals(true))
                Server.PrintToChatAll(shouldEnable? " \x02[Dropshot Debug] \x01NS enabled" : " \x02[Dropshot Debug] \x01NS disabled");
        }
    }

    private HookResult ProcessShotPre(DynamicHook hook)
    {
        if (_weaponaccuracynospread == null)
        {
            return HookResult.Continue;
        }
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01 1 start hook"); //logger before set
        CBasePlayerWeapon weapon = hook.GetParam<CBasePlayerWeapon>(0);
        var playerPawn = weapon.OwnerEntity.Value?.As<CCSPlayerPawn>();
        
        if (playerPawn == null || !playerPawn.IsValid)
            return HookResult.Continue;
        
        var playerController = playerPawn.OriginalController.Value;
        if (playerController == null || !playerController.IsValid || playerController.IsBot)
            return HookResult.Continue;
        
        ulong steamId = playerController.SteamID;
        
        if (GetHorizontalSpeed(playerPawn) > Config.Speed)
        {
            if (Config.Debug)
                Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{playerController.PlayerName} has a speed out of value: \n{GetHorizontalSpeed(playerPawn)}/{Config.Speed} unit/s");
            return HookResult.Continue;
        }

        if (Config.OnGroundChecker && (((PlayerFlags)playerPawn.Flags).HasFlag(PlayerFlags.FL_ONGROUND) || playerPawn.GroundEntity?.IsValid == true))
        {
            if (Config.Debug)
                Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{playerController.PlayerName} stands on the ground");
            return HookResult.Continue;
        }
        
        // Проверяем задержку после последнего выстрела
        if (Config.ShotDelay > 0 && _lastShotTimes.TryGetValue(steamId, out float lastShotTime))
        {
            var timeSinceLastShot = Server.CurrentTime - lastShotTime;
            if (timeSinceLastShot < Config.ShotDelay)
            {
                if (Config.Debug)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{playerController.PlayerName} has delay after shot: \n{timeSinceLastShot}/{Config.ShotDelay} sec");
                return HookResult.Continue;
            }
        }
        
        // Проверяем задержку после последнего прыжка
        if (Config.JumpDelay > 0 && _lastJumpTicks.TryGetValue(steamId, out float lastJumpTime))
        {
            var timeSinceLastJump = Server.CurrentTime - lastJumpTime;
            if (timeSinceLastJump < Config.JumpDelay)
            {
                if (Config.Debug)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{playerController.PlayerName} has delay after jump: \n{timeSinceLastJump}/{Config.JumpDelay} sec");
                return HookResult.Continue;
            }
        }
        
        _oldValue = _weaponaccuracynospread.GetPrimitiveValue<bool>();
        _weaponaccuracynospread.SetValue(true);
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01 2 nospread changed"); //logger after set
        
        // Обновляем время последнего выстрела
        _lastShotTimes[steamId] = Server.CurrentTime;
        return HookResult.Continue;
    }

    private HookResult ProcessShotPost(DynamicHook hook)
    {
        if (_weaponaccuracynospread == null || !_oldValue.HasValue)
        {
            return HookResult.Continue;
        }

        Server.NextFrame(() => _weaponaccuracynospread.SetValue(_oldValue.Value));
        
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01 3 cleanup");
    
        return HookResult.Continue;
    }
    
    private float GetHorizontalSpeed(CCSPlayerPawn pawn)
    {
        Vector velocity = new Vector()
        {
            X=pawn.AbsVelocity.X,
            Y=pawn.AbsVelocity.Y
        };
        return velocity.Length();
    }
    
    private HookResult OnPlayerJump(EventPlayerJump handler, GameEventInfo info)
    {
        var player = handler.Userid;
        if (player == null || !player.IsValid) 
            return HookResult.Continue;
        
        _lastJumpTicks[player.SteamID] = Server.CurrentTime;
        return HookResult.Continue;
    }
    
    // очистка данных
     private void OnMapStart(string mapName)
    {
        _lastShotTimes.Clear();
        _lastJumpTicks.Clear();
        _nospreadEnabled.Clear();
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
            config.Speed = 200;
        }

        if (config.ShotDelay < 0)
        {
            config.ShotDelay = 0;
        }
        
        if (config.JumpDelay < 0)
        {
            config.JumpDelay = 0;
        }
        Config = config;
    }

    public required Config Config { get; set; }
}
