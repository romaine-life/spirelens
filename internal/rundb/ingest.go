package rundb

import (
	"bytes"
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"time"
)

// IngestOptions names the sources. Empty fields are skipped; the CLI fills
// them with the real %APPDATA% locations by default.
type IngestOptions struct {
	ReplaysDir       string
	HistoryDirs      []string
	SpireLensRunsDir string
	// Force re-ingests every source even when its fingerprint is unchanged.
	Force bool
	// Warn receives one line per skipped/malformed input. Nil discards.
	Warn func(format string, args ...any)
}

// SourceReport counts what one source kind did.
type SourceReport struct {
	Seen      int // files or directories found
	Ingested  int // (re)read and written
	Unchanged int // fingerprint matched, skipped
	Failed    int // could not be read or parsed; skipped with a warning
	Notes     map[string]int
}

func (r *SourceReport) note(key string, n int) {
	if r.Notes == nil {
		r.Notes = map[string]int{}
	}
	r.Notes[key] += n
}

// IngestReport is the per-source result of one ingest.
type IngestReport struct {
	Recordings SourceReport
	History    SourceReport
	SpireLens  SourceReport
	Runs       int
	Elapsed    time.Duration
}

type ingester struct {
	db    *sql.DB
	opts  IngestOptions
	now   string
	warnf func(format string, args ...any)
}

// Ingest reads every configured source into db, incrementally: a file or
// recording directory whose fingerprint matches the last ingest is not
// re-read. Malformed inputs are skipped with a warning; only database errors
// abort. The runs table is refreshed from the source tables at the end.
func Ingest(ctx context.Context, db *sql.DB, opts IngestOptions) (*IngestReport, error) {
	start := time.Now()
	in := &ingester{
		db:    db,
		opts:  opts,
		now:   time.Now().UTC().Format(time.RFC3339),
		warnf: opts.Warn,
	}
	if in.warnf == nil {
		in.warnf = func(string, ...any) {}
	}
	report := &IngestReport{}

	if len(opts.HistoryDirs) > 0 {
		if err := in.ingestHistory(ctx, opts.HistoryDirs, &report.History); err != nil {
			return report, fmt.Errorf("history: %w", err)
		}
	}
	if opts.SpireLensRunsDir != "" {
		if err := in.ingestSpireLensRuns(ctx, opts.SpireLensRunsDir, &report.SpireLens); err != nil {
			return report, fmt.Errorf("spirelens runs: %w", err)
		}
	}
	if opts.ReplaysDir != "" {
		if err := in.ingestRecordings(ctx, opts.ReplaysDir, &report.Recordings); err != nil {
			return report, fmt.Errorf("recordings: %w", err)
		}
	}
	n, err := RefreshRuns(ctx, db)
	if err != nil {
		return report, fmt.Errorf("refresh runs: %w", err)
	}
	report.Runs = n
	report.Elapsed = time.Since(start)
	return report, nil
}

// ----------------------------------------------------------- source tracking

type sourceRow struct {
	size        int64
	mtimeNs     int64
	fingerprint string
	status      string
	startTime   sql.NullInt64
}

func (in *ingester) loadSources(ctx context.Context, kind string) (map[string]sourceRow, error) {
	rows, err := in.db.QueryContext(ctx,
		`SELECT path, size, mtime_ns, fingerprint, status, game_start_time FROM source_files WHERE kind = ?`, kind)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]sourceRow{}
	for rows.Next() {
		var p string
		var r sourceRow
		if err := rows.Scan(&p, &r.size, &r.mtimeNs, &r.fingerprint, &r.status, &r.startTime); err != nil {
			return nil, err
		}
		out[p] = r
	}
	return out, rows.Err()
}

// unchanged reports whether a file's size and mtime still match what was
// ingested, so it need not be re-read.
func (r sourceRow) unchanged(info os.FileInfo) bool {
	return r.size == info.Size() && r.mtimeNs == info.ModTime().UnixNano()
}

type execer interface {
	ExecContext(ctx context.Context, query string, args ...any) (sql.Result, error)
}

func (in *ingester) putSource(ctx context.Context, x execer, path, kind string, size, mtimeNs int64,
	fingerprint string, startTime *int64, status, message string) error {
	_, err := x.ExecContext(ctx, `
		INSERT INTO source_files (path, kind, size, mtime_ns, fingerprint, game_start_time, status, message, ingested_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(path) DO UPDATE SET
			kind = excluded.kind, size = excluded.size, mtime_ns = excluded.mtime_ns,
			fingerprint = excluded.fingerprint, game_start_time = excluded.game_start_time,
			status = excluded.status, message = excluded.message, ingested_at = excluded.ingested_at`,
		path, kind, size, mtimeNs, fingerprint, nullInt64Ptr(startTime), status, nullString(message), in.now)
	return err
}

// ------------------------------------------------------------------- helpers

func sha256Hex(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

func readFileStable(path string) ([]byte, os.FileInfo, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, nil, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return nil, nil, err
	}
	b, err := io.ReadAll(f)
	return b, info, err
}

// rawJSON returns the raw JSON text, or nil (SQL NULL) for an absent or
// literal-null value.
func rawJSON(raw json.RawMessage) any {
	t := bytes.TrimSpace(raw)
	if len(t) == 0 || bytes.Equal(t, []byte("null")) {
		return nil
	}
	return string(t)
}

// marshalJSON returns v as compact JSON text, or nil for an empty slice/nil.
func marshalJSON(v any) any {
	if v == nil {
		return nil
	}
	b, err := json.Marshal(v)
	if err != nil || string(b) == "null" || string(b) == "[]" {
		return nil
	}
	return string(b)
}

func nullString(s string) any {
	if s == "" {
		return nil
	}
	return s
}

func nullStringPtr(s *string) any {
	if s == nil {
		return nil
	}
	return *s
}

func nullInt64Ptr(v *int64) any {
	if v == nil {
		return nil
	}
	return *v
}

func int64Ptr(n sql.NullInt64) *int64 {
	if !n.Valid {
		return nil
	}
	v := n.Int64
	return &v
}

func nullIntPtr(v *int) any {
	if v == nil {
		return nil
	}
	return *v
}

func nullBoolPtr(v *bool) any {
	if v == nil {
		return nil
	}
	return boolInt(*v)
}

func boolInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

// numberValue stores a JSON number as INTEGER when it is one that fits in
// int64, as REAL when it is fractional, and as TEXT otherwise (a ulong past
// int64, for example).
func numberValue(n json.Number) any {
	if n == "" {
		return nil
	}
	if v, err := n.Int64(); err == nil {
		return v
	}
	if f, err := n.Float64(); err == nil && bytes.ContainsAny([]byte(n), ".eE") {
		return f
	}
	return n.String()
}

func unixToISO(sec int64) string {
	return time.Unix(sec, 0).UTC().Format(time.RFC3339)
}
