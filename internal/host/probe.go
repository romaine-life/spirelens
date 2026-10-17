package host

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

// BaseLibMinVersion is the BaseLib version floor enforced during env prep.
// BaseLib 3.3.7 hard-references the removed MegaInput.releaseCard field from
// its NPlayerHand patch, preventing every attempted card play on STS2 v0.110.1.
const BaseLibMinVersion = "3.4.7"

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
	modsDir := filepath.Join(gameDir, "mods")
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
			return writeJSONTo(out, res)
		}
	}

	res.BaseLibVersion = readBaseLibVersion(filepath.Join(modsDir, "BaseLib", "BaseLib.json"))
	if res.BaseLibVersion == "" {
		res.AbortReason = "baselib_missing_or_unversioned:expected>=" + BaseLibMinVersion
		return writeJSONTo(out, res)
	}
	if !semverGE(res.BaseLibVersion, BaseLibMinVersion) {
		res.AbortReason = "baselib_too_old:found=" + res.BaseLibVersion + ":expected>=" + BaseLibMinVersion
		return writeJSONTo(out, res)
	}
	return writeJSONTo(out, res)
}

// ProbeBridge polls the SpireLensMcp bridge until ready, mirroring
// env-prep.sh's probe_bridge_ready (90s deadline). Prints {"ready":bool} to out
// and returns the readiness; the caller exits non-zero on a miss so RunSelf
// surfaces a host-layer error and the pod aborts bridge_not_ready.
func ProbeBridge(ctx context.Context, out io.Writer) bool {
	deadline := time.Now().Add(90 * time.Second)
	ready := false
	for time.Now().Before(deadline) {
		if BridgePingOK(DefaultBridgeHost, DefaultBridgePort) {
			ready = true
			break
		}
		time.Sleep(3 * time.Second)
	}
	_ = writeJSONTo(out, map[string]any{"ready": ready})
	return ready
}

func readBaseLibVersion(manifestPath string) string {
	b, err := os.ReadFile(manifestPath)
	if err != nil {
		return ""
	}
	var doc struct {
		Version any `json:"version"`
	}
	if json.Unmarshal(b, &doc) != nil {
		return ""
	}
	switch v := doc.Version.(type) {
	case string:
		return strings.TrimSpace(v)
	case float64:
		return strconv.FormatFloat(v, 'f', -1, 64)
	}
	return ""
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
