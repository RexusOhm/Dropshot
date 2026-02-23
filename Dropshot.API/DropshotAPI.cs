using CounterStrikeSharp.API.Core;

namespace Dropshot.API;

public interface IDropshotApi
{   
    /// <summary>
    /// Проверяет доступен ли дропшот для игрока в текущий момент.
    /// </summary>
    /// <param name="player">CCSPlayerController</param>
    bool IsAllowDropshot(CCSPlayerController player);

    /// <summary>
    /// Событие, которое вызывается в момент выстрела, если активен режим без разброса.
    /// Передает контроллер игрока.
    /// </summary>
    event Action<CCSPlayerController>? OnDropshotShot;
}