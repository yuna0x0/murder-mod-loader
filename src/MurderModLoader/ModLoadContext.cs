using System.Reflection;
using System.Runtime.Loader;

namespace MurderModLoader;

/// <summary>
/// Isolated AssemblyLoadContext per mod. Prevents dependency
/// conflicts between mods while sharing the game's assemblies.
/// </summary>
public sealed class ModLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ModLoadContext(string modDllPath) : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(modDllPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Try mod-local dependencies first
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path != null)
            return LoadFromAssemblyPath(path);

        // Fall back to default context (game + engine assemblies)
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path != null)
            return LoadUnmanagedDllFromPath(path);

        return IntPtr.Zero;
    }
}
