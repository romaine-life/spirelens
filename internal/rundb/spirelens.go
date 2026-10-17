package rundb

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// SpireLens's per-run stat record (Core/RunData.cs, snake_case JSON). Only
// run-level linkage and a handful of headline aggregate numbers are typed;
// everything else stays reachable through raw_json.
type spirelensRun struct {
	RunID           string                     `json:"run_id"`
	StartedAt       string                     `json:"started_at"`
	UpdatedAt       string                     `json:"updated_at"`
	EndedAt         *string                    `json:"ended_at"`
	Character       *string                    `json:"character"`
	Ascension       *int64                     `json:"ascension"`
	FloorReached    *int64                     `json:"floor_reached"`
	Outcome         string                     `json:"outcome"`
	GameStartTime   *int64                     `json:"game_start_time"`
	Aggregates      map[string]json.RawMessage `json:"aggregates"`
	RelicAggregates map[string]json.RawMessage `json:"relic_aggregates"`
	Events          []json.RawMessage          `json:"events"`
}

// The typed headline fields are json.Number: some aggregates are decimals
// (healing is fractional under some relics), and a mistyped field must not
// drop the whole aggregate.
type spirelensCardAggregate struct {
	Plays               json.Number `json:"plays"`
	CombatsInDeck       json.Number `json:"combats_in_deck"`
	TurnsInDeck         json.Number `json:"turns_in_deck"`
	TimesDrawn          json.Number `json:"times_drawn"`
	TotalIntended       json.Number `json:"total_intended"`
	TotalEffective      json.Number `json:"total_effective"`
	TotalBlocked        json.Number `json:"total_blocked"`
	TotalOverkill       json.Number `json:"total_overkill"`
	Kills               json.Number `json:"kills"`
	TotalEnergySpent    json.Number `json:"total_energy_spent"`
	TotalBlockGained    json.Number `json:"total_block_gained"`
	TotalBlockEffective json.Number `json:"total_block_effective"`
	TimesExhausted      json.Number `json:"times_exhausted"`
	FloorAdded          json.Number `json:"floor_added"`
	InitialUpgradeLevel json.Number `json:"initial_upgrade_level"`
	Removed             *bool       `json:"removed"`
}

type spirelensRelicAggregate struct {
	Activations           json.Number `json:"activations"`
	EnergyGenerated       json.Number `json:"energy_generated"`
	AdditionalCardsDrawn  json.Number `json:"additional_cards_drawn"`
	AdditionalBlockGained json.Number `json:"additional_block_gained"`
	TotalHealingRestored  json.Number `json:"total_healing_restored"`
}

// splitInstanceKey splits "CARD.STRIKE_IRONCLAD#3" into ("CARD.STRIKE_IRONCLAD", 3).
// A pooled key without "#n" returns (key, nil).
func splitInstanceKey(key string) (string, any) {
	i := strings.LastIndexByte(key, '#')
	if i < 0 {
		return key, nil
	}
	n, err := strconv.ParseInt(key[i+1:], 10, 64)
	if err != nil {
		return key, nil
	}
	return key[:i], n
}

func (in *ingester) ingestSpireLensRuns(ctx context.Context, dir string, rep *SourceReport) error {
	prior, err := in.loadSources(ctx, "spirelens")
	if err != nil {
		return err
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		in.warnf("spirelens: cannot list %s: %v", dir, err)
		return nil
	}
	names := make([]string, 0, len(entries))
	for _, ent := range entries {
		if ent.IsDir() {
			continue
		}
		// Only <run_id>.json; *.json.*.bak repair backups are not run records.
		if !strings.EqualFold(filepath.Ext(ent.Name()), ".json") {
			rep.note("ignored_non_json_files", 1)
			continue
		}
		names = append(names, ent.Name())
	}
	sort.Strings(names)

	for _, name := range names {
		rep.Seen++
		path := filepath.Join(dir, name)
		info, err := os.Stat(path)
		if err != nil {
			in.warnf("spirelens: cannot stat %s: %v", path, err)
			rep.Failed++
			continue
		}
		if p, ok := prior[path]; ok && !in.opts.Force && p.unchanged(info) {
			rep.Unchanged++
			continue
		}
		content, info, err := readFileStable(path)
		if err != nil {
			in.warnf("spirelens: cannot read %s: %v", path, err)
			rep.Failed++
			continue
		}
		sha := sha256Hex(content)
		size, mtime := info.Size(), info.ModTime().UnixNano()
		if p, ok := prior[path]; ok && !in.opts.Force && p.fingerprint == sha && p.status == "ok" {
			// Touched but identical: just remember the new mtime.
			if err := in.putSource(ctx, in.db, path, "spirelens", size, mtime, sha, int64Ptr(p.startTime), "ok", ""); err != nil {
				return err
			}
			rep.Unchanged++
			continue
		}

		var run spirelensRun
		if err := json.Unmarshal(content, &run); err != nil {
			in.warnf("spirelens: skipping malformed %s: %v", path, err)
			rep.Failed++
			if err := in.putSource(ctx, in.db, path, "spirelens", size, mtime, sha, nil, "error", err.Error()); err != nil {
				return err
			}
			continue
		}
		if run.RunID == "" {
			run.RunID = strings.TrimSuffix(name, filepath.Ext(name))
			rep.note("run_id_from_file_name", 1)
		}
		if run.GameStartTime == nil {
			rep.note("records_without_game_start_time", 1)
		}

		tx, err := in.db.BeginTx(ctx, nil)
		if err != nil {
			return err
		}
		if err := writeSpireLensRun(ctx, tx, path, &run, content, rep); err != nil {
			tx.Rollback()
			return fmt.Errorf("%s: %w", path, err)
		}
		if err := in.putSource(ctx, tx, path, "spirelens", size, mtime, sha, run.GameStartTime, "ok", ""); err != nil {
			tx.Rollback()
			return err
		}
		if err := tx.Commit(); err != nil {
			return err
		}
		rep.Ingested++
	}
	return nil
}

func writeSpireLensRun(ctx context.Context, tx execer, path string, run *spirelensRun, content []byte, rep *SourceReport) error {
	for _, table := range []string{"spirelens_card_aggregates", "spirelens_relic_aggregates", "spirelens_runs"} {
		if _, err := tx.ExecContext(ctx, "DELETE FROM "+table+" WHERE run_id = ?", run.RunID); err != nil {
			return err
		}
	}
	if _, err := tx.ExecContext(ctx, `
		INSERT INTO spirelens_runs (run_id, game_start_time, source_path, started_at, updated_at, ended_at, character,
			ascension, floor_reached, outcome, card_instance_count, relic_count, event_count, raw_json)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		run.RunID, nullInt64Ptr(run.GameStartTime), path, nullString(run.StartedAt), nullString(run.UpdatedAt),
		nullStringPtr(run.EndedAt), nullStringPtr(run.Character), nullInt64Ptr(run.Ascension), nullInt64Ptr(run.FloorReached),
		nullString(run.Outcome), len(run.Aggregates), len(run.RelicAggregates), len(run.Events), string(content)); err != nil {
		return err
	}

	keys := make([]string, 0, len(run.Aggregates))
	for k := range run.Aggregates {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, key := range keys {
		raw := run.Aggregates[key]
		var a spirelensCardAggregate
		if err := json.Unmarshal(raw, &a); err != nil {
			rep.note("malformed_card_aggregates", 1)
			continue
		}
		cardID, number := splitInstanceKey(key)
		if _, err := tx.ExecContext(ctx, `
			INSERT INTO spirelens_card_aggregates (run_id, instance_key, card_id, instance_number, plays, combats_in_deck,
				turns_in_deck, times_drawn, total_intended, total_effective, total_blocked, total_overkill, kills,
				total_energy_spent, total_block_gained, total_block_effective, times_exhausted, floor_added,
				initial_upgrade_level, removed)
			VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
			run.RunID, key, cardID, number, numberValue(a.Plays), numberValue(a.CombatsInDeck),
			numberValue(a.TurnsInDeck), numberValue(a.TimesDrawn), numberValue(a.TotalIntended), numberValue(a.TotalEffective),
			numberValue(a.TotalBlocked), numberValue(a.TotalOverkill), numberValue(a.Kills), numberValue(a.TotalEnergySpent),
			numberValue(a.TotalBlockGained), numberValue(a.TotalBlockEffective), numberValue(a.TimesExhausted),
			numberValue(a.FloorAdded), numberValue(a.InitialUpgradeLevel), nullBoolPtr(a.Removed)); err != nil {
			return err
		}
	}

	keys = keys[:0]
	for k := range run.RelicAggregates {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, key := range keys {
		raw := run.RelicAggregates[key]
		var a spirelensRelicAggregate
		if err := json.Unmarshal(raw, &a); err != nil {
			rep.note("malformed_relic_aggregates", 1)
			continue
		}
		if _, err := tx.ExecContext(ctx, `
			INSERT INTO spirelens_relic_aggregates (run_id, relic_id, activations, energy_generated, additional_cards_drawn,
				additional_block_gained, total_healing_restored)
			VALUES (?, ?, ?, ?, ?, ?, ?)`,
			run.RunID, key, numberValue(a.Activations), numberValue(a.EnergyGenerated), numberValue(a.AdditionalCardsDrawn),
			numberValue(a.AdditionalBlockGained), numberValue(a.TotalHealingRestored)); err != nil {
			return err
		}
	}
	return nil
}
