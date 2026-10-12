using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpireLens.Core.Replay;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins the pure parts of the run-recording format (docs/run-recording.md):
/// the events.jsonl envelope, the save-point key, the canonical-timeline cut
/// across Continue / room restart / death undo, and session counter recovery
/// after a hot reload.
/// </summary>
public class RunRecordingFormatTests
{
    private static ReplayLogEntry E(long seq, string type, string? key = null) => new(seq, type, key, null);

    [Fact]
    public void FormatLine_WritesEnvelopeContextAndData_AndParsesBack()
    {
        var line = ReplayFormat.FormatLine(
            7,
            new DateTime(2026, 10, 12, 1, 2, 3, 456, DateTimeKind.Utc),
            ReplayFormat.EvSavePoint,
            new Dictionary<string, object?> { ["floor"] = 3, ["seq"] = 999 },
            new Dictionary<string, object?> { ["key"] = "a0/c1,2/f3/p-/r00", ["snapshot"] = "snapshots/0002.json.gz" });

        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("seq").GetInt64()); // context cannot override the envelope
        Assert.Equal("2026-10-12T01:02:03.456Z", root.GetProperty("t").GetString());
        Assert.Equal("save_point", root.GetProperty("type").GetString());
        Assert.Equal(3, root.GetProperty("floor").GetInt32());

        Assert.True(ReplayFormat.TryParseEnvelope(line, out var entry));
        Assert.Equal(7, entry.Seq);
        Assert.Equal(ReplayFormat.EvSavePoint, entry.Type);
        Assert.Equal("a0/c1,2/f3/p-/r00", entry.Key);
        Assert.Equal("snapshots/0002.json.gz", entry.Snapshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"seq\":3,\"ty")]          // torn last line after a crash
    [InlineData("not json")]
    [InlineData("{\"type\":\"action\"}")]    // no seq
    public void TryParseEnvelope_RejectsBlankTornAndIncompleteLines(string line)
    {
        Assert.False(ReplayFormat.TryParseEnvelope(line, out _));
    }

    [Fact]
    public void SavePointKey_IgnoresRngOrder_ButSeesAnyCounterChange()
    {
        var a = new[] { new RngPosition("run.Shuffle", 4, 11), new RngPosition("run.UpFront", 9, 22) };
        var b = a.Reverse().ToArray();
        var moved = new[] { new RngPosition("run.Shuffle", 5, 11), new RngPosition("run.UpFront", 9, 22) };

        var ka = ReplayFormat.BuildSavePointKey(0, 3, 2, 4, null, a);
        Assert.Equal(ka, ReplayFormat.BuildSavePointKey(0, 3, 2, 4, null, b));
        Assert.NotEqual(ka, ReplayFormat.BuildSavePointKey(0, 3, 2, 4, null, moved));
        Assert.NotEqual(ka, ReplayFormat.BuildSavePointKey(0, 3, 2, 4, "Monster", a));
        Assert.StartsWith("a0/c3,2/f4/p-/r", ka);
        Assert.StartsWith("a1/cnone/f0/p-/r", ReplayFormat.BuildSavePointKey(1, null, null, 0, null, a));
    }

    [Fact]
    public void Timeline_LinearLogIsCanonicalAsIs()
    {
        var log = new[]
        {
            E(1, ReplayFormat.EvRunStarted),
            E(2, ReplayFormat.EvSavePoint, "K1"),
            E(3, ReplayFormat.EvAction),
            E(4, ReplayFormat.EvSavePoint, "K2"),
            E(5, ReplayFormat.EvRunEnd),
        };

        var t = ReplayFormat.BuildCanonicalTimeline(log);

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, t.Canonical.Select(e => e.Seq));
        Assert.Empty(t.Branches);
        Assert.Empty(t.UnanchoredLoads);
    }

    [Fact]
    public void Timeline_LoadCutsBackToMatchingSavePoint_AndKeepsTheBranch()
    {
        // Enter room (K2), act, die, death undo reloads K2, act again, win.
        var log = new[]
        {
            E(1, ReplayFormat.EvSavePoint, "K1"),
            E(2, ReplayFormat.EvAction),
            E(3, ReplayFormat.EvSavePoint, "K2"),
            E(4, ReplayFormat.EvAction),
            E(5, ReplayFormat.EvRunEnd),
            E(6, ReplayFormat.EvLoad, "K2"),
            E(7, ReplayFormat.EvSavePoint, "K2"), // IncrementNumReloads re-save
            E(8, ReplayFormat.EvAction),
            E(9, ReplayFormat.EvRunEnd),
        };

        var t = ReplayFormat.BuildCanonicalTimeline(log);

        Assert.Equal(new long[] { 1, 2, 3, 6, 7, 8, 9 }, t.Canonical.Select(e => e.Seq));
        var branch = Assert.Single(t.Branches);
        Assert.Equal(3, branch.AnchorSeq);
        Assert.Equal(6, branch.LoadSeq);
        Assert.Equal(new long[] { 4, 5 }, branch.AbandonedSeqs);
    }

    [Fact]
    public void Timeline_RepeatedRestartsOfOneRoomEachCutToTheLatestSavePoint()
    {
        var log = new[]
        {
            E(1, ReplayFormat.EvSavePoint, "K1"),
            E(2, ReplayFormat.EvAction),
            E(3, ReplayFormat.EvLoad, "K1"),
            E(4, ReplayFormat.EvSavePoint, "K1"),
            E(5, ReplayFormat.EvAction),
            E(6, ReplayFormat.EvLoad, "K1"),
            E(7, ReplayFormat.EvSavePoint, "K1"),
            E(8, ReplayFormat.EvAction),
        };

        var t = ReplayFormat.BuildCanonicalTimeline(log);

        Assert.Equal(new long[] { 1, 3, 4, 6, 7, 8 }, t.Canonical.Select(e => e.Seq));
        Assert.Equal(2, t.Branches.Count);
        Assert.Equal(new long[] { 2 }, t.Branches[0].AbandonedSeqs);
        Assert.Equal(new long[] { 5 }, t.Branches[1].AbandonedSeqs);
    }

    [Fact]
    public void Timeline_ContinueImmediatelyAfterSave_CutsNothing()
    {
        var log = new[]
        {
            E(1, ReplayFormat.EvSavePoint, "K1"),
            E(2, ReplayFormat.EvLoad, "K1"),
            E(3, ReplayFormat.EvAction),
        };

        var t = ReplayFormat.BuildCanonicalTimeline(log);

        Assert.Equal(new long[] { 1, 2, 3 }, t.Canonical.Select(e => e.Seq));
        Assert.Empty(t.Branches);
    }

    [Fact]
    public void Timeline_UnknownLoadKeyStartsAnUnanchoredRoot()
    {
        var log = new[]
        {
            E(1, ReplayFormat.EvSavePoint, "K1"),
            E(2, ReplayFormat.EvAction),
            E(3, ReplayFormat.EvLoad, "UNSEEN"),
            E(4, ReplayFormat.EvAction),
        };

        var t = ReplayFormat.BuildCanonicalTimeline(log);

        Assert.Equal(new long[] { 3, 4 }, t.Canonical.Select(e => e.Seq));
        Assert.Equal(new long[] { 3 }, t.UnanchoredLoads);
        var branch = Assert.Single(t.Branches);
        Assert.Null(branch.AnchorSeq);
        Assert.Equal(new long[] { 1, 2 }, branch.AbandonedSeqs);
    }

    [Fact]
    public void Session_RecoversSeqAndSnapshotNumbering_AndDedupesEqualKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "spirelens-recording-test-" + Guid.NewGuid().ToString("N"));
        var previous = ReplayStorage.RootDirProvider;
        ReplayStorage.RootDirProvider = () => root;
        try
        {
            using (var s = ReplaySession.Open(123, () => new ReplayHeader { SeedString = "SEED" }, out bool created)!)
            {
                Assert.True(created);
                var (snap0, reused0) = s.ReserveSnapshot("K1");
                Assert.Equal("snapshots/0000.json.gz", snap0);
                Assert.False(reused0);
                s.Append(ReplayFormat.EvSavePoint, null, new Dictionary<string, object?> { ["key"] = "K1", ["snapshot"] = snap0 });

                var (snapAgain, reusedAgain) = s.ReserveSnapshot("K1");
                Assert.Equal(snap0, snapAgain);
                Assert.True(reusedAgain);

                var (snap1, _) = s.ReserveSnapshot("K2");
                Assert.Equal("snapshots/0001.json.gz", snap1);
                s.Append(ReplayFormat.EvSavePoint, null, new Dictionary<string, object?> { ["key"] = "K2", ["snapshot"] = snap1 });
                s.Append(ReplayFormat.EvAction, null, null);
            }

            // A crash-torn tail must not break recovery or the next append.
            File.AppendAllText(Path.Combine(root, "123", "events.jsonl"), "{\"seq\":99,\"ty");

            using (var s = ReplaySession.Open(123, null, out bool created)!)
            {
                Assert.False(created);
                Assert.Equal("SEED", s.Header.SeedString);
                Assert.Equal("K2", s.LastSnapshotKey);
                Assert.Equal(4, s.Append(ReplayFormat.EvAction, null, null));
                Assert.Equal(("snapshots/0002.json.gz", false), s.ReserveSnapshot("K3"));
            }

            var lines = File.ReadAllLines(Path.Combine(root, "123", "events.jsonl"));
            var parsed = lines.Select(l => ReplayFormat.TryParseEnvelope(l, out var e) ? e.Seq : -1).ToList();
            Assert.Equal(new long[] { 1, 2, 3, -1, 4 }, parsed);

            Assert.Null(ReplaySession.Open(456, null, out _));
        }
        finally
        {
            ReplayStorage.RootDirProvider = previous;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
