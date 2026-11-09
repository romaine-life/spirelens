using System;
using System.Collections.Generic;
using System.Linq;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// The playback state a Core hot reload hands from the old Core to the new
/// one, so a replay continues from the same step and the watcher sees
/// nothing (docs/replay-player.md, "Core hot reload mid-replay").
///
/// A reload never unloads the old Core (the Loader's AssemblyLoadContext is
/// non-collectible); the new Core simply starts with its own statics. The
/// state therefore travels as a plain dictionary of BCL values (and, for a
/// card selection the game is waiting on, the game's own objects) stored in
/// the process's AppDomain data, which both Cores see. Neither Core's types
/// appear in it: the two assemblies' types are distinct. The dictionary is
/// one shared, live object: when an old-Core load is still in flight at
/// reload time, <see cref="LoadDoneKey"/> holds a TaskCompletionSource the
/// old Core completes when the game has finished loading, and the new Core
/// awaits it before it continues.
/// </summary>
internal sealed record ReplayHandoff(
    string? PlaylistName,
    IReadOnlyList<PlaylistRunRef> Runs,
    int RunIndex,
    int Position,
    bool Finished,
    int LastResyncKeyframe,
    int ResyncRepeats,
    int Divergences,
    int SpeedIndex,
    bool Paused,
    bool StepOnce,
    DateTime? AdvanceAt,
    string? Note,
    DateTime NoteUntil,
    IReadOnlyList<string> ObservedSaves,
    bool PickerOpen,
    string Request,
    int JumpKeyframe,
    bool ReopenPickerAfterLoad)
{
    /// <summary>The AppDomain data slot. Versioned: a Core that does not know the shape ignores it.</summary>
    internal const string Slot = "SpireLens.ReplayHandoff.v1";

    internal const string LoadDoneKey = "load_done";
    internal const string PendingSelectionKey = "pending_selection";

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["playlist_name"] = PlaylistName,
        ["run_times"] = Runs.Select(r => r.GameStartTime).ToArray(),
        ["run_labels"] = Runs.Select(r => r.Label).ToArray(),
        ["run_index"] = RunIndex,
        ["position"] = Position,
        ["finished"] = Finished,
        ["last_resync_keyframe"] = LastResyncKeyframe,
        ["resync_repeats"] = ResyncRepeats,
        ["divergences"] = Divergences,
        ["speed_index"] = SpeedIndex,
        ["paused"] = Paused,
        ["step_once"] = StepOnce,
        ["advance_at"] = AdvanceAt,
        ["note"] = Note,
        ["note_until"] = NoteUntil,
        ["observed_saves"] = ObservedSaves.ToArray(),
        ["picker_open"] = PickerOpen,
        ["request"] = Request,
        ["jump_keyframe"] = JumpKeyframe,
        ["reopen_picker_after_load"] = ReopenPickerAfterLoad,
        [LoadDoneKey] = null,
        [PendingSelectionKey] = null,
    };

    /// <summary>The state in <paramref name="d"/>, or null when it is missing a field or has the wrong shape.</summary>
    public static ReplayHandoff? FromDictionary(IReadOnlyDictionary<string, object?> d)
    {
        try
        {
            var times = (long[])d["run_times"]!;
            var labels = (string?[])d["run_labels"]!;
            if (times.Length != labels.Length) return null;
            return new ReplayHandoff(
                (string?)d["playlist_name"],
                times.Select((t, i) => new PlaylistRunRef(t, labels[i])).ToList(),
                (int)d["run_index"]!,
                (int)d["position"]!,
                (bool)d["finished"]!,
                (int)d["last_resync_keyframe"]!,
                (int)d["resync_repeats"]!,
                (int)d["divergences"]!,
                (int)d["speed_index"]!,
                (bool)d["paused"]!,
                (bool)d["step_once"]!,
                (DateTime?)d["advance_at"],
                (string?)d["note"],
                (DateTime)d["note_until"]!,
                (string[])d["observed_saves"]!,
                (bool)d["picker_open"]!,
                (string)d["request"]!,
                (int)d["jump_keyframe"]!,
                (bool)d["reopen_picker_after_load"]!);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidCastException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>Store the live dictionary for the next Core.</summary>
    public static void Publish(Dictionary<string, object?> live) => AppDomain.CurrentDomain.SetData(Slot, live);

    /// <summary>The dictionary a previous Core published, removed from the slot so it is used once.</summary>
    public static Dictionary<string, object?>? Take()
    {
        var live = AppDomain.CurrentDomain.GetData(Slot) as Dictionary<string, object?>;
        AppDomain.CurrentDomain.SetData(Slot, null);
        return live;
    }
}
