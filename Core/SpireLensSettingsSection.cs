using System;
using Godot;
using SpireLens.Core.Patches;
using SpireLens.Core.Replay.Playback;

namespace SpireLens.Core;

/// <summary>
/// The Core's part of the game's Mods → SpireLens page, plus the hook that
/// keeps the Core's cached settings in step with edits made on that page.
///
/// The Loader owns the page (Config/SpireLensConfig.cs) and calls
/// <see cref="Fill"/> every time it opens, so the section is current and is
/// always this Core's code, even after hot reloads. Registration happens on
/// every Core Initialize and is cleared on Shutdown, so an orphaned Core is
/// never called back.
///
/// Only the replay list lives here. The overlay's potion-history and
/// restart-room actions are left out: they need an in-run screen stack that
/// the Settings → Mods page sits on top of, and neither has been shown to
/// work from there.
/// </summary>
internal static class SpireLensSettingsSection
{
    public static void Register()
    {
        if (!RuntimeOptionsProvider.RegisterCoreHooks(OnLoaderOptionsChanged, Fill))
            CoreMain.Logger.Info("Mods page Core section unavailable: the running Loader predates it (full game restart)");
    }

    public static void Unregister() => RuntimeOptionsProvider.RegisterCoreHooks(null, null);

    /// <summary>
    /// A Mods-page control (or Restore Defaults) wrote the config directly.
    /// Drop everything the Core cached from it.
    /// </summary>
    private static void OnLoaderOptionsChanged()
    {
        RuntimeOptionsProvider.Refresh();
        CoreMain.RefreshDebugLogging();
        ViewStatsInjectorPatch.ReloadFromConfig("Mods page");
        SpireLensOptionsMenu.RefreshIfOpen();
    }

    private static void Fill(
        Control host,
        Func<string, Control> makeSection,
        Func<string, Control> makeNote,
        Func<string, string, Action?, Control> makeRow)
    {
        var replay = makeSection("Replay");

        if (ReplayPlayer.IsActive)
        {
            replay.AddChild(makeRow(
                "A replay is playing",
                "Stop replay",
                () => ReplayPlayer.RequestStop()));
            return;
        }

        replay.AddChild(makeNote(
            "Plays a recorded run in the real game. Your saves and progress are backed up and restored."));

        string? blocked = ReplayPlayer.StartBlockedReason();
        if (blocked != null)
            replay.AddChild(makeNote($"Replay unavailable right now: {blocked}."));

        var listings = ReplayPickerMenu.LoadListings();
        if (listings.Count == 0)
        {
            replay.AddChild(makeNote("No recordings yet. Play a run with SpireLens installed to record one."));
            return;
        }

        foreach (var listing in listings)
        {
            if (!listing.Playable)
            {
                replay.AddChild(makeRow($"{listing.Label}  —  {listing.DisabledReason}", "Unavailable", null));
                continue;
            }

            var row = listing;
            replay.AddChild(makeRow(
                listing.Label,
                "Play",
                blocked != null
                    ? null
                    : () =>
                    {
                        string? reason = ReplayPickerMenu.TryStart(row, "Mods page");
                        if (reason != null)
                            CoreMain.Logger.Info($"Mods page: replay not started ({reason})");
                    }));
        }
    }
}
