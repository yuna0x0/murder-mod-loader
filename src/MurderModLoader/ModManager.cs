using System.Reflection;
using HarmonyLib;
using MurderModLoader.API;

namespace MurderModLoader;

/// <summary>
/// Core mod manager. Discovers, loads, and manages mods.
/// Initialized by StartupHook before the game's Main() runs.
/// </summary>
public sealed class ModManager
{
    public static ModManager Instance { get; } = new();

    private readonly List<LoadedMod> _mods = [];
    private readonly string _modsDir;
    private bool _initialized;

    public IReadOnlyList<LoadedMod> Mods => _mods;

    private ModManager()
    {
        // Mods directory is relative to the loader DLL location,
        // which is always in mods/loader/ under the game directory.
        var loaderDir = Path.GetDirectoryName(typeof(ModManager).Assembly.Location) ?? ".";
        _modsDir = Path.GetDirectoryName(loaderDir) ?? Path.Combine(loaderDir, "..");
    }

    /// <summary>
    /// Called by StartupHook.Initialize() before Main().
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Register shutdown hook so OnUnload() is called on exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        Log.Info("Murder Mod Loader initializing...");

        if (!Directory.Exists(_modsDir))
        {
            Log.Info($"No mods directory found at {_modsDir}");
            return;
        }

        // Phase 1: Discover mods
        var discovered = ModDiscovery.ScanModsDirectory(_modsDir);
        if (discovered.Count == 0)
        {
            Log.Info("No mods found.");
            return;
        }
        Log.Info($"Discovered {discovered.Count} mod(s)");

        // Phase 2: Resolve dependencies and sort
        var sorted = ModDependencyResolver.Resolve(discovered);

        // Phase 3: Load mod assemblies
        foreach (var meta in sorted)
        {
            try
            {
                var mod = LoadMod(meta);
                _mods.Add(mod);
                Log.Info($"  Loaded: {meta.Name} v{meta.Version}");
            }
            catch (Exception ex)
            {
                Log.Error($"  Failed to load {meta.Name}: {ex}");
            }
        }

        // Phase 4: Call OnLoad on all mods
        var failedMods = new List<LoadedMod>();
        foreach (var mod in _mods)
        {
            try
            {
                mod.Instance.OnLoad(mod.Context);
            }
            catch (Exception ex)
            {
                Log.Error($"  {mod.Metadata.Name}.OnLoad failed: {ex}");
                failedMods.Add(mod);
            }
        }

        // Remove mods that failed OnLoad so they don't get OnAllModsLoaded
        foreach (var failed in failedMods)
            _mods.Remove(failed);

        // Phase 5: Call OnAllModsLoaded
        foreach (var mod in _mods)
        {
            try
            {
                mod.Instance.OnAllModsLoaded(mod.Context);
            }
            catch (Exception ex)
            {
                Log.Error($"  {mod.Metadata.Name}.OnAllModsLoaded failed: {ex}");
            }
        }

        Log.Info($"Mod loading complete. {_mods.Count} mod(s) active.");
    }

    private LoadedMod LoadMod(ModMetadata meta)
    {
        var modDir = Path.Combine(_modsDir, meta.Id);
        var dllPath = Path.Combine(modDir, meta.DLL);

        if (string.IsNullOrEmpty(meta.DLL) || !File.Exists(dllPath))
            throw new FileNotFoundException($"Mod DLL not found: {dllPath}");

        var loadContext = new ModLoadContext(dllPath);
        var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

        // Find IMurderMod implementation
        var modType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(IMurderMod).IsAssignableFrom(t) && !t.IsAbstract)
            ?? throw new InvalidOperationException(
                $"No IMurderMod implementation found in {meta.DLL}");

        if (modType.GetConstructor(Type.EmptyTypes) == null)
            throw new InvalidOperationException(
                $"IMurderMod implementation '{modType.FullName}' in {meta.DLL} must have a parameterless constructor");

        var instance = (IMurderMod)Activator.CreateInstance(modType)!;

        var context = new ModContext
        {
            Metadata = meta,
            ModDirectory = modDir,
            ModAssembly = assembly,
            Logger = new ModLogger(meta.Id),
        };

        return new LoadedMod(meta, instance, context, loadContext);
    }

    /// <summary>
    /// Called during game shutdown via ProcessExit hook.
    /// </summary>
    public void Shutdown()
    {
        foreach (var mod in _mods)
        {
            try { mod.Instance.OnUnload(); }
            catch (Exception ex)
            {
                Log.Error($"  {mod.Metadata.Name}.OnUnload failed: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// A loaded mod with its context and assembly isolation.
/// </summary>
public sealed record LoadedMod(
    ModMetadata Metadata,
    IMurderMod Instance,
    ModContext Context,
    ModLoadContext LoadContext
);
