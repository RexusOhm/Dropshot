# Dropshot CS2
The plugin is designed for playing in surf combat mode.
Allows you to shoot with nospread when stopping in the air.

### Video demonstration of the plugin in action:

[![](https://markdown-videos-api.jorgenkh.no/youtube/mOquZeqtv7M.gif?width=480&height=320&duration=500)](https://youtu.be/mOquZeqtv7M)

## Installation

Extract the release archive into the **server root**:

```
addons/counterstrikesharp/
├── plugins/
│   └── Dropshot/
│       └── Dropshot.dll
└── shared/
    └── Dropshot.API/
        └── Dropshot.API.dll
```

------------------------------------------------------------------------

## Configuration

Auto-generated at:
`addons/counterstrikesharp/configs/plugins/Dropshot/Dropshot.json`

| Key | Default | Description |
|---|---|---|
| `Player speed` | `200` | Max horizontal speed to allow no-spread |
| `After shot delay` | `5` | Seconds before no-spread re-activates after shooting |
| `After jump delay` | `2` | Seconds before no-spread activates after jumping |
| `Player on ground check` | `true` | Disable no-spread when on ground |
| `Player vertical speed check` | `0` | `0` — off, `1` — less than threshold, `2` — more than threshold |
| `Player vertical speed` | `0` | Vertical speed threshold for the check above |
| `Timer update interval` | `0.1` | State check interval in seconds |
| `Debug` | `false` | Print debug messages to chat |

------------------------------------------------------------------------

## API

Dropshot exposes a public API via `Dropshot.API.dll`.

| Method | Description |
|---|---|
| `IsAllowDropshot(player) → bool` | Returns `true` if the player currently meets all no-spread conditions |
| `event OnDropshotShot` | Fired at the moment NoSpread is applied, before the actual shot. Receives `CCSPlayerController` |

Reference `Dropshot.API.dll` in your `.csproj`:

```xml
<ItemGroup>
    <Reference Include="Dropshot.API">
        <HintPath>Dropshot.API\Dropshot.API.dll</HintPath>
    </Reference>
</ItemGroup>
```

```csharp
private static readonly PluginCapability<IDropshotApi> DropshotCapability = new("dropshot:api");
private IDropshotApi? _dropshotApi;

public override void OnAllPluginsLoaded(bool hotReload)
{
    _dropshotApi = DropshotCapability.Get();
    _dropshotApi.OnDropshotShot += OnDropshotShot;
}

public override void Unload(bool hotReload)
{
    if (_dropshotApi != null)
        _dropshotApi.OnDropshotShot -= OnDropshotShot;
}

private void OnDropshotShot(CCSPlayerController player)
{
    // called when player shoots with no-spread active
}
```

------------------------------------------------------------------------

## License

GPLv3  
https://www.gnu.org/licenses/gpl-3.0.en.html

------------------------------------------------------------------------

## Author

**Rexus Ohm**
