using System;
using System.IO;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// Watches for <c>playlists/play-now.json</c>, the "play this now" request the
/// run-database web page writes (docs/replay-player.md, "Playlists"). Nothing
/// polls: the request is evaluated when the file changes (a
/// <see cref="FileSystemWatcher"/>), when a main menu becomes ready (the scene
/// tree's <c>node_added</c> signal), when a replay ends, and once at Core
/// initialize. No Harmony, no game hook. When the game is at the main menu
/// with no replay playing, the request is consumed (deleted) and its playlist
/// or runs start; otherwise it is left in place and a toast says why, once per
/// file version, so the page can report that it was not picked up.
/// </summary>
internal static class ReplayPlayNowWatcher
{
    /// <summary>A file that doesn't parse may be mid-write by a non-atomic writer.</summary>
    private static readonly TimeSpan UnparsedGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A file another process still holds open has no "closed" notification,
    /// so a locked read retries a bounded number of times.
    /// </summary>
    private const double LockedRetrySeconds = 0.25;
    private const int LockedRetryLimit = 20;

    private static FileSystemWatcher? _watcher;
    private static SceneTree.NodeAddedEventHandler? _nodeAddedHandler;
    private static SceneTree? _tree;
    private static int _generation;
    private static int _evaluateQueued;
    private static int _lockedRetries;
    private static DateTime _notedWrite;

    public static void Initialize()
    {
        Shutdown();
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            _tree = tree;

            SceneTree.NodeAddedEventHandler onNodeAdded = OnNodeAdded;
            tree.NodeAdded += onNodeAdded;
            _nodeAddedHandler = onNodeAdded;

            string dir = ReplayPlaylistLibrary.Dir;
            Directory.CreateDirectory(dir);
            var watcher = new FileSystemWatcher(dir, PlaylistFormat.PlayNowFileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            // A buffer overflow drops events; re-reading the file covers it.
            watcher.Error += (_, _) => QueueEvaluate();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;

            // A request written while Core was not loaded (or before a reload).
            QueueEvaluate();
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Playlists: play-now watcher not started: {e.Message}");
        }
    }

    public static void Shutdown()
    {
        Interlocked.Increment(ref _generation);
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        if (_tree != null && _nodeAddedHandler != null && GodotObject.IsInstanceValid(_tree))
        {
            try { _tree.NodeAdded -= _nodeAddedHandler; } catch { }
        }
        _nodeAddedHandler = null;
        _tree = null;
        Interlocked.Exchange(ref _evaluateQueued, 0);
        _lockedRetries = 0;
    }

    /// <summary>A replay ended; a request it blocked may now start.</summary>
    public static void OnReplayEnded() => QueueEvaluate();

    private static void OnFileEvent(object sender, FileSystemEventArgs e) => QueueEvaluate();

    private static void OnNodeAdded(Node node)
    {
        if (node is not NMainMenu menu) return;
        int generation = _generation;
        // NGame.MainMenu is assigned around the add; judge once the menu is ready.
        menu.Connect(
            Node.SignalName.Ready,
            Callable.From(() => { if (generation == _generation) QueueEvaluate(); }),
            (uint)GodotObject.ConnectFlags.OneShot);
    }

    /// <summary>
    /// Runs <see cref="Evaluate"/> on the main thread. Watcher events arrive on
    /// thread-pool threads and come in bursts (one write raises several), so
    /// they collapse into one deferred call.
    /// </summary>
    private static void QueueEvaluate()
    {
        if (Interlocked.Exchange(ref _evaluateQueued, 1) == 1) return;
        int generation = _generation;
        Callable.From(() =>
        {
            Interlocked.Exchange(ref _evaluateQueued, 0);
            if (generation == _generation) Evaluate();
        }).CallDeferred();
    }

    /// <summary>Re-evaluates once after a delay, on the main thread.</summary>
    private static void EvaluateAfter(double seconds)
    {
        var tree = _tree;
        if (tree == null || !GodotObject.IsInstanceValid(tree)) return;
        int generation = _generation;
        tree.CreateTimer(seconds, processAlways: true).Timeout += () =>
        {
            if (generation == _generation) Evaluate();
        };
    }

    private static void Evaluate()
    {
        try
        {
            string path = ReplayPlaylistLibrary.PlayNowPath;
            if (!File.Exists(path))
            {
                _notedWrite = default;
                _lockedRetries = 0;
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
            catch (IOException)
            {
                // Still held open by its writer.
                if (++_lockedRetries <= LockedRetryLimit) EvaluateAfter(LockedRetrySeconds);
                return;
            }
            _lockedRetries = 0;

            var request = PlaylistFormat.ParsePlayNow(json, out var error);
            if (request == null)
            {
                // A file caught mid-write parses badly. The writer's next write
                // re-evaluates through the watcher; this deadline discards a
                // file nobody finishes.
                var age = DateTime.UtcNow - written;
                if (age < UnparsedGrace)
                {
                    EvaluateAfter((UnparsedGrace - age).TotalSeconds + 0.05);
                    return;
                }
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
            CoreMain.LogDebug($"Playlists: play-now check failed: {e.Message}");
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
