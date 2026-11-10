using System;
using System.IO;
using SpireLens.Core.Replay;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins the on-disk registry that carries a taken-over run's tag until its
/// stats record and recording are created (docs/replay-player.md, "Taking
/// over"): it survives being written and read back, keys by the new run's
/// game start time, and forgets a run when told.
/// </summary>
[Collection("TakenOverRuns")]
public sealed class TakenOverRunsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spirelens-taken-over-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string> _previous = TakenOverRuns.PathProvider;

    public TakenOverRunsTests()
    {
        TakenOverRuns.PathProvider = () => Path.Combine(_dir, "taken-over-runs.json");
    }

    public void Dispose()
    {
        TakenOverRuns.PathProvider = _previous;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TakenOverInfo Info(long from, int floor) => new()
    {
        FromGameStartTime = from,
        FromFloor = floor,
        FromAct = 1,
        FromSeq = 99,
        TakenOverAtUtc = "2026-11-09T23:40:00Z",
    };

    [Fact]
    public void Find_IsNullWhenNothingWasTakenOver()
    {
        Assert.Null(TakenOverRuns.Find(1794301200));
        Assert.Null(TakenOverRuns.Find(0));
    }

    [Fact]
    public void Add_ThenFind_ReturnsTheTagForThatRunOnly()
    {
        TakenOverRuns.Add(1794301200, Info(1794200070, 12));
        TakenOverRuns.Add(1794309999, Info(1794100000, 30));

        var found = TakenOverRuns.Find(1794301200);
        Assert.NotNull(found);
        Assert.Equal(1794200070, found!.FromGameStartTime);
        Assert.Equal(12, found.FromFloor);
        Assert.Equal(1, found.FromAct);
        Assert.Equal(99, found.FromSeq);
        Assert.Equal(30, TakenOverRuns.Find(1794309999)!.FromFloor);
        Assert.Null(TakenOverRuns.Find(1794200070));
    }

    [Fact]
    public void Remove_ForgetsOnlyThatRun()
    {
        TakenOverRuns.Add(1794301200, Info(1794200070, 12));
        TakenOverRuns.Add(1794309999, Info(1794100000, 30));

        TakenOverRuns.Remove(1794301200);

        Assert.Null(TakenOverRuns.Find(1794301200));
        Assert.NotNull(TakenOverRuns.Find(1794309999));
    }
}
