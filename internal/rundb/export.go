package rundb

import (
	"bufio"
	"context"
	"database/sql"
	"encoding/json"
	"io"
	"strings"
)

// ExportOptions selects which decision records export-decisions writes.
type ExportOptions struct {
	// DropHidden omits the record's hidden block (true draw order, monster
	// moves): information the player could not see.
	DropHidden bool
	// IncludeAbandoned also writes records from abandoned branches
	// (save-scums, restarts, undone deaths), marked canonical=false.
	IncludeAbandoned bool
	// FinishedOnly skips runs whose outcome is not win/loss/abandoned.
	FinishedOnly bool
	// Kinds, when non-empty, keeps only these decision kinds.
	Kinds []string
}

type exportContext struct {
	Floor    *int64          `json:"floor"`
	Act      *int64          `json:"act"`
	Coord    *string         `json:"coord"`
	InCombat *bool           `json:"in_combat"`
	RunTime  json.RawMessage `json:"run_time"`
}

type exportLabels struct {
	Outcome      *string `json:"outcome"`
	Win          *bool   `json:"win"`
	Character    *string `json:"character"`
	Ascension    *int64  `json:"ascension"`
	FloorReached *int64  `json:"floor_reached"`
	KilledBy     *string `json:"killed_by"`
	GameVersion  *string `json:"game_version"`
	GameCommit   *string `json:"game_commit"`
}

type exportRecord struct {
	GameStartTime   int64           `json:"game_start_time"`
	DSeq            int64           `json:"dseq"`
	EventSeq        *int64          `json:"event_seq"`
	Canonical       bool            `json:"canonical"`
	FormatVersion   *int64          `json:"format_version"`
	T               *string         `json:"t"`
	Kind            *string         `json:"kind"`
	Phase           *string         `json:"phase"`
	Player          json.RawMessage `json:"player"`
	Context         exportContext   `json:"context"`
	State           json.RawMessage `json:"state"`
	Options         json.RawMessage `json:"options"`
	OptionsComplete *bool           `json:"options_complete"`
	Selection       json.RawMessage `json:"selection"`
	Chosen          json.RawMessage `json:"chosen"`
	ChosenIndex     *int64          `json:"chosen_index"`
	ChosenIndexes   json.RawMessage `json:"chosen_indexes"`
	Hidden          json.RawMessage `json:"hidden,omitempty"`
	Notes           json.RawMessage `json:"notes"`
	Labels          exportLabels    `json:"labels"`
}

func rawOrNull(s sql.NullString) json.RawMessage {
	if !s.Valid || s.String == "" {
		return json.RawMessage("null")
	}
	return json.RawMessage(s.String)
}

func strPtr(s sql.NullString) *string {
	if !s.Valid {
		return nil
	}
	v := s.String
	return &v
}

func intPtr(n sql.NullInt64) *int64 {
	if !n.Valid {
		return nil
	}
	v := n.Int64
	return &v
}

func boolPtr(n sql.NullInt64) *bool {
	if !n.Valid {
		return nil
	}
	v := n.Int64 != 0
	return &v
}

// numberRaw renders a column holding a JSON number (INTEGER, REAL or TEXT
// for an oversized ulong) back to a JSON number.
func numberRaw(v any) json.RawMessage {
	switch x := v.(type) {
	case nil:
		return json.RawMessage("null")
	case []byte:
		return json.RawMessage(x)
	case string:
		return json.RawMessage(x)
	default:
		b, err := json.Marshal(x)
		if err != nil {
			return json.RawMessage("null")
		}
		return b
	}
}

// ExportDecisions streams decision records as JSONL to w, in run then dseq
// order, each with its run's outcome/character/ascension labels. It returns
// the number of records written.
func ExportDecisions(ctx context.Context, db *sql.DB, w io.Writer, opts ExportOptions) (int, error) {
	var where []string
	var args []any
	if !opts.IncludeAbandoned {
		where = append(where, "d.canonical = 1")
	}
	if opts.FinishedOnly {
		where = append(where, "r.outcome IN ('win', 'loss', 'abandoned')")
	}
	if len(opts.Kinds) > 0 {
		where = append(where, "d.kind IN (?"+strings.Repeat(", ?", len(opts.Kinds)-1)+")")
		for _, k := range opts.Kinds {
			args = append(args, k)
		}
	}
	query := `
		SELECT d.game_start_time, d.dseq, d.event_seq, d.canonical, d.format_version, d.t, d.kind, d.phase, d.player,
		       d.floor, d.act, d.coord, d.in_combat, d.run_time,
		       d.state_json, d.options_json, d.options_complete, d.selection_json, d.chosen_json, d.chosen_index,
		       d.chosen_indexes_json, d.hidden_json, d.notes_json,
		       r.outcome, r.character, r.ascension, r.floor_reached, r.killed_by, r.game_version, r.game_commit
		FROM decisions d LEFT JOIN runs r USING (game_start_time)`
	if len(where) > 0 {
		query += " WHERE " + strings.Join(where, " AND ")
	}
	query += " ORDER BY d.game_start_time, d.dseq"

	rows, err := db.QueryContext(ctx, query, args...)
	if err != nil {
		return 0, err
	}
	defer rows.Close()

	bw := bufio.NewWriterSize(w, 1<<20)
	enc := json.NewEncoder(bw)
	enc.SetEscapeHTML(false)
	n := 0
	for rows.Next() {
		var (
			rec                                           exportRecord
			eventSeq, canonical, formatVersion            sql.NullInt64
			t, kind, phase, coord                         sql.NullString
			player, runTime                               any
			floor, act, inCombat                          sql.NullInt64
			state, options, selection, chosen             sql.NullString
			optionsComplete, chosenIndex                  sql.NullInt64
			chosenIndexes, hidden, notes                  sql.NullString
			outcome, character, killedBy, version, commit sql.NullString
			ascension, floorReached                       sql.NullInt64
		)
		if err := rows.Scan(&rec.GameStartTime, &rec.DSeq, &eventSeq, &canonical, &formatVersion, &t, &kind, &phase, &player,
			&floor, &act, &coord, &inCombat, &runTime,
			&state, &options, &optionsComplete, &selection, &chosen, &chosenIndex,
			&chosenIndexes, &hidden, &notes,
			&outcome, &character, &ascension, &floorReached, &killedBy, &version, &commit); err != nil {
			return n, err
		}
		rec.EventSeq = intPtr(eventSeq)
		rec.Canonical = canonical.Valid && canonical.Int64 != 0
		rec.FormatVersion = intPtr(formatVersion)
		rec.T, rec.Kind, rec.Phase = strPtr(t), strPtr(kind), strPtr(phase)
		rec.Player = numberRaw(player)
		rec.Context = exportContext{
			Floor: intPtr(floor), Act: intPtr(act), Coord: strPtr(coord), InCombat: boolPtr(inCombat),
			RunTime: numberRaw(runTime),
		}
		rec.State, rec.Options, rec.Selection, rec.Chosen = rawOrNull(state), rawOrNull(options), rawOrNull(selection), rawOrNull(chosen)
		rec.OptionsComplete = boolPtr(optionsComplete)
		rec.ChosenIndex = intPtr(chosenIndex)
		rec.ChosenIndexes = rawOrNull(chosenIndexes)
		if !opts.DropHidden {
			rec.Hidden = rawOrNull(hidden)
		}
		rec.Notes = rawOrNull(notes)
		rec.Labels = exportLabels{
			Outcome: strPtr(outcome), Character: strPtr(character), Ascension: intPtr(ascension),
			FloorReached: intPtr(floorReached), KilledBy: strPtr(killedBy), GameVersion: strPtr(version), GameCommit: strPtr(commit),
		}
		if outcome.Valid && outcome.String != "in_progress" {
			win := outcome.String == "win"
			rec.Labels.Win = &win
		}
		if err := enc.Encode(&rec); err != nil {
			return n, err
		}
		n++
	}
	if err := rows.Err(); err != nil {
		return n, err
	}
	return n, bw.Flush()
}
