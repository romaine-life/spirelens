package host

import (
	"context"
	"fmt"
	"io"
	"os"

	"github.com/romaine-life/glimmung/harness/step"
)

// BuildDeployParams are the build-deploy host subcommand inputs.
type BuildDeployParams struct {
	Branch   string
	RepoRoot string
}

// BuildDeploy checks out the implementation branch into the laptop checkout and
// builds + deploys the SpireLens mod, the Go port of verify.sh's
// build_and_deploy_mod. The Loader is compiled SkipModsDeploy (host-persistent,
// locked while STS2 runs); Core is hot-reloaded so its deploy lands the run's
// changes.
func BuildDeploy(ctx context.Context, w io.Writer, p BuildDeployParams) *step.LayeredError {
	if w == nil {
		w = os.Stdout
	}
	gameDir, err := ResolveSts2GameDir()
	if err != nil {
		return step.HostError("sts2_dir_unresolved", err.Error(), nil)
	}
	repoRoot := nonEmpty(p.RepoRoot, `D:\repos\SpireLens`)
	steps := [][]string{
		{"git", "remote", "set-url", "origin", "https://forgejo.romaine.life/romaine-life/spirelens.git"},
		{"git", "fetch", "origin", p.Branch},
		{"git", "checkout", "--force", "FETCH_HEAD"},
		{"dotnet", "build", "SpireLens.csproj", "-c", "Debug", "-p:Sts2Path=" + gameDir, "-p:SkipModsDeploy=true"},
		{"dotnet", "build", `Core\SpireLens.Core.csproj`, "-c", "Debug", "-p:Sts2Path=" + gameDir},
	}
	for _, s := range steps {
		if err := mustRun(ctx, w, repoRoot, nil, s[0]+" "+s[1], s[0], s[1:]...); err != nil {
			return step.HostError("build_deploy", fmt.Sprintf("%s failed", s[0]), err)
		}
	}
	return nil
}
