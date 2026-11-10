using System;
using System.Linq;
using Godot;
using SpireLens.Core.Patches;
using SpireLens.Core.Replay.Playback;

namespace SpireLens.Core;

/// <summary>
/// The game's Mod Configuration → SpireLens Replays page, plus the hook that
/// keeps the Core's cached settings in step with edits made on the
/// Mod Configuration → SpireLens settings page.
///
/// The Loader owns both pages (Config/SpireLensConfig.cs and
/// Config/SpireLensReplaysConfig.cs) and calls <see cref="Fill"/> every time
/// the Replays page opens, so it is current and is always this Core's code,
/// even after hot reloads. Registration happens on
/// every Core Initialize and is cleared on Shutdown, so an orphaned Core is
/// never called back.
///
/// Only replays and playlists live here. The overlay's potion-history and
/// restart-room actions are left out: they need an in-run screen stack that
/// the Settings → Mods page sits on top of, and neither has been shown to
/// work from there.
/// </summary>
internal static class SpireLensSettingsSection
{
    /// <summary>
    /// Built-in presets (ReplayPresets.cs) and then saved playlists
    /// (playlists/&lt;slug&gt;.json, docs/replay-player.md), each with its run and
    /// playable counts and a Play button. Read fresh each time the page opens;
    /// saved playlists themselves are made in the web page.
    /// </summary>
    private static void FillPlaylists(
        Func<string, Control> makeSection,
        Func<string, Control> makeNote,
        Func<string, string, Action?, Control> makeRow)
    {
        var section = makeSection("Playlists");
        string? blocked = ReplayPlayer.StartBlockedReason();

        foreach (var (preset, runs) in ReplayPresetLibrary.Resolve())
        {
            var playable = runs.Where(r => r.Playable)
                .Select(r => new PlaylistRunRef(r.GameStartTime, null))
                .ToList();
            var name = preset.Name;
            section.AddChild(PlaylistRow(makeRow, name, runs.Count, playable.Count, blocked, () =>
            {
                CoreMain.Logger.Info($"Mods page: playing preset '{name}' ({playable.Count} runs)");
                ReplayPlayer.StartRuns(name, playable);
            }));
        }

        var playlists = ReplayPlaylistLibrary.List();
        foreach (var playlist in playlists)
        {
            int playable = 0;
            foreach (var (_, listing) in ReplayPlaylistLibrary.Resolve(playlist.Runs))
                if (listing is { Playable: true }) playable++;

            var chosen = playlist;
            section.AddChild(PlaylistRow(makeRow, playlist.Name, playlist.Runs.Count, playable, blocked, () =>
            {
                CoreMain.Logger.Info($"Mods page: playing playlist '{chosen.Name}'");
                ReplayPlayer.StartPlaylist(chosen);
            }));
        }

        section.AddChild(makeNote(playlists.Count == 0
            ? $"No saved playlists yet. Make them in the run-database web page: run `spirelens-db serve`, then open {PlaylistPageUrl}."
            : $"Saved playlists are made in the run-database web page: run `spirelens-db serve`, then open {PlaylistPageUrl}."));
    }

    private static Control PlaylistRow(
        Func<string, string, Action?, Control> makeRow,
        string name,
        int runs,
        int playable,
        string? blocked,
        Action play)
    {
        string label = $"{name}  —  {runs} run{(runs == 1 ? "" : "s")}, {playable} playable";
        if (playable == 0) return makeRow(label, "Unavailable", null);
        return makeRow(label, "Play", blocked != null ? null : play);
    }

    public static void Register()
    {
        if (!RuntimeOptionsProvider.RegisterCoreHooks(OnLoaderOptionsChanged, Fill))
            CoreMain.Logger.Info("Mods page Core section unavailable: the running Loader predates it (full game restart)");
    }

    public static void Unregister() => RuntimeOptionsProvider.RegisterCoreHooks(null, null);

    /// <summary>
    /// A settings-page control (or Restore Defaults) wrote the config directly.
    /// Drop everything the Core cached from it.
    /// </summary>
    private static void OnLoaderOptionsChanged()
    {
        RuntimeOptionsProvider.Refresh();
        CoreMain.RefreshDebugLogging();
        ViewStatsInjectorPatch.ReloadFromConfig("Mods page");
        SpireLensOptionsMenu.RefreshIfOpen();
    }

    /// <summary>Where the run-database web page that makes playlists listens (docs/run-database.md).</summary>
    private const string PlaylistPageUrl = "http://127.0.0.1:8737";

    private static void Fill(
        Control host,
        Func<string, Control> makeSection,
        Func<string, Control> makeNote,
        Func<string, string, Action?, Control> makeRow)
    {
        if (ReplayPlayer.IsActive)
        {
            var playing = makeSection("Replay");
            playing.AddChild(makeRow(
                "A replay is playing",
                "Stop replay",
                () => ReplayPlayer.RequestStop()));
            return;
        }

        FillPlaylists(makeSection, makeNote, makeRow);

        var replay = makeSection("Replay");
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
