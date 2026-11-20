using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpireLens.Core.Replay;

/// <summary>
/// Starts the out-of-process run-database import (<c>spirelens-db ingest</c>,
/// docs/run-database.md) in the background: once when Core loads, and after
/// every run end. SQLite never loads into this hot-reloaded assembly; this
/// only launches the external tool and never waits for it.
///
/// The tool is optional. The release publishes it as its own asset, never in
/// the Workshop package, and home-sync installs it on the user's PCs with its
/// folder on the user's PATH; Workshop users will not have it, so a missing
/// exe is logged once at debug level and skipped. The tool holds
/// an exclusive lock while ingesting, so a second launch while one is running
/// exits on its own. Ingest is incremental, so a launch that finds nothing new
/// costs well under a second.
///
/// No Harmony target: run end piggybacks on <see cref="ReplayRecorder.OnRunEnded"/>
/// (the existing <c>RunHistoryUtilities.CreateRunHistoryEntry</c> postfix,
/// which runs after the game has written <c>history/{start_time}.run</c>), and
/// Core load on <c>CoreMain.Initialize</c>.
/// </summary>
public static class RunDatabaseImporter
{
    /// <summary>Set to 1/true to never launch the import tool.</summary>
    public const string DisableEnvVar = "SPIRELENS_DISABLE_AUTO_IMPORT";

    /// <summary>
    /// Settle time before launching, so writes that land just after the
    /// trigger (the fatal combat's .mcr copy, the stats record's final save,
    /// the gzip of the last decision segment) are usually on disk. Anything
    /// later is picked up by the next import.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

    // Per Core load: Shutdown cancels launches this (soon orphaned) assembly
    // still has pending, so a hot reload never leaves a stray launch behind.
    private static CancellationTokenSource _cts = new();
    private static bool _missingLogged;

    private const string ToolName = "spirelens-db.exe";

    // The game inherits Steam's environment, which predates an install home-sync made
    // while Steam was running, so the user's PATH is also read fresh from the registry.
    private static string? FindTool()
    {
        var process = Environment.GetEnvironmentVariable("PATH") ?? "";
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        foreach (var entry in process.Split(Path.PathSeparator).Concat(Environment.ExpandEnvironmentVariables(user).Split(Path.PathSeparator)))
        {
            var dir = entry.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                var candidate = Path.Combine(dir, ToolName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static bool IsDisabled()
    {
        var v = Environment.GetEnvironmentVariable(DisableEnvVar);
        return !string.IsNullOrEmpty(v) && v != "0"
            && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Core load: import whatever ended while the tool was missing or the game crashed.</summary>
    public static void RequestOnCoreLoad()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _missingLogged = false;
            Schedule("core load");
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Run database import: could not schedule ({e.Message}).");
        }
    }

    /// <summary>
    /// Run end: queued behind the recorder's pending file writes (header
    /// rewrite, decision-segment close/gzip) on the same ordered background
    /// chain, then launched after <see cref="SettleDelay"/>. Never blocks the
    /// game thread.
    /// </summary>
    public static void RequestAfterRunEnd()
    {
        try
        {
            ReplayStorage.Enqueue("schedule run database import", () => Schedule("run end"));
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Run database import: could not schedule ({e.Message}).");
        }
    }

    /// <summary>Called from <c>CoreMain.Shutdown</c>.</summary>
    public static void Shutdown()
    {
        try { _cts.Cancel(); } catch { }
    }

    private static void Schedule(string reason)
    {
        if (IsDisabled()) return;
        if (Playback.ReplayPlaybackGuard.SuppressTracking) return;
        var token = _cts.Token;
        Task.Delay(SettleDelay, token).ContinueWith(
            t =>
            {
                if (t.IsCanceled || token.IsCancellationRequested) return;
                Launch(reason);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static void Launch(string reason)
    {
        try
        {
            string? exe = FindTool();
            if (exe == null)
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    CoreMain.LogDebug($"Run database import skipped: {ToolName} is not on PATH.");
                }
                return;
            }

            // Beside the database and spirelens-db.json, in the mod's user folder.
            string dir = Godot.ProjectSettings.GlobalizePath("user://SpireLens");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("ingest");
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add("--log");
            psi.ArgumentList.Add(Path.Combine(dir, "last-ingest.log"));

            // Not disposed here: the Exited callback may still read it, and
            // one handle per run end is left to the finalizer.
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += (_, _) =>
            {
                try { CoreMain.LogDebug($"Run database import ({reason}) exited with code {process.ExitCode}."); }
                catch { }
            };
            if (!process.Start()) return;
            int pid = process.Id;
            // Stay out of the game's way.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            CoreMain.LogDebug($"Run database import started ({reason}), pid {pid}.");
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Run database import ({reason}) could not start: {e.Message}");
        }
    }
}
