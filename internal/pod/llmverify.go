package pod

import (
	"strconv"

	"github.com/romaine-life/glimmung/harness/step"
)

// llmVerifyHandlers returns the llm-verify phase handlers (slugs unchanged from
// scripts/glimmung-native/verify.sh).
func llmVerifyHandlers() []step.Handler {
	return []step.Handler{
		fn("build-and-deploy", buildAndDeploy),
		fn("prepare-scenario", prepareScenario),
		fn("run-verification", runVerification),
		fn("collect-evidence", collectEvidence),
	}
}

// buildAndDeploy builds + deploys the implementation branch on the laptop.
func buildAndDeploy(c *step.Context) (step.Result, error) {
	ctx := c.RunContext()
	branch, err := c.Input("branch_name")
	if err != nil {
		return step.Result{}, err
	}
	conn, lerr := connect(ctx, c)
	if lerr != nil {
		return abortOrFail(c, lerr)
	}
	if lerr := stageHostBinary(ctx, conn, c); lerr != nil {
		return step.Result{}, lerr
	}
	if lerr := conn.RunSelf(ctx, "build-deploy",
		"--branch", branch,
		"--repo-root", hostCheckoutPath,
	); lerr != nil {
		return step.Result{}, lerr
	}
	return step.Result{}, nil
}

// prepareScenario rigs the deterministic STS2 save, hydrating test-plan.json from
// the declared input so a verify-only run is self-sufficient.
func prepareScenario(c *step.Context) (step.Result, error) {
	ctx := c.RunContext()
	conn, lerr := connect(ctx, c)
	if lerr != nil {
		return abortOrFail(c, lerr)
	}
	if lerr := stageHostBinary(ctx, conn, c); lerr != nil {
		return step.Result{}, lerr
	}
	args := []string{"--working-dir", hostWorkingDir(c), "--issue-number", strconv.Itoa(c.IssueNumber())}
	if tp, ok := c.OptionalInput("test_plan"); ok {
		args = append(args, "--test-plan-b64", base64Token(tp))
	}
	if lerr := conn.RunSelf(ctx, "prepare-scenario", args...); lerr != nil {
		return step.Result{}, lerr
	}
	return step.Result{}, nil
}

// runVerification runs the verification phase on the laptop, hydrating the
// handoff artifacts so a verify-only run has the same artifact set on disk.
func runVerification(c *step.Context) (step.Result, error) {
	extra := []string{}
	if tp, ok := c.OptionalInput("test_plan"); ok {
		extra = append(extra, "--test-plan-b64", base64Token(tp))
	}
	if impl, ok := c.OptionalInput("implementation"); ok {
		extra = append(extra, "--implementation-b64", base64Token(impl))
	}
	return runPhaseOnHost(c, "verification", extra...)
}

// collectEvidence pulls the finalizer artifact tree the run-verification step
// already assembled on the laptop directly into the pod working dir with one
// recursive ScpPullTree (SDK v0.2.0), landing the exact tree the
// verification_finalize primitive reads:
// ${GLIMMUNG_WORKING_DIR}/artifacts/{verification.json,screenshots,evidence}.
// No host-side zip pack, no pod-side unzip.
func collectEvidence(c *step.Context) (step.Result, error) {
	ctx := c.RunContext()
	conn, lerr := connect(ctx, c)
	if lerr != nil {
		return abortOrFail(c, lerr)
	}
	// scp -r copies the remote artifacts/ dir as a child of the local working dir,
	// so the tree lands at ${WorkingDir}/artifacts/ exactly.
	if lerr := conn.ScpPullTree(ctx, hostWorkingDir(c)+"/artifacts", c.WorkingDir()); lerr != nil {
		return step.Result{}, lerr
	}
	return step.Result{}, nil
}
