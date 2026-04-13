using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using MurderGame = Murder.Game;

namespace DebugOverlayMod;

public static class GamePatches
{
    private static ImGuiRenderer? _renderer;
    private static bool _prevF2;

    public static void Apply(Harmony harmony)
    {
        // Patch Draw only — do BeforeLayout + UI + AfterLayout in one place
        // to avoid frame count mismatch between Update and Draw
        var draw = typeof(MurderGame).GetMethod(
            "Draw", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(GameTime)]);

        if (draw == null)
        {
            DebugOverlayMod.Logger?.Warning("Could not find Game.Draw");
            return;
        }

        harmony.Patch(draw, postfix: new HarmonyMethod(
            typeof(GamePatches), nameof(AfterDraw)));

        DebugOverlayMod.Logger?.Info("Patched Game.Draw — press F2 for ImGui overlay");
    }

    public static void AfterDraw(MurderGame __instance, GameTime gameTime)
    {
        // F2 toggle
        var f2 = Keyboard.GetState().IsKeyDown(Keys.F2);
        if (f2 && !_prevF2)
            DebugOverlayMod.ShowOverlay = !DebugOverlayMod.ShowOverlay;
        _prevF2 = f2;

        if (!DebugOverlayMod.ShowOverlay) return;

        // Initialize ImGui on first use
        if (_renderer == null)
        {
            try
            {
                _renderer = new ImGuiRenderer(__instance);
                _renderer.RebuildFontAtlas();
                DebugOverlayMod.Logger?.Info("ImGui initialized");
            }
            catch (Exception ex)
            {
                DebugOverlayMod.Logger?.Error($"ImGui init failed: {ex.Message}");
                DebugOverlayMod.ShowOverlay = false;
                return;
            }
        }

        // Ensure positive delta time for ImGui
        var dt = gameTime.ElapsedGameTime;
        if (dt.TotalSeconds <= 0)
            dt = TimeSpan.FromMilliseconds(16);
        var safeGameTime = new GameTime(gameTime.TotalGameTime, dt);

        _renderer.BeforeLayout(safeGameTime);

        ImGui.Begin("Debug Overlay");
        var scene = MurderGame.Instance?.ActiveScene;
        if (scene != null)
        {
            ImGui.Text($"Scene: {scene.GetType().Name}");

            // Show world name if available
            if (scene.World is Murder.Core.MonoWorld monoWorld)
            {
                var worldAsset = MurderGame.Data.TryGetAsset(monoWorld.WorldAssetGuid);
                var worldName = worldAsset?.Name ?? monoWorld.WorldAssetGuid.ToString()[..8];
                ImGui.Text($"World: {worldName}");
            }

            ImGui.Text($"Entities: {scene.World?.EntityCount ?? 0}");
        }
        ImGui.Text($"Time: {MurderGame.Now:F1}s");
        ImGui.Text($"FPS: {(1.0 / MurderGame.DeltaTime):F0}");

        var gd = ((Microsoft.Xna.Framework.Game)__instance).GraphicsDevice;
        var win = __instance.Window.ClientBounds;
        var mouse = Mouse.GetState();
        ImGui.Separator();
        ImGui.Text($"Backbuffer: {gd.PresentationParameters.BackBufferWidth}x{gd.PresentationParameters.BackBufferHeight}");
        ImGui.Text($"Window: {win.Width}x{win.Height}");
        ImGui.Text($"Mouse: {mouse.X},{mouse.Y}");
        ImGui.Separator();
        if (ImGui.Button("Close (F2)"))
            DebugOverlayMod.ShowOverlay = false;
        ImGui.End();

        _renderer.AfterLayout();
    }
}
