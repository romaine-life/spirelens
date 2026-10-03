using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core;

/// <summary>
/// Round-by-round damage the tracked player dealt and took in the current (or
/// just-finished) combat, for the top-bar room-icon tooltip.
///
/// Display-only: it is reset at every <c>CombatSetUp</c>, kept after
/// <c>CombatEnded</c> so the reward screen can still show the fight, and bound
/// to the floor it was recorded on so a stale fight never shows on a later
/// room.
///
/// Survives Core hot reload, including mid-combat. The table lives in an
/// AppDomain slot (like RunTracker's reward-attribution weak tables), is keyed
/// by the game's live <see cref="IRunState"/>, and holds only BCL types, so a
/// reloaded Core finds the same object the previous load was filling. A
/// Continue builds a new run state and replays the room, which starts fresh.
/// </summary>
internal static class EncounterTurnDamage
{
    internal readonly record struct RoundDamage(int Round, int Dealt, int Taken);

    private const string AppDomainKey = "SpireLens.EncounterTurnDamage";

    // Value: Item1 = floor the combat was set up on; Item2 = round -> [dealt, taken].
    // The dictionary instance doubles as the lock for its own contents.
    private static readonly ConditionalWeakTable<object, Tuple<int, SortedDictionary<int, int[]>>>
        Encounters = GetEncounters();

    private static ConditionalWeakTable<object, Tuple<int, SortedDictionary<int, int[]>>> GetEncounters()
    {
        if (AppDomain.CurrentDomain.GetData(AppDomainKey)
            is ConditionalWeakTable<object, Tuple<int, SortedDictionary<int, int[]>>> existing)
        {
            return existing;
        }

        var created = new ConditionalWeakTable<object, Tuple<int, SortedDictionary<int, int[]>>>();
        AppDomain.CurrentDomain.SetData(AppDomainKey, created);
        return created;
    }

    internal static void BeginCombat(IRunState? runState)
    {
        if (runState == null) return;

        Encounters.AddOrUpdate(
            runState,
            Tuple.Create(runState.TotalFloor, new SortedDictionary<int, int[]>()));
    }

    internal static void Record(IRunState? runState, int round, int dealt, int taken)
    {
        if (runState == null || (dealt <= 0 && taken <= 0)) return;
        if (!Encounters.TryGetValue(runState, out var encounter)) return;

        var rounds = encounter.Item2;
        lock (rounds)
        {
            // Damage resolved during CombatSetUp (before the first round
            // starts) belongs to the first round for display purposes.
            round = Math.Max(1, round);
            if (!rounds.TryGetValue(round, out var totals))
                rounds[round] = totals = new int[2];
            totals[0] += Math.Max(0, dealt);
            totals[1] += Math.Max(0, taken);
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
        if (currentRunState == null
            || !Encounters.TryGetValue(currentRunState, out var encounter)
            || encounter.Item1 != currentRunState.TotalFloor)
        {
            return false;
        }

        var recorded = encounter.Item2;
        lock (recorded)
        {
            int maxRound = lastRound;
            foreach (var round in recorded.Keys)
                maxRound = Math.Max(maxRound, round);
            if (maxRound <= 0) return false;

            var result = new List<RoundDamage>(maxRound);
            for (int round = 1; round <= maxRound; round++)
            {
                recorded.TryGetValue(round, out var totals);
                result.Add(new RoundDamage(
                    round,
                    totals?[0] ?? 0,
                    totals?[1] ?? 0));
            }

            rounds = result;
            return true;
        }
    }
}
