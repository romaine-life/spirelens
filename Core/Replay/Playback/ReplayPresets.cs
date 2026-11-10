using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// How a run ended, read from the game's own run history rather than the
/// recording header, which has only the outcome and final floor.
/// <paramref name="Act"/> is 1-based; <paramref name="LastPoint"/> is the map
/// node the run ended on.
/// </summary>
public sealed record RunEnding(bool Win, bool Abandoned, int Act, MapPointType? LastPoint)
{
    public bool Died => !Win && !Abandoned;

    public static RunEnding From(RunHistory history)
    {
        var acts = history.MapPointHistory;
        var lastAct = acts.Count > 0 ? acts[^1] : null;
        MapPointType? lastPoint = lastAct is { Count: > 0 } ? lastAct[^1].MapPointType : null;
        return new RunEnding(history.Win, history.WasAbandoned, acts.Count, lastPoint);
    }
}

/// <summary>A built-in playlist: every recording whose ending matches.</summary>
public sealed record ReplayPreset(string Name, Func<RunEnding, bool> Matches);

public static class ReplayPresetCatalog
{
    public static readonly IReadOnlyList<ReplayPreset> All =
    [
        new("All wins", e => e.Win),
        new("Died on an Act 1 boss", e => DiedOn(e, 1, MapPointType.Boss)),
        new("Died on an Act 2 boss", e => DiedOn(e, 2, MapPointType.Boss)),
        new("Died on an Act 3 boss", e => DiedOn(e, 3, MapPointType.Boss)),
        new("Died to an elite", e => e.Died && e.LastPoint == MapPointType.Elite),
    ];

    private static bool DiedOn(RunEnding e, int act, MapPointType point) =>
        e.Died && e.Act == act && e.LastPoint == point;
}

/// <summary>
/// Resolves presets against the recordings on disk. A history file never
/// changes once the game writes it, so each ending is read once per Core
/// load; a recording with no history yet (a run still in progress, or one
/// from another profile) matches no preset and is retried next time.
/// </summary>
internal static class ReplayPresetLibrary
{
    private static readonly Dictionary<long, RunEnding> Endings = new();

    public static List<(ReplayPreset Preset, List<ReplayListing> Runs)> Resolve()
    {
        var watch = Stopwatch.StartNew();
        var listings = ReplayLibrary.List(int.MaxValue);
        int loaded = 0;
        var endings = new Dictionary<long, RunEnding>();
        foreach (var listing in listings)
        {
            if (!Endings.TryGetValue(listing.GameStartTime, out var ending))
            {
                ending = TryLoadEnding(listing.GameStartTime);
                if (ending == null) continue;
                Endings[listing.GameStartTime] = ending;
                loaded++;
            }

            endings[listing.GameStartTime] = ending;
        }

        var result = ReplayPresetCatalog.All
            .Select(preset => (preset, listings
                .Where(l => endings.TryGetValue(l.GameStartTime, out var e) && preset.Matches(e))
                .ToList()))
            .ToList();

        if (loaded > 0)
            CoreMain.Logger.Info(
                $"Playlist presets: read {loaded} run histories for {listings.Count} recordings in {watch.ElapsedMilliseconds} ms");
        return result;
    }

    private static RunEnding? TryLoadEnding(long gameStartTime)
    {
        try
        {
            var saves = SaveManager.Instance;
            string fileName = $"{gameStartTime}.run";
            string path = saves.GetProfileScopedPath(Path.Combine(UserDataPathProvider.SavesDir, "history", fileName));
            if (!File.Exists(path)) return null;

            var result = saves.LoadRunHistory(fileName);
            return result.Success && result.SaveData != null ? RunEnding.From(result.SaveData) : null;
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Playlist presets: reading the history of run {gameStartTime} failed: {e.Message}");
            return null;
        }
    }
}
