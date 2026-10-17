// Package replayformat is the Go port of the pure, game-independent parts of
// the SpireLens run-recording format (docs/run-recording.md):
//
//   - the events.jsonl envelope parse (ReplayFormat.TryParseEnvelope),
//   - the save-point key (ReplayFormat.BuildSavePointKey),
//   - the canonical-timeline cut (ReplayFormat.BuildCanonicalTimeline),
//   - the decision-segment file naming (DecisionFormat.ParseSegmentAnchor),
//   - the decision-record branch cut (DecisionFormat.CanonicalRecords).
//
// Core/Replay/ReplayFormat.cs and Core/Replay/DecisionFormat.cs are the
// source of truth. This port must stay behaviorally identical to them; the
// tests in this package mirror Tests/SpireLens.Core.Tests/RunRecordingFormatTests.cs
// and DecisionRecordFormatTests.cs case for case.
package replayformat

import (
	"bytes"
	"encoding/json"
	"fmt"
	"sort"
	"strconv"
)

// FormatVersion is header.json / events.jsonl's format_version this port reads.
const FormatVersion = 1

// DecisionsFormatVersion is the per-record decisions/*.jsonl format_version.
const DecisionsFormatVersion = 1

// Event type names (ReplayFormat.Ev*).
const (
	EvRecordingStarted = "recording_started"
	EvRunStarted       = "run_started"
	EvCoreReloaded     = "core_reloaded"
	EvSavePoint        = "save_point"
	EvLoad             = "load"
	EvAction           = "action"
	EvHookAction       = "hook_action"
	EvActionResumed    = "action_resumed"
	EvActionResult     = "action_result"
	EvChoice           = "choice"
	EvChecksum         = "checksum"
	EvDecision         = "decision"
	EvCombatReplayFile = "combat_replay_file"
	EvRunEnd           = "run_end"
)

// Load reasons (ReplayFormat.LoadReason*).
const (
	LoadReasonContinue    = "continue"
	LoadReasonRoomRestart = "room_restart"
	LoadReasonDeathUndo   = "death_undo"
)

// LogEntry is ReplayLogEntry: the envelope fields the timeline cut needs, plus
// a few extra payload fields the database importer reads off the same line
// (they do not take part in the cut).
type LogEntry struct {
	Seq      int64
	Type     string
	Key      *string // data.key, when a string
	Snapshot *string // data.snapshot, when a string
	DSeq     *int64  // data.dseq, the linked decision record

	// Importer extras, not part of ReplayLogEntry.
	T        string          // envelope "t"
	Floor    *int64          // envelope "floor"
	Act      *int64          // envelope "act"
	InCombat *bool           // envelope "in_combat"
	Data     json.RawMessage // the raw data object (nil when absent or not an object)
}

// KeyOrEmpty returns Key, or "" when it is nil.
func (e LogEntry) KeyOrEmpty() string {
	if e.Key == nil {
		return ""
	}
	return *e.Key
}

// ParseEnvelope parses one events.jsonl line the way
// ReplayFormat.TryParseEnvelope does. ok is false for blank, torn or non-JSON
// lines, a non-object root, a missing or non-integer seq, or a missing or
// non-string type: a crash can leave a partial last line and readers skip it.
func ParseEnvelope(line []byte) (LogEntry, bool) {
	var entry LogEntry
	trimmed := bytes.TrimSpace(line)
	if len(trimmed) == 0 {
		return entry, false
	}
	var root map[string]json.RawMessage
	if err := json.Unmarshal(trimmed, &root); err != nil || root == nil {
		// Unmarshal into a map fails for any non-object root ("null" yields a nil map).
		return entry, false
	}
	seq, ok := rawInt64(root["seq"])
	if !ok {
		return entry, false
	}
	typ, ok := rawString(root["type"])
	if !ok {
		return entry, false
	}
	entry.Seq = seq
	entry.Type = typ
	if t, ok := rawString(root["t"]); ok {
		entry.T = t
	}
	if v, ok := rawInt64(root["floor"]); ok {
		entry.Floor = &v
	}
	if v, ok := rawInt64(root["act"]); ok {
		entry.Act = &v
	}
	if v, ok := rawBool(root["in_combat"]); ok {
		entry.InCombat = &v
	}
	if raw, present := root["data"]; present {
		var data map[string]json.RawMessage
		if json.Unmarshal(raw, &data) == nil && data != nil {
			entry.Data = raw
			if s, ok := rawString(data["key"]); ok {
				entry.Key = &s
			}
			if s, ok := rawString(data["snapshot"]); ok {
				entry.Snapshot = &s
			}
			if d, ok := rawInt64(data["dseq"]); ok {
				entry.DSeq = &d
			}
		}
	}
	return entry, true
}

// rawInt64 mirrors JsonElement.TryGetInt64: a JSON number token written as a
// plain integer that fits in 64 bits. Fractions and exponents ("4.0", "4e0")
// are rejected, as Utf8Parser does for the whole token.
func rawInt64(raw json.RawMessage) (int64, bool) {
	s := string(bytes.TrimSpace(raw))
	if s == "" || !(s[0] == '-' || (s[0] >= '0' && s[0] <= '9')) {
		return 0, false
	}
	v, err := strconv.ParseInt(s, 10, 64)
	if err != nil {
		return 0, false
	}
	return v, true
}

func rawString(raw json.RawMessage) (string, bool) {
	if len(raw) == 0 {
		return "", false
	}
	var s string
	if raw[0] != '"' || json.Unmarshal(raw, &s) != nil {
		return "", false
	}
	return s, true
}

func rawBool(raw json.RawMessage) (bool, bool) {
	var b bool
	if len(raw) == 0 || json.Unmarshal(raw, &b) != nil {
		return false, false
	}
	return b, true
}

// RngPosition is the position of one named RNG stream inside a save.
type RngPosition struct {
	Name    string
	Counter int32
	State0  uint64
}

// BuildSavePointKey is ReplayFormat.BuildSavePointKey: the save's place in the
// run plus an FNV-1a 64 fingerprint of every RNG stream's (name, counter, s0),
// sorted by name ordinally. coordCol/coordRow nil means no coord.
func BuildSavePointKey(actIndex int, coordCol, coordRow *int, floor int, preFinishedRoomType *string, rngs []RngPosition) string {
	sorted := append([]RngPosition(nil), rngs...)
	// C# uses OrderBy (stable) with StringComparer.Ordinal. Ordinal compares
	// UTF-16 code units; byte order of UTF-8 agrees for every non-surrogate
	// character, and RNG stream names are ASCII.
	sort.SliceStable(sorted, func(i, j int) bool { return sorted[i].Name < sorted[j].Name })

	hash := uint64(14695981039346656037)
	for _, r := range sorted {
		hash = fnv(hash, r.Name)
		hash = fnv(hash, strconv.FormatInt(int64(r.Counter), 10))
		hash = fnv(hash, strconv.FormatUint(r.State0, 10))
	}
	coord := "none"
	if coordCol != nil && coordRow != nil {
		coord = fmt.Sprintf("%d,%d", *coordCol, *coordRow)
	}
	room := "-"
	if preFinishedRoomType != nil {
		room = *preFinishedRoomType
	}
	return fmt.Sprintf("a%d/c%s/f%d/p%s/r%016x", actIndex, coord, floor, room, hash)
}

func fnv(hash uint64, s string) uint64 {
	for _, b := range []byte(s) {
		hash ^= uint64(b)
		hash *= 1099511628211
	}
	// Field separator so ("ab","c") and ("a","bc") differ.
	hash ^= 0xff
	hash *= 1099511628211
	return hash
}

// Branch is ReplayBranch: events cut by a load. AnchorSeq is the save point
// the game went back to (nil for an unanchored load), LoadSeq the load that
// cut it, AbandonedSeqs the cut events in path order.
type Branch struct {
	AnchorSeq     *int64
	LoadSeq       int64
	AbandonedSeqs []int64
}

// Timeline is ReplayTimeline.
type Timeline struct {
	Canonical       []LogEntry
	Branches        []Branch
	UnanchoredLoads []int64
}

// CanonicalSeqs is the set of seqs on the canonical path.
func (t Timeline) CanonicalSeqs() map[int64]struct{} {
	set := make(map[int64]struct{}, len(t.Canonical))
	for _, e := range t.Canonical {
		set[e.Seq] = struct{}{}
	}
	return set
}

// BuildCanonicalTimeline is ReplayFormat.BuildCanonicalTimeline. The game only
// ever resumes from a save, so load(K) means everything recorded after the
// most recent save_point(K) on the current path did not happen in the
// surviving timeline: it is cut (kept as a Branch) and the path continues from
// that save point. A load whose key never appeared on the path starts a new
// unanchored root. The load itself always joins the path.
func BuildCanonicalTimeline(log []LogEntry) Timeline {
	ordered := append([]LogEntry(nil), log...)
	sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].Seq < ordered[j].Seq })

	path := make([]LogEntry, 0, len(ordered))
	var branches []Branch
	var unanchored []int64

	for _, entry := range ordered {
		if entry.Type == EvLoad {
			anchor := -1
			if entry.Key != nil {
				for i := len(path) - 1; i >= 0; i-- {
					if path[i].Type == EvSavePoint && path[i].Key != nil && *path[i].Key == *entry.Key {
						anchor = i
						break
					}
				}
			}
			if anchor >= 0 {
				cutFrom := anchor + 1
				if cutFrom < len(path) {
					anchorSeq := path[anchor].Seq
					branches = append(branches, Branch{
						AnchorSeq:     &anchorSeq,
						LoadSeq:       entry.Seq,
						AbandonedSeqs: seqsOf(path[cutFrom:]),
					})
					path = path[:cutFrom]
				}
			} else {
				unanchored = append(unanchored, entry.Seq)
				if len(path) > 0 {
					branches = append(branches, Branch{
						AnchorSeq:     nil,
						LoadSeq:       entry.Seq,
						AbandonedSeqs: seqsOf(path),
					})
					path = path[:0]
				}
			}
		}
		path = append(path, entry)
	}

	return Timeline{
		Canonical:       append([]LogEntry(nil), path...),
		Branches:        branches,
		UnanchoredLoads: unanchored,
	}
}

func seqsOf(entries []LogEntry) []int64 {
	out := make([]int64, len(entries))
	for i, e := range entries {
		out[i] = e.Seq
	}
	return out
}

// CanonicalRecords is DecisionFormat.CanonicalRecords: the records whose
// linked event (event_seq) is on the canonical path, in input order.
func CanonicalRecords[T any](timeline Timeline, records []T, eventSeqOf func(T) int64) []T {
	canonical := timeline.CanonicalSeqs()
	var out []T
	for _, r := range records {
		if _, ok := canonical[eventSeqOf(r)]; ok {
			out = append(out, r)
		}
	}
	return out
}

// SegmentFileName is DecisionFormat.SegmentFileName.
func SegmentFileName(anchorSaveSeq int64) string {
	return fmt.Sprintf("s%06d.jsonl", anchorSaveSeq)
}

// ParseSegmentAnchor is DecisionFormat.ParseSegmentAnchor: the anchor seq in a
// decision segment file name (plain or gzipped, any part suffix), or -1.
func ParseSegmentAnchor(fileName string) int64 {
	if fileName == "" || fileName[0] != 's' {
		return -1
	}
	end := 1
	for end < len(fileName) && fileName[end] >= '0' && fileName[end] <= '9' {
		end++
	}
	if end == 1 {
		return -1
	}
	n, err := strconv.ParseInt(fileName[1:end], 10, 64)
	if err != nil {
		return -1
	}
	return n
}
