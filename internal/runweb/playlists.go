package runweb

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// The playlist files shared with the in-game replay player
// (docs/run-database.md "Playlists", docs/replay-player.md). The JSON keys
// are a contract: snake_case, exactly as below.

// PlaylistFormatVersion is format_version of playlist and play-now files.
const PlaylistFormatVersion = 1

// PlayNowFile is the request file the game consumes (deletes).
const PlayNowFile = "play-now.json"

// PlaylistRun is one entry of a playlist's runs list.
type PlaylistRun struct {
	GameStartTime int64  `json:"game_start_time"`
	Label         string `json:"label"`
}

// Playlist is <playlists>/<slug>.json.
type Playlist struct {
	FormatVersion int             `json:"format_version"`
	Name          string          `json:"name"`
	CreatedAtUTC  string          `json:"created_at_utc"`
	UpdatedAtUTC  string          `json:"updated_at_utc"`
	Filter        json.RawMessage `json:"filter"` // informational; may be null
	Runs          []PlaylistRun   `json:"runs"`
}

// PlayNow is <playlists>/play-now.json: exactly one of Playlist or Runs.
type PlayNow struct {
	FormatVersion  int           `json:"format_version"`
	RequestedAtUTC string        `json:"requested_at_utc"`
	Playlist       string        `json:"playlist,omitempty"`
	Runs           []PlaylistRun `json:"runs,omitempty"`
}

func utcNow() string { return time.Now().UTC().Format("2006-01-02T15:04:05Z") }

// Slugify turns a playlist name into its file stem: lower-case ASCII letters
// and digits, other runs of characters collapsed to "-", at most 60
// characters. "play-now" is reserved for the request file.
func Slugify(name string) string {
	var b strings.Builder
	dash := false
	for _, r := range strings.ToLower(name) {
		switch {
		case (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9'):
			b.WriteRune(r)
			dash = false
		default:
			if b.Len() > 0 && !dash {
				b.WriteByte('-')
				dash = true
			}
		}
	}
	s := strings.Trim(b.String(), "-")
	if len(s) > 60 {
		s = strings.Trim(s[:60], "-")
	}
	if s == "" {
		s = "playlist"
	}
	if s == strings.TrimSuffix(PlayNowFile, ".json") {
		s += "-list"
	}
	return s
}

// validSlug guards path parameters: only what Slugify can produce.
func validSlug(s string) bool {
	if s == "" || len(s) > 64 || s == strings.TrimSuffix(PlayNowFile, ".json") {
		return false
	}
	for _, r := range s {
		if !((r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-') {
			return false
		}
	}
	return true
}

// writeJSONAtomic writes v to path through a temp file in the same
// directory and a rename, so the game never reads a half-written file. The
// temp name does not end in .json, so a reader enumerating *.json never
// sees it. The rename is retried briefly in case the game has the target
// open at that instant.
func writeJSONAtomic(path string, v any) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	b = append(b, '\n')
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, "."+filepath.Base(path)+".*.tmp")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	if _, err := tmp.Write(b); err != nil {
		tmp.Close()
		os.Remove(tmpName)
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		os.Remove(tmpName)
		return err
	}
	if err := tmp.Close(); err != nil {
		os.Remove(tmpName)
		return err
	}
	for attempt := 0; ; attempt++ {
		err = os.Rename(tmpName, path)
		if err == nil || attempt >= 10 {
			break
		}
		time.Sleep(50 * time.Millisecond)
	}
	if err != nil {
		os.Remove(tmpName)
	}
	return err
}

// PlaylistStore reads and writes the playlists directory.
type PlaylistStore struct {
	Dir string
}

func (s PlaylistStore) path(slug string) string { return filepath.Join(s.Dir, slug+".json") }

// StoredPlaylist is a playlist plus its slug, or a file that failed to parse.
type StoredPlaylist struct {
	Slug     string    `json:"slug"`
	Playlist *Playlist `json:"playlist,omitempty"`
	Error    string    `json:"error,omitempty"`
}

// List returns every <slug>.json except play-now.json, sorted by name.
func (s PlaylistStore) List() ([]StoredPlaylist, error) {
	entries, err := os.ReadDir(s.Dir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, nil
		}
		return nil, err
	}
	var out []StoredPlaylist
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".json") || name == PlayNowFile || strings.HasPrefix(name, ".") {
			continue
		}
		slug := strings.TrimSuffix(name, ".json")
		p, err := s.Get(slug)
		if err != nil {
			out = append(out, StoredPlaylist{Slug: slug, Error: err.Error()})
			continue
		}
		out = append(out, StoredPlaylist{Slug: slug, Playlist: p})
	}
	sort.SliceStable(out, func(i, j int) bool {
		ni, nj := out[i].Slug, out[j].Slug
		if out[i].Playlist != nil {
			ni = strings.ToLower(out[i].Playlist.Name)
		}
		if out[j].Playlist != nil {
			nj = strings.ToLower(out[j].Playlist.Name)
		}
		return ni < nj
	})
	return out, nil
}

// Get reads one playlist.
func (s PlaylistStore) Get(slug string) (*Playlist, error) {
	if !validSlug(slug) {
		return nil, fmt.Errorf("invalid playlist id %q", slug)
	}
	b, err := os.ReadFile(s.path(slug))
	if err != nil {
		return nil, err
	}
	var p Playlist
	if err := json.Unmarshal(b, &p); err != nil {
		return nil, fmt.Errorf("malformed playlist file: %w", err)
	}
	if p.Runs == nil {
		p.Runs = []PlaylistRun{}
	}
	return &p, nil
}

// uniqueSlug returns base, or base-2, base-3... so it names no other
// existing playlist. keep is a slug that may be reused (the playlist being
// renamed).
func (s PlaylistStore) uniqueSlug(base, keep string) string {
	slug := base
	for n := 2; ; n++ {
		if slug == keep {
			return slug
		}
		if _, err := os.Stat(s.path(slug)); errors.Is(err, os.ErrNotExist) {
			return slug
		}
		suffix := fmt.Sprintf("-%d", n)
		trimmed := base
		if len(trimmed)+len(suffix) > 64 {
			trimmed = trimmed[:64-len(suffix)]
		}
		slug = trimmed + suffix
	}
}

// Create writes a new playlist and returns its slug.
func (s PlaylistStore) Create(name string, runs []PlaylistRun, filter json.RawMessage) (string, *Playlist, error) {
	name = strings.TrimSpace(name)
	if name == "" {
		return "", nil, errors.New("playlist name is required")
	}
	slug := s.uniqueSlug(Slugify(name), "")
	now := utcNow()
	if len(filter) == 0 {
		filter = json.RawMessage("null")
	}
	if runs == nil {
		runs = []PlaylistRun{}
	}
	p := &Playlist{FormatVersion: PlaylistFormatVersion, Name: name, CreatedAtUTC: now, UpdatedAtUTC: now, Filter: filter, Runs: runs}
	if err := writeJSONAtomic(s.path(slug), p); err != nil {
		return "", nil, err
	}
	return slug, p, nil
}

// Update renames and/or replaces the run list of a playlist. A rename that
// changes the slug writes the new file first, then removes the old one. It
// returns the (possibly new) slug.
func (s PlaylistStore) Update(slug string, name *string, runs []PlaylistRun) (string, *Playlist, error) {
	p, err := s.Get(slug)
	if err != nil {
		return "", nil, err
	}
	newSlug := slug
	if name != nil {
		n := strings.TrimSpace(*name)
		if n == "" {
			return "", nil, errors.New("playlist name is required")
		}
		p.Name = n
		newSlug = s.uniqueSlug(Slugify(n), slug)
	}
	if runs != nil {
		p.Runs = runs
	}
	p.FormatVersion = PlaylistFormatVersion
	p.UpdatedAtUTC = utcNow()
	if err := writeJSONAtomic(s.path(newSlug), p); err != nil {
		return "", nil, err
	}
	if newSlug != slug {
		if err := os.Remove(s.path(slug)); err != nil && !errors.Is(err, os.ErrNotExist) {
			return newSlug, p, fmt.Errorf("renamed, but the old file could not be removed: %w", err)
		}
	}
	return newSlug, p, nil
}

// Delete removes a playlist file.
func (s PlaylistStore) Delete(slug string) error {
	if !validSlug(slug) {
		return fmt.Errorf("invalid playlist id %q", slug)
	}
	return os.Remove(s.path(slug))
}

// RequestPlay writes play-now.json for a saved playlist (slug) or an ad-hoc
// run list.
func (s PlaylistStore) RequestPlay(slug string, runs []PlaylistRun) (*PlayNow, error) {
	req := &PlayNow{FormatVersion: PlaylistFormatVersion, RequestedAtUTC: utcNow()}
	switch {
	case slug != "" && len(runs) > 0:
		return nil, errors.New("give a playlist or runs, not both")
	case slug != "":
		if _, err := s.Get(slug); err != nil {
			return nil, err
		}
		req.Playlist = slug
	case len(runs) > 0:
		req.Runs = runs
	default:
		return nil, errors.New("nothing to play")
	}
	if err := writeJSONAtomic(filepath.Join(s.Dir, PlayNowFile), req); err != nil {
		return nil, err
	}
	return req, nil
}

// PendingPlay returns the play-now request the game has not consumed yet, or nil.
func (s PlaylistStore) PendingPlay() (*PlayNow, error) {
	b, err := os.ReadFile(filepath.Join(s.Dir, PlayNowFile))
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, nil
		}
		return nil, err
	}
	var req PlayNow
	if err := json.Unmarshal(b, &req); err != nil {
		return &PlayNow{}, nil // present but unreadable: still pending from the page's view
	}
	return &req, nil
}
