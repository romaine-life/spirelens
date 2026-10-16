package runweb

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"
)

func TestSlugify(t *testing.T) {
	for in, want := range map[string]string{
		"Regent wins":           "regent-wins",
		"  A10 -- Ironclad!! ":  "a10-ironclad",
		"Défaites":              "d-faites",
		"":                      "playlist",
		"!!!":                   "playlist",
		"play now":              "play-now-list", // play-now.json is the request file
		strings.Repeat("a", 80): strings.Repeat("a", 60),
	} {
		if got := Slugify(in); got != want {
			t.Errorf("Slugify(%q) = %q, want %q", in, got, want)
		}
	}
	for _, bad := range []string{"", "play-now", "../x", "A", "a b", strings.Repeat("a", 65)} {
		if validSlug(bad) {
			t.Errorf("validSlug(%q) = true", bad)
		}
	}
}

func TestPlaylistFile_ContractShape(t *testing.T) {
	store := PlaylistStore{Dir: t.TempDir()}
	slug, _, err := store.Create("Regent wins", []PlaylistRun{{GameStartTime: 1792120868, Label: "2026-10-15 Regent A10 WIN f49"}}, json.RawMessage(`{"character":"CHARACTER.REGENT"}`))
	if err != nil {
		t.Fatal(err)
	}
	if slug != "regent-wins" {
		t.Fatalf("slug = %q", slug)
	}
	raw, err := os.ReadFile(filepath.Join(store.Dir, "regent-wins.json"))
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]json.RawMessage
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatal(err)
	}
	var keys []string
	for k := range m {
		keys = append(keys, k)
	}
	want := []string{"created_at_utc", "filter", "format_version", "name", "runs", "updated_at_utc"}
	if !sameSet(keys, want) {
		t.Fatalf("keys = %v, want %v", keys, want)
	}
	if string(m["format_version"]) != "1" || string(m["name"]) != `"Regent wins"` {
		t.Fatalf("format_version/name = %s/%s", m["format_version"], m["name"])
	}
	if _, err := time.Parse("2006-01-02T15:04:05Z", strings.Trim(string(m["created_at_utc"]), `"`)); err != nil {
		t.Fatalf("created_at_utc not UTC RFC 3339: %s", m["created_at_utc"])
	}
	var runs []map[string]any
	_ = json.Unmarshal(m["runs"], &runs)
	if len(runs) != 1 || runs[0]["game_start_time"] != float64(1792120868) || runs[0]["label"] != "2026-10-15 Regent A10 WIN f49" {
		t.Fatalf("runs = %v", runs)
	}

	// No temp file is left behind, and nothing but <slug>.json is listed.
	entries, _ := os.ReadDir(store.Dir)
	if len(entries) != 1 {
		t.Fatalf("dir has %d entries", len(entries))
	}

	// A null filter is written as null, not omitted.
	slug2, _, err := store.Create("Regent wins", nil, nil)
	if err != nil || slug2 != "regent-wins-2" {
		t.Fatalf("second create: slug=%q err=%v", slug2, err)
	}
	raw2, _ := os.ReadFile(filepath.Join(store.Dir, slug2+".json"))
	if !strings.Contains(string(raw2), `"filter": null`) || !strings.Contains(string(raw2), `"runs": []`) {
		t.Fatalf("empty playlist shape: %s", raw2)
	}
}

func TestPlaylist_RenameReorderDelete(t *testing.T) {
	store := PlaylistStore{Dir: t.TempDir()}
	runs := []PlaylistRun{{1, "a"}, {2, "b"}, {3, "c"}}
	slug, _, err := store.Create("First", runs, nil)
	if err != nil {
		t.Fatal(err)
	}
	reordered := []PlaylistRun{{3, "c"}, {1, "a"}}
	name := "Second name"
	newSlug, p, err := store.Update(slug, &name, reordered)
	if err != nil {
		t.Fatal(err)
	}
	if newSlug != "second-name" || p.Name != name || !reflect.DeepEqual(p.Runs, reordered) {
		t.Fatalf("update: slug=%q playlist=%+v", newSlug, p)
	}
	if _, err := os.Stat(filepath.Join(store.Dir, "first.json")); !os.IsNotExist(err) {
		t.Fatal("old file kept after rename")
	}
	list, err := store.List()
	if err != nil || len(list) != 1 || list[0].Slug != "second-name" {
		t.Fatalf("list = %+v err=%v", list, err)
	}
	if err := store.Delete("second-name"); err != nil {
		t.Fatal(err)
	}
	if list, _ := store.List(); len(list) != 0 {
		t.Fatalf("list after delete = %+v", list)
	}
}

func TestPlayNow_ContractShape(t *testing.T) {
	store := PlaylistStore{Dir: t.TempDir()}
	if _, err := store.RequestPlay("missing", nil); err == nil {
		t.Fatal("expected an error for an unknown playlist")
	}
	slug, _, _ := store.Create("Mine", []PlaylistRun{{5, "x"}}, nil)

	if _, err := store.RequestPlay(slug, nil); err != nil {
		t.Fatal(err)
	}
	raw, _ := os.ReadFile(filepath.Join(store.Dir, PlayNowFile))
	var m map[string]json.RawMessage
	_ = json.Unmarshal(raw, &m)
	if !sameSet(keysOf(m), []string{"format_version", "requested_at_utc", "playlist"}) || string(m["playlist"]) != `"mine"` {
		t.Fatalf("playlist request = %s", raw)
	}

	if _, err := store.RequestPlay("", []PlaylistRun{{7, "seven"}}); err != nil {
		t.Fatal(err)
	}
	raw, _ = os.ReadFile(filepath.Join(store.Dir, PlayNowFile))
	m = nil
	_ = json.Unmarshal(raw, &m)
	if !sameSet(keysOf(m), []string{"format_version", "requested_at_utc", "runs"}) {
		t.Fatalf("runs request = %s", raw)
	}

	// The request file is not a playlist.
	if list, _ := store.List(); len(list) != 1 {
		t.Fatalf("play-now.json listed as a playlist: %+v", list)
	}
	if p, _ := store.PendingPlay(); p == nil || len(p.Runs) != 1 {
		t.Fatalf("pending = %+v", p)
	}
	os.Remove(filepath.Join(store.Dir, PlayNowFile)) // the game consumes it
	if p, _ := store.PendingPlay(); p != nil {
		t.Fatalf("pending after consume = %+v", p)
	}
}

func TestCheckReplayable(t *testing.T) {
	cur := CurrentBuild{Known: true, Commit: "41cef1ea", Version: "v0.111.0", ModelIDHash: "1568834832"}
	good := recordingFacts{hasRecording: true, dirExists: true, headerOK: true, snapshots: 3, commit: "41CEF1EA", version: "v0.111.0", modelIDHash: "1568834832"}
	if r := checkReplayable(good, cur); !r.Replayable {
		t.Fatalf("good recording refused: %s", r.Reason)
	}
	cases := map[string]func(f *recordingFacts){
		"not recorded":         func(f *recordingFacts) { f.hasRecording = false },
		"directory is missing": func(f *recordingFacts) { f.dirExists = false },
		"header":               func(f *recordingFacts) { f.headerOK = false },
		"no snapshots":         func(f *recordingFacts) { f.snapshots = 0 },
		"recorded on":          func(f *recordingFacts) { f.commit = "deadbeef" },
		"content table":        func(f *recordingFacts) { f.modelIDHash = "1" },
	}
	for want, mutate := range cases {
		f := good
		mutate(&f)
		r := checkReplayable(f, cur)
		if r.Replayable || !strings.Contains(r.Reason, want) {
			t.Errorf("%s: got %+v", want, r)
		}
	}
	if r := checkReplayable(good, CurrentBuild{Commit: "41cef1ea"}); r.Replayable {
		t.Error("unknown model hash must not be playable")
	}
	mid := good
	mid.midRun = true
	if r := checkReplayable(mid, cur); !r.Replayable || r.Reason == "" {
		t.Errorf("mid-run recording: %+v", r)
	}
}

func TestBuildWhere(t *testing.T) {
	five, ten := int64(5), int64(10)
	where, args := buildWhere(Filter{
		Character: "CHARACTER.REGENT", AscensionMin: &five, AscensionMax: &ten,
		Outcomes: []string{"win", "bogus", "loss"}, PickedCard: "defy", HasRelic: "Winged Boots", Source: "recorded",
	})
	for _, frag := range []string{"r.character = ?", "r.ascension >= ?", "r.ascension <= ?", "r.outcome IN (?,?)", "run_choices", "run_relics", "r.has_recording = 1"} {
		if !strings.Contains(where, frag) {
			t.Errorf("missing %q in %s", frag, where)
		}
	}
	want := []any{"CHARACTER.REGENT", five, ten, "win", "loss", "CARD.DEFY", "RELIC.WINGED_BOOTS"}
	if !reflect.DeepEqual(args, want) {
		t.Fatalf("args = %v, want %v", args, want)
	}
	if w, a := buildWhere(Filter{}); w != "" || a != nil {
		t.Fatalf("empty filter: %q %v", w, a)
	}
}

func TestLoopbackHost(t *testing.T) {
	for _, h := range []string{"127.0.0.1:8737", "localhost:8737", "[::1]:8737", "LOCALHOST"} {
		if !loopbackHost(h) {
			t.Errorf("%q rejected", h)
		}
	}
	for _, h := range []string{"evil.example:8737", "192.168.1.5:8737", ""} {
		if loopbackHost(h) {
			t.Errorf("%q accepted", h)
		}
	}
}

func TestRunLabel(t *testing.T) {
	c, a, o, f := "CHARACTER.REGENT", int64(10), "win", int64(49)
	st := time.Date(2026, 10, 15, 12, 0, 0, 0, time.Local).Unix()
	if got := RunLabel(st, &c, &a, &o, &f); got != "2026-10-15 Regent A10 WIN f49" {
		t.Fatalf("label = %q", got)
	}
	if got := RunLabel(st, nil, nil, nil, nil); got != "2026-10-15 UNFINISHED" {
		t.Fatalf("bare label = %q", got)
	}
}

func keysOf(m map[string]json.RawMessage) []string {
	var out []string
	for k := range m {
		out = append(out, k)
	}
	return out
}

func sameSet(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	seen := map[string]int{}
	for _, x := range a {
		seen[x]++
	}
	for _, x := range b {
		seen[x]--
	}
	for _, n := range seen {
		if n != 0 {
			return false
		}
	}
	return true
}
