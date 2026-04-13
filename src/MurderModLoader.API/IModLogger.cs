namespace MurderModLoader.API;

/// <summary>
/// Simple logging interface for mods.
/// </summary>
public interface IModLogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
