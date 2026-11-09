using System;
using System.Collections.Generic;
using System.Linq;
using SpireLens.Core.Replay;
using SpireLens.Core.Replay.Playback;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins the replay player's pure sequencing (docs/replay-player.md): which
/// recorded events become fed inputs, keyframes, checksum expectations and
/// the run end; how a rewind's abandoned branch is skipped; the floor index
/// used by restart; the recorded rooms and acts the map floor picker offers;
/// and the build gate.
/// </summary>
public class ReplayPlaybackPlanTests
{
    private static long _seq;

    private static string Line(string type, object? data) =>
        ReplayFormat.FormatLine(++_seq, DateTime.UtcNow, type, null, data);

    private static string LineAt(string type, int act, string coord, object? data) =>
        ReplayFormat.FormatLine(++_seq, DateTime.UtcNow, type,
            new Dictionary<string, object?> { ["act"] = act, ["coord"] = coord }, data);

    private static Dictionary<string, object?> SaveIn(int act, string key, int floor, string? pre = null) => new()
    {
        ["key"] = key, ["snapshot"] = $"snapshots/{key}.json.gz", ["floor"] = floor, ["act"] = act,
        ["pre_finished_room"] = pre, ["reused"] = false,
    };

    private static Dictionary<string, object?> Save(string key, string snapshot, int floor, string? pre = null, bool reused = false) => new()
    {
        ["key"] = key, ["snapshot"] = snapshot, ["floor"] = floor, ["act"] = 0,
        ["pre_finished_room"] = pre, ["reused"] = reused,
    };

    private static Dictionary<string, object?> Action(string kind, string cls, bool playerDriven = true) => new()
    {
        ["class"] = cls, ["player_driven"] = playerDriven, ["net"] = "AAAA",
        ["sem"] = new Dictionary<string, object?> { ["kind"] = kind },
    };

    private static List<string> SampleRun()
    {
        _seq = 0;
        return new List<string>
        {
            Line(ReplayFormat.EvRecordingStarted, new Dictionary<string, object?>()),         // 1
            Line(ReplayFormat.EvRunStarted, new Dictionary<string, object?>()),               // 2
            Line(ReplayFormat.EvSavePoint, Save("K0", "snapshots/0000.json.gz", 0)),          // 3 floor 1 start
            Line(ReplayFormat.EvDecision, new Dictionary<string, object?> { ["kind"] = "event_option", ["index"] = 0 }), // 4
            Line(ReplayFormat.EvSavePoint, Save("K1", "snapshots/0001.json.gz", 1, "Event")), // 5
            Line(ReplayFormat.EvAction, Action("map_vote", "VoteForMapCoordAction")),         // 6
            Line(ReplayFormat.EvAction, Action("move_to_map_coord", "MoveToMapCoordAction")), // 7 derived
            Line(ReplayFormat.EvSavePoint, Save("K2", "snapshots/0002.json.gz", 1)),          // 8 floor 2 start
            Line(ReplayFormat.EvAction, Action("play_card", "PlayCardAction")),               // 9 abandoned attempt
            Line(ReplayFormat.EvLoad, new Dictionary<string, object?> { ["key"] = "K2", ["reason"] = "room_restart" }), // 10
            Line(ReplayFormat.EvSavePoint, Save("K2", "snapshots/0002.json.gz", 1, reused: true)), // 11 reload re-save
            Line(ReplayFormat.EvAction, Action("play_card", "PlayCardAction")),               // 12
            Line(ReplayFormat.EvHookAction, new Dictionary<string, object?> { ["hook_id"] = 1 }), // 13
            Line(ReplayFormat.EvChecksum, new Dictionary<string, object?> { ["id"] = 0, ["value"] = 42u }), // 14
            Line(ReplayFormat.EvChoice, new Dictionary<string, object?> { ["net"] = "AAAA", ["selection_screen"] = "hand_simpleselect" }), // 15
            Line(ReplayFormat.EvAction, Action("ready_to_begin_enemy_turn", "ReadyToBeginEnemyTurnAction", playerDriven: false)), // 16
            Line(ReplayFormat.EvAction, Action("console_cmd", "ConsoleCmdGameAction")),       // 17
            Line(ReplayFormat.EvAction, Action("end_turn", "EndPlayerTurnAction")),           // 18
            Line(ReplayFormat.EvSavePoint, Save("K3", "snapshots/0003.json.gz", 2, "Monster")), // 19
            Line(ReplayFormat.EvDecision, new Dictionary<string, object?> { ["kind"] = "shop_buy", ["auto"] = true }), // 20
            Line(ReplayFormat.EvDecision, new Dictionary<string, object?> { ["kind"] = "reward_take", ["index"] = 0 }), // 21
            "{\"seq\":99,\"ty",                                                              // torn line
            Line(ReplayFormat.EvRunEnd, new Dictionary<string, object?> { ["outcome"] = "win" }), // 22
        };
    }

    [Fact]
    public void Build_FeedsOnlyPlayerInputs_AndSkipsTheAbandonedBranch()
    {
        var plan = ReplayPlaybackPlan.Build(SampleRun());

        Assert.Equal(
            new[]
            {
                "save_point", "event_option", "save_point", "map_vote", "save_point",
                "play_card", "checksum", "choice", "end_turn", "save_point", "reward_take", "run_end",
            },
            plan.Steps.Select(s => s.InputKind));
        Assert.Equal(new long[] { 3, 4, 5, 6, 8, 12, 14, 15, 18, 19, 21, 22 }, plan.Steps.Select(s => s.Seq));
        Assert.Equal(1, plan.SkippedConsoleCommands);
        Assert.Equal(1, plan.UnparsableLines);
        Assert.Single(plan.Branches);
        Assert.Equal(6, plan.InputCount);
    }

    [Fact]
    public void Keyframes_KnowTheirFloorAndSnapshot()
    {
        var plan = ReplayPlaybackPlan.Build(SampleRun());

        Assert.Equal(0, plan.StartKeyframeIndex);
        var start = plan.Steps[plan.StartKeyframeIndex];
        Assert.Equal("snapshots/0000.json.gz", start.Snapshot);
        Assert.True(start.IsRoomStart);
        Assert.Equal(1, start.RoomFloor);

        var afterNeow = plan.Steps.Single(s => s.Key == "K1");
        Assert.False(afterNeow.IsRoomStart);
        Assert.Equal(1, afterNeow.RoomFloor);

        Assert.Equal(2, plan.MaxRoomFloor);
        // These saves carry no map position, so there is no room to pick on the map.
        Assert.Empty(plan.Rooms);
    }

    [Fact]
    public void Rooms_AreTheRecordedRoomStartsByActAndMapPosition()
    {
        _seq = 0;
        var plan = ReplayPlaybackPlan.Build(new List<string>
        {
            LineAt(ReplayFormat.EvSavePoint, 0, "3,0", SaveIn(0, "A0", 0)),          // 1 act 1 start room (floor 1)
            LineAt(ReplayFormat.EvSavePoint, 0, "3,0", SaveIn(0, "A0e", 1, "Event")), // 2 finished, not a room start
            LineAt(ReplayFormat.EvSavePoint, 0, "2,1", SaveIn(0, "A1", 1)),          // 3 floor 2
            LineAt(ReplayFormat.EvSavePoint, 0, "2,1", SaveIn(0, "A1b", 1)),         // 4 same room again: first one wins
            LineAt(ReplayFormat.EvSavePoint, 1, "4,0", SaveIn(1, "B0", 2)),          // 5 act 2 start room (floor 3)
            LineAt(ReplayFormat.EvSavePoint, 1, "5,1", SaveIn(1, "B1", 3)),          // 6 floor 4
            Line(ReplayFormat.EvSavePoint, SaveIn(1, "B2", 4)),                       // 7 no map position: not offered
        });

        Assert.Equal(
            new[] { (0, 0, 3, 0, 1), (2, 0, 2, 1, 2), (4, 1, 4, 0, 3), (5, 1, 5, 1, 4) },
            plan.Rooms.Select(r => (r.KeyframeIndex, r.Act, r.Coord.Col, r.Coord.Row, r.RoomFloor)));
        Assert.Equal(new[] { 0, 1 }, plan.Acts);
        Assert.Equal(new[] { 4, 5 }, plan.RoomsInAct(1).Select(r => r.KeyframeIndex));
        Assert.Empty(plan.RoomsInAct(2));

        Assert.Equal(1, plan.AdjacentAct(0, +1));
        Assert.Null(plan.AdjacentAct(1, +1));
        Assert.Equal(0, plan.AdjacentAct(1, -1));
        Assert.Null(plan.AdjacentAct(0, -1));
    }

    [Theory]
    [InlineData("3,1", 3, 1)]
    [InlineData("0,15", 0, 15)]
    public void MapPosition_ParsesTheRecordedCoord(string text, int col, int row) =>
        Assert.Equal(new MapPosition(col, row), MapPosition.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("a,b")]
    public void MapPosition_RejectsAnythingElse(string? text) => Assert.Null(MapPosition.Parse(text));

    [Fact]
    public void FloorNavigation_FindsTheRoomStartAndNextKeyframe()
    {
        var plan = ReplayPlaybackPlan.Build(SampleRun());

        // Position 7 = the "choice" step inside floor 2's fight.
        Assert.Equal(4, plan.CurrentRoomStartIndex(7));
        Assert.Equal(9, plan.NextKeyframeIndex(7));
        Assert.Equal(0, plan.CurrentRoomStartIndex(2));
        Assert.Equal(-1, plan.CurrentRoomStartIndex(0));
        Assert.Equal(-1, plan.NextKeyframeIndex(10));
        Assert.Equal(3, plan.InputsBefore(7));
    }

    [Theory]
    [InlineData("41cef1ea", 7u, "Singleplayer", "41cef1ea", 7u, true)]
    [InlineData("41CEF1EA", 7u, "Singleplayer", "41cef1ea", 7u, true)]
    [InlineData("41cef1ea", 7u, "Singleplayer", "aaaa0000", 7u, false)]
    [InlineData("41cef1ea", 7u, "Singleplayer", "41cef1ea", 8u, false)]
    [InlineData("41cef1ea", 7u, "Host", "41cef1ea", 7u, false)]
    [InlineData(null, 7u, "Singleplayer", "41cef1ea", 7u, false)]
    [InlineData("41cef1ea", null, "Singleplayer", "41cef1ea", 7u, false)]
    [InlineData("41cef1ea", 7u, "Singleplayer", null, 7u, false)]
    public void VersionGate_RequiresTheExactBuildAndContent(
        string? commit, uint? hash, string? netType, string? liveCommit, uint? liveHash, bool playable)
    {
        var (ok, reason) = ReplayVersionGate.Check(commit, hash, netType, "v0.111.0", liveCommit, liveHash);
        Assert.Equal(playable, ok);
        Assert.Equal(playable, reason == null);
    }
}
