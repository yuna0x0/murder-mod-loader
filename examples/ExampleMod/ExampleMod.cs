using HarmonyLib;
using MurderModLoader.API;

namespace ExampleMod;

public class ExampleMod : IMurderMod
{
    private Harmony? _harmony;

    public void OnLoad(ModContext context)
    {
        context.Logger.Info("Example mod loaded!");

        // Create Harmony instance and apply all patches in this assembly
        _harmony = new Harmony(context.HarmonyId);
        _harmony.PatchAll(context.ModAssembly);
    }

    public void OnUnload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
    }
}

/// <summary>
/// Example Harmony patch — logs when the game window title is set.
/// Replace the target class/method with actual Murder Engine types.
/// </summary>
// [HarmonyPatch(typeof(Murder.Game), "Initialize")]
// public static class GameInitPatch
// {
//     public static void Postfix()
//     {
//         Console.Error.WriteLine("[ExampleMod] Game.Initialize() was called!");
//     }
// }
