namespace MurderModLoader.API;

/// <summary>
/// Mod metadata parsed from mod.yaml.
/// </summary>
public sealed class ModMetadata
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string DLL { get; set; } = "";
    public List<ModDependency> Dependencies { get; init; } = [];
}

public sealed class ModDependency
{
    public required string Id { get; init; }
    public string MinVersion { get; init; } = "";
}
