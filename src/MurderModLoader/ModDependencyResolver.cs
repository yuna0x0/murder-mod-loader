using MurderModLoader.API;

namespace MurderModLoader;

/// <summary>
/// Resolves mod dependencies and produces a load order via topological sort.
/// </summary>
internal static class ModDependencyResolver
{
    public static List<ModMetadata> Resolve(List<ModMetadata> mods)
    {
        var byId = mods.ToDictionary(m => m.Id);
        var sorted = new List<ModMetadata>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        foreach (var mod in mods)
            Visit(mod.Id, byId, sorted, visited, visiting);

        return sorted;
    }

    private static void Visit(
        string id,
        Dictionary<string, ModMetadata> byId,
        List<ModMetadata> sorted,
        HashSet<string> visited,
        HashSet<string> visiting)
    {
        if (visited.Contains(id)) return;
        if (visiting.Contains(id))
        {
            Log.Warning($"Circular dependency detected involving '{id}'");
            return;
        }

        if (!byId.TryGetValue(id, out var mod))
        {
            Log.Warning($"Missing dependency: '{id}'");
            return;
        }

        visiting.Add(id);

        foreach (var dep in mod.Dependencies)
            Visit(dep.Id, byId, sorted, visited, visiting);

        visiting.Remove(id);
        visited.Add(id);
        sorted.Add(mod);
    }
}
