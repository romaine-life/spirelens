using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using SpireLens.Core.Replay.Playback;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>Pins which run endings each built-in playlist preset takes.</summary>
public class ReplayPresetTests
{
    private static RunHistory History(bool win, bool abandoned, params MapPointType[][] acts) => new()
    {
        Win = win,
        WasAbandoned = abandoned,
        MapPointHistory = acts
            .Select(points => points.Select(p => new MapPointHistoryEntry { MapPointType = p }).ToList())
            .ToList(),
    };

    private static readonly MapPointType[] Act = [MapPointType.Monster, MapPointType.Elite, MapPointType.Boss];

    private static IEnumerable<string> PresetsFor(RunHistory history)
    {
        var ending = RunEnding.From(history);
        return ReplayPresetCatalog.All.Where(p => p.Matches(ending)).Select(p => p.Name);
    }

    [Fact]
    public void Win_IsOnlyAllWins()
    {
        Assert.Equal(["All wins"], PresetsFor(History(true, false, Act, Act, Act)));
    }

    [Fact]
    public void DeathAtTheActThreeBoss_IsActThreeBossOnly()
    {
        Assert.Equal(["Died on an Act 3 boss"], PresetsFor(History(false, false, Act, Act, Act)));
    }

    [Fact]
    public void DeathAtTheActOneBoss_IsActOneBossOnly()
    {
        Assert.Equal(["Died on an Act 1 boss"], PresetsFor(History(false, false, Act)));
    }

    [Fact]
    public void DeathToAnElite_IsEliteOnly()
    {
        Assert.Equal(["Died to an elite"], PresetsFor(History(false, false, Act, [MapPointType.Monster, MapPointType.Elite])));
    }

    [Fact]
    public void AbandonedAtABoss_IsNotADeath()
    {
        Assert.Empty(PresetsFor(History(false, true, Act, Act)));
    }

    [Fact]
    public void DeathInAnOrdinaryFight_MatchesNoPreset()
    {
        Assert.Empty(PresetsFor(History(false, false, [MapPointType.Monster])));
    }

    [Fact]
    public void EmptyHistory_HasNoActOrNode()
    {
        var ending = RunEnding.From(History(false, false));
        Assert.Equal(0, ending.Act);
        Assert.Null(ending.LastPoint);
    }
}
