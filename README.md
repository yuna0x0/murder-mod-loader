# murder-mod-loader

Mod loader for [Murder Engine](https://github.com/isadorasophia/murder) games. Uses [Harmony](https://github.com/pardeike/Harmony) for runtime method patching.

## Requirements

- [murder-unpack](https://github.com/yuna0x0/murder-unpack) (`uv tool install murder-unpack`)
- .NET 8 SDK matching the game's architecture (x64 game needs x64 SDK)

## Installation

### Install the tools

```bash
# Install murder-unpack (bundle extraction)
uv tool install murder-unpack

# Install the mod loader installer
dotnet tool install -g murder-mod-install
```

### Install into a game

```bash
# Auto-detects SDK, extracts bundle, sets up mod loader
murder-mod-install "/path/to/game"

# macOS .app bundles are also supported
murder-mod-install "/path/to/Game.app"

# Specify .NET SDK path manually if needed
murder-mod-install "/path/to/game" "/path/to/dotnet-sdk-8.0"
```

### Launch

```bash
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

Reference the mod API in your `.csproj`:

```xml
<PackageReference Include="MurderModLoader.API" Version="0.1.*" />
```

For game type references (Murder, Bang, FNA), set `GameAssemblyPath` in your `.csproj` to the game's `.modded/` directory, or let the build command handle it.

### Build and install a mod

```bash
murder-mod-install build <mod-dir> <game-dir>
```

### Disabling a mod

Place a `.disabled` file in the mod's directory, or set `Enabled: false` in `mod.yaml`.

## Building from Source

```bash
git clone https://github.com/yuna0x0/murder-mod-loader.git
cd murder-mod-loader
dotnet build
```

## Limitations

- **SingleFileBundle only** -- NativeAOT games can't be patched at runtime
- **Architecture match** -- the .NET SDK must match the game (x64 game needs x64 SDK)

## License

[MIT](LICENSE)
