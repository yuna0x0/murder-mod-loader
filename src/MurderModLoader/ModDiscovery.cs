using MurderModLoader.API;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MurderModLoader;

/// <summary>
/// Discovers mods by scanning the mods directory for mod.yaml files.
/// </summary>
internal static class ModDiscovery
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static List<ModMetadata> ScanModsDirectory(string modsDir)
    {
        var mods = new List<ModMetadata>();

        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            // Skip the loader's own directory
            if (Path.GetFileName(dir) == "loader") continue;

            var yamlPath = Path.Combine(dir, "mod.yaml");
            if (!File.Exists(yamlPath)) continue;

            // Skip mods with a .disabled marker file
            if (File.Exists(Path.Combine(dir, ".disabled")))
            {
                Log.Info($"  Skipping disabled mod: {Path.GetFileName(dir)} (.disabled file)");
                continue;
            }

            try
            {
                var yaml = File.ReadAllText(yamlPath);
                var meta = YamlDeserializer.Deserialize<ModMetadata>(yaml);
                if (string.IsNullOrEmpty(meta.Id))
                    meta.Id = Path.GetFileName(dir);

                // Skip mods with Enabled: false in mod.yaml
                if (!meta.Enabled)
                {
                    Log.Info($"  Skipping disabled mod: {meta.Name ?? meta.Id} (Enabled: false)");
                    continue;
                }

                mods.Add(meta);
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to parse {yamlPath}: {ex.Message}");
            }
        }

        return mods;
    }
}
