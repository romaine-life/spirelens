package pod

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

// Run the same host commands against isolated local Git repositories. This
// verifies real push behavior without an SSH host, game install, or credential.
type localHostCommands struct{}

func (localHostCommands) RunCommand(ctx context.Context, command ...string) error {
	cmd := exec.CommandContext(ctx, command[0], command[1:]...)
	cmd.Env = sourceGitEnv()
	out, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s: %w: %s", command[0], err, out)
	}
	return nil
}

func TestPushHostBranchPublishesAndVerifiesTheImplementation(t *testing.T) {
	_, remote, checkout := sourceFixture(t)
	branch := "glimmung/run-test"
	if err := os.WriteFile(filepath.Join(checkout, "implementation.txt"), []byte("implementation\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	host := localHostCommands{}
	if err := pushHostBranch(ctx, host, checkout, remote, branch, "implement run-test"); err != nil {
		t.Fatal(err)
	}
	if !hostBranchExists(ctx, host, checkout, remote, branch) {
		t.Fatal("published implementation branch was not found")
	}
	if hostBranchExists(ctx, host, checkout, remote, "glimmung/missing") {
		t.Fatal("a missing branch was reported as published")
	}
	got := sourceGit(t, "--git-dir", remote, "rev-parse", "refs/heads/"+branch)
	want := sourceGit(t, "-C", checkout, "rev-parse", "HEAD")
	if got != want {
		t.Fatalf("published commit = %s, implementation = %s", got, want)
	}
	if got := sourceGit(t, "--git-dir", remote, "show", "refs/heads/"+branch+":implementation.txt"); got != "implementation" {
		t.Fatalf("published source = %q", got)
	}
}

func TestPushHostBranchPreservesADivergentRemoteBranch(t *testing.T) {
	seed, remote, checkout := sourceFixture(t)
	branch := "glimmung/run-test"
	sourceGit(t, "-C", seed, "checkout", "-b", branch)
	sourceGit(t, "-C", seed, "commit", "--allow-empty", "-m", "existing remote implementation")
	sourceGit(t, "-C", seed, "push", remote, "HEAD:refs/heads/"+branch)
	want := sourceGit(t, "--git-dir", remote, "rev-parse", "refs/heads/"+branch)

	err := pushHostBranch(context.Background(), localHostCommands{}, checkout, remote, branch, "different implementation")
	if err == nil {
		t.Fatal("divergent implementation push unexpectedly succeeded")
	}
	got := sourceGit(t, "--git-dir", remote, "rev-parse", "refs/heads/"+branch)
	if got != want {
		t.Fatalf("existing remote commit changed from %s to %s", want, got)
	}
}

func sourceFixture(t *testing.T) (seed, remote, checkout string) {
	t.Helper()
	root := t.TempDir()
	seed = filepath.Join(root, "seed")
	remote = filepath.Join(root, "remote.git")
	checkout = filepath.Join(root, "checkout")
	sourceGit(t, "init", "-b", "main", seed)
	sourceGit(t, "-C", seed, "config", "user.name", "Source Migration Test")
	sourceGit(t, "-C", seed, "config", "user.email", "source-test@romaine.life")
	sourceGit(t, "-C", seed, "commit", "--allow-empty", "-m", "base")
	sourceGit(t, "clone", "--bare", seed, remote)
	sourceGit(t, "clone", remote, checkout)
	return seed, remote, checkout
}

func sourceGit(t *testing.T, args ...string) string {
	t.Helper()
	cmd := exec.Command("git", args...)
	cmd.Env = sourceGitEnv()
	out, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("git %v: %v\n%s", args, err, out)
	}
	return strings.TrimSpace(string(out))
}

func sourceGitEnv() []string {
	return append(os.Environ(), "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL="+os.DevNull)
}
