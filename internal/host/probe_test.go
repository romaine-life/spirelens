package host

import (
	"os"
	"path/filepath"
	"testing"
)

func writeFile(t *testing.T, path, body string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

const baseLibWithHelperDep = `{"id":"BaseLib","version":"v3.4.8","dependencies":[{"id":"BaseLibCompatibilityHelper","min_version":"0.0.0"}]}`

func TestProbeModsDirAbortsWhenBaseLibDependencyMissing(t *testing.T) {
	mods := t.TempDir()
	writeFile(t, filepath.Join(mods, "BaseLib", "BaseLib.json"), baseLibWithHelperDep)

	res := probeModsDir(mods)
	if res.AbortReason != "baselib_dependency_missing:BaseLibCompatibilityHelper" {
		t.Fatalf("abort = %q", res.AbortReason)
	}
}

func TestProbeModsDirPassesWithBaseLibDependencyBesideIt(t *testing.T) {
	mods := t.TempDir()
	writeFile(t, filepath.Join(mods, "BaseLib", "BaseLib.json"), baseLibWithHelperDep)
	writeFile(t, filepath.Join(mods, "BaseLib", "BaseLibCompatibilityHelper.json"), `{"id":"BaseLibCompatibilityHelper"}`)

	res := probeModsDir(mods)
	if res.AbortReason != "" || res.BaseLibVersion != "v3.4.8" {
		t.Fatalf("abort = %q, version = %q", res.AbortReason, res.BaseLibVersion)
	}
}

func TestProbeModsDirAbortsBelowFloor(t *testing.T) {
	mods := t.TempDir()
	writeFile(t, filepath.Join(mods, "BaseLib", "BaseLib.json"), `{"id":"BaseLib","version":"v3.4.7"}`)

	res := probeModsDir(mods)
	want := "baselib_too_old:found=v3.4.7:expected>=" + BaseLibMinVersion
	if res.AbortReason != want {
		t.Fatalf("abort = %q, want %q", res.AbortReason, want)
	}
}
