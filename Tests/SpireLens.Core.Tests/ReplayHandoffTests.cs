using System;
using System.Collections.Generic;
using SpireLens.Core.Replay.Playback;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins the replay state a Core hot reload hands to the next Core
/// (docs/replay-player.md, "Core hot reload mid-replay"): it survives the
/// trip through the plain dictionary both Cores share, and a dictionary of
/// the wrong shape is refused rather than half-read.
/// </summary>
public class ReplayHandoffTests
{
    private static ReplayHandoff Sample() => new(
        PlaylistName: "Act 1 wins",
        Runs: new List<PlaylistRunRef> { new(1794200070, "A10 win"), new(1794300000, null) },
        RunIndex: 1,
        Position: 412,
        Finished: false,
        LastResyncKeyframe: 37,
        ResyncRepeats: 1,
        Divergences: 2,
        SpeedIndex: 3,
        Paused: true,
        StepOnce: false,
        AdvanceAt: null,
        Note: "Jumped to floor 9",
        NoteUntil: new DateTime(2026, 11, 9, 23, 0, 0, DateTimeKind.Utc),
        ObservedSaves: new List<string> { "a0/c3,1/f1/p-/r1" },
        PickerOpen: true,
        Request: "NextAct",
        JumpKeyframe: -1,
        ReopenPickerAfterLoad: true);

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var sample = Sample();
        var back = ReplayHandoff.FromDictionary(sample.ToDictionary());

        Assert.NotNull(back);
        Assert.Equal(sample.Runs, back!.Runs);
        Assert.Equal(sample.ObservedSaves, back.ObservedSaves);
        Assert.Equal(sample with { Runs = back.Runs, ObservedSaves = back.ObservedSaves }, back);
    }

    [Fact]
    public void RoundTrip_KeepsAPendingRunAdvance()
    {
        var at = new DateTime(2026, 11, 9, 23, 5, 0, DateTimeKind.Utc);
        var back = ReplayHandoff.FromDictionary((Sample() with { AdvanceAt = at }).ToDictionary());
        Assert.Equal(at, back!.AdvanceAt);
    }

    [Fact]
    public void ToDictionary_StartsWithNoLoadAndNoSelection()
    {
        var d = Sample().ToDictionary();
        Assert.Null(d[ReplayHandoff.LoadDoneKey]);
        Assert.Null(d[ReplayHandoff.PendingSelectionKey]);
    }

    [Fact]
    public void FromDictionary_RefusesAMissingField()
    {
        var d = Sample().ToDictionary();
        d.Remove("position");
        Assert.Null(ReplayHandoff.FromDictionary(d));
    }

    [Fact]
    public void FromDictionary_RefusesAWrongType()
    {
        var d = Sample().ToDictionary();
        d["position"] = "412";
        Assert.Null(ReplayHandoff.FromDictionary(d));
    }

    [Fact]
    public void FromDictionary_RefusesMismatchedRunLists()
    {
        var d = Sample().ToDictionary();
        d["run_labels"] = new string?[] { "only one" };
        Assert.Null(ReplayHandoff.FromDictionary(d));
    }
}
