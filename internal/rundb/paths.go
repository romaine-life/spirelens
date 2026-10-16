package rundb

import (
	"os"
	"path/filepath"
	"sort"
)

// GameDataDir is %APPDATA%\SlayTheSpire2 (Godot's user:// root for the game).
func GameDataDir() string {
	base := os.Getenv("APPDATA")
	if base == "" {
		if dir, err := os.UserConfigDir(); err == nil {
			base = dir
		}
	}
	return filepath.Join(base, "SlayTheSpire2")
}

// DefaultDBPath is %APPDATA%\SlayTheSpire2\SpireLens\spirelens.db.
func DefaultDBPath() string {
	return filepath.Join(GameDataDir(), "SpireLens", "spirelens.db")
}

// DefaultReplaysDir is SpireLens's run-recording root.
func DefaultReplaysDir() string {
	return filepath.Join(GameDataDir(), "SpireLens", "replays")
}

// DefaultSpireLensRunsDir is SpireLens's per-run stat records directory.
func DefaultSpireLensRunsDir() string {
	return filepath.Join(GameDataDir(), "SpireLens", "runs")
}

// DefaultPlaylistsDir is where replay playlists shared with the game live.
func DefaultPlaylistsDir() string {
	return filepath.Join(GameDataDir(), "SpireLens", "playlists")
}

// DefaultHistoryDirs finds every saves\history directory the game keeps:
// steam\<steamid>\profileN\saves\history and the modded-profile copies under
// steam\<steamid>\modded\profileN\saves\history.
func DefaultHistoryDirs() []string {
	root := filepath.Join(GameDataDir(), "steam")
	var dirs []string
	for _, pattern := range []string{
		filepath.Join(root, "*", "profile*", "saves", "history"),
		filepath.Join(root, "*", "modded", "profile*", "saves", "history"),
	} {
		matches, _ := filepath.Glob(pattern)
		for _, m := range matches {
			if info, err := os.Stat(m); err == nil && info.IsDir() {
				dirs = append(dirs, m)
			}
		}
	}
	sort.Strings(dirs)
	return dirs
}
