# Run Database

`spirelens-db` builds one SQLite file out of everything this laptop knows
about Slay the Spire 2 runs, for two uses: out-of-game stats now, an in-game
run list later, and training data for a model that plays (see "Decision
records" in [run-recording.md](run-recording.md)).

The database is **derived**. The source files stay the source of truth and
are never modified; deleting the database and re-ingesting rebuilds it.

Code:

- `cmd/spirelens-db/main.go`: the CLI.
- `internal/rundb/`: schema and migrations (`schema.go`), the three importers
  (`recording.go`, `history.go`, `spirelens.go`), the derived `runs` table
  (`runs.go`), `stats.go` and `export.go`.
- `internal/replayformat/`: the Go port of the pure recording-format logic
  in `Core/Replay/ReplayFormat.cs` and `DecisionFormat.cs`. Its tests repeat
  the C# test cases (`RunRecordingFormatTests`, `DecisionRecordFormatTests`).
  **Change both sides together**: a change to the timeline cut in C# needs the
  same change and test here.

## Decisions

Made by the user on 2026-10-14:

- **SQLite never runs inside the hot-reloaded Core.** Native DLL locking and
  reload behavior rule it out. The importer is a standalone Go tool in this
  repo's Go module, using the pure-Go `modernc.org/sqlite` driver (no cgo).
  `modernc.org/sqlite` is pinned to v1.59.0, the last release whose `go.mod`
  still says `go 1.25`; newer ones would raise the module's Go version.
- **Storage lives on this laptop.** The default database is
  `%APPDATA%\SlayTheSpire2\SpireLens\spirelens.db` (plus its `-wal`/`-shm`).
  The raw recordings are backed up to the Synology NAS separately; the
  database is not, because it can be rebuilt.
- **Keep everything forever.** Abandoned branches are kept with
  `canonical = 0`. A source file that disappears keeps its rows.
- **The game's own history files are imported as summaries.** They carry no
  inputs, so they can never be replayed or used as training records.
- **SpireLens stat records** are linked at the run level, a handful of
  headline aggregate numbers are typed into columns, and the raw JSON is kept
  so the other ~2000 fields stay reachable with `json_extract`.

## Commands

Build it with `go build -o spirelens-db.exe ./cmd/spirelens-db`, or run it
with `go run ./cmd/spirelens-db <command>`.

```
spirelens-db ingest [--db path] [--replays dir] [--history dir]... [--no-history]
                    [--spirelens-runs dir] [--force] [--quiet]
spirelens-db stats [--db path]
spirelens-db export-decisions --out file.jsonl [--db path] [--drop-hidden]
                    [--include-abandoned] [--finished-only] [--kinds a,b]
```

Default paths come from `%APPDATA%\SlayTheSpire2`:

| flag | default |
|---|---|
| `--db` | `SpireLens\spirelens.db` |
| `--replays` | `SpireLens\replays` |
| `--spirelens-runs` | `SpireLens\runs` |
| `--history` | every `steam\<steamid>\profileN\saves\history` and `steam\<steamid>\modded\profileN\saves\history` |

Pass an empty string (`--replays ""`) or `--no-history` to skip a source.

### ingest

`ingest` is incremental and idempotent. It is safe to run while the game is
running: it only reads, and a recording that is still being written is
re-read on the next ingest because its fingerprint will have changed.

- **History and SpireLens files** are tracked in `source_files` by size and
  mtime. A file whose size and mtime match is not opened. A touched file
  whose sha256 still matches is not re-imported.
- **Recording directories** are fingerprinted by a hash of every file's
  `(path, size, mtime)`. A changed directory is re-imported whole: its rows in
  the recording tables are deleted and rewritten in one transaction.
- **`runs` is rebuilt** from the source tables at the end of every ingest.
- `--force` re-imports everything.

It never aborts on bad input. A malformed or unreadable file is skipped with
a warning, and is counted under `failed` in the summary. Torn lines, which
the recorder can leave as the last line after a crash, are skipped and
counted. A truncated gzip segment keeps the records read before the
truncation. Only database errors abort the ingest.

Memory: `events.jsonl` is streamed, and only envelopes are kept (payloads
are kept only for `save_point`, `load`, `combat_replay_file` and `run_end`).
Decision segments are streamed record by record straight into the
database.

### stats

`stats` prints headline queries: runs by source, overall win rate, by
character, by ascension, most-picked reward cards, cards in winning decks,
deadliest encounters, most-played cards (SpireLens), decision-record counts
by kind, and a per-recording summary. The queries live in
`internal/rundb/stats.go` and serve as worked examples of the schema.

### export-decisions

`export-decisions` writes one JSON object per line, in run then `dseq` order:

```
game_start_time, dseq, event_seq, canonical, format_version, t, kind, phase, player,
context {floor, act, coord, in_combat, run_time},
state, options, options_complete, selection, chosen, chosen_index, chosen_indexes,
hidden          (omitted with --drop-hidden)
notes,
labels {outcome, win, character, ascension, floor_reached, killed_by, game_version, game_commit}
```

The record fields are the recorder's own (see "Record shape" in
[run-recording.md](run-recording.md)). `labels` are the run's final result
from the `runs` table. `labels.win` is null while a run is in progress.

- By default only canonical records are written: the timeline the run
  actually followed. Use `--include-abandoned` to add save-scummed and
  restarted branches; those records have `canonical: false`.
- `--drop-hidden` removes `hidden` (true draw-pile order and monster moves).
  A trainer of a fair player should use it.
- `net_state` is not exported, and is not stored in the database. It is
  build-specific; read it from the recording if needed.

## Sources

### Recordings (`replays/<start_time>/`)

The format is described in [run-recording.md](run-recording.md).

- `header.json` becomes one `recordings` row. A missing or malformed header
  still imports the events and decisions (`header_ok = 0`).
- `events.jsonl` is parsed with `replayformat.ParseEnvelope`, then
  `BuildCanonicalTimeline` runs on it. Every event is either on the
  canonical path or on a branch cut by a load.
  - `recording_event_counts` holds the total and canonical count per event
    type.
  - `recording_branches` has one row per cut, with the load's `reason`, and
    `cut_run_end` set when the cut removed a `run_end` (a death undo).
  - `recording_save_points` and `recording_combats` index the snapshots and
    `.mcr` files.
- `decisions/s<anchor>[.<n>].jsonl[.gz]` becomes one `decisions` row per
  record, read in anchor order, then closed parts, then the open plain
  segment.
  - `canonical` is 1 exactly when the record's `event_seq` is on the
    canonical path (`DecisionFormat.CanonicalRecords`).
  - `event_found = 0` flags a record whose event line is missing (for
    example a torn last line).
  - If a `dseq` repeats, the later part wins.
- `canonical_outcome` is the outcome of the last `run_end` on the canonical
  path. `death_undone` is set when some branch cut a `run_end`.

### Game history (`saves/history/*.run`)

These files are the game's `RunHistory` JSON, written at run end and named
`{start_time}.run`.

- The same run appears more than once. The game keeps `.run.backup` copies,
  and the modded profiles were seeded from the vanilla ones, so
  `profile1\…\X.run` and `modded\profile1\…\X.run` are the same run. Runs
  are deduplicated by `start_time` read from the JSON.
- Which copy is used: a real `.run` beats a `.run.backup`, then the newest
  mtime, then the path. The other copies are marked `duplicate` in
  `source_files`, and `history_runs.copy_paths` lists them all. If the copies
  differ in content, a warning is printed and the count is reported as
  `runs_with_differing_copies`; on 2026-10-14 every copy was byte-identical.
- A file contains:
  - `history_runs`: run-level fields plus `raw_json`;
  - `run_players`: one row per player (multiplayer runs have two);
  - `run_floors`: one row per map point per player, with HP, gold and the
    room's encounter or event;
  - `run_choices`: what was offered and whether it was picked: card
    rewards, relic and potion choices, and Ancient options;
  - `run_floor_items`: what happened on a floor: cards gained, removed,
    upgraded, transformed or enchanted, things bought, potions used, rest
    site and event choices;
  - `run_deck` and `run_relics`: the final deck and relics.
- `floor` is the map point's 1-based index over the whole run. It matches
  SpireLens's `floor_reached` for the last floor.
- The history file flattens every card reward screen on a floor into one
  `card_choices` list, so `run_choices.ordinal` is a position in that list,
  not a reward-set id.

### SpireLens stat records (`runs/*.json`)

The shape is `Core/RunData.cs`. Only `*.json` files are read; `*.json.*.bak`
repair backups are ignored.

- `spirelens_runs` has one row per file, keyed by `run_id`, with the whole
  file in `raw_json`. Files without `game_start_time` are kept but are not
  linked to a run.
- `spirelens_card_aggregates` is keyed by the per-instance key
  (`CARD.STRIKE_IRONCLAD#3`), split into `card_id` and `instance_number`.
  - Typed columns: plays, damage, block, draws, energy, kills,
    `floor_added`, `removed`.
- `spirelens_relic_aggregates` is keyed by relic id. It holds activations,
  energy, draws, block and healing (healing can be fractional).
- Neither aggregate table has its own `raw_json`. Each aggregate already sits
  verbatim in its run's `spirelens_runs.raw_json`, under `$.aggregates` or
  `$.relic_aggregates` keyed by `instance_key` or `relic_id`. Schema v2
  dropped the per-aggregate copies (see "Example queries" for reaching a
  field that has no column).
- Older SpireLens builds minted a new record on every Continue, so several
  records can share a `game_start_time`. `runs.spirelens_run_id` picks the
  one the recording header names, or else the most recently updated.

## Schema

`schema_version` records each applied migration. Migrations are listed in
`internal/rundb/schema.go`. They are **additive and append-only**: never edit
a shipped one, add a new version. A database newer than the tool is
refused. A migration that frees a lot of space is followed by a `VACUUM`, so
the file actually shrinks; this happens once, on the first run of a newer
tool.

| version | change |
|---|---|
| 1 | Initial schema. |
| 2 | Dropped `raw_json` from `spirelens_card_aggregates` and `spirelens_relic_aggregates`. It duplicated `spirelens_runs.raw_json`. |

| table | key | what |
|---|---|---|
| `runs` | `game_start_time` | One row per game run, merged from all sources (see below). |
| `source_files` | `path` | Ingest bookkeeping: kind, size, mtime, fingerprint, status (`ok`, `duplicate`, `error`), message. |
| `history_runs` | `game_start_time` | Game history run-level fields plus `raw_json`. |
| `run_players` | `(game_start_time, player_id)` | Character, badges, potions. |
| `run_floors` | `(game_start_time, floor, player_id)` | Per-floor history. |
| `run_choices` | `(game_start_time, floor, player_id, kind, ordinal)` | Offered or picked (`card`, `relic`, `potion`, `ancient`). The view `run_card_choices` holds the cards only. |
| `run_floor_items` | `(game_start_time, floor, player_id, kind, ordinal)` | Floor events (`card_gained`, `card_removed`, `relic_bought`, and so on). |
| `run_deck`, `run_relics` | `(game_start_time, player_id, ordinal)` | Final deck and relics. |
| `recordings` | `game_start_time` | Header fields and timeline and decision counts. |
| `recording_event_counts` | `(game_start_time, type)` | Total and canonical events per type. |
| `recording_branches` | `(game_start_time, ordinal)` | Each load that cut events. |
| `recording_save_points`, `recording_combats` | `(game_start_time, seq)` | Snapshot and `.mcr` index, with `canonical`. |
| `decisions` | `(game_start_time, dseq)` | Decision records: indexed fields plus `state_json`, `options_json`, `chosen_json`, `hidden_json`, and so on. |
| `spirelens_runs` | `run_id` | SpireLens records plus `raw_json`. |
| `spirelens_card_aggregates` | `(run_id, instance_key)` | Per physical card. |
| `spirelens_relic_aggregates` | `(run_id, relic_id)` | Per relic. |

**How `runs` is merged.** The key is the game's `start_time`. It is the
history file name, the replays directory name and SpireLens's
`game_start_time`. For each column, the first source that has a value wins:

- `character`, `ascension`: history (`players[0]`), then recording, then
  SpireLens.
- `seed`, `game_mode`, `game_version`: history, then recording.
  `game_commit` comes only from the recording.
- `outcome`:
  1. The recording's canonical `run_end`, if there is one.
  2. `in_progress`, if the recording undid a death. The history file still
     says `loss` until the run really ends.
  3. Otherwise history: `win`, then `abandoned` if `was_abandoned`, else
     `loss`.
  4. Then the recording header, then SpireLens.
- `floor_reached`: history's map-point count, then the recording. After a
  death undo, the recording's canonical maximum is used.
- `killed_by`: the history encounter, else the history event. It is null
  while a run is in progress.
- `recorded_build_matches_current` is never written by ingest. It is left for
  a build-aware tool to fill.

## Example queries

```sql
-- Win rate per character at A10
SELECT character, count(*) runs, sum(outcome = 'win') wins
FROM runs WHERE ascension = 10 AND outcome IN ('win','loss','abandoned')
GROUP BY character;

-- Pick rate of a card when offered
SELECT sum(was_picked) * 1.0 / count(*) FROM run_card_choices WHERE card_id = 'CARD.DEFY';

-- HP lost per elite fight, by encounter
SELECT model_id, avg(damage_taken), count(*) FROM run_floors
WHERE room_type = 'elite' GROUP BY model_id ORDER BY 2 DESC;

-- A SpireLens aggregate field that has no column: read it from the run's raw JSON
SELECT a.card_id, sum(json_extract(j.value, '$.times_discarded'))
FROM spirelens_card_aggregates a
JOIN spirelens_runs s USING (run_id)
JOIN json_each(s.raw_json, '$.aggregates') j ON j.key = a.instance_key
GROUP BY a.card_id;

-- Canonical combat decisions of one run
SELECT dseq, kind, chosen_index, option_count FROM decisions
WHERE game_start_time = 1791840595 AND canonical = 1 AND phase = 'combat';
```

## Known limits

- **Size.** About 392 MB on 2026-10-14, after schema v2 (it was 605 MB with
  the per-aggregate copies). The largest part is the SpireLens runs'
  `raw_json` (about 240 MB). Decision records add about 37 MB per 7k
  records.
- **Multiplayer history.** `runs.character` is `players[0]`, and the file
  does not say which player was local. `run_floors`, `run_deck` and the other
  per-player tables keep every player.
- **Recordings without a history file** are in-progress runs, or runs the
  game never closed. Their `runs` row comes from the recording header and the
  canonical timeline.
