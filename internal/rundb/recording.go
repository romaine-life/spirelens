package rundb

import (
	"bufio"
	"compress/gzip"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"github.com/romaine-life/spirelens/internal/replayformat"
)

// header.json (ReplayHeader in Core/Replay/ReplayStorage.cs).
type recordingHeader struct {
	FormatVersion          *int64  `json:"format_version"`
	DecisionsFormatVersion *int64  `json:"decisions_format_version"`
	GameStartTime          *int64  `json:"game_start_time"`
	SpireLensRunID         *string `json:"spirelens_run_id"`
	CreatedAtUTC           *string `json:"created_at_utc"`
	UpdatedAtUTC           *string `json:"updated_at_utc"`
	RecordingStartedMidRun *bool   `json:"recording_started_mid_run"`
	Game                   *struct {
		Version          *string     `json:"version"`
		Commit           *string     `json:"commit"`
		ReleaseDate      *string     `json:"release_date"`
		MainAssemblyHash json.Number `json:"main_assembly_hash"`
		ModelIDHash      json.Number `json:"model_id_hash"`
	} `json:"game"`
	Mods               json.RawMessage `json:"mods"`
	SpireLensVersion   *string         `json:"spirelens_version"`
	SpireLensCoreBuild *string         `json:"spirelens_core_build"`
	NetType            *string         `json:"net_type"`
	SeedString         *string         `json:"seed_string"`
	SeedHash           json.Number     `json:"seed_hash"`
	Players            []struct {
		NetID     json.Number `json:"net_id"`
		Character *string     `json:"character"`
	} `json:"players"`
	PlayersRaw   json.RawMessage `json:"-"`
	Ascension    *int64          `json:"ascension"`
	GameMode     *string         `json:"game_mode"`
	Modifiers    json.RawMessage `json:"modifiers"`
	DailyTimeUTC *string         `json:"daily_time_utc"`
	Outcome      *string         `json:"outcome"`
	EndedAtUTC   *string         `json:"ended_at_utc"`
	FinalFloor   *int64          `json:"final_floor"`
}

// One decisions/*.jsonl record (DecisionFormat.FormatRecord). The big
// payloads stay raw and are stored as JSON text.
type decisionRecord struct {
	FormatVersion *int64      `json:"format_version"`
	DSeq          *int64      `json:"dseq"`
	EventSeq      *int64      `json:"event_seq"`
	EventType     *string     `json:"event_type"`
	AnchorSaveSeq *int64      `json:"anchor_save_seq"`
	T             *string     `json:"t"`
	Kind          *string     `json:"kind"`
	Phase         *string     `json:"phase"`
	Player        json.Number `json:"player"`
	Context       *struct {
		Floor    *int64      `json:"floor"`
		Act      *int64      `json:"act"`
		Coord    *string     `json:"coord"`
		InCombat *bool       `json:"in_combat"`
		RunTime  json.Number `json:"run_time"`
	} `json:"context"`
	Chosen          json.RawMessage   `json:"chosen"`
	ChosenIndex     *int64            `json:"chosen_index"`
	ChosenIndexes   json.RawMessage   `json:"chosen_indexes"`
	Options         []json.RawMessage `json:"options"`
	OptionsComplete *bool             `json:"options_complete"`
	Selection       json.RawMessage   `json:"selection"`
	State           json.RawMessage   `json:"state"`
	Hidden          json.RawMessage   `json:"hidden"`
	NetState        *struct {
		Form *string `json:"form"`
	} `json:"net_state"`
	Notes json.RawMessage `json:"notes"`
}

type eventPayload struct {
	Outcome  *string `json:"outcome"`
	Reason   *string `json:"reason"`
	Reused   *bool   `json:"reused"`
	Floor    *int64  `json:"floor"`
	Act      *int64  `json:"act"`
	File     *string `json:"file"`
	Bytes    *int64  `json:"bytes"`
	Snapshot *string `json:"snapshot"`
}

// dbError marks a database failure inside a line callback, so it aborts the
// ingest instead of being treated like a truncated file.
type dbError struct{ err error }

func (e dbError) Error() string { return e.err.Error() }
func (e dbError) Unwrap() error { return e.err }

func payloadOf(e replayformat.LogEntry) eventPayload {
	var p eventPayload
	if len(e.Data) > 0 {
		_ = json.Unmarshal(e.Data, &p) // lenient: a mistyped field just stays nil
	}
	return p
}

// keepsData lists the event types whose payload the importer reads; the
// rest drop their data right after the envelope parse to bound memory.
var keepsData = map[string]bool{
	replayformat.EvSavePoint:        true,
	replayformat.EvLoad:             true,
	replayformat.EvCombatReplayFile: true,
	replayformat.EvRunEnd:           true,
}

type segmentFile struct {
	name   string
	anchor int64
	part   int64 // 0 for s<anchor>.jsonl[.gz], n for s<anchor>.<n>.jsonl.gz
	plain  bool
}

// parseSegmentPart reads the reopened-part number of "s000301.2.jsonl.gz".
func parseSegmentPart(name string) int64 {
	rest := strings.TrimPrefix(name, "s")
	parts := strings.Split(rest, ".")
	if len(parts) >= 3 {
		if n, err := strconv.ParseInt(parts[1], 10, 64); err == nil {
			return n
		}
	}
	return 0
}

func listSegments(dir string) ([]segmentFile, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, nil
		}
		return nil, err
	}
	var segs []segmentFile
	for _, ent := range entries {
		name := ent.Name()
		if ent.IsDir() || !(strings.HasSuffix(name, ".jsonl") || strings.HasSuffix(name, ".jsonl.gz")) {
			continue
		}
		anchor := replayformat.ParseSegmentAnchor(name)
		if anchor < 0 {
			continue
		}
		segs = append(segs, segmentFile{name: name, anchor: anchor, part: parseSegmentPart(name), plain: strings.HasSuffix(name, ".jsonl")})
	}
	// Anchor order, then closed parts in part order, then the still-open
	// plain segment (the newest part of its anchor).
	sort.Slice(segs, func(i, j int) bool {
		a, b := segs[i], segs[j]
		if a.anchor != b.anchor {
			return a.anchor < b.anchor
		}
		if a.plain != b.plain {
			return !a.plain
		}
		if a.part != b.part {
			return a.part < b.part
		}
		return a.name < b.name
	})
	return segs, nil
}

// recordingManifest fingerprints a recording directory by (path, size,
// mtime) of every file in it, so an unchanged directory is not re-read.
func recordingManifest(dir string) (fingerprint string, totalSize, maxMtime int64, err error) {
	var lines []string
	walkErr := filepath.WalkDir(dir, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		info, err := d.Info()
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(dir, path)
		lines = append(lines, fmt.Sprintf("%s\t%d\t%d", filepath.ToSlash(rel), info.Size(), info.ModTime().UnixNano()))
		totalSize += info.Size()
		if m := info.ModTime().UnixNano(); m > maxMtime {
			maxMtime = m
		}
		return nil
	})
	if walkErr != nil {
		return "", 0, 0, walkErr
	}
	sort.Strings(lines)
	return sha256Hex([]byte(strings.Join(lines, "\n"))), totalSize, maxMtime, nil
}

func (in *ingester) ingestRecordings(ctx context.Context, root string, rep *SourceReport) error {
	prior, err := in.loadSources(ctx, "recording")
	if err != nil {
		return err
	}
	entries, err := os.ReadDir(root)
	if err != nil {
		in.warnf("recordings: cannot list %s: %v", root, err)
		return nil
	}
	for _, ent := range entries {
		if !ent.IsDir() {
			continue
		}
		st, err := strconv.ParseInt(ent.Name(), 10, 64)
		if err != nil {
			rep.note("ignored_non_run_dirs", 1)
			continue
		}
		rep.Seen++
		dir := filepath.Join(root, ent.Name())
		fp, size, mtime, err := recordingManifest(dir)
		if err != nil {
			in.warnf("recordings: cannot scan %s: %v", dir, err)
			rep.Failed++
			continue
		}
		if p, ok := prior[dir]; ok && !in.opts.Force && p.fingerprint == fp && p.status == "ok" {
			rep.Unchanged++
			continue
		}
		if err := in.ingestRecording(ctx, st, dir, fp, size, mtime, rep); err != nil {
			return fmt.Errorf("%s: %w", dir, err)
		}
		rep.Ingested++
	}
	return nil
}

// readLines streams newline-separated lines of r to fn without a line length
// limit (decision records run to tens of KB). A final line without a
// newline is still delivered; fn decides whether it parses.
func readLines(r io.Reader, fn func(line []byte) error) error {
	br := bufio.NewReaderSize(r, 256*1024)
	for {
		line, err := br.ReadBytes('\n')
		if len(line) > 0 {
			if ferr := fn(line); ferr != nil {
				return ferr
			}
		}
		if err == io.EOF {
			return nil
		}
		if err != nil {
			return err
		}
	}
}

func (in *ingester) ingestRecording(ctx context.Context, st int64, dir, fingerprint string, size, mtime int64, rep *SourceReport) error {
	// ---- header
	var header recordingHeader
	headerOK := false
	var headerRaw []byte
	if b, err := os.ReadFile(filepath.Join(dir, "header.json")); err != nil {
		in.warnf("recordings: %s: no readable header.json: %v", dir, err)
		rep.note("missing_header", 1)
	} else if err := json.Unmarshal(b, &header); err != nil {
		in.warnf("recordings: %s: malformed header.json: %v", dir, err)
		rep.note("malformed_header", 1)
	} else {
		headerOK = true
		headerRaw = b
		if header.GameStartTime != nil && *header.GameStartTime != st {
			in.warnf("recordings: %s: header game_start_time %d disagrees with the directory name; keyed by the directory", dir, *header.GameStartTime)
		}
		var players struct {
			Players json.RawMessage `json:"players"`
		}
		_ = json.Unmarshal(b, &players)
		header.PlayersRaw = players.Players
	}

	// ---- events.jsonl: envelope only, streamed
	var log []replayformat.LogEntry
	torn := 0
	if f, err := os.Open(filepath.Join(dir, "events.jsonl")); err != nil {
		in.warnf("recordings: %s: no readable events.jsonl: %v", dir, err)
		rep.note("missing_events", 1)
	} else {
		err := readLines(f, func(line []byte) error {
			entry, ok := replayformat.ParseEnvelope(line)
			if !ok {
				if len(strings.TrimSpace(string(line))) > 0 {
					torn++
				}
				return nil
			}
			if !keepsData[entry.Type] {
				entry.Data = nil
			}
			log = append(log, entry)
			return nil
		})
		f.Close()
		if err != nil {
			in.warnf("recordings: %s: events.jsonl read stopped early: %v", dir, err)
		}
	}
	if torn > 0 {
		rep.note("torn_event_lines", torn)
		in.warnf("recordings: %s: skipped %d unparsable events.jsonl line(s)", dir, torn)
	}

	timeline := replayformat.BuildCanonicalTimeline(log)
	canonical := timeline.CanonicalSeqs()
	bySeq := make(map[int64]replayformat.LogEntry, len(log))
	for _, e := range log {
		if _, dup := bySeq[e.Seq]; !dup {
			bySeq[e.Seq] = e
		}
	}

	// Canonical outcome: the last run_end on the canonical path.
	var canonicalOutcome *string
	var maxFloor *int64
	for _, e := range timeline.Canonical {
		if e.Type == replayformat.EvRunEnd {
			if p := payloadOf(e); p.Outcome != nil {
				o := *p.Outcome
				canonicalOutcome = &o
			}
		}
		if e.Floor != nil && (maxFloor == nil || *e.Floor > *maxFloor) {
			f := *e.Floor
			maxFloor = &f
		}
	}

	typeTotals := map[string][2]int{}
	for _, e := range log {
		c := typeTotals[e.Type]
		c[0]++
		if _, ok := canonical[e.Seq]; ok {
			c[1]++
		}
		typeTotals[e.Type] = c
	}

	// ---- count snapshots / combats
	countFiles := func(sub string) int {
		entries, err := os.ReadDir(filepath.Join(dir, sub))
		if err != nil {
			return 0
		}
		n := 0
		for _, e := range entries {
			if !e.IsDir() {
				n++
			}
		}
		return n
	}

	segments, err := listSegments(filepath.Join(dir, "decisions"))
	if err != nil {
		in.warnf("recordings: %s: cannot list decisions: %v", dir, err)
	}

	tx, err := in.db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	for _, table := range []string{"recordings", "recording_event_counts", "recording_branches", "recording_save_points", "recording_combats", "decisions"} {
		if _, err := tx.ExecContext(ctx, "DELETE FROM "+table+" WHERE game_start_time = ?", st); err != nil {
			return err
		}
	}

	// ---- decisions, streamed segment by segment
	decisionStmt, err := tx.PrepareContext(ctx, `
		INSERT OR REPLACE INTO decisions (game_start_time, dseq, event_seq, event_type, anchor_save_seq, segment_file,
			format_version, t, kind, phase, player, floor, act, coord, in_combat, run_time, canonical, event_found,
			chosen_index, chosen_indexes_json, option_count, options_complete, chosen_json, options_json,
			selection_json, state_json, hidden_json, notes_json, net_state_form)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
	if err != nil {
		return err
	}
	defer decisionStmt.Close()

	seenDseq := map[int64]bool{}
	tornDecisions := 0
	for _, seg := range segments {
		path := filepath.Join(dir, "decisions", seg.name)
		f, err := os.Open(path)
		if err != nil {
			in.warnf("recordings: cannot open %s: %v", path, err)
			rep.note("unreadable_decision_segments", 1)
			continue
		}
		var r io.Reader = f
		var gz *gzip.Reader
		if !seg.plain {
			gz, err = gzip.NewReader(f)
			if err != nil {
				f.Close()
				in.warnf("recordings: %s is not a readable gzip: %v", path, err)
				rep.note("unreadable_decision_segments", 1)
				continue
			}
			r = gz
		}
		readErr := readLines(r, func(line []byte) error {
			if len(strings.TrimSpace(string(line))) == 0 {
				return nil
			}
			var rec decisionRecord
			if err := json.Unmarshal(line, &rec); err != nil || rec.DSeq == nil {
				tornDecisions++
				return nil
			}
			dseq := *rec.DSeq
			if seenDseq[dseq] {
				// A later part of the same dseq replaces the earlier row.
				rep.note("duplicate_decision_records", 1)
			}
			seenDseq[dseq] = true

			isCanonical, found := false, false
			if rec.EventSeq != nil {
				_, isCanonical = canonical[*rec.EventSeq]
				_, found = bySeq[*rec.EventSeq]
			}
			var floor, act, inCombat, coord, runTime any
			if c := rec.Context; c != nil {
				floor, act, inCombat, coord = nullInt64Ptr(c.Floor), nullInt64Ptr(c.Act), nullBoolPtr(c.InCombat), nullStringPtr(c.Coord)
				runTime = numberValue(c.RunTime)
			}
			var netForm any
			if rec.NetState != nil {
				netForm = nullStringPtr(rec.NetState.Form)
			}
			var optionsJSON any
			if rec.Options != nil {
				b, _ := json.Marshal(rec.Options)
				optionsJSON = string(b)
			}
			_, err := decisionStmt.ExecContext(ctx,
				st, dseq, nullInt64Ptr(rec.EventSeq), nullStringPtr(rec.EventType), nullInt64Ptr(rec.AnchorSaveSeq), seg.name,
				nullInt64Ptr(rec.FormatVersion), nullStringPtr(rec.T), nullStringPtr(rec.Kind), nullStringPtr(rec.Phase),
				numberValue(rec.Player), floor, act, coord, inCombat, runTime, boolInt(isCanonical), boolInt(found),
				nullInt64Ptr(rec.ChosenIndex), rawJSON(rec.ChosenIndexes), len(rec.Options), nullBoolPtr(rec.OptionsComplete),
				rawJSON(rec.Chosen), optionsJSON, rawJSON(rec.Selection), rawJSON(rec.State), rawJSON(rec.Hidden),
				rawJSON(rec.Notes), netForm)
			if err != nil {
				return dbError{err}
			}
			return nil
		})
		if gz != nil {
			gz.Close()
		}
		f.Close()
		if readErr != nil {
			var de dbError
			if errors.As(readErr, &de) {
				return de.err
			}
			// A truncated gzip (crash mid-close) keeps the records read so far.
			in.warnf("recordings: %s: read stopped early, kept the records before it: %v", path, readErr)
			rep.note("truncated_decision_segments", 1)
		}
	}
	if tornDecisions > 0 {
		rep.note("torn_decision_lines", tornDecisions)
		in.warnf("recordings: %s: skipped %d unparsable decision line(s)", dir, tornDecisions)
	}

	var decisionCount, canonicalDecisions int
	if err := tx.QueryRowContext(ctx,
		`SELECT count(*), coalesce(sum(canonical), 0) FROM decisions WHERE game_start_time = ?`, st).
		Scan(&decisionCount, &canonicalDecisions); err != nil {
		return err
	}

	// ---- branches
	deathUndone := false
	for i, b := range timeline.Branches {
		var anchor any
		if b.AnchorSeq != nil {
			anchor = *b.AnchorSeq
		}
		var reason any
		if load, ok := bySeq[b.LoadSeq]; ok {
			reason = nullStringPtr(payloadOf(load).Reason)
		}
		var first, last any
		if n := len(b.AbandonedSeqs); n > 0 {
			first, last = b.AbandonedSeqs[0], b.AbandonedSeqs[n-1]
		}
		abandonedDecisions := 0
		var cutRunEnd any
		for _, s := range b.AbandonedSeqs {
			e, ok := bySeq[s]
			if !ok {
				continue
			}
			if e.DSeq != nil {
				abandonedDecisions++
			}
			if e.Type == replayformat.EvRunEnd {
				deathUndone = true
				if o := payloadOf(e).Outcome; o != nil {
					cutRunEnd = *o
				} else {
					cutRunEnd = "unknown"
				}
			}
		}
		if _, err := tx.ExecContext(ctx, `
			INSERT INTO recording_branches (game_start_time, ordinal, anchor_seq, load_seq, load_reason, abandoned_events,
				first_abandoned_seq, last_abandoned_seq, abandoned_decisions, cut_run_end)
			VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
			st, i, anchor, b.LoadSeq, reason, len(b.AbandonedSeqs), first, last, abandonedDecisions, cutRunEnd); err != nil {
			return err
		}
	}

	// ---- save points and combats
	for _, e := range log {
		_, isCanonical := canonical[e.Seq]
		switch e.Type {
		case replayformat.EvSavePoint:
			p := payloadOf(e)
			if _, err := tx.ExecContext(ctx, `
				INSERT OR REPLACE INTO recording_save_points (game_start_time, seq, t, key, snapshot, reused, floor, act, canonical)
				VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
				st, e.Seq, nullString(e.T), nullStringPtr(e.Key), nullStringPtr(e.Snapshot), nullBoolPtr(p.Reused),
				nullInt64Ptr(p.Floor), nullInt64Ptr(p.Act), boolInt(isCanonical)); err != nil {
				return err
			}
		case replayformat.EvCombatReplayFile:
			p := payloadOf(e)
			if _, err := tx.ExecContext(ctx, `
				INSERT OR REPLACE INTO recording_combats (game_start_time, seq, file, bytes, floor, canonical)
				VALUES (?, ?, ?, ?, ?, ?)`,
				st, e.Seq, nullStringPtr(p.File), nullInt64Ptr(p.Bytes), nullInt64Ptr(e.Floor), boolInt(isCanonical)); err != nil {
				return err
			}
		}
	}

	types := make([]string, 0, len(typeTotals))
	for t := range typeTotals {
		types = append(types, t)
	}
	sort.Strings(types)
	for _, t := range types {
		c := typeTotals[t]
		if _, err := tx.ExecContext(ctx, `
			INSERT INTO recording_event_counts (game_start_time, type, total, canonical) VALUES (?, ?, ?, ?)`,
			st, t, c[0], c[1]); err != nil {
			return err
		}
	}

	// ---- the recording row
	var gameVersion, gameCommit, releaseDate, asmHash, modelHash any
	if g := header.Game; g != nil {
		gameVersion, gameCommit, releaseDate = nullStringPtr(g.Version), nullStringPtr(g.Commit), nullStringPtr(g.ReleaseDate)
		asmHash, modelHash = nullString(g.MainAssemblyHash.String()), nullString(g.ModelIDHash.String())
	}
	var character any
	if len(header.Players) > 0 {
		character = nullStringPtr(header.Players[0].Character)
	}
	var headerJSON any
	if headerOK {
		headerJSON = string(headerRaw)
	}
	if _, err := tx.ExecContext(ctx, `
		INSERT INTO recordings (game_start_time, dir, header_ok, format_version, decisions_format_version, spirelens_run_id,
			created_at, updated_at, recording_started_mid_run, game_version, game_commit, game_release_date,
			main_assembly_hash, model_id_hash, spirelens_version, spirelens_core_build, net_type, seed_string, seed_hash,
			character, player_count, ascension, game_mode, daily_time_utc, header_outcome, ended_at, final_floor,
			canonical_outcome, death_undone, max_canonical_floor, event_count, canonical_event_count, torn_line_count,
			branch_count, unanchored_load_count, decision_count, canonical_decision_count, decision_segment_count,
			torn_decision_line_count, snapshot_file_count, combat_file_count, mods_json, players_json, modifiers_json, header_json)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		st, dir, boolInt(headerOK), nullInt64Ptr(header.FormatVersion), nullInt64Ptr(header.DecisionsFormatVersion),
		nullStringPtr(header.SpireLensRunID), nullStringPtr(header.CreatedAtUTC), nullStringPtr(header.UpdatedAtUTC),
		nullBoolPtr(header.RecordingStartedMidRun), gameVersion, gameCommit, releaseDate, asmHash, modelHash,
		nullStringPtr(header.SpireLensVersion), nullStringPtr(header.SpireLensCoreBuild), nullStringPtr(header.NetType),
		nullStringPtr(header.SeedString), nullString(header.SeedHash.String()), character, len(header.Players),
		nullInt64Ptr(header.Ascension), nullStringPtr(header.GameMode), nullStringPtr(header.DailyTimeUTC),
		nullStringPtr(header.Outcome), nullStringPtr(header.EndedAtUTC), nullInt64Ptr(header.FinalFloor),
		nullStringPtr(canonicalOutcome), boolInt(deathUndone), nullInt64Ptr(maxFloor), len(log), len(timeline.Canonical), torn,
		len(timeline.Branches), len(timeline.UnanchoredLoads), decisionCount, canonicalDecisions, len(segments),
		tornDecisions, countFiles("snapshots"), countFiles("combats"),
		rawJSON(header.Mods), rawJSON(header.PlayersRaw), rawJSON(header.Modifiers), headerJSON); err != nil {
		return err
	}

	if err := in.putSource(ctx, tx, dir, "recording", size, mtime, fingerprint, &st, "ok", ""); err != nil {
		return err
	}
	return tx.Commit()
}
