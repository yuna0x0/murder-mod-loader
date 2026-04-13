# murder-mod-loader

Mod loader for [Murder Engine](https://github.com/isadorasophia/murder) games. Uses [Harmony](https://github.com/pardeike/Harmony) for runtime method patching.

## Requirements

- [murder-unpack](https://github.com/yuna0x0/murder-unpack) (for bundle extraction)
- .NET 8 SDK matching the game's architecture (x64 game needs x64 SDK)

## Setup

```bash
# Build
git clone https://github.com/yuna0x0/murder-mod-loader.git
cd murder-mod-loader
dotnet publish src/MurderModLoader -c Release -o publish/loader/
dotnet publish src/MurderModLoader.Installer -c Release -o publish/installer/

# Install into a game (auto-detects SDK)
dotnet run --project src/MurderModLoader.Installer -- "/path/to/game"

# Launch
/path/to/game/launch-modded.sh    # Linux / macOS
/path/to/game/launch-modded.bat   # Windows
```

## Creating a Mod

Create a directory with `mod.yaml` and a C# project:

```yaml
# mod.yaml
Id: my-mod
Name: My Mod
Version: 1.0.0
DLL: MyMod.dll
```

```csharp
using HarmonyLib;
using MurderModLoader.API;

public class MyMod : IMurderMod
{
    private Harmony? _harmony;
    public void OnLoad(ModContext context)
    {
        _harmony = new Harmony(context.HarmonyId);
        _harmony.PatchAll(context.ModAssembly);
    }
    public void OnUnload() => _harmony?.UnpatchAll(_harmony.Id);
}
```

For game type references (Murder, Bang), set `GameAssemblyPath` in your `.csproj` to the game's `.modded/` directory, or let the build command handle it.

### Build and install a mod

```bash
dotnet run --project src/MurderModLoader.Installer -- build <mod-dir> <game-dir>
```

## Limitations

- **SingleFileBundle only** — NativeAOT games can't be patched at runtime
- **Architecture match** — The .NET SDK must match the game (x64 game needs x64 SDK)

## License

[MIT](LICENSE)
