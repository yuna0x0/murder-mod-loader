using System.Runtime.Loader;

/// <summary>
/// Entry point for DOTNET_STARTUP_HOOKS. Called by the .NET runtime
/// before Main(), allowing us to set up mod loading before the game starts.
/// </summary>
internal class StartupHook
{
    public static void Initialize()
    {
        // Resolve loader dependencies (Harmony, YamlDotNet, API) from the
        // loader directory since DOTNET_STARTUP_HOOKS doesn't probe for them.
        var loaderDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(loaderDir, $"{name.Name}.dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };

        // Fix AppContext.BaseDirectory to point to the game directory.
        // When running via `dotnet exec .modded/Game.dll`, BaseDirectory
        // points to .modded/ but the game expects resources/ at the game root.
        // The loader lives at mods/loader/, so game root is two levels up.
        var gameDir = Path.GetFullPath(Path.Combine(loaderDir, "..", ".."));
        if (Directory.Exists(Path.Combine(gameDir, "resources")))
        {
            var basePath = gameDir.EndsWith(Path.DirectorySeparatorChar) ? gameDir : gameDir + Path.DirectorySeparatorChar;
            AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", basePath);
        }

        // Harmony uses JsonSerializer internally. Murder games disable
        // reflection-based serialization, so re-enable it for Harmony.
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

        MurderModLoader.ModManager.Instance.Initialize();
    }
}
