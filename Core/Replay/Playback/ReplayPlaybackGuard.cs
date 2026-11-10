namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// The one switch every SpireLens writer consults while a replay is driving
/// the game: <see cref="RunTracker"/> (no run record is created, adopted or
/// written), <see cref="ReplayRecorder"/> (nothing is recorded),
/// <see cref="RunDatabaseImporter"/> (no import), and <see cref="RoomResetter"/>
/// (no restart, no death-restore capture).
///
/// True while this Core's player is active, and also while a playback marker
/// from an interrupted session exists (a Core hot reload or a crash in the
/// middle of a replay) until <see cref="ReplayDataGuard"/> has restored the
/// user's data and removed it. Plain static flags: no Harmony involved.
///
/// Also true between a take-over (docs/replay-player.md, "Taking over") and
/// the taken-over run's first room: tracking and recording begin at a room
/// boundary, not in the middle of whatever room the replay was in. The wait
/// ends at the next room-entry save point (<see cref="OnSavePoint"/>, which
/// starts the stats record and the recording) or at a RunStarted (a Continue
/// of the taken-over run's save), whichever comes first.
/// </summary>
public static class ReplayPlaybackGuard
{
    private static volatile bool _playerActive;
    private static volatile bool _markerPresent;
    private static long _takeOverWaitStart;

    public static bool SuppressTracking => _playerActive || _markerPresent || _takeOverWaitStart != 0;

    /// <summary>A run was just taken over (new game start time): suppress until its first room.</summary>
    internal static void BeginTakeOverWait(long gameStartTime) => _takeOverWaitStart = gameStartTime;

    /// <summary>End the take-over wait without starting tracking here (a RunStarted does that itself).</summary>
    internal static void EndTakeOverWait(string why)
    {
        if (_takeOverWaitStart == 0) return;
        _takeOverWaitStart = 0;
        CoreMain.Logger.Info($"Take over: tracking and recording resume ({why})");
    }

    /// <summary>
    /// From <c>RunSaveSnapshotPatch</c>, before the tracker takes its rewind
    /// snapshot: the taken-over run is entering a room, so its stats record and
    /// recording start now; the save the game is writing becomes the
    /// recording's first keyframe.
    /// </summary>
    internal static void OnSavePoint(MegaCrit.Sts2.Core.Rooms.AbstractRoom? preFinishedRoom)
    {
        long start = _takeOverWaitStart;
        if (start == 0 || preFinishedRoom != null) return;
        var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
        var state = rm?.State;
        if (rm == null || state == null || !rm.IsInProgress || rm._startTime != start) return;
        _takeOverWaitStart = 0;
        CoreMain.Logger.Info("Take over: the taken-over run reached its first room; tracking and recording start");
        var info = Replay.TakenOverRuns.Find(start);
        RunTracker.BeginTakenOverRun(state, start, info);
        Replay.ReplayRecorder.AttachToLiveRun();
    }

    public static bool PlayerActive => _playerActive;

    internal static void SetPlayerActive(bool active) => _playerActive = active;

    internal static void SetMarkerPresent(bool present) => _markerPresent = present;
}
