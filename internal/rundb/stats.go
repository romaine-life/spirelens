package rundb

import (
	"context"
	"database/sql"
	"fmt"
	"io"
	"strings"
	"text/tabwriter"
)

// statsQueries are the headline queries `spirelens-db stats` prints. They
// double as worked examples of the schema (docs/run-database.md).
var statsQueries = []struct {
	title string
	sql   string
}{
	{"Runs by source", `
		SELECT sources, count(*) AS runs FROM runs GROUP BY sources ORDER BY runs DESC`},
	{"Overall (finished runs; win rate = wins / finished)", `
		SELECT count(*) AS runs,
		       sum(outcome = 'win') AS wins,
		       sum(outcome = 'loss') AS losses,
		       sum(outcome = 'abandoned') AS abandoned,
		       sum(outcome = 'in_progress' OR outcome IS NULL) AS unfinished,
		       printf('%.1f%%', 100.0 * sum(outcome = 'win') / nullif(sum(outcome IN ('win','loss','abandoned')), 0)) AS win_rate
		FROM runs`},
	{"By character", `
		SELECT coalesce(character, '?') AS character, count(*) AS runs,
		       sum(outcome = 'win') AS wins, sum(outcome = 'loss') AS losses, sum(outcome = 'abandoned') AS abandoned,
		       printf('%.1f%%', 100.0 * sum(outcome = 'win') / nullif(sum(outcome IN ('win','loss','abandoned')), 0)) AS win_rate,
		       printf('%.1f', avg(floor_reached)) AS avg_floor
		FROM runs GROUP BY character ORDER BY runs DESC`},
	{"By ascension", `
		SELECT coalesce(ascension, -1) AS ascension, count(*) AS runs, sum(outcome = 'win') AS wins,
		       printf('%.1f%%', 100.0 * sum(outcome = 'win') / nullif(sum(outcome IN ('win','loss','abandoned')), 0)) AS win_rate
		FROM runs GROUP BY ascension ORDER BY ascension`},
	{"Most-picked cards (card rewards, game history)", `
		SELECT item_id AS card, sum(was_picked) AS picked, count(*) AS offered,
		       printf('%.0f%%', 100.0 * sum(was_picked) / count(*)) AS pick_rate
		FROM run_choices WHERE kind = 'card'
		GROUP BY item_id ORDER BY picked DESC, offered DESC LIMIT 15`},
	{"Cards in winning final decks (min 5 decks)", `
		SELECT d.card_id AS card, count(DISTINCT d.game_start_time) AS winning_decks
		FROM run_deck d JOIN runs r USING (game_start_time)
		WHERE r.outcome = 'win'
		GROUP BY d.card_id HAVING winning_decks >= 5
		ORDER BY winning_decks DESC LIMIT 10`},
	{"Deadliest encounters", `
		SELECT killed_by, count(*) AS deaths FROM runs
		WHERE outcome = 'loss' AND killed_by IS NOT NULL
		GROUP BY killed_by ORDER BY deaths DESC LIMIT 10`},
	{"Most-played cards (SpireLens stats)", `
		SELECT card_id AS card, sum(plays) AS plays, sum(total_effective) AS effective_damage,
		       count(DISTINCT run_id) AS runs
		FROM spirelens_card_aggregates
		GROUP BY card_id ORDER BY plays DESC LIMIT 10`},
	{"Recorded decisions (training records)", `
		SELECT coalesce(kind, '?') AS kind, count(*) AS records, sum(canonical) AS canonical,
		       sum(options_complete = 0) AS partial_options, sum(chosen_index IS NULL) AS no_chosen_index
		FROM decisions GROUP BY kind ORDER BY records DESC`},
	{"Recordings", `
		SELECT r.game_start_time, coalesce(r.character, '?') AS character, coalesce(ru.outcome, '?') AS outcome,
		       r.max_canonical_floor AS floor, r.event_count AS events, r.branch_count AS branches,
		       r.decision_count AS decisions, r.canonical_decision_count AS canonical, r.torn_line_count AS torn
		FROM recordings r LEFT JOIN runs ru USING (game_start_time) ORDER BY r.game_start_time`},
}

// PrintStats runs the headline queries and writes them as aligned tables.
func PrintStats(ctx context.Context, db *sql.DB, w io.Writer) error {
	for _, q := range statsQueries {
		fmt.Fprintf(w, "\n== %s\n", q.title)
		if err := printQuery(ctx, db, w, q.sql); err != nil {
			return fmt.Errorf("%s: %w", q.title, err)
		}
	}
	return nil
}

// TableCounts returns the row count of every table, in schema order.
func TableCounts(ctx context.Context, db *sql.DB) ([][2]string, error) {
	rows, err := db.QueryContext(ctx, `SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY rowid`)
	if err != nil {
		return nil, err
	}
	var names []string
	for rows.Next() {
		var n string
		if err := rows.Scan(&n); err != nil {
			rows.Close()
			return nil, err
		}
		names = append(names, n)
	}
	rows.Close()
	var out [][2]string
	for _, n := range names {
		var c int64
		if err := db.QueryRowContext(ctx, `SELECT count(*) FROM "`+n+`"`).Scan(&c); err != nil {
			return nil, err
		}
		out = append(out, [2]string{n, fmt.Sprint(c)})
	}
	return out, nil
}

func printQuery(ctx context.Context, db *sql.DB, w io.Writer, query string) error {
	rows, err := db.QueryContext(ctx, query)
	if err != nil {
		return err
	}
	defer rows.Close()
	cols, err := rows.Columns()
	if err != nil {
		return err
	}
	tw := tabwriter.NewWriter(w, 0, 0, 2, ' ', 0)
	fmt.Fprintln(tw, strings.Join(cols, "\t"))
	vals := make([]any, len(cols))
	ptrs := make([]any, len(cols))
	for i := range vals {
		ptrs[i] = &vals[i]
	}
	n := 0
	for rows.Next() {
		if err := rows.Scan(ptrs...); err != nil {
			return err
		}
		cells := make([]string, len(cols))
		for i, v := range vals {
			switch x := v.(type) {
			case nil:
				cells[i] = "-"
			case []byte:
				cells[i] = string(x)
			default:
				cells[i] = fmt.Sprint(x)
			}
		}
		fmt.Fprintln(tw, strings.Join(cells, "\t"))
		n++
	}
	if n == 0 {
		fmt.Fprintln(tw, "(no rows)")
	}
	if err := tw.Flush(); err != nil {
		return err
	}
	return rows.Err()
}
