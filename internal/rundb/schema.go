package rundb

import (
	"context"
	"database/sql"
	"fmt"
	"time"

	_ "modernc.org/sqlite" // pure-Go SQLite driver, registered as "sqlite"
)

// migration is one additive schema step. Migrations are append-only: never
// edit or reorder a shipped one, add a new version instead. Every step must
// keep older rows readable (new columns nullable or defaulted).
type migration struct {
	version     int
	description string
	sql         string
	// vacuum asks for a VACUUM after the migration commits, for steps that
	// free a lot of space (VACUUM cannot run inside a transaction).
	vacuum bool
}

var migrations = []migration{
	{1, "initial run database", schemaV1, false},
	{2, "drop per-aggregate raw_json (duplicates spirelens_runs.raw_json)", schemaV2, true},
}

// LatestSchemaVersion is the schema version a fully migrated database has.
func LatestSchemaVersion() int { return migrations[len(migrations)-1].version }

const schemaV1 = `
CREATE TABLE schema_version (
	version     INTEGER PRIMARY KEY,
	description TEXT NOT NULL,
	applied_at  TEXT NOT NULL
);

-- One row per source file (history .run, SpireLens runs/*.json) or recording
-- directory, so a re-run only re-reads what changed. fingerprint is the
-- content sha256 for a file, and a hash of the (path, size, mtime) manifest
-- for a recording directory.
CREATE TABLE source_files (
	path            TEXT PRIMARY KEY,
	kind            TEXT NOT NULL,              -- recording | history | spirelens
	size            INTEGER NOT NULL,
	mtime_ns        INTEGER NOT NULL,
	fingerprint     TEXT NOT NULL,
	game_start_time INTEGER,
	status          TEXT NOT NULL,              -- ok | duplicate | error
	message         TEXT,
	ingested_at     TEXT NOT NULL
);
CREATE INDEX source_files_kind ON source_files(kind, status);
CREATE INDEX source_files_start ON source_files(game_start_time);

-- One row per game run, keyed by the game's own run id (start_time, the
-- {start_time}.run history name and the replays/<start_time> directory).
-- Derived from the source tables below on every ingest, except
-- recorded_build_matches_current, which ingest never writes.
CREATE TABLE runs (
	game_start_time  INTEGER PRIMARY KEY,
	started_at       TEXT,
	sources          TEXT NOT NULL,             -- comma list of recording,history,spirelens
	has_recording    INTEGER NOT NULL DEFAULT 0,
	has_history      INTEGER NOT NULL DEFAULT 0,
	has_spirelens    INTEGER NOT NULL DEFAULT 0,
	character        TEXT,
	ascension        INTEGER,
	seed             TEXT,
	game_mode        TEXT,
	game_version     TEXT,
	game_commit      TEXT,
	player_count     INTEGER,
	outcome          TEXT,                      -- win | loss | abandoned | in_progress
	win              INTEGER,
	floor_reached    INTEGER,
	killed_by        TEXT,
	ended_at         TEXT,
	run_time_seconds INTEGER,
	recording_dir    TEXT,
	spirelens_run_id TEXT,
	recorded_build_matches_current INTEGER,     -- NULL until a build-aware tool fills it
	refreshed_at     TEXT NOT NULL
);
CREATE INDEX runs_character ON runs(character, outcome);
CREATE INDEX runs_ascension ON runs(ascension, outcome);
CREATE INDEX runs_outcome ON runs(outcome);

-- ---------------------------------------------------------------- game history
-- The game's own saves/history/{start_time}.run, deduplicated by start_time
-- across profiles and .run.backup copies.
CREATE TABLE history_runs (
	game_start_time     INTEGER PRIMARY KEY,
	source_path         TEXT NOT NULL,
	copies              INTEGER NOT NULL,
	copy_paths          TEXT NOT NULL,          -- JSON array of every file holding this run
	content_sha256      TEXT NOT NULL,
	schema_version      INTEGER,
	build_id            TEXT,
	platform_type       TEXT,
	game_mode           TEXT,
	seed                TEXT,
	ascension           INTEGER,
	win                 INTEGER,
	was_abandoned       INTEGER,
	killed_by_encounter TEXT,
	killed_by_event     TEXT,
	run_time_seconds    INTEGER,
	player_count        INTEGER,
	character           TEXT,                   -- players[0]
	floor_reached       INTEGER,                -- number of map points visited
	acts_json           TEXT,
	modifiers_json      TEXT,
	raw_json            TEXT NOT NULL
);

CREATE TABLE run_players (
	game_start_time       INTEGER NOT NULL,
	player_id             INTEGER NOT NULL,
	ordinal               INTEGER NOT NULL,
	character             TEXT,
	max_potion_slot_count INTEGER,
	badges_json           TEXT,
	potions_json          TEXT,
	PRIMARY KEY (game_start_time, player_id)
);

-- One row per map point (floor) per player. floor is 1-based over the whole
-- run; act_floor is 1-based within the act.
CREATE TABLE run_floors (
	game_start_time  INTEGER NOT NULL,
	floor            INTEGER NOT NULL,
	player_id        INTEGER NOT NULL,
	act_index        INTEGER NOT NULL,
	act_floor        INTEGER NOT NULL,
	map_point_type   TEXT,
	room_type        TEXT,                      -- last room at this map point
	model_id         TEXT,                      -- encounter / event of that room
	monster_ids_json TEXT,
	rooms_json       TEXT,                      -- every room (a ? can resolve into two)
	turns_taken      INTEGER,
	current_hp       INTEGER,
	max_hp           INTEGER,
	current_gold     INTEGER,
	damage_taken     INTEGER,
	hp_healed        INTEGER,
	max_hp_gained    INTEGER,
	max_hp_lost      INTEGER,
	gold_gained      INTEGER,
	gold_spent       INTEGER,
	gold_lost        INTEGER,
	gold_stolen      INTEGER,
	cards_picked_json  TEXT,
	cards_skipped_json TEXT,
	relics_gained_json TEXT,
	potions_gained_json TEXT,
	stats_json       TEXT,                      -- the raw player_stats entry
	PRIMARY KEY (game_start_time, floor, player_id)
);
CREATE INDEX run_floors_model ON run_floors(model_id);

-- Offered-vs-picked choices per floor. kind: card | relic | potion | ancient.
-- The history file flattens every reward screen of a floor into one list, so
-- ordinal is the position in that list, not a reward-set id.
CREATE TABLE run_choices (
	game_start_time    INTEGER NOT NULL,
	floor              INTEGER NOT NULL,
	player_id          INTEGER NOT NULL,
	kind               TEXT NOT NULL,
	ordinal            INTEGER NOT NULL,
	item_id            TEXT,
	upgrade_level      INTEGER,
	enchantment_id     TEXT,
	was_picked         INTEGER NOT NULL,
	PRIMARY KEY (game_start_time, floor, player_id, kind, ordinal)
);
CREATE INDEX run_choices_item ON run_choices(kind, item_id, was_picked);

CREATE VIEW run_card_choices AS
	SELECT game_start_time, floor, player_id, ordinal, item_id AS card_id,
	       upgrade_level, enchantment_id, was_picked
	FROM run_choices WHERE kind = 'card';

-- What happened on a floor besides choices: card_gained, card_removed,
-- card_upgraded, card_downgraded, card_transformed (detail = final card),
-- card_enchanted (detail = enchantment), relic_bought, relic_removed,
-- potion_bought, colorless_bought, potion_used, potion_discarded,
-- rest_site_choice, event_choice, completed_quest.
CREATE TABLE run_floor_items (
	game_start_time INTEGER NOT NULL,
	floor           INTEGER NOT NULL,
	player_id       INTEGER NOT NULL,
	kind            TEXT NOT NULL,
	ordinal         INTEGER NOT NULL,
	item_id         TEXT,
	detail          TEXT,
	PRIMARY KEY (game_start_time, floor, player_id, kind, ordinal)
);
CREATE INDEX run_floor_items_item ON run_floor_items(kind, item_id);

CREATE TABLE run_deck (
	game_start_time    INTEGER NOT NULL,
	player_id          INTEGER NOT NULL,
	ordinal            INTEGER NOT NULL,
	card_id            TEXT NOT NULL,
	upgrade_level      INTEGER,
	enchantment_id     TEXT,
	enchantment_amount INTEGER,
	floor_added        INTEGER,
	PRIMARY KEY (game_start_time, player_id, ordinal)
);
CREATE INDEX run_deck_card ON run_deck(card_id);

CREATE TABLE run_relics (
	game_start_time INTEGER NOT NULL,
	player_id       INTEGER NOT NULL,
	ordinal         INTEGER NOT NULL,
	relic_id        TEXT NOT NULL,
	floor_added     INTEGER,
	PRIMARY KEY (game_start_time, player_id, ordinal)
);
CREATE INDEX run_relics_relic ON run_relics(relic_id);

-- ------------------------------------------------------------------ recordings
-- SpireLens replays/<start_time>/ (docs/run-recording.md).
CREATE TABLE recordings (
	game_start_time           INTEGER PRIMARY KEY,
	dir                       TEXT NOT NULL,
	header_ok                 INTEGER NOT NULL,
	format_version            INTEGER,
	decisions_format_version  INTEGER,
	spirelens_run_id          TEXT,
	created_at                TEXT,
	updated_at                TEXT,
	recording_started_mid_run INTEGER,
	game_version              TEXT,
	game_commit               TEXT,
	game_release_date         TEXT,
	main_assembly_hash        TEXT,
	model_id_hash             TEXT,
	spirelens_version         TEXT,
	spirelens_core_build      TEXT,
	net_type                  TEXT,
	seed_string               TEXT,
	seed_hash                 TEXT,             -- ulong, kept as text
	character                 TEXT,             -- players[0]
	player_count              INTEGER,
	ascension                 INTEGER,
	game_mode                 TEXT,
	daily_time_utc            TEXT,
	header_outcome            TEXT,
	ended_at                  TEXT,
	final_floor               INTEGER,
	canonical_outcome         TEXT,             -- last run_end on the canonical path
	death_undone              INTEGER NOT NULL DEFAULT 0, -- a branch cut a run_end away
	max_canonical_floor       INTEGER,
	event_count               INTEGER NOT NULL,
	canonical_event_count     INTEGER NOT NULL,
	torn_line_count           INTEGER NOT NULL,
	branch_count              INTEGER NOT NULL,
	unanchored_load_count     INTEGER NOT NULL,
	decision_count            INTEGER NOT NULL,
	canonical_decision_count  INTEGER NOT NULL,
	decision_segment_count    INTEGER NOT NULL,
	torn_decision_line_count  INTEGER NOT NULL,
	snapshot_file_count       INTEGER NOT NULL,
	combat_file_count         INTEGER NOT NULL,
	mods_json                 TEXT,
	players_json              TEXT,
	modifiers_json            TEXT,
	header_json               TEXT
);

CREATE TABLE recording_event_counts (
	game_start_time INTEGER NOT NULL,
	type            TEXT NOT NULL,
	total           INTEGER NOT NULL,
	canonical       INTEGER NOT NULL,
	PRIMARY KEY (game_start_time, type)
);

-- One row per load that cut events (ReplayBranch). anchor_seq is NULL for an
-- unanchored load.
CREATE TABLE recording_branches (
	game_start_time     INTEGER NOT NULL,
	ordinal             INTEGER NOT NULL,
	anchor_seq          INTEGER,
	load_seq            INTEGER NOT NULL,
	load_reason         TEXT,
	abandoned_events    INTEGER NOT NULL,
	first_abandoned_seq INTEGER,
	last_abandoned_seq  INTEGER,
	abandoned_decisions INTEGER NOT NULL,
	cut_run_end         TEXT,                   -- outcome of a run_end the branch cut (death undo)
	PRIMARY KEY (game_start_time, ordinal)
);

CREATE TABLE recording_save_points (
	game_start_time INTEGER NOT NULL,
	seq             INTEGER NOT NULL,
	t               TEXT,
	key             TEXT,
	snapshot        TEXT,
	reused          INTEGER,
	floor           INTEGER,
	act             INTEGER,
	canonical       INTEGER NOT NULL,
	PRIMARY KEY (game_start_time, seq)
);

CREATE TABLE recording_combats (
	game_start_time INTEGER NOT NULL,
	seq             INTEGER NOT NULL,
	file            TEXT,
	bytes           INTEGER,
	floor           INTEGER,
	canonical       INTEGER NOT NULL,
	PRIMARY KEY (game_start_time, seq)
);

-- One row per decision record (decisions/s<anchor>.jsonl[.gz]). canonical
-- follows the events.jsonl branch cut through event_seq; abandoned-branch
-- records are kept with canonical = 0. net_state's base64 is not copied
-- (build-specific, stays in the recording); net_state_form names it.
CREATE TABLE decisions (
	game_start_time     INTEGER NOT NULL,
	dseq                INTEGER NOT NULL,
	event_seq           INTEGER,
	event_type          TEXT,
	anchor_save_seq     INTEGER,
	segment_file        TEXT NOT NULL,
	format_version      INTEGER,
	t                   TEXT,
	kind                TEXT,
	phase               TEXT,
	player              INTEGER,
	floor               INTEGER,
	act                 INTEGER,
	coord               TEXT,
	in_combat           INTEGER,
	run_time            REAL,
	canonical           INTEGER NOT NULL,
	event_found         INTEGER NOT NULL,       -- event_seq exists in events.jsonl
	chosen_index        INTEGER,
	chosen_indexes_json TEXT,
	option_count        INTEGER,
	options_complete    INTEGER,
	chosen_json         TEXT,
	options_json        TEXT,
	selection_json      TEXT,
	state_json          TEXT,
	hidden_json         TEXT,
	notes_json          TEXT,
	net_state_form      TEXT,
	PRIMARY KEY (game_start_time, dseq)
);
CREATE INDEX decisions_kind ON decisions(kind, canonical);
CREATE INDEX decisions_canonical ON decisions(canonical, game_start_time, dseq);

-- ------------------------------------------------------------------- SpireLens
-- SpireLens runs/<run_id>.json (Core/RunData.cs). Several records can share a
-- game_start_time (older builds minted a new record on Continue); runs picks one.
CREATE TABLE spirelens_runs (
	run_id              TEXT PRIMARY KEY,
	game_start_time     INTEGER,
	source_path         TEXT NOT NULL,
	started_at          TEXT,
	updated_at          TEXT,
	ended_at            TEXT,
	character           TEXT,
	ascension           INTEGER,
	floor_reached       INTEGER,
	outcome             TEXT,
	card_instance_count INTEGER NOT NULL,
	relic_count         INTEGER NOT NULL,
	event_count         INTEGER NOT NULL,
	raw_json            TEXT NOT NULL
);
CREATE INDEX spirelens_runs_start ON spirelens_runs(game_start_time);

-- aggregates are keyed "CARD.X#n" per physical card (per-instance shape).
CREATE TABLE spirelens_card_aggregates (
	run_id                TEXT NOT NULL,
	instance_key          TEXT NOT NULL,
	card_id               TEXT NOT NULL,
	instance_number       INTEGER,
	plays                 INTEGER,
	combats_in_deck       INTEGER,
	turns_in_deck         INTEGER,
	times_drawn           INTEGER,
	total_intended        INTEGER,
	total_effective       INTEGER,
	total_blocked         INTEGER,
	total_overkill        INTEGER,
	kills                 INTEGER,
	total_energy_spent    INTEGER,
	total_block_gained    INTEGER,
	total_block_effective INTEGER,
	times_exhausted       INTEGER,
	floor_added           INTEGER,
	initial_upgrade_level INTEGER,
	removed               INTEGER,
	raw_json              TEXT NOT NULL,
	PRIMARY KEY (run_id, instance_key)
);
CREATE INDEX spirelens_card_aggregates_card ON spirelens_card_aggregates(card_id);

CREATE TABLE spirelens_relic_aggregates (
	run_id                  TEXT NOT NULL,
	relic_id                TEXT NOT NULL,
	activations             INTEGER,
	energy_generated        INTEGER,
	additional_cards_drawn  INTEGER,
	additional_block_gained INTEGER,
	total_healing_restored  REAL,              -- can be fractional
	raw_json                TEXT NOT NULL,
	PRIMARY KEY (run_id, relic_id)
);
CREATE INDEX spirelens_relic_aggregates_relic ON spirelens_relic_aggregates(relic_id);
`

// schemaV2 drops the per-card and per-relic raw_json columns. Each value was
// a verbatim copy of an entry already in its run's spirelens_runs.raw_json
// ($.aggregates / $.relic_aggregates), so it added size but no information.
// The headline numeric columns stay.
const schemaV2 = `
ALTER TABLE spirelens_card_aggregates DROP COLUMN raw_json;
ALTER TABLE spirelens_relic_aggregates DROP COLUMN raw_json;
`

// Open opens (creating if needed) the database at path and applies any
// pending migrations.
func Open(ctx context.Context, path string) (*sql.DB, error) {
	// A plain path (no "file:" URI) so Windows backslashes need no escaping;
	// the driver strips the ?_pragma query itself.
	dsn := path +
		"?_pragma=journal_mode(WAL)" +
		"&_pragma=busy_timeout(10000)" +
		"&_pragma=synchronous(NORMAL)" +
		"&_pragma=foreign_keys(ON)"
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, err
	}
	// One connection: pragmas are per connection and the tool is single-threaded.
	db.SetMaxOpenConns(1)
	if err := db.PingContext(ctx); err != nil {
		db.Close()
		return nil, fmt.Errorf("open %s: %w", path, err)
	}
	if err := migrate(ctx, db); err != nil {
		db.Close()
		return nil, err
	}
	return db, nil
}

func currentVersion(ctx context.Context, db *sql.DB) (int, error) {
	var exists int
	if err := db.QueryRowContext(ctx,
		`SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version'`).Scan(&exists); err != nil {
		return 0, err
	}
	if exists == 0 {
		return 0, nil
	}
	var v sql.NullInt64
	if err := db.QueryRowContext(ctx, `SELECT max(version) FROM schema_version`).Scan(&v); err != nil {
		return 0, err
	}
	return int(v.Int64), nil
}

func migrate(ctx context.Context, db *sql.DB) error {
	have, err := currentVersion(ctx, db)
	if err != nil {
		return fmt.Errorf("read schema version: %w", err)
	}
	if have > LatestSchemaVersion() {
		return fmt.Errorf("database schema version %d is newer than this tool (%d); update spirelens-db", have, LatestSchemaVersion())
	}
	vacuum := false
	for _, m := range migrations {
		if m.version <= have {
			continue
		}
		// A fresh database has nothing to reclaim.
		vacuum = vacuum || (m.vacuum && have > 0)
		tx, err := db.BeginTx(ctx, nil)
		if err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, m.sql); err != nil {
			tx.Rollback()
			return fmt.Errorf("migration %d (%s): %w", m.version, m.description, err)
		}
		if _, err := tx.ExecContext(ctx,
			`INSERT INTO schema_version (version, description, applied_at) VALUES (?, ?, ?)`,
			m.version, m.description, time.Now().UTC().Format(time.RFC3339)); err != nil {
			tx.Rollback()
			return err
		}
		if err := tx.Commit(); err != nil {
			return err
		}
	}
	if vacuum {
		// Rewrites the file so the space the migration freed is returned to
		// the OS (dropping a column alone only leaves free pages).
		if _, err := db.ExecContext(ctx, `VACUUM`); err != nil {
			return fmt.Errorf("vacuum after migration: %w", err)
		}
	}
	return nil
}
