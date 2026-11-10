package mcpconfig

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// The embedded template must stay byte-identical to the repo-root .mcp.json so
// the harness binary's MCP wiring can never silently drift from the canonical
// project config.
func TestEmbeddedTemplateMatchesRepoRoot(t *testing.T) {
	repoRoot, err := os.ReadFile(filepath.Join("..", "..", ".mcp.json"))
	if err != nil {
		t.Fatalf("read repo-root .mcp.json: %v", err)
	}
	if !bytes.Equal(bytes.TrimSpace(repoRoot), bytes.TrimSpace(templateJSON)) {
		t.Error("internal/mcpconfig/template.json has drifted from the repo-root .mcp.json; keep them identical")
	}
}

func TestBuildRunConfig(t *testing.T) {
	dir := t.TempDir()
	path, err := BuildRunConfig(dir, `D:\Games\STS2`, "run-123", "0")
	if err != nil {
		t.Fatal(err)
	}
	if filepath.Base(path) != "run-123-0-SpireLens.mcp.json" {
		t.Errorf("unexpected filename: %s", path)
	}
	b, _ := os.ReadFile(path)
	var doc map[string]any
	if err := json.Unmarshal(b, &doc); err != nil {
		t.Fatal(err)
	}
	got := doc["mcpServers"].(map[string]any)["spirelens-mcp"].(map[string]any)["env"].(map[string]any)["STS2_GAME_DIR"]
	if got != `D:\Games\STS2` {
		t.Errorf("STS2_GAME_DIR not overridden, got %v", got)
	}
}

func TestGameDirFromTemplate(t *testing.T) {
	if GameDirFromTemplate() == "" {
		t.Error("template should declare a STS2_GAME_DIR default")
	}
}
