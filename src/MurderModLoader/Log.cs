namespace MurderModLoader;

/// <summary>
/// Simple logging for the mod loader. Writes to stderr so it doesn't
/// interfere with the game's stdout.
/// </summary>
internal static class Log
{
    private const string Prefix = "[ModLoader]";

    public static void Info(string message) =>
        Console.Error.WriteLine($"{Prefix} {message}");

    public static void Warning(string message) =>
        Console.Error.WriteLine($"{Prefix} WARNING: {message}");

    public static void Error(string message) =>
        Console.Error.WriteLine($"{Prefix} ERROR: {message}");
}
