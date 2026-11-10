package host

import (
	"context"
	_ "embed"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/romaine-life/glimmung/harness/step"
	"github.com/romaine-life/spirelens/internal/mcpconfig"
)

//go:embed scripts/prepare_scenario.py
var prepareScenarioPython []byte

// PrepareScenarioParams are the prepare-scenario host subcommand inputs.
type PrepareScenarioParams struct {
	WorkingDir  string
	IssueNumber string
	// TestPlan, when set, is hydrated into the artifact dir before staging so a
	// verify-only run (llm-work skipped) has its test-plan.json on disk.
	TestPlan string
}

// PrepareScenario rigs the deterministic STS2 save the verification phase
// expects, the Go port of prepare-scenario.ps1: read the test plan's
// scenario_setup, then restart→materialize→stop→install→restart→validate_load
// against the MCP server (the embedded Python helper), and assert the loaded
// state matches the recipe. On any failure it writes the scenario_setup_failed
// artifact and returns a typed error.
func PrepareScenario(ctx context.Context, w io.Writer, p PrepareScenarioParams) *step.LayeredError {
	if w == nil {
		w = os.Stdout
	}
	artifactDir := filepath.Join(p.WorkingDir, "sts2-artifacts")
	if err := os.MkdirAll(artifactDir, 0o755); err != nil {
		return step.HostError("artifact_dir", "create artifact dir", err)
	}
	if p.TestPlan != "" {
		_ = os.WriteFile(filepath.Join(artifactDir, "test-plan.json"), []byte(p.TestPlan), 0o644)
	}

	issueNum := 0
	fmt.Sscanf(p.IssueNumber, "%d", &issueNum)

	if lerr := prepareScenarioInner(ctx, w, artifactDir); lerr != nil {
		writeScenarioFailure(artifactDir, issueNum, lerr.Error())
		return lerr
	}
	return nil
}

func prepareScenarioInner(ctx context.Context, w io.Writer, artifactDir string) *step.LayeredError {
	testPlanPath := filepath.Join(artifactDir, "test-plan.json")
	raw, err := os.ReadFile(testPlanPath)
	if err != nil {
		return step.HarnessError("scenario_setup_failed", "test plan artifact not found: "+testPlanPath, err)
	}
	var tp struct {
		ScenarioSetup map[string]any `json:"scenario_setup"`
	}
	if err := json.Unmarshal(raw, &tp); err != nil {
		return step.HarnessError("scenario_setup_failed", "test plan is invalid JSON", err)
	}
	if tp.ScenarioSetup == nil {
		return step.HarnessError("scenario_setup_failed", "test plan did not include scenario_setup; refusing to spend verification time without a deterministic scenario", nil)
	}
	for _, req := range []string{"base_save_name", "scenario_name", "deck"} {
		if isBlank(tp.ScenarioSetup[req]) {
			return step.HarnessError("scenario_setup_failed", "scenario_setup."+req+" is required", nil)
		}
	}

	setupPath := filepath.Join(artifactDir, "scenario-setup-input.json")
	setupBytes, _ := json.MarshalIndent(tp.ScenarioSetup, "", "  ")
	if err := os.WriteFile(setupPath, setupBytes, 0o644); err != nil {
		return step.HostError("scenario_setup_failed", "write scenario-setup-input.json", err)
	}

	mcpDir := mcpconfig.McpDirectoryFromTemplate()
	if mcpDir == "" {
		return step.HarnessError("scenario_setup_failed", "could not resolve the spirelens-mcp --directory from the MCP template", nil)
	}
	outputPath := filepath.Join(artifactDir, "scenario-setup.json")

	type stepFn struct {
		name string
		run  func() error
	}
	steps := []stepFn{
		{"Restart STS2 (materialize phase)", func() error { return RestartSts2(ctx, w, RestartModeRestart, 90, 45) }},
		{"MCP materialize_only", func() error { return runMcpPython(ctx, w, mcpDir, "materialize_only", setupPath, outputPath) }},
		{"Stop STS2 before save install", func() error { return RestartSts2(ctx, w, RestartModeStop, 90, 45) }},
		{"MCP install_only", func() error { return runMcpPython(ctx, w, mcpDir, "install_only", setupPath, outputPath) }},
		{"Restart STS2 (validate phase)", func() error { return RestartSts2(ctx, w, RestartModeRestart, 90, 45) }},
		{"MCP validate_load", func() error { return runMcpPython(ctx, w, mcpDir, "validate_load", setupPath, outputPath) }},
	}
	for _, s := range steps {
		fmt.Fprintf(w, "[prepare-scenario] %s\n", s.name)
		if err := s.run(); err != nil {
			return step.HostError("scenario_setup_failed", s.name, err)
		}
	}

	return assertScenarioReady(outputPath)
}

// assertScenarioReady ports the harness-side backstop checks after validate_load.
func assertScenarioReady(outputPath string) *step.LayeredError {
	raw, err := os.ReadFile(outputPath)
	if err != nil {
		return step.HarnessError("scenario_setup_failed", "scenario-setup.json missing after validate_load", err)
	}
	var result map[string]any
	if err := json.Unmarshal(raw, &result); err != nil {
		return step.HarnessError("scenario_setup_failed", "scenario-setup.json is invalid JSON", err)
	}
	if str(result["status"]) != "pass" {
		return step.HarnessError("scenario_setup_failed", "scenario setup did not pass", nil)
	}
	stateType := str(result["state_type"])
	if stateType == "" || stateType == "menu" || stateType == "unknown" || stateType == "loading" {
		return step.HarnessError("scenario_setup_failed", fmt.Sprintf("scenario loaded into unexpected state_type=%q", stateType), nil)
	}
	if stateType == "monster" || stateType == "elite" || stateType == "boss" {
		if !hasEnemies(result) {
			return step.HarnessError("scenario_setup_failed", fmt.Sprintf("scenario reached %s state before active battle details were available", stateType), nil)
		}
	}
	required := str(result["required_room_type"])
	if required != "" && stateType != lower(required) {
		return step.HarnessError("scenario_setup_failed", fmt.Sprintf("scenario required_room_type=%q but loaded state_type=%q", required, stateType), nil)
	}
	return nil
}

// runMcpPython writes the embedded Python helper to a temp file and runs it via
// uv against the mcp/ directory, mirroring Invoke-McpPython.
func runMcpPython(ctx context.Context, w io.Writer, mcpDir, mode, setupPath, outputPath string) error {
	scriptPath := filepath.Join(os.TempDir(), fmt.Sprintf("prepare-scenario-%d.py", os.Getpid()))
	if err := os.WriteFile(scriptPath, prepareScenarioPython, 0o644); err != nil {
		return fmt.Errorf("write mcp python helper: %w", err)
	}
	defer os.Remove(scriptPath)
	return mustRun(ctx, w, "", nil, "MCP python helper mode "+mode,
		"uv", "run", "--directory", mcpDir, "python", scriptPath, mode, setupPath, outputPath)
}

func writeScenarioFailure(artifactDir string, issueNumber int, message string) {
	doc := map[string]any{
		"layer":                 "scenario_setup",
		"status":                "abort",
		"abort_reason":          "scenario_setup_failed",
		"retryable":             true,
		"human_action_required": false,
		"notes":                 message,
		"issue_number":          issueNumber,
	}
	b, _ := json.MarshalIndent(doc, "", "  ")
	_ = os.WriteFile(filepath.Join(artifactDir, "scenario-setup.json"), b, 0o644)
}

func hasEnemies(result map[string]any) bool {
	gs, ok := result["game_state"].(map[string]any)
	if !ok {
		return false
	}
	battle, ok := gs["battle"].(map[string]any)
	if !ok {
		return false
	}
	enemies, ok := battle["enemies"].([]any)
	return ok && len(enemies) > 0
}

func isBlank(v any) bool {
	s, ok := v.(string)
	if ok {
		return s == ""
	}
	return v == nil
}

func str(v any) string {
	if s, ok := v.(string); ok {
		return s
	}
	return ""
}

func lower(s string) string {
	b := []byte(s)
	for i := range b {
		if b[i] >= 'A' && b[i] <= 'Z' {
			b[i] += 'a' - 'A'
		}
	}
	return string(b)
}
