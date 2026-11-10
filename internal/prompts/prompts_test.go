package prompts

import (
	"strings"
	"testing"
)

func testParams() Params {
	return Params{
		IssueNumber:           "146",
		RepoSlug:              "romaine-life/spirelens",
		RepoRoot:              `D:\repos\SpireLens`,
		McpConfigPath:         `C:\glimmung-runs\run\mcp\x.json`,
		ValidationArtifactDir: `C:\glimmung-runs\run\sts2-artifacts`,
		ScreenshotDir:         `C:\glimmung-runs\run\sts2-screenshots`,
	}
}

// #3 regression guard: a verification (verify-only) build must NEVER construct
// the test-plan prompt. run-phases.ps1 built all three eagerly; BuildPrompt is
// lazy and only renders the requested phase (plus the shared common prefix).
func TestLazyPromptBuild_VerifyOnlyNeverBuildsTestPlan(t *testing.T) {
	built := []string{}
	promptBuilderObserver = func(name string) { built = append(built, name) }
	defer func() { promptBuilderObserver = nil }()

	if _, err := BuildPrompt(PhaseVerification, testParams()); err != nil {
		t.Fatal(err)
	}
	for _, name := range built {
		if name == PhaseTestPlan || name == PhaseImplementation {
			t.Errorf("verify-only build constructed the %q prompt (eager-build regression #3); built=%v", name, built)
		}
	}
	if len(built) != 2 || built[0] != "common" || built[1] != PhaseVerification {
		t.Errorf("expected exactly [common verification], got %v", built)
	}
}

func TestBuildPrompt_PerPhaseLaziness(t *testing.T) {
	for _, phase := range []string{PhaseTestPlan, PhaseImplementation, PhaseVerification} {
		built := []string{}
		promptBuilderObserver = func(name string) { built = append(built, name) }
		if _, err := BuildPrompt(phase, testParams()); err != nil {
			t.Fatalf("%s: %v", phase, err)
		}
		promptBuilderObserver = nil
		if len(built) != 2 || built[1] != phase {
			t.Errorf("%s: expected [common %s], got %v", phase, phase, built)
		}
	}
}

func TestBuildPrompt_UnknownPhase(t *testing.T) {
	if _, err := BuildPrompt("nope", testParams()); err == nil {
		t.Error("unknown phase must error")
	}
}

// The common prefix substitutes the run parameters and toggles the issue-read
// instruction by phase.
func TestCommonPrefixSubstitution(t *testing.T) {
	tp, _ := BuildPrompt(PhaseTestPlan, testParams())
	if !strings.Contains(tp, "gh issue view 146 --repo romaine-life/spirelens") {
		t.Error("test_plan must include the issue-read instruction")
	}
	if !strings.Contains(tp, `D:\repos\SpireLens`) || !strings.Contains(tp, `C:\glimmung-runs\run\sts2-artifacts`) {
		t.Error("paths must be substituted")
	}
	vf, _ := BuildPrompt(PhaseVerification, testParams())
	if strings.Contains(vf, "Read the issue title and body only with") {
		t.Error("verification must NOT include the issue-read instruction")
	}
	if !strings.Contains(vf, "Use the JSON/Markdown handoff artifacts written by earlier phases") {
		t.Error("verification must use the handoff-artifacts instruction")
	}
}

// RoomTypeStaging.Tests.ps1 parity: the test_plan prompt's room-type guidance.
func TestTestPlanPrompt_RoomTypeGuidance(t *testing.T) {
	tp, _ := BuildPrompt(PhaseTestPlan, testParams())
	for _, want := range []string{
		"required_room_type",
		"at the start of Elite combats",
		`"required_room_type":null`,
		"Monster (normal) encounter",
	} {
		if !strings.Contains(tp, want) {
			t.Errorf("test_plan prompt missing room-type token %q", want)
		}
	}
	if strings.Contains(tp, "encounter-type-conditional triggers") {
		t.Error("test_plan prompt must not route encounter-type-conditional triggers at next_normal_encounter")
	}
}

// NoProseUnitTestGate.Tests.ps1 parity: the implementation prompt protects live
// tooltip attribution; the verification prompt cedes unit tests to the harness.
func TestImplementationPrompt_AttributionGuidance(t *testing.T) {
	impl, _ := BuildPrompt(PhaseImplementation, testParams())
	for _, want := range []string{
		"docs/sts2-runtime-primer.md",
		"do not arm an attribution flag in a prefix and clear it in the same method's postfix",
		"Hook.AfterTurnEnd",
		"pending combat data",
		"Energy generated: 1",
	} {
		if !strings.Contains(impl, want) {
			t.Errorf("implementation prompt missing attribution token %q", want)
		}
	}
}

func TestVerificationPrompt_HarnessOwnsUnitTests(t *testing.T) {
	vf, _ := BuildPrompt(PhaseVerification, testParams())
	if !strings.Contains(vf, "do not judge unit-test results") {
		t.Error("verification prompt must cede unit tests to the harness")
	}
	if !strings.Contains(vf, "claimed_result_not_observed") {
		t.Error("verification prompt must mention the mismatch abort reason")
	}
}

func TestPhaseDefinitions(t *testing.T) {
	vd, ok := Definition(PhaseVerification)
	if !ok {
		t.Fatal("verification def missing")
	}
	// verification must DISALLOW dotnet test and the multiplayer tools.
	if !containsTool(vd.DisallowedTools, "Bash(dotnet test *)") {
		t.Error("verification must disallow dotnet test (harness owns it)")
	}
	if !containsTool(vd.DisallowedTools, "mcp__spirelens-mcp__mp_get_game_state") {
		t.Error("verification must disallow multiplayer tools")
	}
	if !containsTool(vd.AllowedTools, "mcp__spirelens-mcp__capture_screenshot") {
		t.Error("verification must allow capture_screenshot")
	}
	if !containsAbort(vd.AllowedAbortReasons, "unit_tests_failed") {
		t.Error("verification must allow unit_tests_failed abort")
	}
}

func containsTool(s []string, v string) bool  { return containsStr(s, v) }
func containsAbort(s []string, v string) bool { return containsStr(s, v) }
func containsStr(s []string, v string) bool {
	for _, x := range s {
		if x == v {
			return true
		}
	}
	return false
}
