using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Cross-platform installer for murder-mod-loader.
/// Extracts a Murder Engine single-file bundle and sets up the mod loader.
/// </summary>
class Program
{
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

        // Step 2: Fix runtimeconfig
        Info("Configuring runtime...");
        FixRuntimeConfig(moddedDir, gameName);

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
        CreateLaunchScript(gameDir, gameName, dotnetDir, inputDir);

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
        Console.WriteLine("  murder-mod-install <game-dir> [sdk-dir]    Install mod loader into a game");
        Console.WriteLine("  murder-mod-install build <mod-dir> <game-dir>   Build a mod and install it");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  game-dir    Path to the game directory (or macOS .app bundle)");
        Console.WriteLine("  sdk-dir     Path to .NET 8 SDK (auto-detected if omitted)");
        Console.WriteLine("  mod-dir     Path to the mod project (containing .csproj and mod.yaml)");
        Console.WriteLine();
        Console.WriteLine("Supports both flat game directories and macOS .app bundles.");
    }

    /// <summary>
    /// Resolves the effective game root directory.
    /// For macOS .app bundles (XXX.app/Contents/MacOS/), returns the MacOS dir.
    /// For flat directories, returns the input directory.
    /// </summary>
    static string? ResolveGameDir(string inputDir)
    {
        // Check if this is a .app bundle
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

        // Check if the user passed a directory that contains a .app-style layout
        // (e.g., they passed the Contents/ or Contents/MacOS/ directly)
        if (Path.GetFileName(inputDir) == "MacOS" &&
            Path.GetFileName(Path.GetDirectoryName(inputDir) ?? "") == "Contents")
        {
            // Already pointing at the MacOS dir
            return inputDir;
        }

        // Flat directory - check it has either the executable or resources/
        return inputDir;
    }

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

        // Parse mod ID from yaml (simple parse, no dependency on YamlDotNet)
        var modId = "";
        var modDll = "";
        foreach (var line in File.ReadLines(yamlPath))
        {
            if (line.StartsWith("Id:")) modId = line["Id:".Length..].Trim();
            if (line.StartsWith("DLL:")) modDll = line["DLL:".Length..].Trim();
        }
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

        // Install to game
        var installDir = Path.Combine(gameDir, "mods", modId);
        Directory.CreateDirectory(installDir);

        // DLLs already provided by the loader or game -- don't duplicate
        var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MurderModLoader.API.dll", "0Harmony.dll", "Murder.dll",
            "Bang.dll", "FNA.dll", "YamlDotNet.dll"
        };

        // Copy managed DLLs
        foreach (var file in Directory.GetFiles(buildOutput, "*.dll"))
        {
            var name = Path.GetFileName(file);
            if (!skipFiles.Contains(name))
                File.Copy(file, Path.Combine(installDir, name), true);
        }

        // Copy native libraries for the current platform from runtimes/
        var runtimesDir = Path.Combine(buildOutput, "runtimes");
        if (Directory.Exists(runtimesDir))
        {
            // Match current OS runtime identifier prefix
            var ridPrefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

            foreach (var platformDir in Directory.GetDirectories(runtimesDir))
            {
                if (!Path.GetFileName(platformDir).StartsWith(ridPrefix)) continue;
                var nativeDir = Path.Combine(platformDir, "native");
                if (!Directory.Exists(nativeDir)) continue;
                foreach (var nativeLib in Directory.GetFiles(nativeDir))
                    File.Copy(nativeLib, Path.Combine(installDir, Path.GetFileName(nativeLib)), true);
            }
        }

        // Copy mod.yaml
        File.Copy(yamlPath, Path.Combine(installDir, "mod.yaml"), true);

        Info($"Installed to {installDir}");
        Info($"  DLL: {modDll}");
        Info($"  Launch the game to test.");
        return 0;
    }

    static string? FindGameExecutable(string gameDir)
    {
        var skipPrefixes = new[] { "lib", "steam_api", "fmod" };

        foreach (var file in Directory.GetFiles(gameDir))
        {
            var name = Path.GetFileName(file);

            // Skip known non-game files
            if (skipPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (name.EndsWith(".dll") || name.EndsWith(".dylib") || name.EndsWith(".so") || name.EndsWith(".lib"))
                continue;

            // Windows: .exe
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return file;

            // Unix: executable binary (no extension, check magic bytes)
            if (!name.Contains('.'))
            {
                try
                {
                    var magic = new byte[4];
                    using var fs = File.OpenRead(file);
                    if (fs.Read(magic) == 4)
                    {
                        // ELF
                        if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
                            return file;
                        // Mach-O (various)
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

    static string? DetectDotnetSdk(string gameExe, string inputDir)
    {
        // Determine game architecture
        var gameArch = DetectArchitecture(gameExe);
        Info($"Game architecture: {gameArch ?? "unknown"}");

        // Check standard dotnet install locations
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

        // Also check PATH
        var pathDotnet = FindInPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
        if (pathDotnet != null)
            candidates.Insert(0, Path.GetDirectoryName(pathDotnet)!);

        // Check the original input directory's parent for a local SDK
        // (works for both flat dirs and .app bundles)
        var parentDir = Path.GetDirectoryName(inputDir);
        if (parentDir != null)
        {
            foreach (var dir in Directory.GetDirectories(parentDir, "dotnet-sdk-8*"))
                candidates.Insert(0, dir);
        }

        foreach (var sdkDir in candidates.Distinct())
        {
            if (!Directory.Exists(sdkDir)) continue;

            // Look for .NET 8 runtime
            var runtimesDir = Path.Combine(sdkDir, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(runtimesDir)) continue;

            foreach (var rtDir in Directory.GetDirectories(runtimesDir, "8.*").OrderByDescending(d => d))
            {
                // Check architecture match if we know the game arch
                if (gameArch != null)
                {
                    var jitLib = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "clrjit.dll" :
                                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libclrjit.dylib" : "libclrjit.so";
                    var jitPath = Path.Combine(rtDir, jitLib);
                    if (File.Exists(jitPath))
                    {
                        var jitArch = DetectArchitecture(jitPath);
                        if (jitArch != null && jitArch != gameArch)
                            continue; // Architecture mismatch
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

            // Mach-O
            if (magic[0] == 0xCF && magic[1] == 0xFA && magic[2] == 0xED && magic[3] == 0xFE)
            {
                var cpu = BitConverter.ToUInt32(magic, 4);
                return cpu == 0x01000007 ? "x64" : cpu == 0x0100000C ? "arm64" : null;
            }
            // ELF
            if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
            {
                fs.Position = 18;
                var machineBytes = new byte[2];
                fs.Read(machineBytes);
                var machine = BitConverter.ToUInt16(machineBytes);
                return machine == 62 ? "x64" : machine == 183 ? "arm64" : null;
            }
            // PE
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
        // Look relative to this executable and working directory
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var candidates = new List<string>
        {
            Path.Combine(exeDir, "..", "loader"),
            Path.Combine(exeDir, "loader"),
            "publish/loader",
            "loader",
        };
        // Also check if the loader is in the same dir as the installer
        candidates.Add(exeDir);

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "MurderModLoader.dll")))
                return Path.GetFullPath(dir);
        }
        return null;
    }

    static bool ExtractBundle(string gameExe, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        // Try murder-unpack first
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

                // Rewrite as framework-dependent
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

        // Copy native libs from game root
        foreach (var ext in nativeExtensions)
        {
            foreach (var lib in Directory.GetFiles(gameDir, $"*{ext}"))
            {
                var name = Path.GetFileName(lib);
                // Skip the game executable itself on Windows
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                File.Copy(lib, Path.Combine(moddedDir, name), true);
                File.Copy(lib, Path.Combine(nativeDir, name), true);
            }
        }

        // Copy FMOD native libs from resources/fmod/pc/
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
        // Store SDK path relative to the directory containing the launch script
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

            // Detect if we need arch -x86_64 (macOS ARM64 host + x64 game)
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
