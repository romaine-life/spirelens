package runweb

import (
	"context"
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

// Filter is the run filter the page sends. It is also stored, as is, in a
// playlist's informational "filter" field.
type Filter struct {
	Character    string   `json:"character,omitempty"`
	AscensionMin *int64   `json:"ascension_min,omitempty"`
	AscensionMax *int64   `json:"ascension_max,omitempty"`
	Outcomes     []string `json:"outcomes,omitempty"`  // win | loss | abandoned | in_progress
	DateFrom     string   `json:"date_from,omitempty"` // YYYY-MM-DD, local time, inclusive
	DateTo       string   `json:"date_to,omitempty"`   // YYYY-MM-DD, local time, inclusive
	FloorMin     *int64   `json:"floor_min,omitempty"`
	FloorMax     *int64   `json:"floor_max,omitempty"`
	KilledBy     string   `json:"killed_by,omitempty"`
	PickedCard   string   `json:"picked_card,omitempty"` // picked at a card reward (game history)
	HasRelic     string   `json:"has_relic,omitempty"`   // in the final relics (game history)
	Source       string   `json:"source,omitempty"`      // any | recorded | history_only
	PlayableOnly bool     `json:"playable_only,omitempty"`
}

// normalizeID accepts "CARD.DEFY", "defy" or "Defy Thing" for prefix CARD.
func normalizeID(prefix, s string) string {
	s = strings.TrimSpace(s)
	if s == "" {
		return ""
	}
	if strings.Contains(s, ".") {
		return strings.ToUpper(s)
	}
	return prefix + "." + strings.ToUpper(strings.ReplaceAll(strings.TrimSpace(s), " ", "_"))
}

func localDay(s string, endOfDay bool) (int64, bool) {
	t, err := time.ParseInLocation("2006-01-02", s, time.Local)
	if err != nil {
		return 0, false
	}
	if endOfDay {
		t = t.AddDate(0, 0, 1)
		return t.Unix() - 1, true
	}
	return t.Unix(), true
}

// buildWhere turns a filter into SQL conditions over runs r (and recordings
// rec). PlayableOnly is applied afterwards in Go, since it needs the disk.
func buildWhere(f Filter) (string, []any) {
	var where []string
	var args []any
	add := func(cond string, a ...any) {
		where = append(where, cond)
		args = append(args, a...)
	}
	if f.Character != "" {
		add("r.character = ?", f.Character)
	}
	if f.AscensionMin != nil {
		add("r.ascension >= ?", *f.AscensionMin)
	}
	if f.AscensionMax != nil {
		add("r.ascension <= ?", *f.AscensionMax)
	}
	var outcomes []any
	for _, o := range f.Outcomes {
		switch o {
		case "win", "loss", "abandoned", "in_progress":
			outcomes = append(outcomes, o)
		}
	}
	if len(outcomes) > 0 {
		add("r.outcome IN (?"+strings.Repeat(",?", len(outcomes)-1)+")", outcomes...)
	}
	if v, ok := localDay(f.DateFrom, false); ok {
		add("r.game_start_time >= ?", v)
	}
	if v, ok := localDay(f.DateTo, true); ok {
		add("r.game_start_time <= ?", v)
	}
	if f.FloorMin != nil {
		add("r.floor_reached >= ?", *f.FloorMin)
	}
	if f.FloorMax != nil {
		add("r.floor_reached <= ?", *f.FloorMax)
	}
	if f.KilledBy != "" {
		add("r.killed_by = ?", f.KilledBy)
	}
	if c := normalizeID("CARD", f.PickedCard); c != "" {
		add(`EXISTS (SELECT 1 FROM run_choices c WHERE c.game_start_time = r.game_start_time
			AND c.kind = 'card' AND c.was_picked = 1 AND c.item_id = ?)`, c)
	}
	if rl := normalizeID("RELIC", f.HasRelic); rl != "" {
		add(`EXISTS (SELECT 1 FROM run_relics x WHERE x.game_start_time = r.game_start_time AND x.relic_id = ?)`, rl)
	}
	switch f.Source {
	case "recorded":
		add("r.has_recording = 1")
	case "history_only":
		add("r.has_recording = 0 AND r.has_history = 1")
	}
	if len(where) == 0 {
		return "", nil
	}
	return " WHERE " + strings.Join(where, " AND "), args
}

// RunRow is one results-table row.
type RunRow struct {
	GameStartTime  int64   `json:"game_start_time"`
	StartedAt      string  `json:"started_at"`
	Character      *string `json:"character"`
	Ascension      *int64  `json:"ascension"`
	Outcome        *string `json:"outcome"`
	Floor          *int64  `json:"floor"`
	KilledBy       *string `json:"killed_by"`
	DeckSize       *int64  `json:"deck_size"`
	RelicCount     *int64  `json:"relic_count"`
	RunTimeSeconds *int64  `json:"run_time_seconds"`
	DamageTaken    *int64  `json:"damage_taken"`
	Decisions      *int64  `json:"decisions"`
	GameVersion    *string `json:"game_version"`
	Sources        string  `json:"sources"`
	HasRecording   bool    `json:"has_recording"`
	Label          string  `json:"label"`
	Replayability
}

// prettyID turns "CHARACTER.NECROBINDER" into "Necrobinder".
func prettyID(id string) string {
	if i := strings.IndexByte(id, '.'); i >= 0 {
		id = id[i+1:]
	}
	words := strings.Split(strings.ToLower(id), "_")
	for i, w := range words {
		if w != "" {
			words[i] = strings.ToUpper(w[:1]) + w[1:]
		}
	}
	return strings.Join(words, " ")
}

// RunLabel is the human label a playlist entry carries, for example
// "2026-10-15 Regent A10 WIN f49".
func RunLabel(gameStartTime int64, character *string, ascension *int64, outcome *string, floor *int64) string {
	parts := []string{time.Unix(gameStartTime, 0).Local().Format("2006-01-02")}
	if character != nil && *character != "" {
		parts = append(parts, prettyID(*character))
	}
	if ascension != nil {
		parts = append(parts, "A"+strconv.FormatInt(*ascension, 10))
	}
	o := "UNFINISHED"
	if outcome != nil {
		switch *outcome {
		case "win", "loss", "abandoned":
			o = strings.ToUpper(*outcome)
		}
	}
	parts = append(parts, o)
	if floor != nil {
		parts = append(parts, "f"+strconv.FormatInt(*floor, 10))
	}
	return strings.Join(parts, " ")
}

func ptrS(n sql.NullString) *string {
	if !n.Valid {
		return nil
	}
	v := n.String
	return &v
}

func ptrI(n sql.NullInt64) *int64 {
	if !n.Valid {
		return nil
	}
	v := n.Int64
	return &v
}

const runSelect = `
	SELECT r.game_start_time, r.started_at, r.character, r.ascension, r.outcome, r.floor_reached, r.killed_by,
	       r.run_time_seconds, r.game_version, r.sources, r.has_recording,
	       (SELECT count(*) FROM run_deck d WHERE d.game_start_time = r.game_start_time
	           AND d.player_id = (SELECT p.player_id FROM run_players p WHERE p.game_start_time = r.game_start_time ORDER BY p.ordinal LIMIT 1)),
	       (SELECT count(*) FROM run_relics x WHERE x.game_start_time = r.game_start_time
	           AND x.player_id = (SELECT p.player_id FROM run_players p WHERE p.game_start_time = r.game_start_time ORDER BY p.ordinal LIMIT 1)),
	       (SELECT sum(f.damage_taken) FROM run_floors f WHERE f.game_start_time = r.game_start_time
	           AND f.player_id = (SELECT p.player_id FROM run_players p WHERE p.game_start_time = r.game_start_time ORDER BY p.ordinal LIMIT 1)),
	       rec.canonical_decision_count, rec.header_ok, rec.snapshot_file_count, rec.game_commit, rec.game_version,
	       rec.model_id_hash, rec.recording_started_mid_run
	FROM runs r LEFT JOIN recordings rec USING (game_start_time)`

// QueryRuns returns the runs matching f, newest first, with replayability.
func QueryRuns(ctx context.Context, db *sql.DB, f Filter, cur CurrentBuild, replaysDir string) ([]RunRow, error) {
	where, args := buildWhere(f)
	rows, err := db.QueryContext(ctx, runSelect+where+" ORDER BY r.game_start_time DESC", args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []RunRow{}
	for rows.Next() {
		var (
			row                                     RunRow
			startedAt, character, outcome, killedBy sql.NullString
			version, sources                        sql.NullString
			ascension, floor, runTime               sql.NullInt64
			hasRec                                  int64
			deck, relics, damage                    sql.NullInt64
			decisions, headerOK, snapshots, midRun  sql.NullInt64
			commit, recVersion, hash                sql.NullString
		)
		if err := rows.Scan(&row.GameStartTime, &startedAt, &character, &ascension, &outcome, &floor, &killedBy,
			&runTime, &version, &sources, &hasRec, &deck, &relics, &damage,
			&decisions, &headerOK, &snapshots, &commit, &recVersion, &hash, &midRun); err != nil {
			return nil, err
		}
		row.StartedAt = startedAt.String
		row.Character, row.Ascension, row.Outcome, row.Floor, row.KilledBy = ptrS(character), ptrI(ascension), ptrS(outcome), ptrI(floor), ptrS(killedBy)
		row.RunTimeSeconds, row.GameVersion, row.Sources = ptrI(runTime), ptrS(version), sources.String
		if deck.Valid && deck.Int64 > 0 {
			row.DeckSize = ptrI(deck)
		}
		if relics.Valid && relics.Int64 > 0 {
			row.RelicCount = ptrI(relics)
		}
		row.DamageTaken = ptrI(damage)
		row.Decisions = ptrI(decisions)
		row.HasRecording = hasRec != 0
		row.Label = RunLabel(row.GameStartTime, row.Character, row.Ascension, row.Outcome, row.Floor)

		facts := recordingFacts{
			hasRecording: row.HasRecording,
			headerOK:     headerOK.Int64 != 0,
			snapshots:    snapshots.Int64,
			commit:       commit.String,
			version:      recVersion.String,
			modelIDHash:  hash.String,
			midRun:       midRun.Int64 != 0,
		}
		if row.HasRecording {
			facts.dirExists = recordingDirExists(replaysDir, row.GameStartTime)
		}
		row.Replayability = checkReplayable(facts, cur)
		if f.PlayableOnly && !row.Replayable {
			continue
		}
		out = append(out, row)
	}
	return out, rows.Err()
}

func recordingDirExists(replaysDir string, gameStartTime int64) bool {
	info, err := os.Stat(filepath.Join(replaysDir, strconv.FormatInt(gameStartTime, 10)))
	return err == nil && info.IsDir()
}

// Option is one choice in a filter dropdown or autocomplete list.
type Option struct {
	Value string `json:"value"`
	Label string `json:"label"`
	Count int64  `json:"count"`
}

// Meta is what the page needs to draw its filters.
type Meta struct {
	Characters []Option `json:"characters"`
	KilledBy   []Option `json:"killed_by"`
	Cards      []Option `json:"cards"`  // cards ever picked at a reward
	Relics     []Option `json:"relics"` // relics ever held at run end
	Ascension  [2]int64 `json:"ascension"`
	Floor      [2]int64 `json:"floor"`
	DateRange  [2]int64 `json:"date_range"` // game_start_time min/max
	RunCount   int64    `json:"run_count"`
}

func options(ctx context.Context, db *sql.DB, query string) ([]Option, error) {
	rows, err := db.QueryContext(ctx, query)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []Option{}
	for rows.Next() {
		var o Option
		if err := rows.Scan(&o.Value, &o.Count); err != nil {
			return nil, err
		}
		o.Label = prettyID(o.Value)
		out = append(out, o)
	}
	return out, rows.Err()
}

// LoadMeta reads the filter vocabularies.
func LoadMeta(ctx context.Context, db *sql.DB) (*Meta, error) {
	m := &Meta{}
	var err error
	if m.Characters, err = options(ctx, db, `SELECT character, count(*) FROM runs WHERE character IS NOT NULL GROUP BY character ORDER BY count(*) DESC`); err != nil {
		return nil, fmt.Errorf("characters: %w", err)
	}
	if m.KilledBy, err = options(ctx, db, `SELECT killed_by, count(*) FROM runs WHERE killed_by IS NOT NULL GROUP BY killed_by ORDER BY count(*) DESC`); err != nil {
		return nil, fmt.Errorf("killed_by: %w", err)
	}
	if m.Cards, err = options(ctx, db, `SELECT item_id, count(DISTINCT game_start_time) FROM run_choices WHERE kind = 'card' AND was_picked = 1 AND item_id IS NOT NULL GROUP BY item_id ORDER BY item_id`); err != nil {
		return nil, fmt.Errorf("cards: %w", err)
	}
	if m.Relics, err = options(ctx, db, `SELECT relic_id, count(DISTINCT game_start_time) FROM run_relics GROUP BY relic_id ORDER BY relic_id`); err != nil {
		return nil, fmt.Errorf("relics: %w", err)
	}
	var a0, a1, f0, f1, d0, d1 sql.NullInt64
	if err := db.QueryRowContext(ctx, `SELECT min(ascension), max(ascension), min(floor_reached), max(floor_reached),
		min(game_start_time), max(game_start_time), count(*) FROM runs`).Scan(&a0, &a1, &f0, &f1, &d0, &d1, &m.RunCount); err != nil {
		return nil, err
	}
	m.Ascension = [2]int64{a0.Int64, a1.Int64}
	m.Floor = [2]int64{f0.Int64, f1.Int64}
	m.DateRange = [2]int64{d0.Int64, d1.Int64}
	return m, nil
}

// LookupRuns returns playlist entries (with fresh labels) for the given
// runs, keeping order and dropping repeats, and the runs that were dropped
// because they have no replays/<game_start_time>/ directory. A run missing
// from the database uses its label in keep, if any.
func LookupRuns(ctx context.Context, db *sql.DB, ids []int64, replaysDir string, keep map[int64]string) ([]PlaylistRun, []int64, error) {
	out := []PlaylistRun{}
	var dropped []int64
	seen := map[int64]bool{}
	for _, id := range ids {
		if seen[id] {
			continue
		}
		seen[id] = true
		if !recordingDirExists(replaysDir, id) {
			dropped = append(dropped, id)
			continue
		}
		var character, outcome sql.NullString
		var ascension, floor sql.NullInt64
		err := db.QueryRowContext(ctx, `SELECT character, ascension, outcome, floor_reached FROM runs WHERE game_start_time = ?`, id).
			Scan(&character, &ascension, &outcome, &floor)
		if err == sql.ErrNoRows {
			if l, ok := keep[id]; ok && l != "" {
				out = append(out, PlaylistRun{GameStartTime: id, Label: l})
				continue
			}
		} else if err != nil {
			return nil, nil, err
		}
		out = append(out, PlaylistRun{GameStartTime: id, Label: RunLabel(id, ptrS(character), ptrI(ascension), ptrS(outcome), ptrI(floor))})
	}
	return out, dropped, nil
}
