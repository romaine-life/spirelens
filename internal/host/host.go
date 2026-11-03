// Package host is the Windows gaming-laptop face of the glimmung-spirelens
// binary. Its subcommands are invoked over ssh by the orchestrator pod via
// remotehost.Conn.RunSelf, replacing the retired pwsh-over-ssh here-docs. Inputs
// arrive as typed FLAGS rather than a here-doc of hand-set env vars (the typed
// replacement for failure mode #4).
//
// Subcommands:
//
//	run-phase         grade one phase (test_plan|implementation|verification):
//	                  lazy prompt, deterministic unit-test gate before the agent,
//	                  claude.exe via harness/agent, evidence guard, verdict write.
//	prepare-scenario  rig the deterministic STS2 save (MCP materialize/install/
//	                  validate_load).
//	prepare-host      install SpireLensMcp + launch STS2 with the bridge.
//	restart-sts2      stop / restart STS2 and wait for the bridge.
package host

import (
	"context"
	"encoding/base64"
	"flag"
	"fmt"
	"os"
)

// Dispatch routes a host subcommand. args is os.Args[1:] with the "host" prefix
// already stripped (args[0] is the subcommand). It returns a process exit code.
func Dispatch(ctx context.Context, args []string) int {
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "host: missing subcommand (run-phase|prepare-scenario|prepare-host|restart-sts2)")
		return 2
	}
	sub, rest := args[0], args[1:]
	switch sub {
	case "run-phase":
		return runPhaseCmd(ctx, rest)
	case "prepare-scenario":
		return prepareScenarioCmd(ctx, rest)
	case "prepare-host":
		return prepareHostCmd(ctx, rest)
	case "restart-sts2":
		return restartSts2Cmd(ctx, rest)
	case "probe-mods":
		return probeModsCmd(ctx, rest)
	case "probe-bridge":
		return probeBridgeCmd(ctx, rest)
	case "build-deploy":
		return buildDeployCmd(ctx, rest)
	default:
		fmt.Fprintf(os.Stderr, "host: unknown subcommand %q\n", sub)
		return 2
	}
}

func runPhaseCmd(ctx context.Context, args []string) int {
	fs := flag.NewFlagSet("run-phase", flag.ContinueOnError)
	var p RunPhaseParams
	var tokenB64 string
	fs.StringVar(&p.Phase, "phase", "", "phase slug (test_plan|implementation|verification)")
	fs.StringVar(&p.IssueNumber, "issue-number", "", "GitHub issue number")
	fs.StringVar(&p.RepoSlug, "repo-slug", "", "owner/repo")
	fs.StringVar(&p.RepoRoot, "repo-root", `D:\repos\SpireLens`, "code-under-test checkout root")
	fs.StringVar(&p.WorkingDir, "working-dir", "", "per-run working dir")
	fs.StringVar(&p.RunID, "run-id", "", "GLIMMUNG_RUN_ID")
	fs.StringVar(&p.AttemptIndex, "attempt-index", "0", "GLIMMUNG_ATTEMPT_INDEX")
	fs.StringVar(&tokenB64, "github-token-b64", "", "base64-encoded GitHub token for GH_TOKEN")
	var testPlanB64, implB64 string
	fs.StringVar(&testPlanB64, "test-plan-b64", "", "base64 test-plan.json to hydrate (verify-only runs)")
	fs.StringVar(&implB64, "implementation-b64", "", "base64 implementation.json to hydrate (verify-only runs)")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	p.GitHubToken = decodeB64(tokenB64)
	p.TestPlan = decodeB64(testPlanB64)
	p.Implementation = decodeB64(implB64)
	if lerr := RunPhase(ctx, p); lerr != nil {
		fmt.Fprintln(os.Stderr, lerr.Error())
		return 1
	}
	return 0
}

func prepareScenarioCmd(ctx context.Context, args []string) int {
	fs := flag.NewFlagSet("prepare-scenario", flag.ContinueOnError)
	var p PrepareScenarioParams
	fs.StringVar(&p.WorkingDir, "working-dir", "", "per-run working dir")
	fs.StringVar(&p.IssueNumber, "issue-number", "", "GitHub issue number")
	var testPlanB64 string
	fs.StringVar(&testPlanB64, "test-plan-b64", "", "base64 test-plan.json to hydrate (verify-only runs)")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	p.TestPlan = decodeB64(testPlanB64)
	if lerr := PrepareScenario(ctx, os.Stdout, p); lerr != nil {
		fmt.Fprintln(os.Stderr, lerr.Error())
		return 1
	}
	return 0
}

func prepareHostCmd(ctx context.Context, args []string) int {
	fs := flag.NewFlagSet("prepare-host", flag.ContinueOnError)
	var p PrepareHostParams
	fs.BoolVar(&p.InstallMcp, "install-mcp", false, "install/refresh SpireLensMcp")
	fs.BoolVar(&p.StartSts2, "start-sts2", false, "launch STS2 with the bridge")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if err := PrepareHost(ctx, os.Stdout, p); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	return 0
}

func buildDeployCmd(ctx context.Context, args []string) int {
	fs := flag.NewFlagSet("build-deploy", flag.ContinueOnError)
	var p BuildDeployParams
	fs.StringVar(&p.Branch, "branch", "", "implementation branch to build")
	fs.StringVar(&p.RepoRoot, "repo-root", `D:\repos\SpireLens`, "code-under-test checkout root")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if lerr := BuildDeploy(ctx, os.Stdout, p); lerr != nil {
		fmt.Fprintln(os.Stderr, lerr.Error())
		return 1
	}
	return 0
}

func decodeB64(s string) string {
	if s == "" {
		return ""
	}
	b, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		return ""
	}
	return string(b)
}

func probeModsCmd(_ context.Context, args []string) int {
	fs := flag.NewFlagSet("probe-mods", flag.ContinueOnError)
	if err := fs.Parse(args); err != nil {
		return 2
	}
	// The verdict JSON streams back to the pod over RunSelf's stdout.
	if err := ProbeMods(os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	return 0
}

func probeBridgeCmd(ctx context.Context, args []string) int {
	fs := flag.NewFlagSet("probe-bridge", flag.ContinueOnError)
	if err := fs.Parse(args); err != nil {
		return 2
	}
	// {"ready":bool} streams to stdout; a non-zero exit on a miss is what the pod
	// keys its bridge_not_ready abort on.
	if !ProbeBridge(ctx, os.Stdout) {
		fmt.Fprintln(os.Stderr, "bridge not ready")
		return 1
	}
	return 0
}

func restartSts2Cmd(ctx context.Context, args []string) int {
	fs := flag.NewFlagSet("restart-sts2", flag.ContinueOnError)
	mode := fs.String("mode", "Restart", "Restart|Stop")
	startup := fs.Int("startup-timeout", DefaultStartupTimeoutSecs, "startup timeout seconds")
	shutdown := fs.Int("shutdown-timeout", DefaultShutdownTimeoutSecs, "shutdown timeout seconds")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if err := RestartSts2(ctx, os.Stdout, RestartMode(*mode), *startup, *shutdown); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	return 0
}
