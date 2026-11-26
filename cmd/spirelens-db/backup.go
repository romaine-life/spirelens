package main

import (
	"context"
	"database/sql"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	"github.com/romaine-life/logline"
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

// openLog is --log's file: the command's events in the estate's log line
// (AHT-204, at-home-tools docs/app-logs.md), appended and moved to .old past
// 1 MiB, so every background run's import and backup stay searchable. With no
// --log, the events go nowhere and the console keeps its human report. finish
// records how the command ended.
func openLog(path, command string) (*logline.Logger, func(err error), error) {
	if path == "" {
		return logline.New(io.Discard), func(error) {}, nil
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, nil, err
	}
	log := logline.OpenFile(path)
	log.Info(command + " started")
	return log, func(err error) {
		if err != nil {
			log.Error(command+" failed", "error", logline.Redact(err.Error()))
		} else {
			log.Info(command + " finished")
		}
	}, nil
}

// warning is one of ingest's or backup's warnings, on the console and in the
// log; --quiet leaves both out. Its text names files and their errors.
func warning(log *logline.Logger, quiet bool, format string, a ...any) {
	if quiet {
		return
	}
	text := fmt.Sprintf(format, a...)
	fmt.Fprintln(os.Stderr, "warning: "+text)
	log.Warn("warning", "detail", logline.Redact(text))
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
	logPath := fs.String("log", "", "append the backup's events to this log file, in the estate's log line")
	fs.Parse(args)

	log, finish, err := openLog(*logPath, "backup")
	if err != nil {
		return err
	}
	defer func() { finish(retErr) }()
	// A panic is logged before finish could call it finished; then it still ends the program.
	defer log.Panic()

	// Same lock as ingest: a backup never overlaps an ingest (or its backup).
	release, locked, err := rundb.AcquireIngestLock(*dbPath)
	if err != nil {
		return err
	}
	if !locked {
		fmt.Println("an ingest or backup is running; nothing to do")
		log.Info("backup skipped", "reason", "an ingest or backup is running")
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
	return backupWith(ctx, log, db, *dbPath, backupTo.ptr(), *replays, *spirelensRuns, db != nil, *quiet)
}

// backupWith resolves the destination and runs one backup, printing a
// one-line summary. Unreachable and disabled are not errors.
func backupWith(ctx context.Context, log *logline.Logger, db *sql.DB, dbPath string, flagDest *string, replays, spirelensRuns string, snapshot, quiet bool) error {
	cfg, err := rundb.LoadConfig(dbPath)
	if err != nil {
		fmt.Printf("warning: %v (using defaults)\n", err)
		log.Warn("config unreadable; using defaults", "error", logline.Redact(err.Error()))
	}
	opts := rundb.BackupOptions{
		Dest:             rundb.ResolveBackupTo(flagDest, cfg),
		ReplaysDir:       replays,
		SpireLensRunsDir: spirelensRuns,
		DBPath:           dbPath,
		Warn: func(format string, a ...any) {
			warning(log, quiet, format, a...)
		},
	}
	if snapshot {
		opts.DB = db
	}
	report := rundb.Backup(ctx, opts)
	fmt.Println(rundb.FormatBackupReport(report))
	level := logline.Info
	switch report.Status {
	case "partial", "unreachable", "failed":
		level = logline.Warn
	}
	log.Log(level, "backup "+report.Status, "dest", report.Dest,
		"copied", report.FilesCopied, "checked", report.FilesChecked, "failed", report.FilesFailed,
		"bytes", report.BytesCopied, "took", (time.Duration(report.DurationMs) * time.Millisecond).Round(100*time.Millisecond),
		"snapshot", report.DBSnapshot, "shrunk_kept", report.ShrunkKept, "message", logline.Redact(report.Message))
	return nil
}
