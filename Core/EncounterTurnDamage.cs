using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core;

/// <summary>
/// Round-by-round damage the tracked player dealt and took in the current (or
/// just-finished) combat, for the top-bar room-icon tooltip.
///
/// Display-only and memory-only: it is reset at every <c>CombatSetUp</c>, kept
/// after <c>CombatEnded</c> so the reward screen can still show the fight, and
/// bound to the floor and run state it was recorded in so a stale fight never
/// shows on a later room. Nothing here is persisted, so a Core reload between
/// combats drops the last fight's breakdown (mid-combat restore is out of scope
/// for the tracker as a whole).
/// </summary>
internal static class EncounterTurnDamage
{
    internal readonly record struct RoundDamage(int Round, int Dealt, int Taken);

    private static readonly object Lock = new();
    private static readonly SortedDictionary<int, (int Dealt, int Taken)> Rounds = new();
    private static WeakReference<IRunState>? _runState;
    private static int _floor = -1;

    internal static void BeginCombat(IRunState? runState)
    {
        lock (Lock)
        {
            Rounds.Clear();
            _runState = runState == null ? null : new WeakReference<IRunState>(runState);
            _floor = runState?.TotalFloor ?? -1;
        }
    }

    internal static void Record(int round, int dealt, int taken)
    {
        if (dealt <= 0 && taken <= 0) return;

        lock (Lock)
        {
            if (_runState == null) return;

            // Damage resolved during CombatSetUp (before the first round
            // starts) belongs to the first round for display purposes.
            round = Math.Max(1, round);
            Rounds.TryGetValue(round, out var totals);
            Rounds[round] = (totals.Dealt + Math.Max(0, dealt), totals.Taken + Math.Max(0, taken));
        }
    }

    /// <summary>
    /// Returns the recorded rounds when they belong to the room the player is
    /// standing in. Rounds with no damage either way are filled in as zeros so
    /// the breakdown reads as a contiguous turn list.
    /// </summary>
    internal static bool TryGetForCurrentRoom(
        IRunState? currentRunState,
        int lastRound,
        out IReadOnlyList<RoundDamage> rounds)
    {
        rounds = Array.Empty<RoundDamage>();
        if (currentRunState == null) return false;

        lock (Lock)
        {
            if (_runState == null
                || !_runState.TryGetTarget(out var recorded)
                || !ReferenceEquals(recorded, currentRunState)
                || currentRunState.TotalFloor != _floor)
            {
                return false;
            }

            int maxRound = lastRound;
            foreach (var round in Rounds.Keys)
                maxRound = Math.Max(maxRound, round);
            if (maxRound <= 0) return false;

            var result = new List<RoundDamage>(maxRound);
            for (int round = 1; round <= maxRound; round++)
            {
                Rounds.TryGetValue(round, out var totals);
                result.Add(new RoundDamage(round, totals.Dealt, totals.Taken));
            }

            rounds = result;
            return true;
        }
    }

    internal static void Reset()
    {
        lock (Lock)
        {
            Rounds.Clear();
            _runState = null;
            _floor = -1;
        }
    }
}
