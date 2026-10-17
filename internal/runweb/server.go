// Package runweb is the local web page of `spirelens-db serve`: filter runs
// in the run database and build replay playlists the in-game replay player
// reads (docs/run-database.md "Run browser and playlists").
package runweb

import (
	"context"
	"database/sql"
	"embed"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/romaine-life/spirelens/internal/rundb"
)

//go:embed assets
var assets embed.FS

// IngestStatus describes the ingest that ran before (or during) serving.
type IngestStatus struct {
	State   string `json:"state"` // ok | locked | failed | skipped
	Message string `json:"message"`
	At      string `json:"at"`
}

// Server serves the page and its JSON API.
type Server struct {
	DB         *sql.DB
	DBPath     string // locates last-backup.json for the header's backup line
	ReplaysDir string
	GameDir    string
	Playlists  PlaylistStore
	// Ingest runs one incremental ingest (respecting the ingest lock).
	Ingest func(ctx context.Context) IngestStatus

	mu     sync.Mutex
	build  CurrentBuild
	ingest IngestStatus
}

// Init records the startup ingest and detects the current build.
func (s *Server) Init(ctx context.Context, st IngestStatus) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.ingest = st
	s.build = DetectCurrentBuild(ctx, s.DB, s.GameDir)
}

func (s *Server) current() (CurrentBuild, IngestStatus) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.build, s.ingest
}

// Handler returns the HTTP handler. Requests must name a loopback Host
// (blocks DNS-rebinding pages), and every state-changing request must carry
// the X-SpireLens header (a cross-site form or fetch cannot add it without a
// CORS preflight, which this server never grants).
func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	static, _ := fs.Sub(assets, "assets")
	mux.Handle("GET /", http.FileServerFS(static))
	mux.HandleFunc("GET /api/meta", s.handleMeta)
	mux.HandleFunc("POST /api/runs", s.handleRuns)
	mux.HandleFunc("POST /api/ingest", s.handleIngest)
	mux.HandleFunc("GET /api/playlists", s.handleListPlaylists)
	mux.HandleFunc("POST /api/playlists", s.handleCreatePlaylist)
	mux.HandleFunc("PUT /api/playlists/{slug}", s.handleUpdatePlaylist)
	mux.HandleFunc("DELETE /api/playlists/{slug}", s.handleDeletePlaylist)
	mux.HandleFunc("GET /api/play", s.handlePlayStatus)
	mux.HandleFunc("POST /api/play", s.handlePlay)

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !loopbackHost(r.Host) {
			http.Error(w, "spirelens-db serve answers only on localhost", http.StatusForbidden)
			return
		}
		if r.Method != http.MethodGet && r.Method != http.MethodHead && r.Header.Get("X-SpireLens") == "" {
			http.Error(w, "missing X-SpireLens header", http.StatusForbidden)
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		mux.ServeHTTP(w, r)
	})
}

func loopbackHost(hostport string) bool {
	host := hostport
	if h, _, err := net.SplitHostPort(hostport); err == nil {
		host = h
	}
	host = strings.Trim(host, "[]")
	if strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(v)
}

func writeErr(w http.ResponseWriter, status int, err error) {
	writeJSON(w, status, map[string]string{"error": err.Error()})
}

func readJSON(r *http.Request, v any) error {
	dec := json.NewDecoder(http.MaxBytesReader(nil, r.Body, 1<<20))
	if err := dec.Decode(v); err != nil {
		return fmt.Errorf("bad request body: %w", err)
	}
	return nil
}

func (s *Server) handleMeta(w http.ResponseWriter, r *http.Request) {
	meta, err := LoadMeta(r.Context(), s.DB)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err)
		return
	}
	build, ingest := s.current()
	var backup *rundb.BackupReport
	if s.DBPath != "" {
		backup = rundb.LoadBackupState(s.DBPath)
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"meta":          meta,
		"build":         build,
		"ingest":        ingest,
		"backup":        backup, // last-backup.json, or null when no backup has run
		"playlists_dir": s.Playlists.Dir,
	})
}

func (s *Server) handleRuns(w http.ResponseWriter, r *http.Request) {
	var f Filter
	if err := readJSON(r, &f); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	build, _ := s.current()
	runs, err := QueryRuns(r.Context(), s.DB, f, build, s.ReplaysDir)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"runs": runs})
}

func (s *Server) handleIngest(w http.ResponseWriter, r *http.Request) {
	st := IngestStatus{State: "skipped", Message: "ingest is not available", At: time.Now().Format(time.RFC3339)}
	if s.Ingest != nil {
		st = s.Ingest(r.Context())
	}
	s.Init(r.Context(), st)
	build, ingest := s.current()
	writeJSON(w, http.StatusOK, map[string]any{"ingest": ingest, "build": build})
}

// playlistView is a stored playlist with each run's current replayability.
type playlistView struct {
	Slug         string            `json:"slug"`
	Name         string            `json:"name,omitempty"`
	CreatedAtUTC string            `json:"created_at_utc,omitempty"`
	UpdatedAtUTC string            `json:"updated_at_utc,omitempty"`
	Filter       json.RawMessage   `json:"filter,omitempty"`
	Runs         []playlistRunView `json:"runs"`
	Error        string            `json:"error,omitempty"`
}

type playlistRunView struct {
	PlaylistRun
	Replayability
	Known bool `json:"known"` // the run is in the database
}

func (s *Server) handleListPlaylists(w http.ResponseWriter, r *http.Request) {
	stored, err := s.Playlists.List()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err)
		return
	}
	build, _ := s.current()
	all, err := QueryRuns(r.Context(), s.DB, Filter{}, build, s.ReplaysDir)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err)
		return
	}
	byID := make(map[int64]RunRow, len(all))
	for _, row := range all {
		byID[row.GameStartTime] = row
	}
	views := []playlistView{}
	for _, sp := range stored {
		v := playlistView{Slug: sp.Slug, Error: sp.Error, Runs: []playlistRunView{}}
		if p := sp.Playlist; p != nil {
			v.Name, v.CreatedAtUTC, v.UpdatedAtUTC, v.Filter = p.Name, p.CreatedAtUTC, p.UpdatedAtUTC, p.Filter
			for _, pr := range p.Runs {
				rv := playlistRunView{PlaylistRun: pr}
				if row, ok := byID[pr.GameStartTime]; ok {
					rv.Known = true
					rv.Replayability = row.Replayability
				} else {
					rv.Reason = "not in the run database"
				}
				v.Runs = append(v.Runs, rv)
			}
		}
		views = append(views, v)
	}
	writeJSON(w, http.StatusOK, map[string]any{"playlists": views})
}

type playlistBody struct {
	Name   *string         `json:"name"`
	Runs   []int64         `json:"runs"`
	Filter json.RawMessage `json:"filter"`
}

func (s *Server) handleCreatePlaylist(w http.ResponseWriter, r *http.Request) {
	var body playlistBody
	if err := readJSON(r, &body); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	if body.Name == nil {
		writeErr(w, http.StatusBadRequest, errors.New("playlist name is required"))
		return
	}
	runs, dropped, err := LookupRuns(r.Context(), s.DB, body.Runs, s.ReplaysDir, nil)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err)
		return
	}
	slug, p, err := s.Playlists.Create(*body.Name, runs, body.Filter)
	if err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"slug": slug, "playlist": p, "dropped": nonNil(dropped)})
}

func (s *Server) handleUpdatePlaylist(w http.ResponseWriter, r *http.Request) {
	slug := r.PathValue("slug")
	if !validSlug(slug) {
		writeErr(w, http.StatusBadRequest, fmt.Errorf("invalid playlist id %q", slug))
		return
	}
	var body playlistBody
	if err := readJSON(r, &body); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	var runs []PlaylistRun
	var dropped []int64
	if body.Runs != nil {
		// A run the database does not know keeps its stored label.
		keep := map[int64]string{}
		if old, err := s.Playlists.Get(slug); err == nil {
			for _, pr := range old.Runs {
				keep[pr.GameStartTime] = pr.Label
			}
		}
		var err error
		runs, dropped, err = LookupRuns(r.Context(), s.DB, body.Runs, s.ReplaysDir, keep)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err)
			return
		}
	}
	newSlug, p, err := s.Playlists.Update(slug, body.Name, runs)
	if err != nil {
		status := http.StatusBadRequest
		if errors.Is(err, os.ErrNotExist) {
			status = http.StatusNotFound
		}
		writeErr(w, status, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"slug": newSlug, "playlist": p, "dropped": nonNil(dropped)})
}

func (s *Server) handleDeletePlaylist(w http.ResponseWriter, r *http.Request) {
	if err := s.Playlists.Delete(r.PathValue("slug")); err != nil {
		status := http.StatusBadRequest
		if errors.Is(err, os.ErrNotExist) {
			status = http.StatusNotFound
		}
		writeErr(w, status, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"deleted": true})
}

type playBody struct {
	Playlist string  `json:"playlist"`
	Runs     []int64 `json:"runs"`
}

func (s *Server) handlePlay(w http.ResponseWriter, r *http.Request) {
	var body playBody
	if err := readJSON(r, &body); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	var runs []PlaylistRun
	var dropped []int64
	if len(body.Runs) > 0 {
		var err error
		runs, dropped, err = LookupRuns(r.Context(), s.DB, body.Runs, s.ReplaysDir, nil)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err)
			return
		}
		if len(runs) == 0 {
			writeErr(w, http.StatusBadRequest, errors.New("none of those runs has a recording"))
			return
		}
	}
	req, err := s.Playlists.RequestPlay(body.Playlist, runs)
	if err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"request": req, "dropped": nonNil(dropped)})
}

func (s *Server) handlePlayStatus(w http.ResponseWriter, r *http.Request) {
	req, err := s.Playlists.PendingPlay()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"pending": req != nil, "request": req})
}

func nonNil(v []int64) []int64 {
	if v == nil {
		return []int64{}
	}
	return v
}
