package rundb

import (
	"context"
	"database/sql"
	"sort"
	"strings"
	"time"
)

// runRow accumulates one game run across the three sources.
type runRow struct {
	startTime int64

	hasRecording, hasHistory, hasSpireLens bool

	// history
	hCharacter, hSeed, hGameMode, hBuild, hKilledEnc, hKilledEvent sql.NullString
	hAscension, hRunTime, hPlayers, hFloors                        sql.NullInt64
	hWin, hAbandoned                                               sql.NullBool

	// recording
	rDir, rCharacter, rSeed, rGameMode, rVersion, rCommit, rRunID sql.NullString
	rHeaderOutcome, rCanonicalOutcome, rEndedAt                   sql.NullString
	rAscension, rPlayers, rFinalFloor, rMaxFloor                  sql.NullInt64
	rDeathUndone                                                  bool

	// spirelens (the chosen record)
	sRunID, sCharacter, sOutcome, sEndedAt, sUpdatedAt sql.NullString
	sAscension, sFloor                                 sql.NullInt64
}

func firstString(vals ...sql.NullString) any {
	for _, v := range vals {
		if v.Valid && v.String != "" {
			return v.String
		}
	}
	return nil
}

func firstInt(vals ...sql.NullInt64) any {
	for _, v := range vals {
		if v.Valid {
			return v.Int64
		}
	}
	return nil
}

// outcome picks the run's outcome. The recording's canonical timeline is
// authoritative when it reached a run_end, or when it cut one away (a death
// undo, after which the game's history file still says "loss" until the run
// really ends). Otherwise the game's history file, then the recording header,
// then the SpireLens record.
func (r *runRow) outcome() any {
	if r.hasRecording {
		if r.rCanonicalOutcome.Valid && r.rCanonicalOutcome.String != "" {
			return r.rCanonicalOutcome.String
		}
		if r.rDeathUndone {
			return "in_progress"
		}
	}
	if r.hasHistory {
		switch {
		case r.hWin.Valid && r.hWin.Bool:
			return "win"
		case r.hAbandoned.Valid && r.hAbandoned.Bool:
			return "abandoned"
		default:
			return "loss"
		}
	}
	if v := firstString(r.rHeaderOutcome, r.sOutcome); v != nil {
		return v
	}
	return nil
}

// RefreshRuns rebuilds every runs row from the source tables. It upserts so
// a column ingest does not own (recorded_build_matches_current) survives.
// Returns the number of runs.
func RefreshRuns(ctx context.Context, db *sql.DB) (int, error) {
	runs := map[int64]*runRow{}
	get := func(st int64) *runRow {
		r := runs[st]
		if r == nil {
			r = &runRow{startTime: st}
			runs[st] = r
		}
		return r
	}

	hist, err := db.QueryContext(ctx, `
		SELECT game_start_time, character, seed, lower(game_mode), build_id, killed_by_encounter, killed_by_event,
		       ascension, run_time_seconds, player_count, floor_reached, win, was_abandoned
		FROM history_runs`)
	if err != nil {
		return 0, err
	}
	for hist.Next() {
		var st int64
		var x runRow
		if err := hist.Scan(&st, &x.hCharacter, &x.hSeed, &x.hGameMode, &x.hBuild, &x.hKilledEnc, &x.hKilledEvent,
			&x.hAscension, &x.hRunTime, &x.hPlayers, &x.hFloors, &x.hWin, &x.hAbandoned); err != nil {
			hist.Close()
			return 0, err
		}
		r := get(st)
		r.hasHistory = true
		r.hCharacter, r.hSeed, r.hGameMode, r.hBuild, r.hKilledEnc, r.hKilledEvent = x.hCharacter, x.hSeed, x.hGameMode, x.hBuild, x.hKilledEnc, x.hKilledEvent
		r.hAscension, r.hRunTime, r.hPlayers, r.hFloors, r.hWin, r.hAbandoned = x.hAscension, x.hRunTime, x.hPlayers, x.hFloors, x.hWin, x.hAbandoned
	}
	hist.Close()
	if err := hist.Err(); err != nil {
		return 0, err
	}

	rec, err := db.QueryContext(ctx, `
		SELECT game_start_time, dir, character, seed_string, lower(game_mode), game_version, game_commit, spirelens_run_id,
		       header_outcome, canonical_outcome, ended_at, ascension, player_count, final_floor, max_canonical_floor, death_undone
		FROM recordings`)
	if err != nil {
		return 0, err
	}
	for rec.Next() {
		var st int64
		var x runRow
		var undone int64
		if err := rec.Scan(&st, &x.rDir, &x.rCharacter, &x.rSeed, &x.rGameMode, &x.rVersion, &x.rCommit, &x.rRunID,
			&x.rHeaderOutcome, &x.rCanonicalOutcome, &x.rEndedAt, &x.rAscension, &x.rPlayers, &x.rFinalFloor, &x.rMaxFloor, &undone); err != nil {
			rec.Close()
			return 0, err
		}
		r := get(st)
		r.hasRecording = true
		r.rDir, r.rCharacter, r.rSeed, r.rGameMode, r.rVersion, r.rCommit, r.rRunID = x.rDir, x.rCharacter, x.rSeed, x.rGameMode, x.rVersion, x.rCommit, x.rRunID
		r.rHeaderOutcome, r.rCanonicalOutcome, r.rEndedAt = x.rHeaderOutcome, x.rCanonicalOutcome, x.rEndedAt
		r.rAscension, r.rPlayers, r.rFinalFloor, r.rMaxFloor = x.rAscension, x.rPlayers, x.rFinalFloor, x.rMaxFloor
		r.rDeathUndone = undone != 0
	}
	rec.Close()
	if err := rec.Err(); err != nil {
		return 0, err
	}

	// SpireLens: several records can share a start time. Prefer the one the
	// recording header names, then the most recently updated.
	sl, err := db.QueryContext(ctx, `
		SELECT game_start_time, run_id, character, outcome, ended_at, updated_at, ascension, floor_reached
		FROM spirelens_runs WHERE game_start_time IS NOT NULL
		ORDER BY game_start_time, updated_at`)
	if err != nil {
		return 0, err
	}
	type slRow struct {
		runID, character, outcome, endedAt, updatedAt sql.NullString
		ascension, floor                              sql.NullInt64
	}
	slByStart := map[int64][]slRow{}
	for sl.Next() {
		var st int64
		var x slRow
		if err := sl.Scan(&st, &x.runID, &x.character, &x.outcome, &x.endedAt, &x.updatedAt, &x.ascension, &x.floor); err != nil {
			sl.Close()
			return 0, err
		}
		slByStart[st] = append(slByStart[st], x)
	}
	sl.Close()
	if err := sl.Err(); err != nil {
		return 0, err
	}
	for st, rows := range slByStart {
		r := get(st)
		r.hasSpireLens = true
		chosen := rows[len(rows)-1] // latest updated_at
		if r.rRunID.Valid {
			for _, x := range rows {
				if x.runID.String == r.rRunID.String {
					chosen = x
				}
			}
		}
		r.sRunID, r.sCharacter, r.sOutcome, r.sEndedAt, r.sUpdatedAt = chosen.runID, chosen.character, chosen.outcome, chosen.endedAt, chosen.updatedAt
		r.sAscension, r.sFloor = chosen.ascension, chosen.floor
	}

	keys := make([]int64, 0, len(runs))
	for k := range runs {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { return keys[i] < keys[j] })

	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return 0, err
	}
	defer tx.Rollback()
	stmt, err := tx.PrepareContext(ctx, `
		INSERT INTO runs (game_start_time, started_at, sources, has_recording, has_history, has_spirelens, character,
			ascension, seed, game_mode, game_version, game_commit, player_count, outcome, win, floor_reached, killed_by,
			ended_at, run_time_seconds, recording_dir, spirelens_run_id, refreshed_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(game_start_time) DO UPDATE SET
			started_at = excluded.started_at, sources = excluded.sources, has_recording = excluded.has_recording,
			has_history = excluded.has_history, has_spirelens = excluded.has_spirelens, character = excluded.character,
			ascension = excluded.ascension, seed = excluded.seed, game_mode = excluded.game_mode,
			game_version = excluded.game_version, game_commit = excluded.game_commit, player_count = excluded.player_count,
			outcome = excluded.outcome, win = excluded.win, floor_reached = excluded.floor_reached,
			killed_by = excluded.killed_by, ended_at = excluded.ended_at, run_time_seconds = excluded.run_time_seconds,
			recording_dir = excluded.recording_dir, spirelens_run_id = excluded.spirelens_run_id,
			refreshed_at = excluded.refreshed_at`)
	if err != nil {
		return 0, err
	}
	defer stmt.Close()

	now := time.Now().UTC().Format(time.RFC3339)
	for _, st := range keys {
		r := runs[st]
		var sources []string
		if r.hasRecording {
			sources = append(sources, "recording")
		}
		if r.hasHistory {
			sources = append(sources, "history")
		}
		if r.hasSpireLens {
			sources = append(sources, "spirelens")
		}
		outcome := r.outcome()
		var win any
		if o, ok := outcome.(string); ok {
			win = boolInt(o == "win")
		}
		// A run still in progress (death undone) was not killed by anything yet.
		var killedBy any
		if outcome != "in_progress" {
			killedBy = firstString(r.hKilledEnc, r.hKilledEvent)
		}
		// A canonical recording floor beats a stale history file after a death undo.
		var floor any
		if r.hasRecording && r.rDeathUndone && r.rMaxFloor.Valid {
			floor = r.rMaxFloor.Int64
		} else {
			floor = firstInt(r.hFloors, r.rFinalFloor, r.rMaxFloor, r.sFloor)
		}
		if _, err := stmt.ExecContext(ctx,
			st, unixToISO(st), strings.Join(sources, ","), boolInt(r.hasRecording), boolInt(r.hasHistory), boolInt(r.hasSpireLens),
			firstString(r.hCharacter, r.rCharacter, r.sCharacter),
			firstInt(r.hAscension, r.rAscension, r.sAscension),
			firstString(r.hSeed, r.rSeed),
			firstString(r.hGameMode, r.rGameMode),
			firstString(r.hBuild, r.rVersion),
			firstString(r.rCommit),
			firstInt(r.hPlayers, r.rPlayers),
			outcome, win, floor,
			killedBy,
			firstString(r.rEndedAt, r.sEndedAt),
			firstInt(r.hRunTime),
			firstString(r.rDir),
			firstString(r.sRunID),
			now); err != nil {
			return 0, err
		}
	}
	if err := tx.Commit(); err != nil {
		return 0, err
	}
	return len(keys), nil
}
