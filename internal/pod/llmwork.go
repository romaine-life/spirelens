package pod

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"

	"github.com/romaine-life/glimmung/harness/runcallbacks"
	"github.com/romaine-life/glimmung/harness/step"
)

// llmWorkHandlers returns the llm-work phase handlers (slugs unchanged from
// scripts/glimmung-native/{test-plan,implement}.sh).
func llmWorkHandlers() []step.Handler {
	return []step.Handler{
		fn("run-test-plan", runTestPlan),
		fn("collect-test-plan", collectTestPlan),
		fn("run-implementation", runImplementation),
		fn("push-branch", pushBranch),
		fn("collect-implementation", collectImplementation),
	}
}

func runTestPlan(c *step.Context) (step.Result, error) {
	return runPhaseOnHost(c, "test_plan")
}

func runImplementation(c *step.Context) (step.Result, error) {
	return runPhaseOnHost(c, "implementation")
}

// runPhaseOnHost connects, stages the host binary, mints the GitHub token, and
// runs the run-phase host subcommand for an agent phase. extra carries
// phase-specific flags (e.g. hydration b64).
func runPhaseOnHost(c *step.Context, phase string, extra ...string) (step.Result, error) {
	ctx := c.RunContext()
	conn, lerr := connect(ctx, c)
	if lerr != nil {
		return abortOrFail(c, lerr)
	}
	if lerr := stageHostBinary(ctx, conn, c); lerr != nil {
		return step.Result{}, lerr
	}
	token, lerr := runcallbacks.FromContext(c).MintGitHubToken(ctx)
	if lerr != nil {
		return step.Result{}, lerr
	}
	args := append([]string{
		"--phase", phase,
		"--issue-number", strconv.Itoa(c.IssueNumber()),
		"--repo-slug", c.IssueRepo(),
		"--repo-root", hostCheckoutPath,
		"--working-dir", hostWorkingDir(c),
		"--run-id", c.RunID(),
		"--attempt-index", strconv.Itoa(c.AttemptIndex()),
		"--github-token-b64", base64Token(token),
	}, extra...)
	if lerr := conn.RunSelf(ctx, "run-phase", args...); lerr != nil {
		return step.Result{}, lerr
	}
	return step.Result{}, nil
}

func collectTestPlan(c *step.Context) (step.Result, error) {
	return collectJSONOutput(c, "test-plan.json", "test_plan")
}

func collectImplementation(c *step.Context) (step.Result, error) {
	return collectJSONOutput(c, "implementation.json", "implementation")
}

// collectJSONOutput pulls a phase JSON artifact from the laptop and emits it as
// a phase output, mirroring native_emit_json_output (one JSON-object line whose
// value is the compact artifact JSON).
func collectJSONOutput(c *step.Context, artifactName, outputKey string) (step.Result, error) {
	ctx := c.RunContext()
	conn, lerr := connect(ctx, c)
	if lerr != nil {
		return abortOrFail(c, lerr)
	}
	local := filepath.Join(c.WorkingDir(), artifactName)
	if lerr := conn.ScpPull(ctx, hostArtifact(c, artifactName), local); lerr != nil {
		return step.Result{}, lerr
	}
	b, err := os.ReadFile(local)
	if err != nil {
		return step.Result{}, step.HarnessError("artifact_unreadable", "read "+artifactName, err)
	}
	var parsed any
	if err := json.Unmarshal(b, &parsed); err != nil {
		return step.Result{}, step.HarnessError("artifact_invalid", artifactName+" is not valid JSON", err)
	}
	if err := c.EmitJSONOutput(outputKey, parsed); err != nil {
		return step.Result{}, err
	}
	return step.Result{}, nil
}

// pushBranch commits and pushes the laptop checkout to glimmung/<run_id> on
// Forgejo, then verifies the branch from the same home-side host.
func pushBranch(c *step.Context) (step.Result, error) {
	ctx := c.RunContext()
	conn, lerr := connect(ctx, c)
	if lerr != nil {
		return abortOrFail(c, lerr)
	}
	repo := c.IssueRepo()
	branch := "glimmung/" + c.RunID()
	remote := issueRepoURL(c)
	commitMsg := fmt.Sprintf("glimmung: implement %s#%d (%s)", repo, c.IssueNumber(), c.RunID())

	if err := pushHostBranch(ctx, conn, hostCheckoutPath, remote, branch, commitMsg); err != nil {
		return step.Result{}, step.HostError("push_branch", "push implementation to Forgejo", err)
	}

	if !hostBranchExists(ctx, conn, hostCheckoutPath, remote, branch) {
		return step.Result{}, c.Abort("implementation_branch_missing:" + branch)
	}
	if err := c.EmitOutput("branch_name", branch); err != nil {
		return step.Result{}, err
	}
	return step.Result{}, nil
}

type hostCommander interface {
	RunCommand(context.Context, ...string) error
}

// pushHostBranch relies on the host's credential helper. Ordinary push rejects
// a divergent destination instead of overwriting another implementation.
func pushHostBranch(ctx context.Context, conn hostCommander, repoRoot, remote, branch, commitMsg string) error {
	gitSteps := [][]string{
		{"git", "-C", repoRoot, "config", "user.email", "glimmung-native@romaine.life"},
		{"git", "-C", repoRoot, "config", "user.name", "Glimmung Native Runner"},
		{"git", "-C", repoRoot, "checkout", "-B", branch},
		{"git", "-C", repoRoot, "add", "-A"},
		{"git", "-C", repoRoot, "commit", "--allow-empty", "-m", commitMsg},
		{"git", "-C", repoRoot, "push", remote, "HEAD:refs/heads/" + branch},
	}
	for _, command := range gitSteps {
		if err := conn.RunCommand(ctx, command...); err != nil {
			return fmt.Errorf("remote git %s: %w", command[3], err)
		}
	}
	return nil
}

func hostBranchExists(ctx context.Context, conn hostCommander, repoRoot, remote, branch string) bool {
	return conn.RunCommand(ctx, "git", "-C", repoRoot, "ls-remote", "--exit-code", "--heads", remote, "refs/heads/"+branch) == nil
}
