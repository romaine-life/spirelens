package prompts

// MCP tool groups, ported verbatim from run-phases.ps1.
var (
	CatalogMcpTools = []string{
		"mcp__spirelens-mcp__lookup_card",
		"mcp__spirelens-mcp__lookup_character",
		"mcp__spirelens-mcp__list_cards",
		"mcp__spirelens-mcp__list_characters",
		"mcp__spirelens-mcp__lookup_relic",
		"mcp__spirelens-mcp__list_relics",
		"mcp__spirelens-mcp__lookup_encounter",
		"mcp__spirelens-mcp__list_encounters",
		"mcp__spirelens-mcp__get_catalog_summary",
		"mcp__spirelens-mcp__get_validation_capabilities",
	}

	SingleplayerMcpTools = []string{
		"mcp__spirelens-mcp__capture_screenshot",
		"mcp__spirelens-mcp__get_game_state",
		"mcp__spirelens-mcp__bridge_health",
		"mcp__spirelens-mcp__reload_spirelens_core",
		"mcp__spirelens-mcp__set_spirelens_view_stats_enabled",
		"mcp__spirelens-mcp__open_card_pile",
		"mcp__spirelens-mcp__close_card_pile",
		"mcp__spirelens-mcp__list_visible_cards",
		"mcp__spirelens-mcp__show_card_tooltip",
		"mcp__spirelens-mcp__list_visible_relics",
		"mcp__spirelens-mcp__show_relic_tooltip",
		"mcp__spirelens-mcp__start_singleplayer_run",
		"mcp__spirelens-mcp__enter_debug_room",
		"mcp__spirelens-mcp__configure_live_combat",
		"mcp__spirelens-mcp__list_save_files",
		"mcp__spirelens-mcp__inspect_save",
		"mcp__spirelens-mcp__materialize_scenario_save",
		"mcp__spirelens-mcp__install_save_as_current",
		"mcp__spirelens-mcp__validate_current_run_save",
		"mcp__spirelens-mcp__load_current_run_save",
		"mcp__spirelens-mcp__combat_play_card",
		"mcp__spirelens-mcp__combat_end_turn",
		"mcp__spirelens-mcp__combat_select_card",
		"mcp__spirelens-mcp__combat_confirm_selection",
		"mcp__spirelens-mcp__use_potion",
		"mcp__spirelens-mcp__discard_potion",
		"mcp__spirelens-mcp__proceed_to_map",
		"mcp__spirelens-mcp__rewards_claim",
		"mcp__spirelens-mcp__rewards_pick_card",
		"mcp__spirelens-mcp__rewards_skip_card",
		"mcp__spirelens-mcp__map_choose_node",
		"mcp__spirelens-mcp__rest_choose_option",
		"mcp__spirelens-mcp__shop_purchase",
		"mcp__spirelens-mcp__event_choose_option",
		"mcp__spirelens-mcp__event_advance_dialogue",
		"mcp__spirelens-mcp__deck_select_card",
		"mcp__spirelens-mcp__deck_confirm_selection",
		"mcp__spirelens-mcp__deck_cancel_selection",
		"mcp__spirelens-mcp__bundle_select",
		"mcp__spirelens-mcp__bundle_confirm_selection",
		"mcp__spirelens-mcp__bundle_cancel_selection",
		"mcp__spirelens-mcp__relic_select",
		"mcp__spirelens-mcp__relic_skip",
		"mcp__spirelens-mcp__treasure_claim_relic",
		"mcp__spirelens-mcp__crystal_sphere_set_tool",
		"mcp__spirelens-mcp__crystal_sphere_click_cell",
		"mcp__spirelens-mcp__crystal_sphere_proceed",
	}

	MultiplayerMcpTools = []string{
		"mcp__spirelens-mcp__mp_get_game_state",
		"mcp__spirelens-mcp__mp_combat_play_card",
		"mcp__spirelens-mcp__mp_combat_end_turn",
		"mcp__spirelens-mcp__mp_combat_undo_end_turn",
		"mcp__spirelens-mcp__mp_combat_select_card",
		"mcp__spirelens-mcp__mp_combat_confirm_selection",
		"mcp__spirelens-mcp__mp_use_potion",
		"mcp__spirelens-mcp__mp_discard_potion",
		"mcp__spirelens-mcp__mp_proceed_to_map",
		"mcp__spirelens-mcp__mp_rewards_claim",
		"mcp__spirelens-mcp__mp_rewards_pick_card",
		"mcp__spirelens-mcp__mp_rewards_skip_card",
		"mcp__spirelens-mcp__mp_map_vote",
		"mcp__spirelens-mcp__mp_rest_choose_option",
		"mcp__spirelens-mcp__mp_shop_purchase",
		"mcp__spirelens-mcp__mp_event_choose_option",
		"mcp__spirelens-mcp__mp_event_advance_dialogue",
		"mcp__spirelens-mcp__mp_deck_select_card",
		"mcp__spirelens-mcp__mp_deck_confirm_selection",
		"mcp__spirelens-mcp__mp_deck_cancel_selection",
		"mcp__spirelens-mcp__mp_bundle_select",
		"mcp__spirelens-mcp__mp_bundle_confirm_selection",
		"mcp__spirelens-mcp__mp_bundle_cancel_selection",
		"mcp__spirelens-mcp__mp_relic_select",
		"mcp__spirelens-mcp__mp_relic_skip",
		"mcp__spirelens-mcp__mp_treasure_claim_relic",
		"mcp__spirelens-mcp__mp_crystal_sphere_set_tool",
		"mcp__spirelens-mcp__mp_crystal_sphere_click_cell",
		"mcp__spirelens-mcp__mp_crystal_sphere_proceed",
	}
)

// PhaseDef is the per-phase metadata run-phases.ps1's $phaseDefinitions held.
type PhaseDef struct {
	Name                string
	JSON                string
	Markdown            string
	TimeoutSeconds      int
	MaxBudgetUSD        string
	AllowedAbortReasons []string
	AllowedTools        []string
	DisallowedTools     []string
}

// Definition returns the phase definition for a phase name.
func Definition(phase string) (PhaseDef, bool) {
	d, ok := definitions[phase]
	return d, ok
}

func concat(groups ...[]string) []string {
	out := []string{}
	for _, g := range groups {
		out = append(out, g...)
	}
	return out
}

var definitions = map[string]PhaseDef{
	PhaseTestPlan: {
		Name:                PhaseTestPlan,
		JSON:                "test-plan.json",
		Markdown:            "test-plan.md",
		TimeoutSeconds:      480,
		MaxBudgetUSD:        "3.00",
		AllowedAbortReasons: []string{"card_not_found", "card_ambiguous", "character_not_found", "metadata_unavailable", "mcp_capability_missing", "game_state_unreachable", "validation_plan_impossible", "phase_timeout"},
		AllowedTools: concat([]string{
			"Read", "Write", "Glob", "Grep", "ToolSearch",
			"Bash(gh issue view *)", "Bash(rg *)", "Bash(git grep *)",
		}, CatalogMcpTools),
		DisallowedTools: concat(SingleplayerMcpTools, MultiplayerMcpTools, []string{
			"Bash(gh issue view *--comments*)", "Bash(gh api *)",
			"Edit", "NotebookEdit", "WebFetch", "WebSearch", "Agent", "Task", "TaskOutput", "TaskStop",
		}),
	},
	PhaseImplementation: {
		Name:                PhaseImplementation,
		JSON:                "implementation.json",
		Markdown:            "implementation.md",
		TimeoutSeconds:      1800,
		MaxBudgetUSD:        "12.00",
		AllowedAbortReasons: []string{"change_too_large", "requires_new_library", "requires_architecture_change", "unsafe_refactor", "missing_code_context", "conflicting_requirements", "cannot_implement_without_guessing", "phase_timeout"},
		AllowedTools: concat([]string{
			"Read", "Write", "Edit", "Glob", "Grep", "ToolSearch", "TodoWrite",
			"Bash", "PowerShell", "Bash(gh issue view *)", "PowerShell(gh issue view *)",
		}, CatalogMcpTools),
		DisallowedTools: concat(SingleplayerMcpTools, MultiplayerMcpTools, []string{
			"Bash(dotnet test *)", "PowerShell(dotnet test *)",
			"Read(C:\\Users\\*\\.claude\\*)", "Read(C:\\Users\\*\\.claude\\**)",
			"Bash(gh issue view *--comments*)", "PowerShell(gh issue view *--comments*)",
			"Bash(gh api *)", "PowerShell(gh api *)",
			"Bash(gh issue comment *)", "PowerShell(gh issue comment *)",
			"Bash(gh issue edit *)", "PowerShell(gh issue edit *)",
			"Bash(gh pr *)", "PowerShell(gh pr *)",
			"Bash(git add *)", "PowerShell(git add *)",
			"Bash(git branch *)", "PowerShell(git branch *)",
			"Bash(git checkout *)", "PowerShell(git checkout *)",
			"Bash(git commit *)", "PowerShell(git commit *)",
			"Bash(git push *)", "PowerShell(git push *)",
			"mcp__spirelens-mcp__list_save_files",
			"mcp__spirelens-mcp__inspect_save",
			"mcp__spirelens-mcp__materialize_scenario_save",
			"mcp__spirelens-mcp__install_save_as_current",
			"mcp__spirelens-mcp__validate_current_run_save",
			"mcp__spirelens-mcp__load_current_run_save",
			"mcp__spirelens-mcp__list_scenario_commands",
			"mcp__spirelens-mcp__run_scenario_command",
			"Bash(git switch *)", "PowerShell(git switch *)",
			"WebFetch", "WebSearch", "Agent", "Task", "TaskOutput", "TaskStop",
		}),
	},
	PhaseVerification: {
		Name:                PhaseVerification,
		JSON:                "verification.json",
		Markdown:            "verification.md",
		TimeoutSeconds:      600,
		MaxBudgetUSD:        "6.00",
		AllowedAbortReasons: []string{"unit_tests_failed", "live_validation_failed", "screenshot_missing", "screenshot_not_relevant", "target_evidence_missing", "mcp_state_mismatch", "game_state_unreachable", "claimed_result_not_observed", "artifact_contract_missing", "phase_timeout"},
		AllowedTools: concat([]string{
			"Read", "Write", "Glob", "Grep", "ToolSearch", "TodoWrite", "Bash", "PowerShell",
		}, CatalogMcpTools, SingleplayerMcpTools),
		DisallowedTools: concat(MultiplayerMcpTools, []string{
			"Edit", "NotebookEdit",
			"Bash(dotnet test *)", "PowerShell(dotnet test *)",
			"Bash(dotnet build *)", "PowerShell(dotnet build *)",
			"Bash(gh *)", "PowerShell(gh *)",
			"Bash(git add *)", "PowerShell(git add *)",
			"Bash(git branch *)", "PowerShell(git branch *)",
			"Bash(git checkout *)", "PowerShell(git checkout *)",
			"Bash(git commit *)", "PowerShell(git commit *)",
			"Bash(git push *)", "PowerShell(git push *)",
			"Bash(git switch *)", "PowerShell(git switch *)",
			"WebFetch", "WebSearch", "Agent", "Task", "TaskOutput", "TaskStop",
		}),
	},
}
