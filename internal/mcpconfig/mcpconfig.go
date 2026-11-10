// Package mcpconfig embeds the canonical spirelens-mcp client config template
// and builds the per-run MCP config the verification agent uses.
//
// The template is compiled INTO the harness binary, so the binary built from a
// run's git_ref carries its own MCP config — git_ref controls the harness's MCP
// wiring with no separate staging step (this is what replaces the retired
// native_stage_harness scp of .mcp.json onto the laptop). A test asserts the
// embedded copy stays byte-identical to the repo-root .mcp.json so the two never
// drift.
package mcpconfig

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

//go:embed template.json
var templateJSON []byte

// Template returns the embedded canonical .mcp.json template bytes.
func Template() []byte { return templateJSON }

// BuildRunConfig writes a per-run MCP config under workingDir/mcp, derived from
// the embedded template with the spirelens-mcp server's STS2_GAME_DIR overridden
// to the resolved game dir. It returns the written path. Mirrors
// native-runtime.ps1's New-NativeMcpConfig (UTF-8, the
// "<run>-<attempt>-SpireLens.mcp.json" filename, depth-20 JSON).
func BuildRunConfig(workingDir, gameDir, runID, attemptIndex string) (string, error) {
	var doc map[string]any
	if err := json.Unmarshal(templateJSON, &doc); err != nil {
		return "", fmt.Errorf("parse embedded mcp template: %w", err)
	}
	servers, ok := doc["mcpServers"].(map[string]any)
	if !ok {
		return "", fmt.Errorf("mcp template missing mcpServers object")
	}
	server, ok := servers["spirelens-mcp"].(map[string]any)
	if !ok {
		return "", fmt.Errorf("mcp template missing the spirelens-mcp server")
	}
	env, ok := server["env"].(map[string]any)
	if !ok {
		env = map[string]any{}
		server["env"] = env
	}
	env["STS2_GAME_DIR"] = gameDir

	mcpDir := filepath.Join(workingDir, "mcp")
	if err := os.MkdirAll(mcpDir, 0o755); err != nil {
		return "", fmt.Errorf("create mcp dir: %w", err)
	}
	out := filepath.Join(mcpDir, fmt.Sprintf("%s-%s-SpireLens.mcp.json", runID, attemptIndex))
	encoded, err := json.MarshalIndent(doc, "", "  ")
	if err != nil {
		return "", fmt.Errorf("encode mcp config: %w", err)
	}
	if err := os.WriteFile(out, encoded, 0o644); err != nil {
		return "", fmt.Errorf("write mcp config: %w", err)
	}
	return out, nil
}

// McpDirectoryFromTemplate returns the spirelens-mcp server's --directory arg
// (the on-host mcp/ checkout dir uv runs from), empty when absent. Mirrors
// prepare-scenario.ps1's Get-McpDirectory.
func McpDirectoryFromTemplate() string {
	var doc struct {
		McpServers map[string]struct {
			Args []string `json:"args"`
		} `json:"mcpServers"`
	}
	if err := json.Unmarshal(templateJSON, &doc); err != nil {
		return ""
	}
	s, ok := doc.McpServers["spirelens-mcp"]
	if !ok {
		return ""
	}
	for i := 0; i+1 < len(s.Args); i++ {
		if s.Args[i] == "--directory" {
			return s.Args[i+1]
		}
	}
	return ""
}

// GameDirFromTemplate returns the STS2_GAME_DIR declared in the embedded
// template, empty when absent. Used as one candidate by the game-dir resolver.
func GameDirFromTemplate() string {
	var doc struct {
		McpServers map[string]struct {
			Env map[string]string `json:"env"`
		} `json:"mcpServers"`
	}
	if err := json.Unmarshal(templateJSON, &doc); err != nil {
		return ""
	}
	if s, ok := doc.McpServers["spirelens-mcp"]; ok {
		return s.Env["STS2_GAME_DIR"]
	}
	return ""
}
