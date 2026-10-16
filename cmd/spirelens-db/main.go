// Command spirelens-db builds and queries the SpireLens run database: a
// SQLite file derived from SpireLens run recordings, the game's own run
// history files and SpireLens's per-run stat records. It runs outside the
// game (SQLite never loads into the hot-reloaded Core). See
// docs/run-database.md.
//
//	spirelens-db ingest [--db path] [--replays dir] [--history dir]... [--spirelens-runs dir] [--force]
//	spirelens-db stats [--db path]
//	spirelens-db export-decisions --out file.jsonl [--db path] [--drop-hidden] [--include-abandoned] [--finished-only] [--kinds a,b]
//	spirelens-db serve [--addr 127.0.0.1:8737] [--db path] [--game-dir dir] [--no-ingest]
package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/romaine-life/spirelens/internal/rundb"
)

func main() {
	if len(os.Args) < 2 {
		usage(os.Stderr)
		os.Exit(2)
	}
	ctx := context.Background()
	var err error
	switch os.Args[1] {
	case "ingest":
		err = runIngest(ctx, os.Args[2:])
	case "stats":
		err = runStats(ctx, os.Args[2:])
	case "export-decisions":
		err = runExport(ctx, os.Args[2:])
	case "serve":
		err = runServe(ctx, os.Args[2:])
	case "-h", "--help", "help":
		usage(os.Stdout)
		return
	default:
		fmt.Fprintf(os.Stderr, "spirelens-db: unknown command %q\n\n", os.Args[1])
		usage(os.Stderr)
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "spirelens-db:", err)
		os.Exit(1)
	}
}

func usage(w io.Writer) {
	fmt.Fprintf(w, `usage: spirelens-db <command> [flags]

commands:
  ingest             read recordings, game run history and SpireLens run records into the database
  stats              print headline queries
  export-decisions   write canonical decision records as JSONL training data
  serve              local web page to filter runs and build replay playlists (localhost only)

defaults (from %%APPDATA%%):
  --db              %s
  --replays         %s
  --spirelens-runs  %s
  --history         every steam\<id>\[modded\]profileN\saves\history

run "spirelens-db <command> -h" for a command's flags.
`, rundb.DefaultDBPath(), rundb.DefaultReplaysDir(), rundb.DefaultSpireLensRunsDir())
}

type stringList []string

func (s *stringList) String() string     { return strings.Join(*s, ",") }
func (s *stringList) Set(v string) error { *s = append(*s, v); return nil }

func runIngest(ctx context.Context, args []string) (retErr error) {
	fs := flag.NewFlagSet("ingest", flag.ExitOnError)
	dbPath := fs.String("db", rundb.DefaultDBPath(), "database file")
	replays := fs.String("replays", rundb.DefaultReplaysDir(), "SpireLens replays directory (\"\" to skip)")
	spirelensRuns := fs.String("spirelens-runs", rundb.DefaultSpireLensRunsDir(), "SpireLens runs directory (\"\" to skip)")
	var history stringList
	fs.Var(&history, "history", "game saves\\history directory (repeatable; default: every profile found)")
	noHistory := fs.Bool("no-history", false, "skip the game's run history files")
	force := fs.Bool("force", false, "re-ingest every source even if unchanged")
	quiet := fs.Bool("quiet", false, "do not print per-file warnings")
	logPath := fs.String("log", "", "write all output to this file (overwritten) instead of the console; used by SpireLens's background import")
	fs.Parse(args)

	if *logPath != "" {
		if err := os.MkdirAll(filepath.Dir(*logPath), 0o755); err != nil {
			return err
		}
		f, err := os.Create(*logPath)
		if err != nil {
			return err
		}
		defer func() {
			// main prints errors to stderr after this returns, when the file
			// is closed: record the outcome here instead.
			if retErr != nil {
				fmt.Fprintln(f, "spirelens-db:", retErr)
			} else {
				fmt.Fprintln(f, "done")
			}
			f.Close()
		}()
		os.Stdout, os.Stderr = f, f
		fmt.Printf("spirelens-db ingest started %s\n", time.Now().Format(time.RFC3339))
	}

	historyDirs := []string(history)
	if len(historyDirs) == 0 && !*noHistory {
		historyDirs = rundb.DefaultHistoryDirs()
	}
	if *noHistory {
		historyDirs = nil
	}

	if err := os.MkdirAll(filepath.Dir(*dbPath), 0o755); err != nil {
		return err
	}
	// One ingest at a time: SpireLens starts one at every run end and Core
	// load, and a user may run one by hand. A second one exits quietly; the
	// next trigger picks up whatever it would have read.
	release, locked, err := rundb.AcquireIngestLock(*dbPath)
	if err != nil {
		return err
	}
	if !locked {
		fmt.Println("another spirelens-db ingest is running; nothing to do")
		return nil
	}
	defer release()

	db, err := rundb.Open(ctx, *dbPath)
	if err != nil {
		return err
	}
	defer db.Close()

	warnings := 0
	opts := rundb.IngestOptions{
		ReplaysDir:       *replays,
		HistoryDirs:      historyDirs,
		SpireLensRunsDir: *spirelensRuns,
		Force:            *force,
		Warn: func(format string, a ...any) {
			warnings++
			if !*quiet {
				fmt.Fprintf(os.Stderr, "warning: "+format+"\n", a...)
			}
		},
	}
	fmt.Printf("database: %s\n", *dbPath)
	for _, d := range historyDirs {
		fmt.Printf("history:  %s\n", d)
	}
	report, err := rundb.Ingest(ctx, db, opts)
	if err != nil {
		return err
	}

	tw := tabwriter.NewWriter(os.Stdout, 0, 0, 2, ' ', 0)
	fmt.Fprintln(tw, "\nsource\tseen\tingested\tunchanged\tfailed\tnotes")
	for _, s := range []struct {
		name string
		r    rundb.SourceReport
	}{{"recordings", report.Recordings}, {"history", report.History}, {"spirelens", report.SpireLens}} {
		fmt.Fprintf(tw, "%s\t%d\t%d\t%d\t%d\t%s\n", s.name, s.r.Seen, s.r.Ingested, s.r.Unchanged, s.r.Failed, formatNotes(s.r.Notes))
	}
	tw.Flush()
	fmt.Printf("\nruns: %d   warnings: %d   elapsed: %s\n", report.Runs, warnings, report.Elapsed.Round(1e6))

	counts, err := rundb.TableCounts(ctx, db)
	if err != nil {
		return err
	}
	fmt.Println("\ntable rows:")
	tw = tabwriter.NewWriter(os.Stdout, 0, 0, 2, ' ', tabwriter.AlignRight)
	for _, c := range counts {
		fmt.Fprintf(tw, "  %s\t%s\t\n", c[0], c[1])
	}
	return tw.Flush()
}

func formatNotes(notes map[string]int) string {
	keys := make([]string, 0, len(notes))
	for k := range notes {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	parts := make([]string, len(keys))
	for i, k := range keys {
		parts[i] = fmt.Sprintf("%s=%d", k, notes[k])
	}
	return strings.Join(parts, " ")
}

func runStats(ctx context.Context, args []string) error {
	fs := flag.NewFlagSet("stats", flag.ExitOnError)
	dbPath := fs.String("db", rundb.DefaultDBPath(), "database file")
	fs.Parse(args)
	if _, err := os.Stat(*dbPath); err != nil {
		return fmt.Errorf("no database at %s (run ingest first): %w", *dbPath, err)
	}
	db, err := rundb.Open(ctx, *dbPath)
	if err != nil {
		return err
	}
	defer db.Close()
	return rundb.PrintStats(ctx, db, os.Stdout)
}

func runExport(ctx context.Context, args []string) error {
	fs := flag.NewFlagSet("export-decisions", flag.ExitOnError)
	dbPath := fs.String("db", rundb.DefaultDBPath(), "database file")
	out := fs.String("out", "", "output JSONL file (\"-\" for stdout)")
	dropHidden := fs.Bool("drop-hidden", false, "omit each record's hidden block (information the player could not see)")
	includeAbandoned := fs.Bool("include-abandoned", false, "also export abandoned-branch records (canonical=false)")
	finishedOnly := fs.Bool("finished-only", false, "only runs that ended (win, loss or abandoned)")
	kinds := fs.String("kinds", "", "comma-separated decision kinds to keep (default: all)")
	fs.Parse(args)
	if *out == "" {
		return fmt.Errorf("export-decisions: --out is required")
	}
	if _, err := os.Stat(*dbPath); err != nil {
		return fmt.Errorf("no database at %s (run ingest first): %w", *dbPath, err)
	}
	db, err := rundb.Open(ctx, *dbPath)
	if err != nil {
		return err
	}
	defer db.Close()

	opts := rundb.ExportOptions{DropHidden: *dropHidden, IncludeAbandoned: *includeAbandoned, FinishedOnly: *finishedOnly}
	for _, k := range strings.Split(*kinds, ",") {
		if k = strings.TrimSpace(k); k != "" {
			opts.Kinds = append(opts.Kinds, k)
		}
	}

	var w io.Writer = os.Stdout
	if *out != "-" {
		f, err := os.Create(*out)
		if err != nil {
			return err
		}
		defer f.Close()
		w = f
	}
	n, err := rundb.ExportDecisions(ctx, db, w, opts)
	if err != nil {
		return err
	}
	fmt.Fprintf(os.Stderr, "exported %d decision records\n", n)
	return nil
}
