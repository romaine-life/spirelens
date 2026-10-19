package host

import (
	"context"
	"fmt"
	"io"
	"os"
	"path/filepath"
)

// Host-local locations ported from prepare-host.ps1.
const (
	mcpRepoRoot = `D:\repos\spire-lens-mcp`
	mcpRepoURL  = "https://github.com/romaine-life/spire-lens-mcp.git"
)

// PrepareHostParams are the prepare-host host subcommand inputs.
type PrepareHostParams struct {
	InstallMcp bool
	StartSts2  bool
}

// PrepareHost installs/refreshes SpireLensMcp on the laptop and (optionally)
// launches STS2 with the bridge — the Go port of prepare-host.ps1. It is invoked
// by the env-prep pod phase's install-mcp-start-sts2 step.
func PrepareHost(ctx context.Context, w io.Writer, p PrepareHostParams) error {
	if w == nil {
		w = os.Stdout
	}
	gameDir, err := ResolveSts2GameDir()
	if err != nil {
		return err
	}
	modsDir := filepath.Join(gameDir, "mods")

	if p.InstallMcp {
		if err := installMcp(ctx, w, gameDir, modsDir); err != nil {
			return err
		}
	}
	if p.StartSts2 {
		// 60s startup / 45s shutdown, matching prepare-host.ps1's restart call.
		return RestartSts2(ctx, w, RestartModeRestart, 60, 45)
	}
	return nil
}

func installMcp(ctx context.Context, w io.Writer, gameDir, modsDir string) error {
	// 1. Clone or fast-forward the in-house SpireLensMcp repo to main.
	if fileExists(filepath.Join(mcpRepoRoot, ".git")) {
		for _, args := range [][]string{
			{"-C", mcpRepoRoot, "remote", "set-url", "origin", mcpRepoURL},
			{"-C", mcpRepoRoot, "fetch", "origin", "main"},
			{"-C", mcpRepoRoot, "checkout", "main"},
			{"-C", mcpRepoRoot, "pull", "--ff-only"},
		} {
			if err := mustRun(ctx, w, "", nil, "git "+args[len(args)-1], "git", args...); err != nil {
				return err
			}
		}
	} else {
		if err := mustRun(ctx, w, "", nil, "git clone spire-lens-mcp", "git", "clone", mcpRepoURL, mcpRepoRoot); err != nil {
			return err
		}
	}

	// 2. py_compile sanity, then build the bridge via the mcp repo's own build.ps1.
	mcpPy := filepath.Join(mcpRepoRoot, "mcp")
	if err := mustRun(ctx, w, "", nil, "py_compile server.py", "uv", "run", "--directory", mcpPy, "python", "-m", "py_compile", "server.py"); err != nil {
		return err
	}
	buildScript := filepath.Join(mcpRepoRoot, "build.ps1")
	if err := mustRun(ctx, w, "", nil, "build SpireLensMcpBridge", "pwsh", "-NoProfile", "-File", buildScript, "-GameDir", gameDir, "-Configuration", "Release"); err != nil {
		return err
	}

	// 3. Stop STS2 so the mods/ files are not locked.
	if err := StopSts2(ctx, w, DefaultShutdownTimeoutSecs); err != nil {
		return err
	}

	// 4. Remove stale mod artifacts from prior layouts.
	_ = os.RemoveAll(filepath.Join(modsDir, "SpireLensMcp"))
	_ = os.Remove(filepath.Join(modsDir, "SpireLensMcp.dll"))
	_ = os.Remove(filepath.Join(modsDir, "SpireLensMcp.json"))

	// 5. Deploy the freshly built bridge dll + manifest.
	if err := copyFile(filepath.Join(mcpRepoRoot, "out", "SpireLensMcpBridge", "SpireLensMcpBridge.dll"), filepath.Join(modsDir, "SpireLensMcpBridge.dll")); err != nil {
		return fmt.Errorf("deploy bridge dll: %w", err)
	}
	if err := copyFile(filepath.Join(mcpRepoRoot, "mod_manifest.json"), filepath.Join(modsDir, "SpireLensMcpBridge.json")); err != nil {
		return fmt.Errorf("deploy bridge manifest: %w", err)
	}
	return nil
}

func copyFile(src, dst string) error {
	b, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	return os.WriteFile(dst, b, 0o644)
}
