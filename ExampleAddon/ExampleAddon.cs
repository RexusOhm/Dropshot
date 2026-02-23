using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using Dropshot.API;

namespace ExampleAddon;

public class DropshotExamplePlugin : BasePlugin
{
    public override string ModuleName => "DropshotAddonExample";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Example Author";

    // Инициализируем capability
    private static readonly PluginCapability<IDropshotApi> DropshotCapability = new("dropshot:api");
    private IDropshotApi? _dropshotApi;
    
    // Храним временную метку выстрела
    private readonly bool[] _isLastShotDropshot = new bool[65];

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        // Все плагины загружены — безопасно получаем API
        _dropshotApi = DropshotCapability.Get();

        if (_dropshotApi == null)
        {
            Console.WriteLine("[DropshotAddonExample] Dropshot API не найден! Убедитесь что Dropshot загружен.");
            return;
        }

        // Подписываемся на событие выстрела в дропшоте
        _dropshotApi.OnDropshotShot += OnDropshotShot;

        Console.WriteLine("[DropshotAddonExample] Dropshot API успешно подключён.");
    }

    public override void Unload(bool hotReload)
    {
        // Обязательно отписываемся — иначе при перезагрузке плагина старый обработчик останется висеть в памяти
        if (_dropshotApi != null)
        {
            _dropshotApi.OnDropshotShot -= OnDropshotShot;
        }
        
        DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        DeregisterEventHandler<EventWeaponFire>(OnWeaponFire);
    }

    /// <summary>
    /// Этот метод вызывается каждый раз, когда игрок стреляет в режиме дропшота.
    /// Вызов происходит до фактического момента выстрела, во время установки для игрока нулевого разброса.
    /// </summary>
    /// <remarks>Получает контроллер игрока (CCSPlayerController player)</remarks>
    private void OnDropshotShot(CCSPlayerController player)
    {
        Server.PrintToChatAll($" \x04[DropshotAddonExample] \x01{player.PlayerName} сделал дропшот выстрел!");
        
        if (player.IsValid)
        {
            _isLastShotDropshot[player.Slot] = true;
        }
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        // Сбрасываем метку на следующем кадре сервера
        Server.NextFrame(() => {
            if (player != null && player.IsValid) _isLastShotDropshot[player.Slot] = false;
        });
        return HookResult.Continue;
    }
    
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        if (attacker == null || !attacker.IsValid || attacker.IsBot) 
            return HookResult.Continue;

        bool isDropshotKill = false;

        // 1. Точная проверка момента выстрела
        if (_isLastShotDropshot[attacker.Slot])
        {
            isDropshotKill = true;
        }
        // 2. Резервная проверка состояния (если событие не успело обновить флаг)
        // _dropshotApi.IsAllowDropshot(attacker) проверяет игрока доступен ли ему дропшот в данный момент и возвращает bool.
        else if (_dropshotApi != null && _dropshotApi.IsAllowDropshot(attacker))
        {
            isDropshotKill = true;
        }

        if (isDropshotKill)
        {
            Server.PrintToChatAll($" \x04[DropshotAddonExample] Игрок \x01{attacker.PlayerName} совершил Dropshot Kill!");
        }

        return HookResult.Continue;
    }
}