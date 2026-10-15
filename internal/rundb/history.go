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

// The game's run history file (MegaCrit.Sts2.Core.Runs.RunHistory), written
// to saves/history/{start_time}.run at run end. Only the fields the tables
// use are typed; the whole file is kept in history_runs.raw_json.
type historyRun struct {
	SchemaVersion     *int64              `json:"schema_version"`
	PlatformType      string              `json:"platform_type"`
	GameMode          string              `json:"game_mode"`
	Win               *bool               `json:"win"`
	Seed              string              `json:"seed"`
	StartTime         *int64              `json:"start_time"`
	RunTime           json.Number         `json:"run_time"`
	Ascension         *int64              `json:"ascension"`
	BuildID           string              `json:"build_id"`
	WasAbandoned      *bool               `json:"was_abandoned"`
	KilledByEncounter string              `json:"killed_by_encounter"`
	KilledByEvent     string              `json:"killed_by_event"`
	Players           []historyPlayer     `json:"players"`
	Acts              json.RawMessage     `json:"acts"`
	Modifiers         json.RawMessage     `json:"modifiers"`
	MapPointHistory   [][]historyMapPoint `json:"map_point_history"`
}

type historyCard struct {
	ID                  string `json:"id"`
	CurrentUpgradeLevel *int64 `json:"current_upgrade_level"`
	Enchantment         *struct {
		ID     string `json:"id"`
		Amount *int64 `json:"amount"`
	} `json:"enchantment"`
	FloorAddedToDeck *int64 `json:"floor_added_to_deck"`
}

func (c historyCard) enchantmentID() any {
	if c.Enchantment == nil {
		return nil
	}
	return nullString(c.Enchantment.ID)
}

type historyPlayer struct {
	ID        *int64        `json:"id"`
	Character string        `json:"character"`
	Deck      []historyCard `json:"deck"`
	Relics    []struct {
		ID               string `json:"id"`
		FloorAddedToDeck *int64 `json:"floor_added_to_deck"`
	} `json:"relics"`
	Potions            json.RawMessage `json:"potions"`
	Badges             json.RawMessage `json:"badges"`
	MaxPotionSlotCount *int64          `json:"max_potion_slot_count"`
}

type historyRoom struct {
	RoomType   string          `json:"room_type"`
	ModelID    *string         `json:"model_id"`
	MonsterIDs json.RawMessage `json:"monster_ids"`
	TurnsTaken *int64          `json:"turns_taken"`
}

type historyMapPoint struct {
	MapPointType string            `json:"map_point_type"`
	Rooms        []historyRoom     `json:"rooms"`
	PlayerStats  []json.RawMessage `json:"player_stats"`
}

type historyChoice struct {
	Choice    string `json:"choice"`
	WasPicked bool   `json:"was_picked"`
}

type historyPlayerStats struct {
	PlayerID    *int64 `json:"player_id"`
	CurrentGold *int64 `json:"current_gold"`
	CurrentHP   *int64 `json:"current_hp"`
	MaxHP       *int64 `json:"max_hp"`
	DamageTaken *int64 `json:"damage_taken"`
	HPHealed    *int64 `json:"hp_healed"`
	MaxHPGained *int64 `json:"max_hp_gained"`
	MaxHPLost   *int64 `json:"max_hp_lost"`
	GoldGained  *int64 `json:"gold_gained"`
	GoldSpent   *int64 `json:"gold_spent"`
	GoldLost    *int64 `json:"gold_lost"`
	GoldStolen  *int64 `json:"gold_stolen"`
	CardChoices []struct {
		Card      historyCard `json:"card"`
		WasPicked bool        `json:"was_picked"`
	} `json:"card_choices"`
	RelicChoices  []historyChoice `json:"relic_choices"`
	PotionChoices []historyChoice `json:"potion_choices"`
	AncientChoice []struct {
		TextKey   string `json:"TextKey"`
		WasChosen bool   `json:"was_chosen"`
	} `json:"ancient_choice"`
	EventChoices []struct {
		Title struct {
			Key string `json:"key"`
		} `json:"title"`
	} `json:"event_choices"`
	CardsGained      []historyCard `json:"cards_gained"`
	CardsRemoved     []historyCard `json:"cards_removed"`
	CardsTransformed []struct {
		OriginalCard historyCard `json:"original_card"`
		FinalCard    historyCard `json:"final_card"`
	} `json:"cards_transformed"`
	CardsEnchanted []struct {
		Card        historyCard `json:"card"`
		Enchantment string      `json:"enchantment"`
	} `json:"cards_enchanted"`
	UpgradedCards   []string `json:"upgraded_cards"`
	DowngradedCards []string `json:"downgraded_cards"`
	BoughtRelics    []string `json:"bought_relics"`
	BoughtPotions   []string `json:"bought_potions"`
	BoughtColorless []string `json:"bought_colorless"`
	PotionUsed      []string `json:"potion_used"`
	PotionDiscarded []string `json:"potion_discarded"`
	RelicsRemoved   []string `json:"relics_removed"`
	RestSiteChoices []string `json:"rest_site_choices"`
	CompletedQuests []string `json:"completed_quests"`
}

type historyFile struct {
	path      string
	isBackup  bool
	size      int64
	mtimeNs   int64
	sha       string
	startTime int64
	content   []byte // nil when unchanged and not read
	changed   bool
}

// isHistoryName accepts "<digits>.run" and "<digits>.run.backup".
func isHistoryName(name string) (backup bool, ok bool) {
	base := name
	switch {
	case strings.HasSuffix(name, ".run.backup"):
		base, backup = strings.TrimSuffix(name, ".run.backup"), true
	case strings.HasSuffix(name, ".run"):
		base = strings.TrimSuffix(name, ".run")
	default:
		return false, false
	}
	if _, err := strconv.ParseInt(base, 10, 64); err != nil {
		return false, false
	}
	return backup, true
}

func (in *ingester) ingestHistory(ctx context.Context, dirs []string, rep *SourceReport) error {
	prior, err := in.loadSources(ctx, "history")
	if err != nil {
		return err
	}

	var files []*historyFile
	seenDir := map[string]bool{}
	for _, dir := range dirs {
		abs, _ := filepath.Abs(dir)
		if seenDir[strings.ToLower(abs)] {
			continue
		}
		seenDir[strings.ToLower(abs)] = true
		entries, err := os.ReadDir(dir)
		if err != nil {
			in.warnf("history: cannot list %s: %v", dir, err)
			continue
		}
		for _, ent := range entries {
			if ent.IsDir() {
				continue
			}
			backup, ok := isHistoryName(ent.Name())
			if !ok {
				rep.note("ignored_non_run_files", 1)
				continue
			}
			rep.Seen++
			path := filepath.Join(dir, ent.Name())
			info, err := ent.Info()
			if err != nil {
				in.warnf("history: cannot stat %s: %v", path, err)
				rep.Failed++
				continue
			}
			hf := &historyFile{path: path, isBackup: backup, size: info.Size(), mtimeNs: info.ModTime().UnixNano()}
			if p, ok := prior[path]; ok && !in.opts.Force && p.unchanged(info) {
				if p.status == "error" || !p.startTime.Valid {
					rep.note("still_failing_unchanged", 1)
					continue
				}
				hf.sha = p.fingerprint
				hf.startTime = p.startTime.Int64
				files = append(files, hf)
				continue
			}
			content, info2, err := readFileStable(path)
			if err != nil {
				in.warnf("history: cannot read %s: %v", path, err)
				rep.Failed++
				continue
			}
			hf.size, hf.mtimeNs = info2.Size(), info2.ModTime().UnixNano()
			hf.sha = sha256Hex(content)
			hf.content = content
			hf.changed = true
			var head struct {
				StartTime *int64 `json:"start_time"`
			}
			if err := json.Unmarshal(content, &head); err != nil || head.StartTime == nil {
				msg := "missing start_time"
				if err != nil {
					msg = err.Error()
				}
				in.warnf("history: skipping malformed %s: %s", path, msg)
				rep.Failed++
				if err := in.putSource(ctx, in.db, path, "history", hf.size, hf.mtimeNs, hf.sha, nil, "error", msg); err != nil {
					return err
				}
				continue
			}
			hf.startTime = *head.StartTime
			if name := filepath.Base(path); !strings.HasPrefix(name, strconv.FormatInt(hf.startTime, 10)+".") {
				rep.note("name_start_time_mismatch", 1)
				in.warnf("history: %s has start_time %d (name disagrees); keyed by start_time", path, hf.startTime)
			}
			files = append(files, hf)
		}
	}

	// Dedupe: one run per start_time, across profiles and .backup copies.
	groups := map[int64][]*historyFile{}
	for _, f := range files {
		groups[f.startTime] = append(groups[f.startTime], f)
	}
	keys := make([]int64, 0, len(groups))
	for k := range groups {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { return keys[i] < keys[j] })

	existing := map[int64]string{}
	{
		rows, err := in.db.QueryContext(ctx, `SELECT game_start_time, content_sha256 FROM history_runs`)
		if err != nil {
			return err
		}
		for rows.Next() {
			var st int64
			var sha string
			if err := rows.Scan(&st, &sha); err != nil {
				rows.Close()
				return err
			}
			existing[st] = sha
		}
		rows.Close()
	}

	for _, st := range keys {
		group := groups[st]
		sort.SliceStable(group, func(i, j int) bool {
			a, b := group[i], group[j]
			if a.isBackup != b.isBackup {
				return !a.isBackup // a real .run beats its .backup
			}
			if a.mtimeNs != b.mtimeNs {
				return a.mtimeNs > b.mtimeNs // then the newest write
			}
			return a.path < b.path
		})
		chosen := group[0]
		if len(group) > 1 {
			rep.note("duplicate_copies", len(group)-1)
			shas := map[string]bool{}
			for _, f := range group {
				shas[f.sha] = true
			}
			if len(shas) > 1 {
				rep.note("runs_with_differing_copies", 1)
				in.warnf("history: start_time %d has %d copies with different content; using %s", st, len(group), chosen.path)
			}
		}
		if chosen.isBackup {
			rep.note("runs_only_in_backup", 1)
		}

		anyChanged := false
		for _, f := range group {
			anyChanged = anyChanged || f.changed
		}
		if sha, ok := existing[st]; ok && sha == chosen.sha && !anyChanged && !in.opts.Force {
			rep.Unchanged++
			continue
		}

		if chosen.content == nil {
			content, info, err := readFileStable(chosen.path)
			if err != nil {
				in.warnf("history: cannot read %s: %v", chosen.path, err)
				rep.Failed++
				continue
			}
			chosen.content = content
			chosen.sha = sha256Hex(content)
			chosen.size, chosen.mtimeNs = info.Size(), info.ModTime().UnixNano()
		}
		var run historyRun
		if err := json.Unmarshal(chosen.content, &run); err != nil {
			in.warnf("history: skipping malformed %s: %v", chosen.path, err)
			rep.Failed++
			if err := in.putSource(ctx, in.db, chosen.path, "history", chosen.size, chosen.mtimeNs, chosen.sha, &st, "error", err.Error()); err != nil {
				return err
			}
			continue
		}

		tx, err := in.db.BeginTx(ctx, nil)
		if err != nil {
			return err
		}
		if err := writeHistoryRun(ctx, tx, st, chosen, group, &run); err != nil {
			tx.Rollback()
			return fmt.Errorf("%s: %w", chosen.path, err)
		}
		for _, f := range group {
			status, msg := "ok", ""
			if f != chosen {
				status, msg = "duplicate", "same run as "+chosen.path
			}
			if err := in.putSource(ctx, tx, f.path, "history", f.size, f.mtimeNs, f.sha, &st, status, msg); err != nil {
				tx.Rollback()
				return err
			}
		}
		if err := tx.Commit(); err != nil {
			return err
		}
		rep.Ingested++
	}
	rep.note("unique_runs", len(keys))
	return nil
}

func writeHistoryRun(ctx context.Context, tx execer, st int64, chosen *historyFile, group []*historyFile, run *historyRun) error {
	for _, table := range []string{"history_runs", "run_players", "run_floors", "run_choices", "run_floor_items", "run_deck", "run_relics"} {
		if _, err := tx.ExecContext(ctx, "DELETE FROM "+table+" WHERE game_start_time = ?", st); err != nil {
			return err
		}
	}

	paths := make([]string, len(group))
	for i, f := range group {
		paths[i] = f.path
	}
	floors := 0
	for _, act := range run.MapPointHistory {
		floors += len(act)
	}
	character := ""
	if len(run.Players) > 0 {
		character = run.Players[0].Character
	}
	var runTime any
	if run.RunTime != "" {
		if f, err := run.RunTime.Float64(); err == nil {
			runTime = int64(f)
		}
	}
	if _, err := tx.ExecContext(ctx, `
		INSERT INTO history_runs (game_start_time, source_path, copies, copy_paths, content_sha256,
			schema_version, build_id, platform_type, game_mode, seed, ascension, win, was_abandoned,
			killed_by_encounter, killed_by_event, run_time_seconds, player_count, character,
			floor_reached, acts_json, modifiers_json, raw_json)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		st, chosen.path, len(group), marshalJSON(paths), chosen.sha,
		nullInt64Ptr(run.SchemaVersion), nullString(run.BuildID), nullString(run.PlatformType), nullString(run.GameMode),
		nullString(run.Seed), nullInt64Ptr(run.Ascension), nullBoolPtr(run.Win), nullBoolPtr(run.WasAbandoned),
		noneToNull(run.KilledByEncounter), noneToNull(run.KilledByEvent), runTime, len(run.Players), nullString(character),
		floors, rawJSON(run.Acts), rawJSON(run.Modifiers), string(chosen.content)); err != nil {
		return err
	}

	for i, p := range run.Players {
		pid := int64(i + 1)
		if p.ID != nil {
			pid = *p.ID
		}
		if _, err := tx.ExecContext(ctx, `
			INSERT OR REPLACE INTO run_players (game_start_time, player_id, ordinal, character, max_potion_slot_count, badges_json, potions_json)
			VALUES (?, ?, ?, ?, ?, ?, ?)`,
			st, pid, i, nullString(p.Character), nullInt64Ptr(p.MaxPotionSlotCount), rawJSON(p.Badges), rawJSON(p.Potions)); err != nil {
			return err
		}
		for j, c := range p.Deck {
			var amount any
			if c.Enchantment != nil {
				amount = nullInt64Ptr(c.Enchantment.Amount)
			}
			if _, err := tx.ExecContext(ctx, `
				INSERT OR REPLACE INTO run_deck (game_start_time, player_id, ordinal, card_id, upgrade_level, enchantment_id, enchantment_amount, floor_added)
				VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
				st, pid, j, c.ID, upgradeLevel(c.CurrentUpgradeLevel), c.enchantmentID(), amount, nullInt64Ptr(c.FloorAddedToDeck)); err != nil {
				return err
			}
		}
		for j, r := range p.Relics {
			if _, err := tx.ExecContext(ctx, `
				INSERT OR REPLACE INTO run_relics (game_start_time, player_id, ordinal, relic_id, floor_added)
				VALUES (?, ?, ?, ?, ?)`,
				st, pid, j, r.ID, nullInt64Ptr(r.FloorAddedToDeck)); err != nil {
				return err
			}
		}
	}

	floor := 0
	for actIndex, act := range run.MapPointHistory {
		for actFloor, mp := range act {
			floor++
			var roomType, modelID, monsters any
			var turns any
			if n := len(mp.Rooms); n > 0 {
				last := mp.Rooms[n-1]
				roomType = nullString(last.RoomType)
				modelID = nullStringPtr(last.ModelID)
				monsters = rawJSON(last.MonsterIDs)
				turns = nullInt64Ptr(last.TurnsTaken)
			}
			rooms := marshalJSON(mp.Rooms)
			stats := mp.PlayerStats
			if len(stats) == 0 {
				// A map point with no per-player stats still gets its floor row.
				stats = []json.RawMessage{json.RawMessage(`{}`)}
			}
			for statIndex, raw := range stats {
				var ps historyPlayerStats
				if err := json.Unmarshal(raw, &ps); err != nil {
					continue // one odd entry must not drop the run
				}
				pid := int64(statIndex + 1)
				if ps.PlayerID != nil {
					pid = *ps.PlayerID
				} else if len(mp.PlayerStats) == 0 {
					pid = 0
				}

				var picked, skipped, relicsGained, potionsGained []string
				for _, c := range ps.CardChoices {
					if c.WasPicked {
						picked = append(picked, c.Card.ID)
					} else {
						skipped = append(skipped, c.Card.ID)
					}
				}
				for _, r := range ps.RelicChoices {
					if r.WasPicked {
						relicsGained = append(relicsGained, r.Choice)
					}
				}
				relicsGained = append(relicsGained, ps.BoughtRelics...)
				for _, p := range ps.PotionChoices {
					if p.WasPicked {
						potionsGained = append(potionsGained, p.Choice)
					}
				}
				potionsGained = append(potionsGained, ps.BoughtPotions...)

				if _, err := tx.ExecContext(ctx, `
					INSERT OR REPLACE INTO run_floors (game_start_time, floor, player_id, act_index, act_floor, map_point_type,
						room_type, model_id, monster_ids_json, rooms_json, turns_taken, current_hp, max_hp, current_gold,
						damage_taken, hp_healed, max_hp_gained, max_hp_lost, gold_gained, gold_spent, gold_lost, gold_stolen,
						cards_picked_json, cards_skipped_json, relics_gained_json, potions_gained_json, stats_json)
					VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
					st, floor, pid, actIndex, actFloor+1, nullString(mp.MapPointType),
					roomType, modelID, monsters, rooms, turns,
					nullInt64Ptr(ps.CurrentHP), nullInt64Ptr(ps.MaxHP), nullInt64Ptr(ps.CurrentGold),
					nullInt64Ptr(ps.DamageTaken), nullInt64Ptr(ps.HPHealed), nullInt64Ptr(ps.MaxHPGained), nullInt64Ptr(ps.MaxHPLost),
					nullInt64Ptr(ps.GoldGained), nullInt64Ptr(ps.GoldSpent), nullInt64Ptr(ps.GoldLost), nullInt64Ptr(ps.GoldStolen),
					marshalJSON(picked), marshalJSON(skipped), marshalJSON(relicsGained), marshalJSON(potionsGained),
					rawJSON(raw)); err != nil {
					return err
				}

				choice := func(kind string, ordinal int, item string, upgrade, ench any, picked bool) error {
					_, err := tx.ExecContext(ctx, `
						INSERT OR REPLACE INTO run_choices (game_start_time, floor, player_id, kind, ordinal, item_id, upgrade_level, enchantment_id, was_picked)
						VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
						st, floor, pid, kind, ordinal, nullString(item), upgrade, ench, boolInt(picked))
					return err
				}
				for i, c := range ps.CardChoices {
					if err := choice("card", i, c.Card.ID, upgradeLevel(c.Card.CurrentUpgradeLevel), c.Card.enchantmentID(), c.WasPicked); err != nil {
						return err
					}
				}
				for i, r := range ps.RelicChoices {
					if err := choice("relic", i, r.Choice, nil, nil, r.WasPicked); err != nil {
						return err
					}
				}
				for i, p := range ps.PotionChoices {
					if err := choice("potion", i, p.Choice, nil, nil, p.WasPicked); err != nil {
						return err
					}
				}
				for i, a := range ps.AncientChoice {
					if err := choice("ancient", i, a.TextKey, nil, nil, a.WasChosen); err != nil {
						return err
					}
				}

				item := func(kind string, ordinal int, id string, detail any) error {
					_, err := tx.ExecContext(ctx, `
						INSERT OR REPLACE INTO run_floor_items (game_start_time, floor, player_id, kind, ordinal, item_id, detail)
						VALUES (?, ?, ?, ?, ?, ?, ?)`,
						st, floor, pid, kind, ordinal, nullString(id), detail)
					return err
				}
				type strList struct {
					kind  string
					items []string
				}
				for _, l := range []strList{
					{"card_upgraded", ps.UpgradedCards},
					{"card_downgraded", ps.DowngradedCards},
					{"relic_bought", ps.BoughtRelics},
					{"potion_bought", ps.BoughtPotions},
					{"colorless_bought", ps.BoughtColorless},
					{"potion_used", ps.PotionUsed},
					{"potion_discarded", ps.PotionDiscarded},
					{"relic_removed", ps.RelicsRemoved},
					{"rest_site_choice", ps.RestSiteChoices},
					{"completed_quest", ps.CompletedQuests},
				} {
					for i, id := range l.items {
						if err := item(l.kind, i, id, nil); err != nil {
							return err
						}
					}
				}
				for i, c := range ps.CardsGained {
					if err := item("card_gained", i, c.ID, nil); err != nil {
						return err
					}
				}
				for i, c := range ps.CardsRemoved {
					if err := item("card_removed", i, c.ID, nil); err != nil {
						return err
					}
				}
				for i, c := range ps.CardsTransformed {
					if err := item("card_transformed", i, c.OriginalCard.ID, nullString(c.FinalCard.ID)); err != nil {
						return err
					}
				}
				for i, c := range ps.CardsEnchanted {
					if err := item("card_enchanted", i, c.Card.ID, nullString(c.Enchantment)); err != nil {
						return err
					}
				}
				for i, ev := range ps.EventChoices {
					if err := item("event_choice", i, ev.Title.Key, nil); err != nil {
						return err
					}
				}
			}
		}
	}
	return nil
}

func upgradeLevel(v *int64) any {
	if v == nil {
		return 0
	}
	return *v
}

// noneToNull maps the game's ModelId.none ("NONE.NONE") and "" to NULL.
func noneToNull(s string) any {
	if s == "" || s == "NONE.NONE" {
		return nil
	}
	return s
}
