namespace MurderModLoader.API;

/// <summary>
/// Interface that all Murder Engine mods must implement.
/// The mod loader discovers and calls these methods during game startup.
/// </summary>
public interface IMurderMod
{
    /// <summary>
    /// Called after the mod assembly is loaded, before the game initializes.
    /// Use this for Harmony patches and early setup.
    /// </summary>
    void OnLoad(ModContext context);

    /// <summary>
    /// Called after all mods are loaded. Use for cross-mod integration.
    /// </summary>
    void OnAllModsLoaded(ModContext context) { }

    /// <summary>
    /// Called when the game is shutting down.
    /// </summary>
    void OnUnload() { }
}
