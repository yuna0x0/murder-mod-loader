using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Cross-platform installer for murder-mod-loader.
/// Extracts a Murder Engine single-file bundle and sets up the mod loader.
/// </summary>
class Program
{
    static readonly HttpClient Http = new();

    static int Main(string[] args)
    {
        if (args.Length < 1 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length < 1 ? 1 : 0;
        }

        // Subcommand dispatch
        if (args[0] == "build")
            return BuildMod(args.Skip(1).ToArray());
        if (args[0] == "add")
            return AddMod(args.Skip(1).ToArray());

        var inputDir = Path.GetFullPath(args[0]);
        string? dotnetDir = args.Length > 1 ? Path.GetFullPath(args[1]) : null;

        if (!Directory.Exists(inputDir))
        {
            Error($"Directory not found: {inputDir}");
            return 1;
        }

        // Resolve the effective game directory.
        // For macOS .app bundles, the game root is Contents/MacOS/.
        // For flat directories, it's the directory itself.
        var gameDir = ResolveGameDir(inputDir);
        if (gameDir == null)
        {
            Error($"Could not determine game directory layout for: {inputDir}");
            return 1;
        }
        if (gameDir != inputDir)
            Info($"Detected macOS .app bundle, game root: {gameDir}");

        // Find game executable
        var gameExe = FindGameExecutable(gameDir);
        if (gameExe == null)
        {
            Error("No game executable found in the directory.");
            return 1;
        }
        var gameName = Path.GetFileNameWithoutExtension(gameExe);
        Info($"Game: {gameName}");

        // Find or auto-detect dotnet runtime
        dotnetDir ??= DetectDotnetSdk(gameExe, inputDir);
        if (dotnetDir == null)
        {
            Error("Could not find a compatible .NET 8 SDK.");
            Error("Please provide the path as the second argument.");
            Error("Download from: https://dotnet.microsoft.com/download/dotnet/8.0");
            return 1;
        }
        var dotnetExe = Path.Combine(dotnetDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnetExe))
        {
            Error($"dotnet executable not found at: {dotnetExe}");
            return 1;
        }
        Info($"SDK: {dotnetDir}");

        // Find loader publish directory
        var loaderDir = FindLoaderPublish(dotnetDir);
        if (loaderDir == null)
        {
            Error("Mod loader not built. Run: dotnet publish src/MurderModLoader -c Release -o publish/");
            return 1;
        }

        // Step 1: Extract bundle
        var moddedDir = Path.Combine(gameDir, ".modded");
        Info("\nExtracting game bundle...");
        if (!ExtractBundle(gameExe, moddedDir))
            return 1;

        // Detect the real entry-point DLL name from extracted bundle.
        var entryName = DetectEntryPointName(moddedDir) ?? gameName;
        if (entryName != gameName)
            Info($"  Bundle entry point: {entryName} (differs from executable: {gameName})");

        // Step 2: Fix runtimeconfig
        Info("Configuring runtime...");
        FixRuntimeConfig(moddedDir, entryName);

        // Step 3: Copy native libraries
        Info("Copying native libraries...");
        CopyNativeLibs(gameDir, moddedDir);

        // Step 4: Install mod loader
        Info("Installing mod loader...");
        var modsLoaderDir = Path.Combine(gameDir, "mods", "loader");
        Directory.CreateDirectory(modsLoaderDir);
        foreach (var file in Directory.GetFiles(loaderDir))
            File.Copy(file, Path.Combine(modsLoaderDir, Path.GetFileName(file)), true);

        // Step 5: Create launch scripts
        Info("Creating launch scripts...");
        CreateLaunchScript(gameDir, entryName, dotnetDir, inputDir);

        Info("\nInstallation complete!");
        Info($"  Mods directory: {Path.Combine(gameDir, "mods")}");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Info($"  Launch: {Path.Combine(gameDir, "launch-modded.bat")}");
        else
            Info($"  Launch: {Path.Combine(gameDir, "launch-modded.sh")}");

        return 0;
    }

    static void PrintUsage()
    {
        Console.WriteLine("murder-mod-install -- Murder Engine mod loader installer");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  murder-mod-install <game-dir> [sdk-dir]         Install mod loader into a game");
        Console.WriteLine("  murder-mod-install build <mod-dir> <game-dir>   Build and install a mod from source");
        Console.WriteLine("  murder-mod-install add <source> <game-dir>      Install a mod from NuGet, git, or zip");
        Console.WriteLine();
        Console.WriteLine("Sources for 'add':");
        Console.WriteLine("  nuget:<package-id>          NuGet package (latest version)");
        Console.WriteLine("  nuget:<package-id>@<ver>    NuGet package (specific version)");
        Console.WriteLine("  git:<url>                   Git repository");
        Console.WriteLine("  https://.../*.zip           Zip file URL (direct download)");
        Console.WriteLine("  /path/to/mod.zip            Local zip file");
        Console.WriteLine();
        Console.WriteLine("Supports both flat game directories and macOS .app bundles.");
    }

    // ─── add command ────────────────────────────────────────────────────────────

    static int AddMod(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: murder-mod-install add <source> <game-dir>");
            Console.WriteLine();
            Console.WriteLine("Sources:");
            Console.WriteLine("  nuget:<package-id>          NuGet package (latest version)");
            Console.WriteLine("  nuget:<package-id>@<ver>    NuGet package (specific version)");
            Console.WriteLine("  git:<url>                   Git repository");
            Console.WriteLine("  https://.../*.zip           Zip file URL");
            Console.WriteLine("  /path/to/mod.zip            Local zip file");
            return 1;
        }

        var source = args[0];
        var inputDir = Path.GetFullPath(args[1]);
        var gameDir = ResolveGameDir(inputDir) ?? inputDir;

        if (!Directory.Exists(gameDir))
        {
            Error($"Game directory not found: {gameDir}");
            return 1;
        }

        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(Path.Combine(modsDir, "loader")))
            Warning("Mod loader not installed. Run murder-mod-install on the game first.");

        // Dispatch by source type
        if (source.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
            return AddFromNuGet(source["nuget:".Length..], gameDir);
        if (source.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
            return AddFromGit(source["git:".Length..], gameDir);
        if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (source.StartsWith("http://") || source.StartsWith("https://"))
                return AddFromZipUrl(source, gameDir);
            return AddFromZipFile(source, gameDir);
        }

        // Try as NuGet package ID if no prefix
        if (!source.Contains('/') && !source.Contains('\\') && !source.Contains('.'))
            return AddFromNuGet(source, gameDir);

        Error($"Unknown source format: {source}");
        Error("Use nuget:<id>, git:<url>, or a .zip path/URL.");
        return 1;
    }

    static int AddFromNuGet(string spec, string gameDir)
    {
        // Parse package-id and optional version: "MyMod" or "MyMod@1.0.0"
        string packageId;
        string? version = null;
        if (spec.Contains('@'))
        {
            var parts = spec.Split('@', 2);
            packageId = parts[0].Trim();
            version = parts[1].Trim();
        }
        else
        {
            packageId = spec.Trim();
        }

        if (string.IsNullOrEmpty(packageId))
        {
            Error("Empty package ID.");
            return 1;
        }

        Info($"Fetching NuGet package: {packageId}" + (version != null ? $" v{version}" : " (latest)") + "...");

        try
        {
            // Resolve latest version if not specified
            version ??= ResolveLatestNuGetVersion(packageId);
            if (version == null)
            {
                Error($"Package '{packageId}' not found on NuGet.");
                return 1;
            }
            Info($"  Version: {version}");

            // Download .nupkg
            var lowerPkgId = packageId.ToLowerInvariant();
            var lowerVer = version.ToLowerInvariant();
            var nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{lowerPkgId}/{lowerVer}/{lowerPkgId}.{lowerVer}.nupkg";
            var tempZip = Path.Combine(Path.GetTempPath(), $"{lowerPkgId}.{lowerVer}.nupkg");

            Info($"  Downloading...");
            DownloadFile(nupkgUrl, tempZip);

            // Extract and install
            var result = InstallFromZip(tempZip, gameDir);
            File.Delete(tempZip);
            return result;
        }
        catch (Exception ex)
        {
            Error($"Failed to install from NuGet: {ex.Message}");
            return 1;
        }
    }

    static string? ResolveLatestNuGetVersion(string packageId)
    {
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
        try
        {
            var json = Http.GetStringAsync(url).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var versions = doc.RootElement.GetProperty("versions");
            if (versions.GetArrayLength() == 0) return null;
            // Return last (highest) version
            return versions[versions.GetArrayLength() - 1].GetString();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    static int AddFromGit(string url, string gameDir)
    {
        Info($"Cloning git repository: {url}...");

        var tempDir = Path.Combine(Path.GetTempPath(), "murder-mod-git-" + Guid.NewGuid().ToString()[..8]);
        try
        {
            var result = Run("git", $"clone --depth 1 \"{url}\" \"{tempDir}\"");
            if (result != 0)
            {
                Error("Git clone failed.");
                return 1;
            }

            // Check for mod.yaml
            var yamlPath = Path.Combine(tempDir, "mod.yaml");
            if (!File.Exists(yamlPath))
            {
                Error("Repository does not contain mod.yaml at root.");
                return 1;
            }

            // Parse mod ID
            var modId = ParseYamlField(yamlPath, "Id");
            if (string.IsNullOrEmpty(modId))
            {
                Error("Could not parse Id from mod.yaml");
                return 1;
            }

            // Check if it has a .csproj (needs building) or pre-built DLLs
            var csprojs = Directory.GetFiles(tempDir, "*.csproj");
            if (csprojs.Length > 0)
            {
                Info($"  Found project, building...");
                var moddedDir = Path.Combine(gameDir, ".modded");
                var buildOutput = Path.Combine(tempDir, "bin", "Release", "net8.0", "publish");
                var gameAsmArg = Directory.Exists(moddedDir) ? $" -p:GameAssemblyPath=\"{moddedDir}\"" : "";
                var buildResult = Run("dotnet", $"publish \"{csprojs[0]}\" -c Release -o \"{buildOutput}\"{gameAsmArg}");
                if (buildResult != 0)
                {
                    Error("Build failed.");
                    return 1;
                }
                return InstallBuiltMod(buildOutput, yamlPath, modId, gameDir);
            }

            // No .csproj -- treat as pre-built: copy DLLs and mod.yaml directly
            var dllName = ParseYamlField(yamlPath, "DLL") ?? "";
            if (!File.Exists(Path.Combine(tempDir, dllName)))
            {
                Error($"Pre-built DLL not found: {dllName}. Does the repo need building?");
                return 1;
            }

            Info($"  Installing pre-built mod '{modId}'...");
            var installDir = Path.Combine(gameDir, "mods", modId);
            Directory.CreateDirectory(installDir);
            CopyModFiles(tempDir, installDir);
            Info($"Installed to {installDir}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    static int AddFromZipUrl(string url, string gameDir)
    {
        Info($"Downloading: {url}...");
        var tempZip = Path.Combine(Path.GetTempPath(), "murder-mod-" + Guid.NewGuid().ToString()[..8] + ".zip");
        try
        {
            DownloadFile(url, tempZip);
            return InstallFromZip(tempZip, gameDir);
        }
        catch (Exception ex)
        {
            Error($"Download failed: {ex.Message}");
            return 1;
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    static int AddFromZipFile(string zipPath, string gameDir)
    {
        zipPath = Path.GetFullPath(zipPath);
        if (!File.Exists(zipPath))
        {
            Error($"File not found: {zipPath}");
            return 1;
        }
        Info($"Installing from: {zipPath}...");
        return InstallFromZip(zipPath, gameDir);
    }

    /// <summary>
    /// Install a mod from a .zip or .nupkg file.
    /// Looks for mod.yaml in the archive (at root or one level deep).
    /// For .nupkg, also checks contentFiles/ and content/ directories.
    /// </summary>
    static int InstallFromZip(string zipPath, string gameDir)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "murder-mod-zip-" + Guid.NewGuid().ToString()[..8]);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            // Find mod.yaml -- at root, one level deep, or in NuGet content dirs
            var yamlPath = FindModYaml(tempDir);
            if (yamlPath == null)
            {
                Error("No mod.yaml found in archive.");
                Error("Expected mod.yaml at archive root or in a subdirectory.");
                return 1;
            }

            var modDir = Path.GetDirectoryName(yamlPath)!;
            var modId = ParseYamlField(yamlPath, "Id");
            if (string.IsNullOrEmpty(modId))
            {
                modId = Path.GetFileName(modDir);
                if (string.IsNullOrEmpty(modId)) modId = "unknown-mod";
            }

            Info($"  Found mod: {modId}");

            var installDir = Path.Combine(gameDir, "mods", modId);
            Directory.CreateDirectory(installDir);
            CopyModFiles(modDir, installDir);

            Info($"Installed to {installDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Error($"Failed to extract: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    static string? FindModYaml(string dir)
    {
        // Root level
        var root = Path.Combine(dir, "mod.yaml");
        if (File.Exists(root)) return root;

        // One level deep (e.g., zip contains a folder)
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var subYaml = Path.Combine(sub, "mod.yaml");
            if (File.Exists(subYaml)) return subYaml;
        }

        // NuGet package layout: contentFiles/any/any/ or content/
        foreach (var searchDir in new[] { "contentFiles", "content" })
        {
            var contentDir = Path.Combine(dir, searchDir);
            if (!Directory.Exists(contentDir)) continue;
            foreach (var yaml in Directory.GetFiles(contentDir, "mod.yaml", SearchOption.AllDirectories))
                return yaml;
        }

        return null;
    }

    /// <summary>
    /// Copy mod files (DLLs, native libs, mod.yaml, etc.) from source to install dir.
    /// Skips files already provided by the loader or engine.
    /// </summary>
    static void CopyModFiles(string sourceDir, string installDir)
    {
        var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MurderModLoader.API.dll", "0Harmony.dll", "Murder.dll",
            "Bang.dll", "FNA.dll", "YamlDotNet.dll"
        };
        // Skip NuGet metadata files
        var skipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".nuspec", ".xml", ".json"
        };

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(name);
            if (skipFiles.Contains(name)) continue;
            // Skip NuGet metadata but keep mod.yaml
            if (skipExtensions.Contains(ext) && name != "mod.yaml") continue;
            // Skip PDB in release installs? No, keep them for debugging.
            File.Copy(file, Path.Combine(installDir, name), true);
        }

        // Copy native libs from runtimes/ for current platform
        var runtimesDir = Path.Combine(sourceDir, "runtimes");
        if (Directory.Exists(runtimesDir))
        {
            var ridPrefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

            foreach (var platformDir in Directory.GetDirectories(runtimesDir))
            {
                if (!Path.GetFileName(platformDir).StartsWith(ridPrefix)) continue;
                var nativeDir = Path.Combine(platformDir, "native");
                if (!Directory.Exists(nativeDir)) continue;
                foreach (var lib in Directory.GetFiles(nativeDir))
                    File.Copy(lib, Path.Combine(installDir, Path.GetFileName(lib)), true);
            }
        }
    }

    static int InstallBuiltMod(string buildOutput, string yamlPath, string modId, string gameDir)
    {
        var installDir = Path.Combine(gameDir, "mods", modId);
        Directory.CreateDirectory(installDir);

        CopyModFiles(buildOutput, installDir);
        File.Copy(yamlPath, Path.Combine(installDir, "mod.yaml"), true);

        Info($"Installed to {installDir}");
        return 0;
    }

    static void DownloadFile(string url, string destPath)
    {
        using var response = Http.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var fs = File.Create(destPath);
        response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
    }

    static string? ParseYamlField(string yamlPath, string field)
    {
        foreach (var line in File.ReadLines(yamlPath))
        {
            if (line.StartsWith($"{field}:"))
                return line[$"{field}:".Length..].Trim();
        }
        return null;
    }

    // ─── build command ──────────────────────────────────────────────────────────

    static int BuildMod(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: murder-mod-install build <mod-project-dir> <game-dir>");
            return 1;
        }

        var modDir = Path.GetFullPath(args[0]);
        var inputDir = Path.GetFullPath(args[1]);

        if (!Directory.Exists(modDir))
        {
            Error($"Mod directory not found: {modDir}");
            return 1;
        }
        if (!Directory.Exists(inputDir))
        {
            Error($"Game directory not found: {inputDir}");
            return 1;
        }

        // Resolve .app bundle if needed
        var gameDir = ResolveGameDir(inputDir) ?? inputDir;

        // Find mod.yaml
        var yamlPath = Path.Combine(modDir, "mod.yaml");
        if (!File.Exists(yamlPath))
        {
            Error($"mod.yaml not found in {modDir}");
            return 1;
        }

        var modId = ParseYamlField(yamlPath, "Id") ?? "";
        var modDll = ParseYamlField(yamlPath, "DLL") ?? "";
        if (string.IsNullOrEmpty(modId))
        {
            Error("Could not parse Id from mod.yaml");
            return 1;
        }

        // Find .csproj
        var csprojs = Directory.GetFiles(modDir, "*.csproj");
        if (csprojs.Length == 0)
        {
            Error($"No .csproj found in {modDir}");
            return 1;
        }

        // Build -- point GameAssemblyPath to the game's extracted assemblies
        Info($"Building mod '{modId}'...");
        var buildOutput = Path.Combine(modDir, "bin", "Release", "net8.0", "publish");
        var moddedDir = Path.Combine(gameDir, ".modded");
        var gameAsmArg = Directory.Exists(moddedDir) ? $" -p:GameAssemblyPath=\"{moddedDir}\"" : "";
        var buildResult = Run("dotnet", $"publish \"{csprojs[0]}\" -c Release -o \"{buildOutput}\"{gameAsmArg}");
        if (buildResult != 0)
        {
            Error("Build failed.");
            return 1;
        }

        return InstallBuiltMod(buildOutput, yamlPath, modId, gameDir);
    }

    // ─── game detection ─────────────────────────────────────────────────────────

    static string? ResolveGameDir(string inputDir)
    {
        if (inputDir.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
            inputDir.EndsWith(".app/", StringComparison.OrdinalIgnoreCase) ||
            inputDir.EndsWith(".app" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var macosDir = Path.Combine(inputDir, "Contents", "MacOS");
            if (Directory.Exists(macosDir))
                return macosDir;
            Error($"  .app bundle missing Contents/MacOS/: {inputDir}");
            return null;
        }

        if (Path.GetFileName(inputDir) == "MacOS" &&
            Path.GetFileName(Path.GetDirectoryName(inputDir) ?? "") == "Contents")
            return inputDir;

        return inputDir;
    }

    static string? FindGameExecutable(string gameDir)
    {
        var skipPrefixes = new[] { "lib", "steam_api", "fmod" };

        foreach (var file in Directory.GetFiles(gameDir))
        {
            var name = Path.GetFileName(file);

            if (skipPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (name.EndsWith(".dll") || name.EndsWith(".dylib") || name.EndsWith(".so") || name.EndsWith(".lib"))
                continue;

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return file;

            if (!name.Contains('.'))
            {
                try
                {
                    var magic = new byte[4];
                    using var fs = File.OpenRead(file);
                    if (fs.Read(magic) == 4)
                    {
                        if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
                            return file;
                        if ((magic[0] == 0xCF && magic[1] == 0xFA) || (magic[0] == 0xFE && magic[1] == 0xED) ||
                            (magic[0] == 0xCA && magic[1] == 0xFE) || (magic[0] == 0xBE && magic[1] == 0xBA))
                            return file;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    static string? DetectEntryPointName(string moddedDir)
    {
        var configs = Directory.GetFiles(moddedDir, "*.runtimeconfig.json");
        if (configs.Length == 1)
            return Path.GetFileNameWithoutExtension(configs[0]).Replace(".runtimeconfig", "");

        foreach (var config in configs)
        {
            var name = Path.GetFileNameWithoutExtension(config).Replace(".runtimeconfig", "");
            if (File.Exists(Path.Combine(moddedDir, $"{name}.deps.json")) &&
                File.Exists(Path.Combine(moddedDir, $"{name}.dll")))
                return name;
        }
        return null;
    }

    // ─── SDK detection ──────────────────────────────────────────────────────────

    static string? DetectDotnetSdk(string gameExe, string inputDir)
    {
        var gameArch = DetectArchitecture(gameExe);
        Info($"Game architecture: {gameArch ?? "unknown"}");

        var candidates = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add(@"C:\Program Files\dotnet");
            candidates.Add(@"C:\Program Files (x86)\dotnet");
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/usr/local/share/dotnet");
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
        }
        else
        {
            candidates.Add("/usr/share/dotnet");
            candidates.Add("/usr/local/share/dotnet");
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
        }

        var pathDotnet = FindInPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
        if (pathDotnet != null)
            candidates.Insert(0, Path.GetDirectoryName(pathDotnet)!);

        var parentDir = Path.GetDirectoryName(inputDir);
        if (parentDir != null)
        {
            foreach (var dir in Directory.GetDirectories(parentDir, "dotnet-sdk-8*"))
                candidates.Insert(0, dir);
        }

        foreach (var sdkDir in candidates.Distinct())
        {
            if (!Directory.Exists(sdkDir)) continue;

            var runtimesDir = Path.Combine(sdkDir, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(runtimesDir)) continue;

            foreach (var rtDir in Directory.GetDirectories(runtimesDir, "8.*").OrderByDescending(d => d))
            {
                if (gameArch != null)
                {
                    var jitLib = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "clrjit.dll" :
                                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libclrjit.dylib" : "libclrjit.so";
                    var jitPath = Path.Combine(rtDir, jitLib);
                    if (File.Exists(jitPath))
                    {
                        var jitArch = DetectArchitecture(jitPath);
                        if (jitArch != null && jitArch != gameArch)
                            continue;
                    }
                }
                return sdkDir;
            }
        }
        return null;
    }

    static string? DetectArchitecture(string filePath)
    {
        try
        {
            var magic = new byte[8];
            using var fs = File.OpenRead(filePath);
            fs.Read(magic);

            if (magic[0] == 0xCF && magic[1] == 0xFA && magic[2] == 0xED && magic[3] == 0xFE)
            {
                var cpu = BitConverter.ToUInt32(magic, 4);
                return cpu == 0x01000007 ? "x64" : cpu == 0x0100000C ? "arm64" : null;
            }
            if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
            {
                fs.Position = 18;
                var machineBytes = new byte[2];
                fs.Read(machineBytes);
                var machine = BitConverter.ToUInt16(machineBytes);
                return machine == 62 ? "x64" : machine == 183 ? "arm64" : null;
            }
            if (magic[0] == (byte)'M' && magic[1] == (byte)'Z')
            {
                fs.Position = 60;
                var peOffsetBytes = new byte[4];
                fs.Read(peOffsetBytes);
                var peOffset = BitConverter.ToInt32(peOffsetBytes);
                fs.Position = peOffset + 4;
                var machineBytes = new byte[2];
                fs.Read(machineBytes);
                var machine = BitConverter.ToUInt16(machineBytes);
                return machine == 0x8664 ? "x64" : machine == 0xAA64 ? "arm64" : machine == 0x14C ? "x86" : null;
            }
        }
        catch { }
        return null;
    }

    // ─── utilities ──────────────────────────────────────────────────────────────

    static string? FindInPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in pathVar.Split(sep))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    static string? FindLoaderPublish(string? dotnetDir)
    {
        // Check local paths first (running from repo or alongside loader)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var candidates = new List<string>
        {
            Path.Combine(exeDir, "..", "loader"),
            Path.Combine(exeDir, "loader"),
            "publish/loader",
            "loader",
            exeDir,
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "MurderModLoader.dll")))
                return Path.GetFullPath(dir);
        }

        // Check user-level cache (from previous download)
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "murder-mod-loader", "loader");
        if (File.Exists(Path.Combine(cacheDir, "MurderModLoader.dll")))
            return cacheDir;

        // Download from GitHub releases
        Info("Downloading mod loader from GitHub...");
        try
        {
            return DownloadLoader(cacheDir);
        }
        catch (Exception ex)
        {
            Error($"Failed to download loader: {ex.Message}");
            return null;
        }
    }

    static string DownloadLoader(string cacheDir)
    {
        // Resolve latest release tag via GitHub API
        var releaseUrl = "https://api.github.com/repos/yuna0x0/murder-mod-loader/releases/latest";
        Http.DefaultRequestHeaders.UserAgent.TryParseAdd("murder-mod-install");
        var json = Http.GetStringAsync(releaseUrl).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var assets = doc.RootElement.GetProperty("assets");
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";

        // Find the loader zip (murder-mod-loader-vX.Y.Z.zip)
        string? loaderUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("murder-mod-loader-") && name.EndsWith(".zip") &&
                !name.Contains("install"))
            {
                loaderUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (loaderUrl == null)
            throw new InvalidOperationException("Loader zip not found in latest release");

        Info($"  Version: {tag}");
        var tempZip = Path.Combine(Path.GetTempPath(), $"murder-mod-loader-{tag}.zip");
        DownloadFile(loaderUrl, tempZip);

        // Extract to cache
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, true);
        Directory.CreateDirectory(cacheDir);
        ZipFile.ExtractToDirectory(tempZip, cacheDir);
        File.Delete(tempZip);

        Info($"  Cached to {cacheDir}");
        return cacheDir;
    }

    static bool ExtractBundle(string gameExe, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var murderUnpack = FindInPath("murder-unpack");
        if (murderUnpack != null)
        {
            var result = Run("murder-unpack", $"analyze-binary \"{gameExe}\" --extract-assemblies \"{outputDir}\"");
            return result == 0;
        }

        Error("murder-unpack not found. Install it: uv tool install murder-unpack");
        return false;
    }

    static void FixRuntimeConfig(string moddedDir, string gameName)
    {
        var configPath = Path.Combine(moddedDir, $"{gameName}.runtimeconfig.json");
        if (!File.Exists(configPath)) return;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var opts = root.GetProperty("runtimeOptions");

            if (opts.TryGetProperty("includedFrameworks", out var frameworks))
            {
                if (frameworks.GetArrayLength() == 0)
                {
                    Warning("  Runtime config has empty includedFrameworks array");
                    return;
                }

                var fw = frameworks[0];
                var name = fw.GetProperty("name").GetString() ?? "Microsoft.NETCore.App";

                var newConfig = new Dictionary<string, object>
                {
                    ["runtimeOptions"] = new Dictionary<string, object>
                    {
                        ["tfm"] = opts.TryGetProperty("tfm", out var tfm) ? tfm.GetString()! : "net8.0",
                        ["framework"] = new Dictionary<string, string>
                        {
                            ["name"] = name,
                            ["version"] = "8.0.0"
                        },
                        ["configProperties"] = opts.TryGetProperty("configProperties", out var props)
                            ? JsonSerializer.Deserialize<Dictionary<string, object>>(props.GetRawText())!
                            : new Dictionary<string, object>()
                    }
                };

                File.WriteAllText(configPath, JsonSerializer.Serialize(newConfig, new JsonSerializerOptions { WriteIndented = true }));
                Info("  Runtime config updated");
            }
        }
        catch (Exception ex)
        {
            Warning($"  Could not update runtime config: {ex.Message}");
        }
    }

    static void CopyNativeLibs(string gameDir, string moddedDir)
    {
        string[] nativeExtensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? [".dll"]
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? [".dylib"] : [".so"];

        string runtimeId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

        var nativeDir = Path.Combine(moddedDir, "runtimes", runtimeId, "native");
        Directory.CreateDirectory(nativeDir);

        foreach (var ext in nativeExtensions)
        {
            foreach (var lib in Directory.GetFiles(gameDir, $"*{ext}"))
            {
                var name = Path.GetFileName(lib);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(lib, Path.Combine(moddedDir, name), true);
                File.Copy(lib, Path.Combine(nativeDir, name), true);
            }
        }

        var fmodDir = Path.Combine(gameDir, "resources", "fmod", "pc");
        if (Directory.Exists(fmodDir))
        {
            foreach (var ext in nativeExtensions)
            {
                foreach (var lib in Directory.GetFiles(fmodDir, $"*{ext}"))
                {
                    var name = Path.GetFileName(lib);
                    File.Copy(lib, Path.Combine(moddedDir, name), true);
                    File.Copy(lib, Path.Combine(nativeDir, name), true);
                }
            }
        }
    }

    static void CreateLaunchScript(string gameDir, string gameName, string dotnetDir, string inputDir)
    {
        var relativeSdk = Path.GetRelativePath(gameDir, dotnetDir);
        var useRelative = !relativeSdk.StartsWith("..");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var sdkRef = useRelative ? $"%~dp0{relativeSdk}\\dotnet.exe" : $"{dotnetDir}\\dotnet.exe";
            var bat = Path.Combine(gameDir, "launch-modded.bat");
            File.WriteAllText(bat, $"""
                @echo off
                cd /d "%~dp0"

                REM Auto-detect dotnet if SDK was moved
                set "DOTNET_EXE={sdkRef}"
                if not exist "%DOTNET_EXE%" (
                    where dotnet >nul 2>&1 && set "DOTNET_EXE=dotnet"
                )
                if not exist "%DOTNET_EXE%" (
                    echo ERROR: .NET 8 SDK not found. Set DOTNET_EXE or install from https://dotnet.microsoft.com/download/dotnet/8.0
                    exit /b 1
                )

                set "DOTNET_STARTUP_HOOKS=%~dp0mods\loader\MurderModLoader.dll"
                "%DOTNET_EXE%" exec --runtimeconfig ".modded\{gameName}.runtimeconfig.json" --depsfile ".modded\{gameName}.deps.json" --additionalProbingPath ".modded" ".modded\{gameName}.dll" %*
                """);
        }
        else
        {
            var sdkRef = useRelative
                ? $"\"$SCRIPT_DIR/{relativeSdk}/dotnet\""
                : $"\"{dotnetDir}/dotnet\"";

            var gameArch = DetectArchitecture(Path.Combine(gameDir, ".modded", $"{gameName}.dll")) ??
                           DetectArchitecture(FindGameExecutable(gameDir) ?? "");
            var needsRosetta = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                               RuntimeInformation.OSArchitecture == Architecture.Arm64 &&
                               gameArch == "x64";

            var archPrefix = needsRosetta ? "arch -x86_64 " : "";

            var sh = Path.Combine(gameDir, "launch-modded.sh");
            File.WriteAllText(sh, $"""
                #!/bin/bash
                SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
                cd "$SCRIPT_DIR"

                # Auto-detect dotnet if SDK was moved
                DOTNET_EXE={sdkRef}
                if [ ! -f "$DOTNET_EXE" ]; then
                    DOTNET_EXE="$(command -v dotnet 2>/dev/null || true)"
                fi
                if [ -z "$DOTNET_EXE" ] || [ ! -f "$DOTNET_EXE" ]; then
                    echo "ERROR: .NET 8 SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/8.0"
                    exit 1
                fi

                export DOTNET_STARTUP_HOOKS="$SCRIPT_DIR/mods/loader/MurderModLoader.dll"
                exec {archPrefix}"$DOTNET_EXE" exec \
                    --runtimeconfig ".modded/{gameName}.runtimeconfig.json" \
                    --depsfile ".modded/{gameName}.deps.json" \
                    --additionalProbingPath ".modded" \
                    ".modded/{gameName}.dll" "$@"
                """);

            var chmodResult = Run("chmod", $"+x \"{sh}\"");
            if (chmodResult != 0)
                Warning("Could not set launch script as executable. Run: chmod +x " + sh);
        }
    }

    static int Run(string cmd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd) { Arguments = args, UseShellExecute = false };
            var proc = Process.Start(psi);
            proc?.WaitForExit(120_000);
            return proc?.ExitCode ?? 1;
        }
        catch { return 1; }
    }

    static void Info(string msg) => Console.WriteLine(msg);
    static void Warning(string msg) => Console.WriteLine($"WARNING: {msg}");
    static void Error(string msg) => Console.Error.WriteLine($"ERROR: {msg}");
}
