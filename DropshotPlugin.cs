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
    [JsonPropertyName("After shot delay")] public float Delay { get; set; } = 5;
    [JsonPropertyName("After jump delay")] public float JDelay { get; set; } = 2;
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;
}

public class DropshotPlugin : BasePlugin, IPluginConfig<Config>
{
    public static readonly MemoryFunctionWithReturn<CBasePlayerWeapon, IntPtr, IntPtr, IntPtr, float, float> CBasePlayerWeaponGetInaccuracy =
        new("55 48 89 E5 41 57 41 56 49 89 D6 41 55 41 54 49 89 FC 53 48 89 F3 48 83 EC 48 E8 ? ? ? ?");
    
    public override string ModuleName => "Dropshot Plugin";
    public override string ModuleVersion => "1.0.6";
    public override string ModuleAuthor => "Rexus Ohm";

    //private readonly bool[] _nospreadEnabled = new bool[64];
    private readonly Dictionary<ulong, bool> _nospreadEnabled = new();
    
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
        CBasePlayerWeaponGetInaccuracy.Hook(ProcessShotPre, HookMode.Pre);
        CBasePlayerWeaponGetInaccuracy.Hook(ProcessShotPost, HookMode.Post);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        
        //RegisterListener<Listeners.OnTick>(() =>
        AddTimer(0.1f, () => 
        {
            foreach (var player in Utilities.GetPlayers().Where(p => 
                         p.IsValid && p.PawnIsAlive && 
                         p.Pawn.Value is not null && !p.IsBot))
            {
                UpdatePlayerSpread(player);
            }
        }, TimerFlags.REPEAT);
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
        if(((PlayerFlags)player.Flags).HasFlag(PlayerFlags.FL_ONGROUND) || player.GroundEntity?.IsValid == true)
        {
            shouldEnable = false;
        }

        // Проверяем задержку после последнего выстрела
        if (_lastShotTimes.TryGetValue(player.SteamID, out DateTime lastShotTime))
        {
            double timeSinceLastShot = (DateTime.UtcNow - lastShotTime).TotalSeconds;
            if (timeSinceLastShot < Config.Delay)
            {
                shouldEnable = false;
            }
        }
        
        // Проверяем задержку после последнего прыжка
        if (_lastJumpTicks.TryGetValue(player.SteamID, out DateTime lastJumpTime))
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
            _nospreadEnabled[player.SteamID] = shouldEnable;
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

        if (((PlayerFlags)playerPawn.Flags).HasFlag(PlayerFlags.FL_ONGROUND) ||
            playerPawn.GroundEntity?.IsValid == true)
        {
            if (Config.Debug)
                Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{playerController.PlayerName} stands on the ground");
            return HookResult.Continue;
        }
        
        // Проверяем задержку после последнего выстрела
        if (_lastShotTimes.TryGetValue(steamId, out DateTime lastShotTime))
        {
            double timeSinceLastShot = (DateTime.UtcNow - lastShotTime).TotalSeconds;
            if (timeSinceLastShot < Config.Delay)
            {
                if (Config.Debug)
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{playerController.PlayerName} has delay after shot: \n{timeSinceLastShot}/{Config.Delay} sec");
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
                    Server.PrintToChatAll($" \x02[Dropshot Debug] \x01{playerController.PlayerName} has delay after jump: \n{timeSinceLastJump}/{Config.JDelay} sec");
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

        Server.NextFrameAsync(() => _weaponaccuracynospread.SetValue(_oldValue.Value));
        
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll(" \x02[Dropshot Debug] \x01 3");
    
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
    
    // очистка данных
     private void OnMapStart(string mapName)
    {
        //Array.Clear(_nospreadEnabled, 0, _nospreadEnabled.Length);
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
