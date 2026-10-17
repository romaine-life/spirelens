package main

import (
	"context"
	"database/sql"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/romaine-life/spirelens/internal/rundb"
)

// optString is a string flag that remembers whether it was given, so
// --backup-to "" (disable) differs from no flag (use config/default).
type optString struct {
	set bool
	v   string
}

func (o *optString) String() string     { return o.v }
func (o *optString) Set(v string) error { o.set, o.v = true, v; return nil }
func (o *optString) ptr() *string {
	if !o.set {
		return nil
	}
	return &o.v
}

// redirectToLog sends stdout/stderr to path (overwritten). The returned
// finish records the command's outcome in the file and closes it: main
// prints errors to stderr only after the command returns, when the file is
// already closed.
func redirectToLog(path, command string) (func(err error), error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, err
	}
	f, err := os.Create(path)
	if err != nil {
		return nil, err
	}
	os.Stdout, os.Stderr = f, f
	fmt.Printf("spirelens-db %s started %s\n", command, time.Now().Format(time.RFC3339))
	return func(err error) {
		if err != nil {
			fmt.Fprintln(f, "spirelens-db:", err)
		} else {
			fmt.Fprintln(f, "done")
		}
		f.Close()
	}, nil
}

func runBackup(ctx context.Context, args []string) (retErr error) {
	fs := flag.NewFlagSet("backup", flag.ExitOnError)
	dbPath := fs.String("db", rundb.DefaultDBPath(), "database file (snapshotted, and next to spirelens-db.json)")
	replays := fs.String("replays", rundb.DefaultReplaysDir(), "SpireLens replays directory")
	spirelensRuns := fs.String("spirelens-runs", rundb.DefaultSpireLensRunsDir(), "SpireLens runs directory")
	var backupTo optString
	fs.Var(&backupTo, "backup-to", `backup destination (default: backup_to in spirelens-db.json, else `+rundb.DefaultBackupTo+`; "" disables)`)
	noSnapshot := fs.Bool("no-db-snapshot", false, "skip the daily database snapshot")
	quiet := fs.Bool("quiet", false, "do not print per-file warnings")
	logPath := fs.String("log", "", "write all output to this file (overwritten)")
	fs.Parse(args)

	if *logPath != "" {
		finish, err := redirectToLog(*logPath, "backup")
		if err != nil {
			return err
		}
		defer func() { finish(retErr) }()
	}

	// Same lock as ingest: a backup never overlaps an ingest (or its backup).
	release, locked, err := rundb.AcquireIngestLock(*dbPath)
	if err != nil {
		return err
	}
	if !locked {
		fmt.Println("an ingest or backup is running; nothing to do")
		return nil
	}
	defer release()

	var db *sql.DB
	if !*noSnapshot {
		if _, err := os.Stat(*dbPath); err == nil {
			if db, err = rundb.Open(ctx, *dbPath); err != nil {
				return err
			}
			defer db.Close()
		}
	}
	return backupWith(ctx, db, *dbPath, backupTo.ptr(), *replays, *spirelensRuns, db != nil, *quiet)
}

// backupWith resolves the destination and runs one backup, printing a
// one-line summary. Unreachable and disabled are not errors.
func backupWith(ctx context.Context, db *sql.DB, dbPath string, flagDest *string, replays, spirelensRuns string, snapshot, quiet bool) error {
	cfg, err := rundb.LoadConfig(dbPath)
	if err != nil {
		fmt.Printf("warning: %v (using defaults)\n", err)
	}
	opts := rundb.BackupOptions{
		Dest:             rundb.ResolveBackupTo(flagDest, cfg),
		ReplaysDir:       replays,
		SpireLensRunsDir: spirelensRuns,
		DBPath:           dbPath,
		Warn: func(format string, a ...any) {
			if !quiet {
				fmt.Printf("warning: "+format+"\n", a...)
			}
		},
	}
	if snapshot {
		opts.DB = db
	}
	report := rundb.Backup(ctx, opts)
	fmt.Println(rundb.FormatBackupReport(report))
	return nil
}
