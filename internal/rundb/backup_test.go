package rundb

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func writeAt(t *testing.T, path, content string, mtime time.Time) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(path, mtime, mtime); err != nil {
		t.Fatal(err)
	}
}

func readFile(t *testing.T, path string) string {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

func TestResolveBackupTo_FlagThenConfigThenDefault(t *testing.T) {
	nas, empty, flag := `\\nas\x`, "", `D:\elsewhere`
	if got := ResolveBackupTo(nil, Config{}); got != DefaultBackupTo {
		t.Errorf("default = %q", got)
	}
	if got := ResolveBackupTo(nil, Config{BackupTo: &nas}); got != nas {
		t.Errorf("config = %q", got)
	}
	if got := ResolveBackupTo(nil, Config{BackupTo: &empty}); got != "" {
		t.Errorf("empty config must disable, got %q", got)
	}
	if got := ResolveBackupTo(&flag, Config{BackupTo: &nas}); got != flag {
		t.Errorf("flag = %q", got)
	}
	if got := ResolveBackupTo(&empty, Config{BackupTo: &nas}); got != "" {
		t.Errorf("empty flag must disable, got %q", got)
	}
}

func TestBackup_IncrementalNeverDeletesAndKeepsShrunkCopies(t *testing.T) {
	root := t.TempDir()
	replays := filepath.Join(root, "replays")
	runs := filepath.Join(root, "runs")
	dest := filepath.Join(root, "nas")
	dbPath := filepath.Join(root, "spirelens.db")
	t0 := time.Date(2026, 10, 16, 10, 0, 0, 0, time.UTC)

	writeAt(t, filepath.Join(replays, "100", "header.json"), `{"outcome":"in_progress"}`, t0)
	writeAt(t, filepath.Join(replays, "100", "events.jsonl"), "{\"seq\":1}\n", t0)
	writeAt(t, filepath.Join(replays, "100", "snapshots", "0000.json.gz"), "gz", t0)
	writeAt(t, filepath.Join(replays, "100", "snapshots", "0001.json.gz.tmp"), "in flight", t0)
	writeAt(t, filepath.Join(runs, "abc.json"), `{"run_id":"abc"}`, t0)
	writeAt(t, filepath.Join(runs, "abc.json.repair.bak"), "not a record", t0)

	opts := BackupOptions{Dest: dest, ReplaysDir: replays, SpireLensRunsDir: runs, DBPath: dbPath}
	r := Backup(context.Background(), opts)
	if r.Status != "ok" || r.FilesCopied != 4 {
		t.Fatalf("first backup: %+v", r)
	}
	if _, err := os.Stat(filepath.Join(dest, "replays", "100", "snapshots", "0001.json.gz.tmp")); !os.IsNotExist(err) {
		t.Fatal("an in-flight .tmp file was copied")
	}
	if _, err := os.Stat(filepath.Join(dest, "runs", "abc.json.repair.bak")); !os.IsNotExist(err) {
		t.Fatal("a non-record file in runs was copied")
	}

	// Unchanged: nothing copied.
	if r := Backup(context.Background(), opts); r.FilesCopied != 0 || r.FilesChecked != 4 {
		t.Fatalf("second backup: %+v", r)
	}

	// Grown, deleted, and shrunk files.
	t1 := t0.Add(time.Minute)
	writeAt(t, filepath.Join(replays, "100", "events.jsonl"), "{\"seq\":1}\n{\"seq\":2}\n", t1)
	os.Remove(filepath.Join(replays, "100", "snapshots", "0000.json.gz"))
	writeAt(t, filepath.Join(replays, "100", "header.json"), `{"outcome":"win"}`, t1) // rewritten smaller: not append-only
	r = Backup(context.Background(), opts)
	if r.FilesCopied != 2 || r.ShrunkKept != 0 {
		t.Fatalf("third backup: %+v", r)
	}
	if got := readFile(t, filepath.Join(dest, "replays", "100", "events.jsonl")); !strings.Contains(got, `"seq":2`) {
		t.Fatalf("grown events.jsonl not recopied: %q", got)
	}
	if got := readFile(t, filepath.Join(dest, "replays", "100", "header.json")); got != `{"outcome":"win"}` {
		t.Fatalf("header not updated: %q", got)
	}
	if _, err := os.Stat(filepath.Join(dest, "replays", "100", "snapshots", "0000.json.gz")); err != nil {
		t.Fatal("a local delete propagated to the backup")
	}

	t2 := t1.Add(time.Minute)
	writeAt(t, filepath.Join(replays, "100", "events.jsonl"), "{\"se", t2) // damaged: shorter than the backup
	r = Backup(context.Background(), opts)
	if r.ShrunkKept != 1 {
		t.Fatalf("shrunk file: %+v", r)
	}
	if got := readFile(t, filepath.Join(dest, "replays", "100", "events.jsonl")); !strings.Contains(got, `"seq":2`) {
		t.Fatalf("backup copy of an append-only file was overwritten by a shorter one: %q", got)
	}
	matches, _ := filepath.Glob(filepath.Join(dest, "replays", "100", "events.jsonl.local-*"))
	if len(matches) != 1 {
		t.Fatalf("local copy of the shrunk file not kept beside it: %v", matches)
	}

	// No temp files are ever left at the destination.
	_ = filepath.Walk(dest, func(p string, info os.FileInfo, err error) error {
		if err == nil && strings.Contains(filepath.Base(p), ".partial-") {
			t.Errorf("partial file left: %s", p)
		}
		return nil
	})

	if st := LoadBackupState(dbPath); st == nil || st.LastSuccessUTC == "" {
		t.Fatalf("last-backup.json not written: %+v", st)
	}
}

func TestBackup_UnreachableAndDisabledAreQuiet(t *testing.T) {
	root := t.TempDir()
	blocker := filepath.Join(root, "file")
	writeAt(t, blocker, "x", time.Now())
	dbPath := filepath.Join(root, "spirelens.db")

	r := Backup(context.Background(), BackupOptions{Dest: filepath.Join(blocker, "under-a-file"), DBPath: dbPath})
	if r.Status != "unreachable" || !strings.Contains(FormatBackupReport(r), "NAS unreachable") {
		t.Fatalf("unreachable: %+v", r)
	}
	if r := Backup(context.Background(), BackupOptions{Dest: "", DBPath: dbPath}); r.Status != "disabled" {
		t.Fatalf("disabled: %+v", r)
	}
}

func TestPruneDBSnapshots_KeepsNewestAndOnlyTouchesDatedFiles(t *testing.T) {
	dir := t.TempDir()
	for _, n := range []string{
		"spirelens-2026-10-10.db.gz", "spirelens-2026-10-11.db.gz", "spirelens-2026-10-12.db.gz",
		"spirelens-2026-10-13.db.gz", "notes.txt",
	} {
		writeAt(t, filepath.Join(dir, n), "x", time.Now())
	}
	pruneDBSnapshots(dir, 2)
	entries, _ := os.ReadDir(dir)
	var names []string
	for _, e := range entries {
		names = append(names, e.Name())
	}
	want := "notes.txt,spirelens-2026-10-12.db.gz,spirelens-2026-10-13.db.gz"
	if got := strings.Join(names, ","); got != want {
		t.Fatalf("after prune: %s, want %s", got, want)
	}
}
