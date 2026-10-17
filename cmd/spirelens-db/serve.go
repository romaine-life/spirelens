package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"time"

	"github.com/romaine-life/spirelens/internal/rundb"
	"github.com/romaine-life/spirelens/internal/runweb"
)

func runServe(ctx context.Context, args []string) error {
	fs := flag.NewFlagSet("serve", flag.ExitOnError)
	addr := fs.String("addr", "127.0.0.1:8737", "listen address (loopback only)")
	dbPath := fs.String("db", rundb.DefaultDBPath(), "database file")
	replays := fs.String("replays", rundb.DefaultReplaysDir(), "SpireLens replays directory")
	playlists := fs.String("playlists", rundb.DefaultPlaylistsDir(), "playlists directory shared with the game")
	gameDir := fs.String("game-dir", "", "Slay the Spire 2 install directory (default: found through Steam)")
	noIngest := fs.Bool("no-ingest", false, "do not ingest before serving")
	fs.Parse(args)

	host, _, err := net.SplitHostPort(*addr)
	if err != nil {
		return fmt.Errorf("--addr %q: %w", *addr, err)
	}
	if ip := net.ParseIP(host); host != "localhost" && (ip == nil || !ip.IsLoopback()) {
		return fmt.Errorf("--addr %q: serve binds to a loopback address only (127.0.0.1, ::1 or localhost)", *addr)
	}

	if err := os.MkdirAll(filepath.Dir(*dbPath), 0o755); err != nil {
		return err
	}
	if err := os.MkdirAll(*playlists, 0o755); err != nil {
		return err
	}
	db, err := rundb.Open(ctx, *dbPath)
	if err != nil {
		return err
	}
	defer db.Close()

	ingest := func(ctx context.Context) runweb.IngestStatus {
		now := time.Now().Format(time.RFC3339)
		release, locked, err := rundb.AcquireIngestLock(*dbPath)
		if err != nil {
			return runweb.IngestStatus{State: "failed", Message: err.Error(), At: now}
		}
		if !locked {
			return runweb.IngestStatus{State: "locked", Message: "another ingest is running; serving the data as it is", At: now}
		}
		defer release()
		warnings := 0
		report, err := rundb.Ingest(ctx, db, rundb.IngestOptions{
			ReplaysDir:       *replays,
			HistoryDirs:      rundb.DefaultHistoryDirs(),
			SpireLensRunsDir: rundb.DefaultSpireLensRunsDir(),
			Warn:             func(string, ...any) { warnings++ },
		})
		if err != nil {
			return runweb.IngestStatus{State: "failed", Message: err.Error(), At: now}
		}
		n := report.Recordings.Ingested + report.History.Ingested + report.SpireLens.Ingested
		return runweb.IngestStatus{State: "ok", Message: fmt.Sprintf("%d new or changed sources, %d warnings", n, warnings), At: time.Now().Format(time.RFC3339)}
	}

	srv := &runweb.Server{
		DB:         db,
		DBPath:     *dbPath,
		ReplaysDir: *replays,
		GameDir:    *gameDir,
		Playlists:  runweb.PlaylistStore{Dir: *playlists},
		Ingest:     ingest,
	}
	status := runweb.IngestStatus{State: "skipped", Message: "ingest skipped (--no-ingest)", At: time.Now().Format(time.RFC3339)}
	if !*noIngest {
		fmt.Println("ingesting…")
		status = ingest(ctx)
		fmt.Printf("ingest: %s (%s)\n", status.State, status.Message)
	}
	srv.Init(ctx, status)

	ln, err := net.Listen("tcp", *addr)
	if err != nil {
		return err
	}
	httpSrv := &http.Server{Handler: srv.Handler(), ReadHeaderTimeout: 10 * time.Second}
	fmt.Printf("serving http://%s/  (Ctrl+C to stop)\n", ln.Addr())

	ctx, stop := signal.NotifyContext(ctx, os.Interrupt)
	defer stop()
	go func() {
		<-ctx.Done()
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = httpSrv.Shutdown(shutdownCtx)
	}()
	if err := httpSrv.Serve(ln); err != nil && !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}
