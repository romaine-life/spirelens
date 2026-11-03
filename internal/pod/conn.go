// Package pod is the orchestrator-pod face of the glimmung-spirelens binary: one
// step.Handler per workflow step slug. Handlers reach the warm gaming laptop
// through the SDK's harness/remotehost venue (MintAndConnect / RunSelf / ScpPush
// / ScpPullTree / RunCommand) and invoke the binary's own host face over ssh,
// replacing the retired pwsh-over-ssh here-docs in scripts/glimmung-native/*.sh.
//
// The host face is the SAME binary cross-compiled for Windows and staged onto
// the laptop per run, so git_ref controls the harness end to end (the binary is
// built from the run's checkout). This replaces the retired native_stage_harness
// scp of .github/scripts/*.ps1.
package pod

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"

	"github.com/romaine-life/glimmung/harness/remotehost"
	"github.com/romaine-life/glimmung/harness/step"
)

// hostTag is the tailnet tag the spirelens laptop advertises.
const hostTag = "tag:spirelens-host"

// sshUser is the Windows local account the SSH cert authenticates as (spirelens#179
// Q5: single-user, no dedicated automation account).
func sshUser() string {
	if u := os.Getenv("SPIRELENS_SSH_USER"); u != "" {
		return u
	}
	return "nelsonlaptopuser"
}

// hostWorkingDir is the laptop per-run working dir. Forward slashes throughout
// the host paths: scp's remote-path parser treats `C:\` as a second host (the
// colon), and the retired lib.sh used forward slashes for exactly this reason;
// Go's filepath and Windows file APIs both accept them.
func hostWorkingDir(c *step.Context) string {
	return "C:/glimmung-runs/" + c.RunRef()
}

// hostBinaryPath is where the staged Windows host binary lands on the laptop.
func hostBinaryPath(c *step.Context) string {
	return hostWorkingDir(c) + "/hostbin/glimmung-spirelens.exe"
}

// hostArtifact returns a forward-slash host path under the per-run sts2-artifacts dir.
func hostArtifact(c *step.Context, name string) string {
	return hostWorkingDir(c) + "/sts2-artifacts/" + name
}

// connect mints credentials, brings up the tailnet, resolves the laptop, and
// returns a connected remotehost.Conn whose RunSelf targets the staged host
// binary. Any venue failure is a host-layer error.
//
// MintAndConnect is step-idempotent within a pod (SDK v0.2.0): a prepare phase
// runs several steps in one process-per-step pod sharing the working dir, so a
// per-step call converges on the existing venue — the ed25519 keypair is reused
// while on disk, tailscaled/up runs only when no daemon is already serving the
// pod's socket, and only the short-TTL ssh cert is re-minted each call. So every
// step may simply call connect().
func connect(ctx context.Context, c *step.Context) (*remotehost.Conn, *step.LayeredError) {
	return connectCapturing(ctx, c, nil)
}

// connectCapturing is connect with RunSelf's streamed remote stdout redirected to
// stdout instead of the default os.Stdout. A nil stdout keeps the default (the
// pod step's captured log stream, where the runner prices each agent `usage`
// line). The mod-set probe sets a buffer to read its structured JSON verdict
// straight off RunSelf's streaming stdout (SDK v0.2.0), replacing the retired
// write-to-file-then-scp-pull probe handoff.
func connectCapturing(ctx context.Context, c *step.Context, stdout io.Writer) (*remotehost.Conn, *step.LayeredError) {
	cfg := remotehost.FromContext(c, sshUser(), hostBinaryPath(c))
	if stdout != nil {
		cfg.Stdout = stdout
	}
	return remotehost.MintAndConnect(ctx, cfg, hostTag)
}

// stageHostBinary cross-compiles the Windows host binary from the pod's git_ref
// checkout and scp's it onto the laptop, so RunSelf can exec it. git_ref controls
// the harness because the binary is built from the run's checkout.
//
// The single .exe is pushed directly with Conn.ScpPush (SDK v0.2.0): no one-file
// directory wrapper. Only the remote hostbin/ parent dir is ensured first, since
// a single-file scp does not create intermediate directories.
func stageHostBinary(ctx context.Context, c *remotehost.Conn, sc *step.Context) *step.LayeredError {
	repoRoot := sc.Env("GLIMMUNG_REPO_ROOT")
	if repoRoot == "" {
		repoRoot = "/workspace/spirelens"
	}
	exePath := filepath.Join(sc.WorkingDir(), "glimmung-spirelens.exe")
	build := exec.CommandContext(ctx, "go", "build", "-o", exePath, "./cmd/glimmung-spirelens")
	build.Dir = repoRoot
	build.Env = append(os.Environ(), "GOOS=windows", "GOARCH=amd64")
	build.Stdout = os.Stdout
	build.Stderr = os.Stderr
	if err := build.Run(); err != nil {
		return step.HarnessError("host_binary_build", "cross-compile windows host binary", err)
	}
	// Ensure the remote hostbin/ dir exists, then push the single file into it.
	hostbinDir := hostWorkingDir(sc) + "/hostbin"
	_ = c.RunCommand(ctx, "powershell", "-NoProfile", "-Command", fmt.Sprintf("New-Item -ItemType Directory -Force -Path '%s' | Out-Null", hostbinDir))
	if lerr := c.ScpPush(ctx, exePath, hostBinaryPath(sc)); lerr != nil {
		return lerr
	}
	return nil
}
