using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpireLens.Core.Replay;

/// <summary>
/// Marks a run the player took over from a replay (docs/replay-player.md,
/// "Taking over"). The game state was cloned from a recording at that point,
/// so the run's stats and recording begin there rather than at a real run
/// start: data from such a run is not the same kind of information as a run
/// played from its first floor, and anything that reads it later (the run
/// database, analysis, model training) can tell them apart by this tag.
/// Stamped on the stats record (<see cref="RunData.TakenOver"/>) and the
/// recording header (<see cref="ReplayHeader.TakenOver"/>).
/// </summary>
public sealed class TakenOverInfo
{
    /// <summary>The game start time of the recording the run was taken over from.</summary>
    public long FromGameStartTime { get; set; }

    /// <summary>The floor (the game's total floor) the replay was on when the player took over.</summary>
    public int FromFloor { get; set; }

    /// <summary>The act index (0-based) the replay was in.</summary>
    public int FromAct { get; set; }

    /// <summary>The recording's event seq of the room-start save point the run continued from.</summary>
    public long FromSeq { get; set; }

    /// <summary>When the player took over (ISO-8601 UTC).</summary>
    public string TakenOverAtUtc { get; set; } = "";
}

/// <summary>
/// Which live runs were taken over from a replay, keyed by their new game
/// start time. SpireLens's stats record and recording for such a run are
/// created later than the take-over itself (at the next room, or at a
/// Continue after a restart), so the tag waits here on disk until then;
/// each consumer stamps it into its own file. An entry is removed when its
/// run ends.
/// </summary>
public static class TakenOverRuns
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static Func<string> PathProvider = () =>
        Godot.ProjectSettings.GlobalizePath("user://SpireLens/taken-over-runs.json");

    private static readonly object Lock = new();

    public static TakenOverInfo? Find(long gameStartTime)
    {
        if (gameStartTime <= 0) return null;
        lock (Lock)
        {
            return Load().TryGetValue(gameStartTime.ToString(), out var info) ? info : null;
        }
    }

    public static void Add(long gameStartTime, TakenOverInfo info)
    {
        lock (Lock)
        {
            var all = Load();
            all[gameStartTime.ToString()] = info;
            Save(all);
        }
    }

    public static void Remove(long gameStartTime)
    {
        lock (Lock)
        {
            var all = Load();
            if (all.Remove(gameStartTime.ToString())) Save(all);
        }
    }

    private static Dictionary<string, TakenOverInfo> Load()
    {
        try
        {
            string path = PathProvider();
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, TakenOverInfo>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Taken-over runs: {e.Message}; treating as empty");
            return new();
        }
    }

    private static void Save(Dictionary<string, TakenOverInfo> all)
    {
        string path = PathProvider();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
