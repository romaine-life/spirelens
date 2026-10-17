using System;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using Timer = Godot.Timer;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// Watches for <c>playlists/play-now.json</c>, the "play this now" request the
/// run-database web page writes (docs/replay-player.md, "Playlists"). A stock
/// Godot <see cref="Timer"/> under the scene root ticks once a second while
/// Core is loaded — no Harmony, no game hook. When the game is at the main
/// menu with no replay playing, the request is consumed (deleted) and its
/// playlist or runs start; otherwise it is left in place and a toast says why,
/// once per file version, so the page can report that it was not picked up.
/// </summary>
internal static class ReplayPlayNowWatcher
{
    private static Timer? _timer;
    private static DateTime _notedWrite;

    public static void Initialize()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            Shutdown();
            _timer = new Timer
            {
                Name = "SpireLensPlayNowWatcher",
                WaitTime = 1.0,
                OneShot = false,
                Autostart = true,
                ProcessMode = Node.ProcessModeEnum.Always,
            };
            _timer.Timeout += OnTick;
            tree.Root.CallDeferred(Node.MethodName.AddChild, _timer);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Playlists: play-now watcher not started: {e.Message}");
        }
    }

    public static void Shutdown()
    {
        if (_timer != null && GodotObject.IsInstanceValid(_timer))
        {
            try { _timer.Timeout -= OnTick; } catch { }
            _timer.QueueFree();
        }
        _timer = null;
    }

    private static void OnTick()
    {
        try
        {
            string path = ReplayPlaylistLibrary.PlayNowPath;
            if (!File.Exists(path))
            {
                _notedWrite = default;
                return;
            }

            var written = File.GetLastWriteTimeUtc(path);
            string? blocked = ReplayPlayer.StartBlockedReason()
                              ?? (NGame.Instance?.MainMenu == null ? "go to the main menu first" : null);
            if (blocked != null)
            {
                if (written != _notedWrite)
                {
                    _notedWrite = written;
                    CoreMain.Logger.Info($"Playlists: play-now request waiting ({blocked})");
                    Toast($"Playlist request waiting: {blocked}");
                }
                return;
            }

            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException) { return; } // still being written; next tick

            var request = PlaylistFormat.ParsePlayNow(json, out var error);
            if (request == null)
            {
                // A file caught mid-write parses badly; give the writer a moment.
                if (DateTime.UtcNow - written < TimeSpan.FromSeconds(3)) return;
                TryDelete(path);
                CoreMain.Logger.Warn($"Playlists: play-now request unreadable, discarded: {error}");
                Toast("Playlist request unreadable");
                return;
            }

            TryDelete(path);
            _notedWrite = default;
            if (request.PlaylistSlug != null)
            {
                var playlist = ReplayPlaylistLibrary.Load(request.PlaylistSlug);
                if (playlist == null)
                {
                    CoreMain.Logger.Warn($"Playlists: play-now names unknown playlist '{request.PlaylistSlug}'");
                    Toast($"Playlist not found: {request.PlaylistSlug}");
                    return;
                }
                CoreMain.Logger.Info($"Playlists: play-now starting playlist '{playlist.Name}' ({playlist.Runs.Count} runs)");
                ReplayPlayer.StartPlaylist(playlist);
            }
            else if (request.Runs is { Count: > 0 } runs)
            {
                CoreMain.Logger.Info($"Playlists: play-now starting {runs.Count} run(s)");
                ReplayPlayer.StartRuns(null, runs);
            }
            else
            {
                Toast("Playlist request had no runs");
            }
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"Playlists: play-now tick failed: {e.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) { CoreMain.Logger.Warn($"Playlists: could not delete play-now request: {e.Message}"); }
    }

    private static void Toast(string message)
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree) HotReloadToast.Show(tree, message);
        }
        catch { }
    }
}
