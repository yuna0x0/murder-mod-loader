using HarmonyLib;
using MurderModLoader.API;

namespace DebugOverlayMod;

/// <summary>
/// ImGui debug overlay for Murder Engine games.
/// Press F2 to toggle the overlay.
/// </summary>
public class DebugOverlayMod : IMurderMod
{
    private Harmony? _harmony;
    internal static IModLogger? Logger;
    internal static bool ShowOverlay = true;

    public void OnLoad(ModContext context)
    {
        Logger = context.Logger;
        _harmony = new Harmony(context.HarmonyId);

        try
        {
            GamePatches.Apply(_harmony);
        }
        catch (Exception ex)
        {
            Logger.Error($"Harmony patch failed: {ex.Message}");
        }
    }

    public void OnUnload() => _harmony?.UnpatchAll(_harmony.Id);
}
