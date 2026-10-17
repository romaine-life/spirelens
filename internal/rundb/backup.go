package rundb

import (
	"compress/gzip"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

// DefaultBackupTo is the user's Synology share (decided 2026-10-16). The UNC
// path, not the S: drive letter, so it works without the mapping.
const DefaultBackupTo = `\\NelsonNAS\files\Games\SpireLens`

// Config is spirelens-db.json next to the database. Every field is optional.
type Config struct {
	// BackupTo is the backup destination. Absent means DefaultBackupTo; an
	// empty string disables backups.
	BackupTo *string `json:"backup_to,omitempty"`
}

// ConfigPath is spirelens-db.json in the database's directory.
func ConfigPath(dbPath string) string {
	return filepath.Join(filepath.Dir(dbPath), "spirelens-db.json")
}

// LoadConfig reads spirelens-db.json; a missing file is an empty config.
func LoadConfig(dbPath string) (Config, error) {
	var c Config
	b, err := os.ReadFile(ConfigPath(dbPath))
	if errors.Is(err, os.ErrNotExist) {
		return c, nil
	}
	if err != nil {
		return c, err
	}
	if err := json.Unmarshal(b, &c); err != nil {
		return c, fmt.Errorf("%s: %w", ConfigPath(dbPath), err)
	}
	return c, nil
}

// ResolveBackupTo applies the precedence flag > config file > default.
// flag is nil when --backup-to was not given. "" means disabled.
func ResolveBackupTo(flag *string, cfg Config) string {
	if flag != nil {
		return strings.TrimSpace(*flag)
	}
	if cfg.BackupTo != nil {
		return strings.TrimSpace(*cfg.BackupTo)
	}
	return DefaultBackupTo
}

// BackupOptions names what to back up and where.
type BackupOptions struct {
	Dest             string // backup root; "" disables
	ReplaysDir       string // mirrored to <Dest>\replays
	SpireLensRunsDir string // *.json mirrored to <Dest>\runs
	DBPath           string // snapshotted to <Dest>\db, at most once a day
	DB               *sql.DB
	// KeepDBSnapshots is how many dated database snapshots to keep (default 5).
	KeepDBSnapshots int
	// ReachTimeout bounds the reachability probe (default 15 s): an offline
	// SMB host can otherwise block a stat for a minute.
	ReachTimeout time.Duration
	Now          func() time.Time
	Warn         func(format string, args ...any)
}

// BackupReport is also persisted as last-backup.json for the web page.
type BackupReport struct {
	Status         string `json:"status"` // ok | partial | unreachable | disabled | failed
	Message        string `json:"message,omitempty"`
	Dest           string `json:"dest"`
	StartedAtUTC   string `json:"started_at_utc"`
	FinishedAtUTC  string `json:"finished_at_utc"`
	DurationMs     int64  `json:"duration_ms"`
	FilesChecked   int    `json:"files_checked"`
	FilesCopied    int    `json:"files_copied"`
	BytesCopied    int64  `json:"bytes_copied"`
	FilesFailed    int    `json:"files_failed"`
	ShrunkKept     int    `json:"shrunk_kept,omitempty"` // append-only files that shrank locally; the NAS copy was kept
	DBSnapshot     string `json:"db_snapshot,omitempty"` // file written this run, if any
	LastSuccessUTC string `json:"last_success_utc,omitempty"`
}

// BackupStatePath is last-backup.json next to the database.
func BackupStatePath(dbPath string) string {
	return filepath.Join(filepath.Dir(dbPath), "last-backup.json")
}

// LoadBackupState reads last-backup.json, or nil.
func LoadBackupState(dbPath string) *BackupReport {
	b, err := os.ReadFile(BackupStatePath(dbPath))
	if err != nil {
		return nil
	}
	var r BackupReport
	if json.Unmarshal(b, &r) != nil {
		return nil
	}
	return &r
}

const utcLayout = "2006-01-02T15:04:05Z"

// Backup mirrors the recordings and stat records to opts.Dest and takes a
// daily database snapshot. It never deletes or truncates anything at the
// destination (keep-everything; a local delete must not propagate), and
// every file lands through a temp name plus rename, so a partial copy never
// looks complete. An unreachable destination is a quiet "unreachable"
// result, not an error: the next run catches up because the copy is
// incremental.
func Backup(ctx context.Context, opts BackupOptions) *BackupReport {
	now := time.Now
	if opts.Now != nil {
		now = opts.Now
	}
	warn := opts.Warn
	if warn == nil {
		warn = func(string, ...any) {}
	}
	start := now()
	rep := &BackupReport{Dest: opts.Dest, StartedAtUTC: start.UTC().Format(utcLayout)}
	if prev := LoadBackupState(opts.DBPath); prev != nil {
		rep.LastSuccessUTC = prev.LastSuccessUTC
	}
	finish := func() *BackupReport {
		end := now()
		rep.FinishedAtUTC = end.UTC().Format(utcLayout)
		rep.DurationMs = end.Sub(start).Milliseconds()
		if rep.Status == "ok" || rep.Status == "partial" {
			rep.LastSuccessUTC = rep.FinishedAtUTC
		}
		if opts.DBPath != "" {
			_ = writeFileAtomic(BackupStatePath(opts.DBPath), mustJSON(rep))
		}
		return rep
	}

	if opts.Dest == "" {
		rep.Status, rep.Message = "disabled", "no backup destination configured"
		return finish()
	}
	timeout := opts.ReachTimeout
	if timeout <= 0 {
		timeout = 15 * time.Second
	}
	if err := probeReachable(opts.Dest, timeout); err != nil {
		rep.Status, rep.Message = "unreachable", err.Error()
		return finish()
	}

	if opts.ReplaysDir != "" {
		mirrorTree(ctx, opts.ReplaysDir, filepath.Join(opts.Dest, "replays"), nil, rep, warn)
	}
	if opts.SpireLensRunsDir != "" {
		onlyJSON := func(rel string) bool {
			return !strings.ContainsAny(rel, `\/`) && strings.EqualFold(filepath.Ext(rel), ".json")
		}
		mirrorTree(ctx, opts.SpireLensRunsDir, filepath.Join(opts.Dest, "runs"), onlyJSON, rep, warn)
	}
	if opts.DB != nil && opts.DBPath != "" {
		keep := opts.KeepDBSnapshots
		if keep <= 0 {
			keep = 5
		}
		name, err := snapshotDB(ctx, opts.DB, opts.DBPath, filepath.Join(opts.Dest, "db"), start, keep, rep)
		if err != nil {
			rep.FilesFailed++
			warn("backup: database snapshot failed: %v", err)
		}
		rep.DBSnapshot = name
	}

	rep.Status = "ok"
	if rep.FilesFailed > 0 {
		rep.Status = "partial"
		rep.Message = fmt.Sprintf("%d file(s) failed; they are retried next time", rep.FilesFailed)
	}
	if ctx.Err() != nil {
		rep.Status, rep.Message = "partial", "interrupted"
	}
	return finish()
}

// probeReachable creates the destination root (MkdirAll on a UNC path also
// proves the share answers), bounded by timeout.
func probeReachable(dest string, timeout time.Duration) error {
	done := make(chan error, 1)
	go func() { done <- os.MkdirAll(dest, 0o755) }()
	select {
	case err := <-done:
		if err != nil {
			return fmt.Errorf("backup destination unreachable: %w", err)
		}
		return nil
	case <-time.After(timeout):
		return fmt.Errorf("backup destination unreachable: no answer from %s within %s", dest, timeout)
	}
}

// appendOnly reports whether a recording file only ever grows while it is
// written (events.jsonl, the open decision segment). If such a file is
// smaller locally than at the destination, the local copy was damaged or
// replaced: the destination keeps its copy and the local one lands beside it.
func appendOnly(rel string) bool {
	base := filepath.Base(rel)
	return base == "events.jsonl" || strings.HasSuffix(base, ".jsonl")
}

// sameFile is the incremental test: equal size and an mtime within 2 s (SMB
// and the NAS file system may round timestamps).
func sameFile(src, dst fs.FileInfo) bool {
	if src.Size() != dst.Size() {
		return false
	}
	d := src.ModTime().Sub(dst.ModTime())
	return d <= 2*time.Second && d >= -2*time.Second
}

func mirrorTree(ctx context.Context, srcRoot, dstRoot string, include func(rel string) bool, rep *BackupReport, warn func(string, ...any)) {
	err := filepath.WalkDir(srcRoot, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			warn("backup: cannot read %s: %v", path, err)
			if d != nil && d.IsDir() && path != srcRoot {
				return fs.SkipDir
			}
			return nil
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if d.IsDir() {
			return nil
		}
		rel, _ := filepath.Rel(srcRoot, path)
		// In-flight writer temp files (the recorder's atomic writes, gzip
		// closes) are not data yet.
		if strings.HasSuffix(rel, ".tmp") || (include != nil && !include(rel)) {
			return nil
		}
		info, err := d.Info()
		if err != nil {
			return nil // vanished between listing and stat
		}
		rep.FilesChecked++
		dst := filepath.Join(dstRoot, rel)
		target := dst
		if di, err := os.Stat(dst); err == nil {
			if sameFile(info, di) {
				return nil
			}
			if appendOnly(rel) && info.Size() < di.Size() {
				rep.ShrunkKept++
				target = fmt.Sprintf("%s.local-%s", dst, info.ModTime().UTC().Format("20060102T150405Z"))
				if ti, err := os.Stat(target); err == nil && sameFile(info, ti) {
					return nil
				}
				warn("backup: %s is smaller locally than on the backup; kept the backup copy, saved the local one as %s", rel, filepath.Base(target))
			}
		}
		n, err := copyFileAtomic(path, target, info)
		if err != nil {
			rep.FilesFailed++
			warn("backup: %s: %v", rel, err)
			return nil
		}
		rep.FilesCopied++
		rep.BytesCopied += n
		return nil
	})
	if err != nil && !errors.Is(err, context.Canceled) && !errors.Is(err, fs.ErrNotExist) {
		warn("backup: walking %s: %v", srcRoot, err)
	}
}

// copyFileAtomic copies src to dst through dst.partial-<pid> and a rename,
// then stamps dst with src's mtime so the next run sees it as unchanged.
// A file still being appended may be copied with a torn last line; that is
// the same state a crash leaves, readers skip it, and the next run recopies
// the grown file.
func copyFileAtomic(src, dst string, info fs.FileInfo) (int64, error) {
	in, err := os.Open(src)
	if err != nil {
		return 0, err
	}
	defer in.Close()
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return 0, err
	}
	tmp := fmt.Sprintf("%s.partial-%d", dst, os.Getpid())
	out, err := os.Create(tmp)
	if err != nil {
		return 0, err
	}
	n, err := io.Copy(out, in)
	if err == nil {
		err = out.Sync()
	}
	if cerr := out.Close(); err == nil {
		err = cerr
	}
	if err == nil {
		err = os.Chtimes(tmp, info.ModTime(), info.ModTime())
	}
	if err == nil {
		err = renameRetry(tmp, dst)
	}
	if err != nil {
		os.Remove(tmp)
		return 0, err
	}
	return n, nil
}

func renameRetry(from, to string) error {
	var err error
	for attempt := 0; attempt < 5; attempt++ {
		if err = os.Rename(from, to); err == nil {
			return nil
		}
		time.Sleep(200 * time.Millisecond)
	}
	return err
}

var dbSnapshotName = regexp.MustCompile(`^spirelens-(\d{4}-\d{2}-\d{2})\.db\.gz$`)

// snapshotDB writes <dstDir>\spirelens-<local date>.db.gz at most once per
// day: VACUUM INTO a local temp file (a consistent copy even while the game
// imports), gzip it to the destination through a temp name, then keep only
// the newest `keep` dated snapshots there. The database is rebuildable from
// the recordings and history files, so this is a convenience copy.
func snapshotDB(ctx context.Context, db *sql.DB, dbPath, dstDir string, now time.Time, keep int, rep *BackupReport) (string, error) {
	name := fmt.Sprintf("spirelens-%s.db.gz", now.Local().Format("2006-01-02"))
	dst := filepath.Join(dstDir, name)
	rep.FilesChecked++
	if _, err := os.Stat(dst); err == nil {
		return "", nil // already have today's
	}
	if err := os.MkdirAll(dstDir, 0o755); err != nil {
		return "", err
	}

	local := dbPath + ".backup-tmp"
	os.Remove(local)
	defer os.Remove(local)
	if _, err := db.ExecContext(ctx, `VACUUM INTO ?`, local); err != nil {
		return "", fmt.Errorf("vacuum into: %w", err)
	}
	in, err := os.Open(local)
	if err != nil {
		return "", err
	}
	defer in.Close()
	tmp := fmt.Sprintf("%s.partial-%d", dst, os.Getpid())
	out, err := os.Create(tmp)
	if err != nil {
		return "", err
	}
	zw, _ := gzip.NewWriterLevel(out, gzip.BestSpeed)
	_, err = io.Copy(zw, in)
	if cerr := zw.Close(); err == nil {
		err = cerr
	}
	if err == nil {
		err = out.Sync()
	}
	if cerr := out.Close(); err == nil {
		err = cerr
	}
	if err == nil {
		err = renameRetry(tmp, dst)
	}
	if err != nil {
		os.Remove(tmp)
		return "", err
	}
	if fi, err := os.Stat(dst); err == nil {
		rep.FilesCopied++
		rep.BytesCopied += fi.Size()
	}
	pruneDBSnapshots(dstDir, keep)
	return name, nil
}

// pruneDBSnapshots deletes dated snapshots beyond the newest keep. It only
// ever touches files named spirelens-YYYY-MM-DD.db.gz in the db folder.
func pruneDBSnapshots(dir string, keep int) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	var dated []string
	for _, e := range entries {
		if !e.IsDir() && dbSnapshotName.MatchString(e.Name()) {
			dated = append(dated, e.Name())
		}
	}
	sort.Strings(dated) // ISO dates sort chronologically
	for i := 0; i < len(dated)-keep; i++ {
		os.Remove(filepath.Join(dir, dated[i]))
	}
}

func mustJSON(v any) []byte {
	b, _ := json.MarshalIndent(v, "", "  ")
	return append(b, '\n')
}

// writeFileAtomic writes a small local file through a temp name.
func writeFileAtomic(path string, b []byte) error {
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// FormatBackupReport is the one-line summary printed to the log.
func FormatBackupReport(r *BackupReport) string {
	switch r.Status {
	case "disabled":
		return "backup: disabled (backup_to is empty)"
	case "unreachable":
		return fmt.Sprintf("backup skipped: NAS unreachable (%s)", r.Message)
	}
	s := fmt.Sprintf("backup %s: %d of %d files copied (%.1f MB) to %s in %s",
		r.Status, r.FilesCopied, r.FilesChecked, float64(r.BytesCopied)/(1<<20), r.Dest,
		(time.Duration(r.DurationMs) * time.Millisecond).Round(100*time.Millisecond))
	if r.DBSnapshot != "" {
		s += "; database snapshot " + r.DBSnapshot
	}
	if r.ShrunkKept > 0 {
		s += fmt.Sprintf("; %d shrunk file(s) kept beside the backup copy", r.ShrunkKept)
	}
	if r.Message != "" {
		s += " (" + r.Message + ")"
	}
	return s
}
