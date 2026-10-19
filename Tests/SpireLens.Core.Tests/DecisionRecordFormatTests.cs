using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using SpireLens.Core.Replay;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins the pure parts of the per-decision training records
/// (docs/run-recording.md, "Decision records"): record shape, option
/// matching, the events.jsonl &lt;-&gt; decisions join, the branch cut applied
/// to records, and segment numbering / recovery.
/// </summary>
[Collection("ReplayStorageRoot")]
public class DecisionRecordFormatTests
{
    [Fact]
    public void FormatRecord_CarriesJoinKeysVersionAndPayload_AndParsesBack()
    {
        var options = new[]
        {
            new LegalOption("play_card", CombatCardIndex: 3, TargetCombatId: 2, Label: "CARD.STRIKE"),
            new LegalOption("end_turn"),
        };
        var line = DecisionFormat.FormatRecord(new DecisionRecordParts
        {
            DecisionSeq = 12,
            EventSeq = 340,
            EventType = ReplayFormat.EvAction,
            AnchorSaveSeq = 301,
            Utc = new DateTime(2026, 10, 12, 0, 0, 1, DateTimeKind.Utc),
            Kind = "play_card",
            Phase = DecisionFormat.PhaseCombat,
            Player = 1,
            ChosenIndex = 0,
            Options = options.Select(o => (object?)o.ToDictionary()).ToList(),
            State = new Dictionary<string, object?> { ["combat"] = new Dictionary<string, object?> { ["energy"] = 3 } },
            Hidden = new Dictionary<string, object?> { ["draw_pile_order"] = new List<object?> { "CARD.DEFEND" } },
            NetStateForm = "NetFullCombatState",
            NetStateBase64 = "AAEC",
        });

        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal(DecisionFormat.FormatVersion, root.GetProperty("format_version").GetInt32());
        Assert.Equal(301, root.GetProperty("anchor_save_seq").GetInt64());
        Assert.Equal("combat", root.GetProperty("phase").GetString());
        Assert.Equal(0, root.GetProperty("chosen_index").GetInt32());
        Assert.Equal(2, root.GetProperty("options")[0].GetProperty("target").GetInt32());
        Assert.Equal("CARD.STRIKE", root.GetProperty("options")[0].GetProperty("label").GetString());
        Assert.Equal("CARD.DEFEND", root.GetProperty("hidden").GetProperty("draw_pile_order")[0].GetString());
        Assert.Equal("AAEC", root.GetProperty("net_state").GetProperty("b64").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("notes").ValueKind);

        Assert.True(DecisionFormat.TryParseLink(line, out long dseq, out long eventSeq));
        Assert.Equal(12, dseq);
        Assert.Equal(340, eventSeq);
        Assert.False(DecisionFormat.TryParseLink("{\"dseq\":1", out _, out _));
    }

    [Fact]
    public void FindChosenIndex_MatchesIdentityNotLabel()
    {
        var options = new List<LegalOption>
        {
            new("play_card", CombatCardIndex: 5, TargetCombatId: 1, Label: "CARD.BASH"),
            new("play_card", CombatCardIndex: 5, TargetCombatId: 2, Label: "CARD.BASH"),
            new("use_potion", TargetCombatId: 2, PotionSlot: 0),
            new("end_turn"),
        };

        Assert.Equal(1, DecisionFormat.FindChosenIndex(options, new LegalOption("play_card", CombatCardIndex: 5, TargetCombatId: 2)));
        Assert.Equal(3, DecisionFormat.FindChosenIndex(options, new LegalOption("end_turn", Label: "anything")));
        Assert.Null(DecisionFormat.FindChosenIndex(options, new LegalOption("play_card", CombatCardIndex: 5, TargetCombatId: 9)));
        Assert.Null(DecisionFormat.FindChosenIndex(options, new LegalOption("use_potion", TargetCombatId: 2, PotionSlot: 1)));
    }

    [Fact]
    public void MapTravelRule_MirrorsTheMapScreen()
    {
        var start = (3, 0);
        var boss = (3, 16);
        IReadOnlyList<(int, int)>? Children((int Col, int Row) from) =>
            from == (6, 7) ? new[] { (6, 8) } : null;
        IReadOnlyList<(int, int)>? FreeTravel((int Col, int Row) from) =>
            new[] { (0, from.Row + 1), (3, from.Row + 1), (6, from.Row + 1) };

        // A new act (nothing visited): only the act's starting point, not row 1.
        Assert.Equal(new[] { start }, MapTravelRule.Travelable(Array.Empty<(int, int)>(), 16, start, boss, null, Children));
        // Ordinary travel: the game's travelable set (children).
        Assert.Equal(new[] { (6, 8) }, MapTravelRule.Travelable(new[] { start, (6, 7) }, 16, start, boss, null, Children));
        // Free travel (Winged Boots): whatever the game's rule returns, i.e. the whole next row.
        Assert.Equal(3, MapTravelRule.Travelable(new[] { start, (6, 7) }, 16, start, boss, null, FreeTravel).Count);
        // Last row leads to the boss; the boss leads to a second boss when there is one.
        Assert.Equal(new[] { boss }, MapTravelRule.Travelable(new[] { (2, 15) }, 16, start, boss, null, Children));
        Assert.Equal(new[] { (4, 17) }, MapTravelRule.Travelable(new[] { boss }, 16, start, boss, (4, 17), Children));
        // Unknown current point falls back to the start, like the screen does.
        Assert.Equal(new[] { start }, MapTravelRule.Travelable(new[] { (1, 5) }, 16, start, boss, null, Children));
    }

    [Fact]
    public void SegmentNames_RoundTripTheirAnchor()
    {
        Assert.Equal("s000301.jsonl", DecisionFormat.SegmentFileName(301));
        Assert.Equal(301, DecisionFormat.ParseSegmentAnchor("s000301.jsonl"));
        Assert.Equal(301, DecisionFormat.ParseSegmentAnchor("s000301.jsonl.gz"));
        Assert.Equal(301, DecisionFormat.ParseSegmentAnchor("s000301.1.jsonl.gz"));
        Assert.Equal(-1, DecisionFormat.ParseSegmentAnchor("header.json"));
    }

    [Fact]
    public void EventEnvelope_ExposesTheLinkedDecisionSeq()
    {
        var line = ReplayFormat.FormatLine(9, DateTime.UtcNow, ReplayFormat.EvAction, null,
            new Dictionary<string, object?> { ["dseq"] = 4L });
        Assert.True(ReplayFormat.TryParseEnvelope(line, out var e));
        Assert.Equal(4, e.DecisionSeq);
    }

    [Fact]
    public void CanonicalRecords_FollowTheEventBranchCut()
    {
        // Room entered (save 2), two decisions, death, death undo reloads save 2, one decision.
        var log = new[]
        {
            new ReplayLogEntry(1, ReplayFormat.EvSavePoint, "K1", null),
            new ReplayLogEntry(2, ReplayFormat.EvSavePoint, "K2", null),
            new ReplayLogEntry(3, ReplayFormat.EvAction, null, null, 1),
            new ReplayLogEntry(4, ReplayFormat.EvChoice, null, null, 2),
            new ReplayLogEntry(5, ReplayFormat.EvRunEnd, null, null),
            new ReplayLogEntry(6, ReplayFormat.EvLoad, "K2", null),
            new ReplayLogEntry(7, ReplayFormat.EvSavePoint, "K2", null),
            new ReplayLogEntry(8, ReplayFormat.EvAction, null, null, 3),
        };
        var records = new[] { (Dseq: 1L, EventSeq: 3L), (Dseq: 2L, EventSeq: 4L), (Dseq: 3L, EventSeq: 8L) };

        var timeline = ReplayFormat.BuildCanonicalTimeline(log);
        var kept = DecisionFormat.CanonicalRecords(timeline, records, r => r.EventSeq);

        Assert.Equal(new long[] { 3 }, kept.Select(r => r.Dseq));
    }

    [Fact]
    public void Session_DecisionSeqAndSegments_SurviveReopen_AndClosedSegmentsAreGzipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "spirelens-decisions-test-" + Guid.NewGuid().ToString("N"));
        var previous = ReplayStorage.RootDirProvider;
        ReplayStorage.RootDirProvider = () => root;
        try
        {
            string decisions = Path.Combine(root, "77", "decisions");
            using (var s = ReplaySession.Open(77, () => new ReplayHeader(), out _)!)
            {
                long save = s.Append(ReplayFormat.EvSavePoint, null, new Dictionary<string, object?> { ["key"] = "K1", ["snapshot"] = "snapshots/0000.json.gz" });
                s.OnSavePointAppended(save);
                long d1 = s.NextDecisionSeq();
                long e1 = s.Append(ReplayFormat.EvAction, null, new Dictionary<string, object?> { ["dseq"] = d1 });
                s.QueueDecisionRecord(() => $"{{\"dseq\":{d1},\"event_seq\":{e1}}}");

                long save2 = s.Append(ReplayFormat.EvSavePoint, null, new Dictionary<string, object?> { ["key"] = "K2", ["snapshot"] = "snapshots/0001.json.gz" });
                s.OnSavePointAppended(save2);
                long d2 = s.NextDecisionSeq();
                long e2 = s.Append(ReplayFormat.EvChoice, null, new Dictionary<string, object?> { ["dseq"] = d2 });
                s.QueueDecisionRecord(() => $"{{\"dseq\":{d2},\"event_seq\":{e2}}}");
                Assert.Equal(new long[] { 1, 2 }, new[] { d1, d2 });
            }
            Assert.True(ReplayStorage.WaitForPendingWrites(TimeSpan.FromSeconds(10)));

            // Segment 1 (anchor = first save point) was closed by the second save point.
            Assert.True(File.Exists(Path.Combine(decisions, "s000001.jsonl.gz")));
            Assert.False(File.Exists(Path.Combine(decisions, "s000001.jsonl")));
            using (var gz = new GZipStream(File.OpenRead(Path.Combine(decisions, "s000001.jsonl.gz")), CompressionMode.Decompress))
            using (var reader = new StreamReader(gz))
            {
                Assert.True(DecisionFormat.TryParseLink(reader.ReadLine()!, out long dseq, out long eventSeq));
                Assert.Equal((1L, 2L), (dseq, eventSeq));
            }
            Assert.True(File.Exists(Path.Combine(decisions, "s000003.jsonl")));

            using (var s = ReplaySession.Open(77, null, out _)!)
            {
                Assert.Equal(3, s.LastSavePointSeq);
                Assert.Equal(3, s.NextDecisionSeq()); // recovered from the events.jsonl link
                s.CloseDecisionSegment();
            }
            Assert.True(ReplayStorage.WaitForPendingWrites(TimeSpan.FromSeconds(10)));
            Assert.True(File.Exists(Path.Combine(decisions, "s000003.jsonl.gz")));
        }
        finally
        {
            ReplayStorage.RootDirProvider = previous;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
