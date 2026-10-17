using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Validation;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// Belt-and-braces protection of the user's real save data around a replay
/// (docs/replay-player.md, "What is protected"). The game's own switches do
/// the primary work (ShouldSave=false, no IncrementNumReloads, no daily time —
/// see <see cref="ReplayPlayer"/>); this class covers what those switches do
/// not: progress the game mutates in memory mid-run (cards/relics seen, combat
/// stats, epochs, the game-over score) and writes to progress.save at map-node
/// saves, and any file a missed path might touch.
///
/// Before playback: copy current_run.save, current_run_mp.save,
/// progress.save and prefs.save (plus their .backup siblings) and the
/// in-memory progress (serialized with the game's own JSON) into
/// user://SpireLens/replay-playback-backup/, with a manifest that doubles as
/// the "playback in progress" marker. After playback — or on the next Core
/// load if the game crashed or Core was hot-reloaded mid-replay — put the
/// in-memory progress back, save it through the game (so a cloud copy follows),
/// copy back every file whose bytes changed, delete any file that did not
/// exist before, and remove the marker.
/// </summary>
internal static class ReplayDataGuard
{
    private sealed class Manifest
    {
        public string StartedAtUtc { get; set; } = "";
        public long ReplayedGameStartTime { get; set; }
        public List<FileEntry> Files { get; set; } = new();
        public bool HasProgressJson { get; set; }
    }

    private sealed class FileEntry
    {
        public string Path { get; set; } = "";
        public string Backup { get; set; } = "";
        public bool Existed { get; set; }
        public string? Sha256 { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static Func<string> BackupDirProvider = () =>
        Godot.ProjectSettings.GlobalizePath("user://SpireLens/replay-playback-backup/");

    private static string BackupDir => BackupDirProvider();
    private static string ManifestPath => Path.Combine(BackupDir, "manifest.json");
    private static string ProgressJsonPath => Path.Combine(BackupDir, "progress.json");

    public static bool MarkerExists => File.Exists(ManifestPath);

    /// <summary>Game start time of the replay an existing marker belongs to (0 if none/unreadable).</summary>
    public static long MarkerGameStartTime()
    {
        try
        {
            return MarkerExists
                ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), Json)?.ReplayedGameStartTime ?? 0
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>The save files the game keeps for the current profile, as absolute paths.</summary>
    private static List<string> ProtectedFiles()
    {
        var saves = SaveManager.Instance;
        int profile = saves.CurrentProfileId;
        var rel = new List<string>
        {
            saves._runSaveManager.CurrentRunSavePath,
            saves._runSaveManager.CurrentMultiplayerRunSavePath,
            ProgressSaveManager.GetProgressPathForProfile(profile),
            PrefsSaveManager.GetPrefsPath(profile),
        };
        var abs = new List<string>();
        foreach (var r in rel)
        {
            string path = Godot.ProjectSettings.GlobalizePath(saves._saveStore.GetFullPath(r));
            abs.Add(path);
            abs.Add(path + ".backup");
        }
        return abs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Take the backup. Throws if it cannot be taken completely: playback must
    /// not start without it.
    /// </summary>
    public static void Backup(long replayedGameStartTime)
    {
        if (MarkerExists)
            throw new InvalidOperationException("a previous replay's data backup has not been restored yet");

        Directory.CreateDirectory(BackupDir);
        var manifest = new Manifest
        {
            StartedAtUtc = DateTime.UtcNow.ToString("o"),
            ReplayedGameStartTime = replayedGameStartTime,
        };

        int n = 0;
        foreach (var path in ProtectedFiles())
        {
            var entry = new FileEntry { Path = path, Backup = $"file{n++:D2}.bin", Existed = File.Exists(path) };
            if (entry.Existed)
            {
                var bytes = File.ReadAllBytes(path);
                File.WriteAllBytes(Path.Combine(BackupDir, entry.Backup), bytes);
                entry.Sha256 = Hash(bytes);
            }
            manifest.Files.Add(entry);
        }

        var progress = SaveManager.Instance.Progress;
        if (progress != null)
        {
            File.WriteAllText(ProgressJsonPath, SaveManager.ToJson(progress.ToSerializable()));
            manifest.HasProgressJson = true;
        }

        // The manifest is written last: its presence means the backup is complete.
        string tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, Json));
        File.Move(tmp, ManifestPath, overwrite: true);
        CoreMain.Logger.Info($"Replay data guard: backed up {manifest.Files.Count(f => f.Existed)} save files and in-memory progress");
    }

    /// <summary>
    /// Restore everything the backup holds and remove the marker. Safe to call
    /// when there is no marker (no-op). Logs, never throws.
    /// </summary>
    public static void Restore(string reason)
    {
        if (!MarkerExists)
        {
            ReplayPlaybackGuard.SetMarkerPresent(false);
            return;
        }

        Manifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), Json);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay data guard: manifest unreadable, backup kept for manual recovery at {BackupDir}: {e.Message}");
            return;
        }
        if (manifest == null) return;

        bool ok = true;

        // 1. In-memory progress, saved back through the game (local + cloud).
        if (manifest.HasProgressJson && File.Exists(ProgressJsonPath) && ProgressLoaded())
        {
            try
            {
                var read = SaveManager.FromJson<SerializableProgress>(File.ReadAllText(ProgressJsonPath));
                if (read.Success && read.SaveData != null)
                {
                    SaveManager.Instance.Progress = ProgressState.FromSerializable(read.SaveData, new DeserializationContext());
                    SaveManager.Instance.SaveProgressFile();
                }
                else
                {
                    ok = false;
                    CoreMain.Logger.Error($"Replay data guard: in-memory progress backup unreadable ({read.Status})");
                }
            }
            catch (Exception e)
            {
                ok = false;
                CoreMain.Logger.Error($"Replay data guard: restoring in-memory progress failed: {e.Message}");
            }
        }

        // 2. Files back to their exact pre-replay bytes.
        int restored = 0, removed = 0;
        foreach (var f in manifest.Files)
        {
            try
            {
                if (f.Existed)
                {
                    var backupPath = Path.Combine(BackupDir, f.Backup);
                    var original = File.ReadAllBytes(backupPath);
                    bool same = File.Exists(f.Path) && Hash(File.ReadAllBytes(f.Path)) == f.Sha256;
                    if (!same)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(f.Path)!);
                        File.WriteAllBytes(f.Path, original);
                        restored++;
                    }
                }
                else if (File.Exists(f.Path))
                {
                    File.Delete(f.Path);
                    removed++;
                }
            }
            catch (Exception e)
            {
                ok = false;
                CoreMain.Logger.Error($"Replay data guard: restoring {f.Path} failed: {e.Message}");
            }
        }

        if (!ok)
        {
            CoreMain.Logger.Error($"Replay data guard: restore incomplete ({reason}); backup kept at {BackupDir}");
            return;
        }

        try { Directory.Delete(BackupDir, recursive: true); }
        catch (Exception e) { CoreMain.Logger.Warn($"Replay data guard: could not remove backup dir: {e.Message}"); }
        ReplayPlaybackGuard.SetMarkerPresent(false);
        CoreMain.Logger.Info($"Replay data guard: restored ({reason}): {restored} file(s) rewritten, {removed} removed, progress reloaded");
    }

    /// <summary>
    /// At game start (crash recovery) the game may not have loaded progress
    /// yet; then the file restore alone is enough, since it loads that file.
    /// </summary>
    private static bool ProgressLoaded()
    {
        try { return SaveManager.Instance.Progress != null; }
        catch { return false; }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
