using System.Reflection;

namespace MurderModLoader.API;

/// <summary>
/// Provides context to a mod during initialization.
/// </summary>
public sealed class ModContext
{
    /// <summary>Mod metadata from mod.yaml.</summary>
    public required ModMetadata Metadata { get; init; }

    /// <summary>Absolute path to the mod's directory.</summary>
    public required string ModDirectory { get; init; }

    /// <summary>The mod's loaded assembly.</summary>
    public required Assembly ModAssembly { get; init; }

    /// <summary>Logger scoped to this mod.</summary>
    public required IModLogger Logger { get; init; }

    /// <summary>Unique Harmony instance ID for this mod.</summary>
    public string HarmonyId => $"murdermod.{Metadata.Id}";
}
