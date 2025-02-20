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
    public override string ModuleVersion => "1.0.2";
    public override string ModuleAuthor => "Rexus Ohm";

    private readonly bool[] _nospreadEnabled = new bool[64];
    
    private readonly ConVar? _weaponaccuracynospread = ConVar.Find("weapon_accuracy_nospread");
    
    private readonly Dictionary<int, float> _lastShotTicks = new();
    private readonly Dictionary<int, float> _lastJumpTicks = new();
    
    private HookResult OnWeaponFire(EventWeaponFire handler, GameEventInfo info)
    {
        var player = handler.Userid.Slot;
        {
            _lastShotTicks[player] = Server.CurrentTime;
        }
        return HookResult.Continue;
    }
   
    private HookResult OnPlayerJump(EventPlayerJump handler, GameEventInfo info)
    {
        var playerjump = handler.Userid.Slot;
        {
            _lastJumpTicks[playerjump] = Server.CurrentTime;
        }
        return HookResult.Continue;
    }
    
    public override void Load(bool hotReload)
    {
        CBasePlayerWeapon_GetInaccuracy.Hook(ProcessShotPre, HookMode.Pre);
        CBasePlayerWeapon_GetInaccuracy.Hook(ProcessShotPost, HookMode.Post);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        
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
        CBasePlayerWeapon_GetInaccuracy.Hook(ProcessShotPre, HookMode.Pre);
        CBasePlayerWeapon_GetInaccuracy.Hook(ProcessShotPost, HookMode.Post);
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

        if (_lastShotTicks.TryGetValue(player.Slot, out var lastShotTick)&&Server.CurrentTime < lastShotTick + Config.Delay)
        {
            shouldEnable = false;
        }
        
        if (_lastJumpTicks.TryGetValue(player.Slot, out var lastJumpTick)&&Server.CurrentTime < lastJumpTick + Config.JDelay)
        {
            shouldEnable = false;
        }

        if (oldValue != shouldEnable)
        {
            player.ReplicateConVar("weapon_accuracy_nospread", shouldEnable? "1" : "0");
            _nospreadEnabled[player.Slot] = shouldEnable;
            if(Config.Debug.Equals(true))
                Server.PrintToChatAll(shouldEnable? "NS enabled" : "NS disabled");
        }
    }
    //
    private bool? _oldValue;
    public HookResult ProcessShotPre(DynamicHook hook)
    {
        if (_weaponaccuracynospread == null)
        {
            return HookResult.Continue;
        }
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll("1"); //logger before set
        CBasePlayerWeapon weapon = hook.GetParam<CBasePlayerWeapon>(0);
        var cBasePlayerPawn = weapon.OwnerEntity.Value?.As<CBasePlayerPawn>();
        if (cBasePlayerPawn == null)
            return HookResult.Continue;
        var player = cBasePlayerPawn.Controller.Value?.As<CCSPlayerController>();
        if (player == null || !player.IsValid)
            return HookResult.Continue;
        if (GetHorizontalSpeed(cBasePlayerPawn) > Config.Speed)
            return HookResult.Continue;
        if (((PlayerFlags)player.Flags).HasFlag(PlayerFlags.FL_ONGROUND) || cBasePlayerPawn.GroundEntity?.IsValid == true)
            return HookResult.Continue;
        if (_lastShotTicks.TryGetValue(player.Slot, out var lastShotTick)&&Server.CurrentTime < lastShotTick + Config.Delay)
            return HookResult.Continue;
        if (_lastJumpTicks.TryGetValue(player.Slot, out var lastJumpTick)&&Server.CurrentTime < lastJumpTick + Config.JDelay)
            return HookResult.Continue;
        _oldValue = _weaponaccuracynospread.GetPrimitiveValue<bool>();
        _weaponaccuracynospread.SetValue(true);
        if(Config.Debug.Equals(true))
            Server.PrintToChatAll("2"); //logger after set

        return HookResult.Continue;
    }

    public HookResult ProcessShotPost(DynamicHook hook)
    {
        if (_oldValue.HasValue)
        {
            _weaponaccuracynospread!.SetValue(_oldValue);
        }
        return HookResult.Continue;
    }
    //
    
    private float GetHorizontalSpeed(CBasePlayerPawn pawn)
    {
        Vector velocity = new Vector()
        {
            X=pawn.AbsVelocity.X,
            Y=pawn.AbsVelocity.Y
        };
        return velocity.Length();
    }

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