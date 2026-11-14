package host

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// BaseLibMinVersion is the BaseLib version floor enforced during env prep.
// BaseLib 3.4.7 references the removed SingletonModel type and fails to load on
// STS2 v0.113, which takes SpireLens down with it.
const BaseLibMinVersion = "3.4.8"

// allowedMods is the AGENTS.md mod allowlist; SpireLensMcp may be absent before
// install (per-run), so only a name OUTSIDE this set is unexpected.
var allowedMods = []string{"BaseLib", "SpireLens", "SpireLensMcp"}

// ModProbeResult is the structured verdict the probe-mods subcommand prints to
// stdout. RunSelf streams the host stdout line-unbuffered back to the pod step
// (SDK v0.2.0), so the pod captures this JSON straight off the stream instead of
// scp-pulling a file the host wrote.
type ModProbeResult struct {
	ModsFound      []string `json:"mods_found"`
	BaseLibVersion string   `json:"baselib_version"`
	AbortReason    string   `json:"abort_reason,omitempty"`
}

// ProbeMods inspects the live mods/ dir against the AGENTS.md allowlist and the
// BaseLib version floor, mirroring env-prep.sh's probe_mod_set, and prints the
// verdict JSON to out. It always exits 0; the pod reads the streamed JSON and
// decides the abort. Game-dir resolution failure is surfaced as a host_unavailable
// abort.
func ProbeMods(out io.Writer) error {
	res := ModProbeResult{ModsFound: []string{}}
	gameDir, err := ResolveSts2GameDir()
	if err != nil {
		res.AbortReason = "host_unavailable"
		return writeJSONTo(out, res)
	}
	return writeJSONTo(out, probeModsDir(filepath.Join(gameDir, "mods")))
}

func probeModsDir(modsDir string) ModProbeResult {
	res := ModProbeResult{ModsFound: []string{}}
	entries, _ := os.ReadDir(modsDir)
	for _, e := range entries {
		if e.IsDir() {
			res.ModsFound = append(res.ModsFound, e.Name())
		}
	}
	sort.Slice(res.ModsFound, func(i, j int) bool {
		return strings.ToLower(res.ModsFound[i]) < strings.ToLower(res.ModsFound[j])
	})

	for _, m := range res.ModsFound {
		if !containsFold(allowedMods, m) {
			res.AbortReason = "unexpected_mod:" + m
			return res
		}
	}

	manifest := readModManifest(filepath.Join(modsDir, "BaseLib", "BaseLib.json"))
	res.BaseLibVersion = manifest.version
	if res.BaseLibVersion == "" {
		res.AbortReason = "baselib_missing_or_unversioned:expected>=" + BaseLibMinVersion
		return res
	}
	if !semverGE(res.BaseLibVersion, BaseLibMinVersion) {
		res.AbortReason = "baselib_too_old:found=" + res.BaseLibVersion + ":expected>=" + BaseLibMinVersion
		return res
	}
	// The game refuses to load a mod whose declared dependency has no manifest,
	// and every mod depending on it goes down too.
	for _, dep := range manifest.dependencies {
		if !manifestExists(modsDir, dep) {
			res.AbortReason = "baselib_dependency_missing:" + dep
			return res
		}
	}
	return res
}

// manifestExists reports whether <id>.json sits anywhere under modsDir, which is
// where the game's mod loader looks for manifests.
func manifestExists(modsDir, id string) bool {
	found := false
	_ = filepath.WalkDir(modsDir, func(_ string, d os.DirEntry, err error) error {
		if err == nil && !d.IsDir() && strings.EqualFold(d.Name(), id+".json") {
			found = true
			return filepath.SkipAll
		}
		return nil
	})
	return found
}

// ProbeBridge waits (90s deadline) for the SpireLensMcp bridge to report game
// startup complete — see WaitSts2Ready; nothing polls. Prints {"ready":bool}
// to out and returns the readiness; the caller exits non-zero on a miss so
// RunSelf surfaces a host-layer error and the pod aborts bridge_not_ready.
func ProbeBridge(ctx context.Context, out io.Writer) bool {
	err := WaitSts2Ready(DefaultBridgeHost, DefaultBridgePort, 90)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
	}
	ready := err == nil
	_ = writeJSONTo(out, map[string]any{"ready": ready})
	return ready
}

type modManifest struct {
	version      string
	dependencies []string
}

func readModManifest(manifestPath string) modManifest {
	var m modManifest
	b, err := os.ReadFile(manifestPath)
	if err != nil {
		return m
	}
	var doc struct {
		Version      any `json:"version"`
		Dependencies []struct {
			ID string `json:"id"`
		} `json:"dependencies"`
	}
	if json.Unmarshal(b, &doc) != nil {
		return m
	}
	switch v := doc.Version.(type) {
	case string:
		m.version = strings.TrimSpace(v)
	case float64:
		m.version = strconv.FormatFloat(v, 'f', -1, 64)
	}
	for _, d := range doc.Dependencies {
		if id := strings.TrimSpace(d.ID); id != "" {
			m.dependencies = append(m.dependencies, id)
		}
	}
	return m
}

// semverGE reports whether a >= b numerically per MAJOR.MINOR.PATCH, accepting a
// leading v and stripping per-component pre-release suffixes. An empty or
// non-numeric a is below any b (fail-closed). Ports native_semver_ge.
func semverGE(a, b string) bool {
	av := splitSemver(a)
	bv := splitSemver(b)
	for i := 0; i < 3; i++ {
		ai, aok := av[i], avalid(av[i])
		bi, bok := bv[i], avalid(bv[i])
		if !aok || !bok {
			return false
		}
		an, _ := strconv.Atoi(ai)
		bn, _ := strconv.Atoi(bi)
		if an > bn {
			return true
		}
		if an < bn {
			return false
		}
	}
	return true
}

func splitSemver(v string) [3]string {
	v = strings.TrimPrefix(strings.TrimSpace(v), "v")
	parts := strings.Split(v, ".")
	var out [3]string
	for i := 0; i < 3; i++ {
		if i < len(parts) {
			out[i] = strings.SplitN(parts[i], "-", 2)[0]
		} else {
			out[i] = "0"
		}
	}
	return out
}

func avalid(s string) bool {
	if s == "" {
		return false
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

func containsFold(set []string, v string) bool {
	for _, s := range set {
		if strings.EqualFold(s, v) {
			return true
		}
	}
	return false
}

// writeJSONTo emits v as indented JSON followed by a newline, so RunSelf's
// line-unbuffered forwarder flushes the whole verdict to the pod step's stream.
func writeJSONTo(w io.Writer, v any) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	_, err = w.Write(append(b, '\n'))
	return err
}
