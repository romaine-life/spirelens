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
/// </summary>
public static class ReplayPlaybackGuard
{
    private static volatile bool _playerActive;
    private static volatile bool _markerPresent;

    public static bool SuppressTracking => _playerActive || _markerPresent;

    public static bool PlayerActive => _playerActive;

    internal static void SetPlayerActive(bool active) => _playerActive = active;

    internal static void SetMarkerPresent(bool present) => _markerPresent = present;
}
