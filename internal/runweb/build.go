package runweb

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// CurrentBuild is the game build a recording must match to be replayable.
//
// The rule (the same one the in-game player applies, ReplayVersionGate.Check
// in Core/Replay/Playback/ReplayPlaybackPlan.cs): a recording plays only when its header's
// game.commit equals the running game's commit (case-insensitive) AND its
// game.model_id_hash equals the running game's ModelIdSerializationCache.Hash.
//
// From outside the game:
//   - the commit is read from the installed game's release_info.json when the
//     install can be found (the same file the recorder stamps headers from);
//   - the model_id_hash is computed by the running game and is not on disk,
//     so it is taken from the newest recording made on that commit. If no
//     recording exists on the installed commit, nothing is marked playable;
//   - if the install cannot be found, the newest recording's commit and hash
//     are assumed to be current, and the page says so.
//
// The game re-checks every run itself and skips incompatible ones, so a
// wrong guess here costs a skipped run, never a bad replay.
type CurrentBuild struct {
	Known       bool   `json:"known"`
	Commit      string `json:"commit"`
	Version     string `json:"version"`
	ModelIDHash string `json:"model_id_hash"`
	Source      string `json:"source"`
	Note        string `json:"note"`
	InstallDir  string `json:"install_dir,omitempty"`
}

type releaseInfo struct {
	Commit  string `json:"commit"`
	Version string `json:"version"`
}

const gameFolderName = "Slay the Spire 2"

// FindGameInstall returns the Slay the Spire 2 install directory, or "".
// Order: explicit dir, then every Steam library listed in
// libraryfolders.vdf (Steam's path from the registry, then the default).
func FindGameInstall(explicit string) string {
	hasRelease := func(dir string) bool {
		_, err := os.Stat(filepath.Join(dir, "release_info.json"))
		return err == nil
	}
	if explicit != "" {
		if hasRelease(explicit) {
			return explicit
		}
		return ""
	}
	var steamRoots []string
	if p := steamPathFromRegistry(); p != "" {
		steamRoots = append(steamRoots, p)
	}
	steamRoots = append(steamRoots, `C:\Program Files (x86)\Steam`, `C:\Program Files\Steam`)
	if home, err := os.UserHomeDir(); err == nil {
		steamRoots = append(steamRoots, filepath.Join(home, ".local", "share", "Steam"))
	}
	pathRe := regexp.MustCompile(`"path"\s+"([^"]+)"`)
	seen := map[string]bool{}
	for _, root := range steamRoots {
		libs := []string{root}
		for _, vdf := range []string{
			filepath.Join(root, "steamapps", "libraryfolders.vdf"),
			filepath.Join(root, "config", "libraryfolders.vdf"),
		} {
			if b, err := os.ReadFile(vdf); err == nil {
				for _, m := range pathRe.FindAllStringSubmatch(string(b), -1) {
					libs = append(libs, strings.ReplaceAll(m[1], `\\`, `\`))
				}
			}
		}
		for _, lib := range libs {
			dir := filepath.Join(lib, "steamapps", "common", gameFolderName)
			key := strings.ToLower(filepath.Clean(dir))
			if seen[key] {
				continue
			}
			seen[key] = true
			if hasRelease(dir) {
				return dir
			}
		}
	}
	return ""
}

// DetectCurrentBuild applies the rule documented on CurrentBuild.
func DetectCurrentBuild(ctx context.Context, db *sql.DB, gameDir string) CurrentBuild {
	var b CurrentBuild
	install := FindGameInstall(gameDir)
	var rel releaseInfo
	if install != "" {
		if raw, err := os.ReadFile(filepath.Join(install, "release_info.json")); err == nil && json.Unmarshal(raw, &rel) == nil && rel.Commit != "" {
			b.InstallDir = install
		} else {
			rel = releaseInfo{}
		}
	}

	type rec struct{ commit, version, hash string }
	var recs []rec
	rows, err := db.QueryContext(ctx, `
		SELECT game_commit, coalesce(game_version, ''), coalesce(model_id_hash, '')
		FROM recordings WHERE header_ok = 1 AND game_commit IS NOT NULL
		ORDER BY game_start_time DESC`)
	if err == nil {
		for rows.Next() {
			var r rec
			if rows.Scan(&r.commit, &r.version, &r.hash) == nil {
				recs = append(recs, r)
			}
		}
		rows.Close()
	}

	const hashNote = "The model-id hash is computed by the running game and is not on disk, so it is taken from the newest recording made on that commit."
	switch {
	case rel.Commit != "":
		b.Commit, b.Version = rel.Commit, rel.Version
		for _, r := range recs {
			if strings.EqualFold(r.commit, rel.Commit) {
				b.ModelIDHash = r.hash
				break
			}
		}
		if b.ModelIDHash != "" {
			b.Known = true
			b.Source = "installed game + newest recording on that commit"
			b.Note = fmt.Sprintf("Current build: the installed game, %s (commit %s), from release_info.json. %s", rel.Version, rel.Commit, hashNote)
		} else {
			b.Source = "installed game (no recording on it yet)"
			b.Note = fmt.Sprintf("The installed game is %s (commit %s), and no recording has been made on it yet, so no recording can match it: nothing is marked playable. Play one run on this build to record it.", rel.Version, rel.Commit)
		}
	case len(recs) > 0:
		b.Known = true
		b.Commit, b.Version, b.ModelIDHash = recs[0].commit, recs[0].version, recs[0].hash
		b.Source = "newest recording (game install not found)"
		b.Note = fmt.Sprintf("The game install was not found, so the current build is assumed to be the newest recording's: %s (commit %s). If the game has updated since, the game will skip the runs that no longer match.", b.Version, b.Commit)
	default:
		b.Source = "unknown"
		b.Note = "No game install and no recordings were found, so no run can be marked playable."
	}
	return b
}

// Replayability says whether a run can be played on the current build, and
// why not when it cannot.
type Replayability struct {
	Replayable bool   `json:"replayable"`
	Reason     string `json:"reason,omitempty"` // why not, or an informational note when replayable
}

type recordingFacts struct {
	hasRecording bool
	dirExists    bool
	headerOK     bool
	snapshots    int64
	commit       string
	version      string
	modelIDHash  string
	midRun       bool
}

func checkReplayable(f recordingFacts, cur CurrentBuild) Replayability {
	switch {
	case !f.hasRecording:
		return Replayability{Reason: "not recorded (game history only)"}
	case !f.dirExists:
		return Replayability{Reason: "recording directory is missing"}
	case !f.headerOK:
		return Replayability{Reason: "recording header is unreadable"}
	case f.snapshots == 0:
		return Replayability{Reason: "no snapshots (recorded before snapshots were captured)"}
	case f.commit == "" || f.modelIDHash == "":
		return Replayability{Reason: "recording does not name its game build"}
	case !cur.Known || cur.ModelIDHash == "":
		return Replayability{Reason: "current game build unknown"}
	case !strings.EqualFold(f.commit, cur.Commit):
		return Replayability{Reason: fmt.Sprintf("recorded on %s (%s); current build is %s (%s)", orQ(f.version), f.commit, orQ(cur.Version), cur.Commit)}
	case f.modelIDHash != cur.ModelIDHash:
		return Replayability{Reason: fmt.Sprintf("game content table differs (model_id_hash %s, current %s)", f.modelIDHash, cur.ModelIDHash)}
	}
	r := Replayability{Replayable: true}
	if f.midRun {
		r.Reason = "recording started mid-run; plays from its earliest snapshot"
	}
	return r
}

func orQ(s string) string {
	if s == "" {
		return "?"
	}
	return s
}
