package replayformat

import (
	"reflect"
	"strings"
	"testing"
)

// These cases mirror Tests/SpireLens.Core.Tests/RunRecordingFormatTests.cs and
// DecisionRecordFormatTests.cs one for one, so the Go port and the C# source
// of truth are pinned to the same behavior. Session/file tests (ReplaySession)
// are writer-side and have no Go counterpart.

func e(seq int64, typ string, key ...string) LogEntry {
	entry := LogEntry{Seq: seq, Type: typ}
	if len(key) > 0 {
		k := key[0]
		entry.Key = &k
	}
	return entry
}

func withDseq(entry LogEntry, dseq int64) LogEntry {
	entry.DSeq = &dseq
	return entry
}

func canonicalSeqs(t Timeline) []int64 {
	return seqsOf(t.Canonical)
}

// FormatLine_WritesEnvelopeContextAndData_AndParsesBack: the line below is
// what ReplayFormat.FormatLine writes for that test's inputs (context cannot
// override the envelope seq).
func TestParseEnvelope_ReadsEnvelopeContextAndData(t *testing.T) {
	line := `{"seq":7,"t":"2026-10-12T01:02:03.456Z","type":"save_point","floor":3,"data":{"key":"a0/c1,2/f3/p-/r00","snapshot":"snapshots/0002.json.gz"}}`
	entry, ok := ParseEnvelope([]byte(line))
	if !ok {
		t.Fatal("expected the line to parse")
	}
	if entry.Seq != 7 || entry.Type != EvSavePoint {
		t.Fatalf("got seq=%d type=%q", entry.Seq, entry.Type)
	}
	if entry.KeyOrEmpty() != "a0/c1,2/f3/p-/r00" {
		t.Fatalf("key = %q", entry.KeyOrEmpty())
	}
	if entry.Snapshot == nil || *entry.Snapshot != "snapshots/0002.json.gz" {
		t.Fatalf("snapshot = %v", entry.Snapshot)
	}
	if entry.T != "2026-10-12T01:02:03.456Z" || entry.Floor == nil || *entry.Floor != 3 {
		t.Fatalf("context not read: t=%q floor=%v", entry.T, entry.Floor)
	}
}

// TryParseEnvelope_RejectsBlankTornAndIncompleteLines.
func TestParseEnvelope_RejectsBlankTornAndIncompleteLines(t *testing.T) {
	for _, line := range []string{
		"",
		`{"seq":3,"ty`, // torn last line after a crash
		"not json",
		`{"type":"action"}`, // no seq
	} {
		if _, ok := ParseEnvelope([]byte(line)); ok {
			t.Errorf("expected %q to be rejected", line)
		}
	}
}

// SavePointKey_IgnoresRngOrder_ButSeesAnyCounterChange.
func TestSavePointKey_IgnoresRngOrder_ButSeesAnyCounterChange(t *testing.T) {
	a := []RngPosition{{"run.Shuffle", 4, 11}, {"run.UpFront", 9, 22}}
	b := []RngPosition{a[1], a[0]}
	moved := []RngPosition{{"run.Shuffle", 5, 11}, {"run.UpFront", 9, 22}}
	col, row := 3, 2
	monster := "Monster"

	ka := BuildSavePointKey(0, &col, &row, 4, nil, a)
	if ka != BuildSavePointKey(0, &col, &row, 4, nil, b) {
		t.Error("key depends on RNG order")
	}
	if ka == BuildSavePointKey(0, &col, &row, 4, nil, moved) {
		t.Error("key ignores a counter change")
	}
	if ka == BuildSavePointKey(0, &col, &row, 4, &monster, a) {
		t.Error("key ignores the pre-finished room")
	}
	if !strings.HasPrefix(ka, "a0/c3,2/f4/p-/r") {
		t.Errorf("unexpected key shape %q", ka)
	}
	if k := BuildSavePointKey(1, nil, nil, 0, nil, a); !strings.HasPrefix(k, "a1/cnone/f0/p-/r") {
		t.Errorf("unexpected coordless key %q", k)
	}
}

// Timeline_LinearLogIsCanonicalAsIs.
func TestTimeline_LinearLogIsCanonicalAsIs(t *testing.T) {
	log := []LogEntry{
		e(1, EvRunStarted),
		e(2, EvSavePoint, "K1"),
		e(3, EvAction),
		e(4, EvSavePoint, "K2"),
		e(5, EvRunEnd),
	}
	tl := BuildCanonicalTimeline(log)
	if got := canonicalSeqs(tl); !reflect.DeepEqual(got, []int64{1, 2, 3, 4, 5}) {
		t.Fatalf("canonical = %v", got)
	}
	if len(tl.Branches) != 0 || len(tl.UnanchoredLoads) != 0 {
		t.Fatalf("unexpected branches %v / unanchored %v", tl.Branches, tl.UnanchoredLoads)
	}
}

// Timeline_LoadCutsBackToMatchingSavePoint_AndKeepsTheBranch.
func TestTimeline_LoadCutsBackToMatchingSavePoint_AndKeepsTheBranch(t *testing.T) {
	// Enter room (K2), act, die, death undo reloads K2, act again, win.
	log := []LogEntry{
		e(1, EvSavePoint, "K1"),
		e(2, EvAction),
		e(3, EvSavePoint, "K2"),
		e(4, EvAction),
		e(5, EvRunEnd),
		e(6, EvLoad, "K2"),
		e(7, EvSavePoint, "K2"), // IncrementNumReloads re-save
		e(8, EvAction),
		e(9, EvRunEnd),
	}
	tl := BuildCanonicalTimeline(log)
	if got := canonicalSeqs(tl); !reflect.DeepEqual(got, []int64{1, 2, 3, 6, 7, 8, 9}) {
		t.Fatalf("canonical = %v", got)
	}
	if len(tl.Branches) != 1 {
		t.Fatalf("branches = %v", tl.Branches)
	}
	br := tl.Branches[0]
	if br.AnchorSeq == nil || *br.AnchorSeq != 3 || br.LoadSeq != 6 || !reflect.DeepEqual(br.AbandonedSeqs, []int64{4, 5}) {
		t.Fatalf("branch = %+v", br)
	}
}

// Timeline_RepeatedRestartsOfOneRoomEachCutToTheLatestSavePoint.
func TestTimeline_RepeatedRestartsOfOneRoomEachCutToTheLatestSavePoint(t *testing.T) {
	log := []LogEntry{
		e(1, EvSavePoint, "K1"),
		e(2, EvAction),
		e(3, EvLoad, "K1"),
		e(4, EvSavePoint, "K1"),
		e(5, EvAction),
		e(6, EvLoad, "K1"),
		e(7, EvSavePoint, "K1"),
		e(8, EvAction),
	}
	tl := BuildCanonicalTimeline(log)
	if got := canonicalSeqs(tl); !reflect.DeepEqual(got, []int64{1, 3, 4, 6, 7, 8}) {
		t.Fatalf("canonical = %v", got)
	}
	if len(tl.Branches) != 2 {
		t.Fatalf("branches = %v", tl.Branches)
	}
	if !reflect.DeepEqual(tl.Branches[0].AbandonedSeqs, []int64{2}) || !reflect.DeepEqual(tl.Branches[1].AbandonedSeqs, []int64{5}) {
		t.Fatalf("branches = %+v", tl.Branches)
	}
}

// Timeline_ContinueImmediatelyAfterSave_CutsNothing.
func TestTimeline_ContinueImmediatelyAfterSave_CutsNothing(t *testing.T) {
	log := []LogEntry{
		e(1, EvSavePoint, "K1"),
		e(2, EvLoad, "K1"),
		e(3, EvAction),
	}
	tl := BuildCanonicalTimeline(log)
	if got := canonicalSeqs(tl); !reflect.DeepEqual(got, []int64{1, 2, 3}) {
		t.Fatalf("canonical = %v", got)
	}
	if len(tl.Branches) != 0 {
		t.Fatalf("branches = %v", tl.Branches)
	}
}

// Timeline_UnknownLoadKeyStartsAnUnanchoredRoot.
func TestTimeline_UnknownLoadKeyStartsAnUnanchoredRoot(t *testing.T) {
	log := []LogEntry{
		e(1, EvSavePoint, "K1"),
		e(2, EvAction),
		e(3, EvLoad, "UNSEEN"),
		e(4, EvAction),
	}
	tl := BuildCanonicalTimeline(log)
	if got := canonicalSeqs(tl); !reflect.DeepEqual(got, []int64{3, 4}) {
		t.Fatalf("canonical = %v", got)
	}
	if !reflect.DeepEqual(tl.UnanchoredLoads, []int64{3}) {
		t.Fatalf("unanchored = %v", tl.UnanchoredLoads)
	}
	if len(tl.Branches) != 1 || tl.Branches[0].AnchorSeq != nil || !reflect.DeepEqual(tl.Branches[0].AbandonedSeqs, []int64{1, 2}) {
		t.Fatalf("branches = %+v", tl.Branches)
	}
}

// The C# cut orders by seq first; a log read out of order must give the same answer.
func TestTimeline_OrdersBySeqBeforeCutting(t *testing.T) {
	log := []LogEntry{
		e(3, EvLoad, "K1"),
		e(1, EvSavePoint, "K1"),
		e(2, EvAction),
	}
	tl := BuildCanonicalTimeline(log)
	if got := canonicalSeqs(tl); !reflect.DeepEqual(got, []int64{1, 3}) {
		t.Fatalf("canonical = %v", got)
	}
}

// SegmentNames_RoundTripTheirAnchor.
func TestSegmentNames_RoundTripTheirAnchor(t *testing.T) {
	if got := SegmentFileName(301); got != "s000301.jsonl" {
		t.Fatalf("SegmentFileName = %q", got)
	}
	for name, want := range map[string]int64{
		"s000301.jsonl":      301,
		"s000301.jsonl.gz":   301,
		"s000301.1.jsonl.gz": 301,
		"header.json":        -1,
		"s.jsonl":            -1,
		"":                   -1,
	} {
		if got := ParseSegmentAnchor(name); got != want {
			t.Errorf("ParseSegmentAnchor(%q) = %d, want %d", name, got, want)
		}
	}
}

// EventEnvelope_ExposesTheLinkedDecisionSeq.
func TestEventEnvelope_ExposesTheLinkedDecisionSeq(t *testing.T) {
	entry, ok := ParseEnvelope([]byte(`{"seq":9,"t":"2026-10-12T00:00:00.000Z","type":"action","data":{"dseq":4}}`))
	if !ok || entry.DSeq == nil || *entry.DSeq != 4 {
		t.Fatalf("dseq not exposed: ok=%v entry=%+v", ok, entry)
	}
}

// CanonicalRecords_FollowTheEventBranchCut.
func TestCanonicalRecords_FollowTheEventBranchCut(t *testing.T) {
	// Room entered (save 2), two decisions, death, death undo reloads save 2, one decision.
	log := []LogEntry{
		e(1, EvSavePoint, "K1"),
		e(2, EvSavePoint, "K2"),
		withDseq(e(3, EvAction), 1),
		withDseq(e(4, EvChoice), 2),
		e(5, EvRunEnd),
		e(6, EvLoad, "K2"),
		e(7, EvSavePoint, "K2"),
		withDseq(e(8, EvAction), 3),
	}
	type rec struct{ dseq, eventSeq int64 }
	records := []rec{{1, 3}, {2, 4}, {3, 8}}

	kept := CanonicalRecords(BuildCanonicalTimeline(log), records, func(r rec) int64 { return r.eventSeq })
	if len(kept) != 1 || kept[0].dseq != 3 {
		t.Fatalf("kept = %+v", kept)
	}
}
