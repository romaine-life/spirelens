package host

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strings"
)

// runCmd runs a command, streaming combined output to w (or os.Stdout), and
// returns its exit code and any start/wait error. It is the host face's single
// child-process primitive (git, dotnet, uv, taskkill, schtasks, az, tailscale).
func runCmd(ctx context.Context, w io.Writer, dir string, env []string, name string, args ...string) (int, error) {
	if w == nil {
		w = os.Stdout
	}
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = dir
	if env != nil {
		cmd.Env = env
	}
	cmd.Stdout = w
	cmd.Stderr = w
	err := cmd.Run()
	if err == nil {
		return 0, nil
	}
	var exitErr *exec.ExitError
	if asExit(err, &exitErr) {
		return exitErr.ExitCode(), err
	}
	return -1, err
}

// mustRun runs a command and returns an error annotated with desc on failure.
func mustRun(ctx context.Context, w io.Writer, dir string, env []string, desc, name string, args ...string) error {
	code, err := runCmd(ctx, w, dir, env, name, args...)
	if err != nil || code != 0 {
		return fmt.Errorf("%s failed (exit %d): %v", desc, code, err)
	}
	return nil
}

func asExit(err error, target **exec.ExitError) bool {
	if ee, ok := err.(*exec.ExitError); ok {
		*target = ee
		return true
	}
	return false
}

func env(extra ...string) []string {
	return append(os.Environ(), extra...)
}

func nonEmpty(parts ...string) string {
	for _, p := range parts {
		if strings.TrimSpace(p) != "" {
			return p
		}
	}
	return ""
}
