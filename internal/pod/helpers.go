package pod

import (
	"context"
	"os"
	"os/exec"
	"strings"

	"github.com/romaine-life/glimmung/harness/remotehost"
	"github.com/romaine-life/glimmung/harness/step"
)

// fn adapts a slug + func into a step.Handler.
func fn(slug string, f func(*step.Context) (step.Result, error)) step.Handler {
	return step.HandlerFunc{StepSlug: slug, Fn: f}
}

// abortOrFail maps an unreachable/ambiguous host to a clean host_unavailable
// abort (the laptop is asleep / pre-logon) and any other venue failure to the
// typed error, mirroring env-prep.sh's host_unavailable aborts.
func abortOrFail(c *step.Context, lerr *step.LayeredError) (step.Result, error) {
	switch lerr.Code {
	case "host_unreachable", "host_ambiguous":
		return step.Result{}, c.Abort("host_unavailable")
	}
	return step.Result{}, lerr
}

// podRepoRoot is the orchestrator pod's per-run checkout root.
func podRepoRoot(c *step.Context) string {
	if r := strings.TrimSpace(c.Env("GLIMMUNG_REPO_ROOT")); r != "" {
		return r
	}
	return "/workspace/spirelens"
}

// issueRepoURL is the home-side source remote derived from GLIMMUNG_ISSUE_REPO.
// Cloud checkouts may read the GitHub push mirror, but never write to it.
func issueRepoURL(c *step.Context) string {
	return "https://forgejo.romaine.life/" + c.IssueRepo() + ".git"
}

// hostCheckoutPath is the laptop's persistent SpireLens checkout (the code under test).
const hostCheckoutPath = `D:\repos\SpireLens`

// syncHostCheckout forces the laptop checkout to the run's exact commit (the
// pod's resolved HEAD), so the run owns the working directory instead of relying
// on a human git pull. Git runs on the home-side host, where the Forgejo remote
// and credential helper are available; no source credential leaves the host.
func syncHostCheckout(ctx context.Context, c *step.Context, conn *remotehost.Conn) *step.LayeredError {
	sha, err := podHeadSHA(ctx, c)
	if err != nil {
		return step.HarnessError("sync_checkout", "resolve pod HEAD sha", err)
	}
	steps := [][]string{
		{"git", "-C", hostCheckoutPath, "remote", "set-url", "origin", issueRepoURL(c)},
		{"git", "-C", hostCheckoutPath, "fetch", "--prune", "origin"},
		{"git", "-C", hostCheckoutPath, "cat-file", "-e", sha + "^{commit}"},
		{"git", "-C", hostCheckoutPath, "checkout", "--force", "--detach", sha},
		{"git", "-C", hostCheckoutPath, "reset", "--hard", sha},
	}
	for _, command := range steps {
		if err := conn.RunCommand(ctx, command...); err != nil {
			return step.HostError("sync_checkout", "remote git "+command[3], err)
		}
	}
	return nil
}

func podHeadSHA(ctx context.Context, c *step.Context) (string, error) {
	cmd := exec.CommandContext(ctx, "git", "-C", podRepoRoot(c), "rev-parse", "HEAD")
	cmd.Stderr = os.Stderr
	out, err := cmd.Output()
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(out)), nil
}
