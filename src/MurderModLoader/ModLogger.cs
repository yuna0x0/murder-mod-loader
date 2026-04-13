using MurderModLoader.API;

namespace MurderModLoader;

internal sealed class ModLogger(string modId) : IModLogger
{
    public void Info(string message) => Log.Info($"[{modId}] {message}");
    public void Warning(string message) => Log.Warning($"[{modId}] {message}");
    public void Error(string message) => Log.Error($"[{modId}] {message}");
}
