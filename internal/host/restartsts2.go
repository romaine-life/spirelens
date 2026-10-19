package host

import (
	"context"
	"io"
	"os"
)

// RestartMode is the restart-sts2 mode.
type RestartMode string

const (
	RestartModeRestart RestartMode = "Restart"
	RestartModeStop    RestartMode = "Stop"
)

// Bridge defaults ported from restart-sts2.ps1.
const (
	DefaultBridgeHost          = "127.0.0.1"
	DefaultBridgePort          = 15526
	DefaultStartupTimeoutSecs  = 240
	DefaultShutdownTimeoutSecs = 45
)

// RestartSts2 stops STS2 and, for Restart mode, starts it and waits for the
// bridge — the Go port of restart-sts2.ps1's main orchestration. The game dir is
// resolved from the env / embedded MCP template / Steam install candidates
// rather than re-read from an MCP config path argument.
func RestartSts2(ctx context.Context, w io.Writer, mode RestartMode, startupTimeoutSecs, shutdownTimeoutSecs int) error {
	if w == nil {
		w = os.Stdout
	}
	gameDir, err := ResolveSts2GameDir()
	if err != nil {
		return err
	}
	if err := StopSts2(ctx, w, shutdownTimeoutSecs); err != nil {
		return err
	}
	if mode == RestartModeStop {
		return nil
	}
	launchArgs := nonEmpty(os.Getenv("SPIRELENS_HOST_STS2_LAUNCH_ARGS"), "--rendering-driver opengl3")
	if err := StartSts2(ctx, w, gameDir, launchArgs); err != nil {
		return err
	}
	return WaitSts2Ready(DefaultBridgeHost, DefaultBridgePort, startupTimeoutSecs)
}
