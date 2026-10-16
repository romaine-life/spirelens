# AGENTS

This repo is a hot-reloadable Slay the Spire 2 mod focused on per-card attribution: not just what a card says it should do, but what it actually caused in the run.

## User-Owned Verification

During interactive Codex development work, the user owns test execution and
behavioral verification. Unless the user explicitly requests verification in
the current task, do **not** run tests, set up or manipulate live game/MCP test
scenarios, capture verification screenshots, or otherwise attempt to prove the
feature's behavior. Implement the requested change and clearly report that
tests and behavioral verification were not run.

Normal implementation and handoff steps are still expected: the agent may build
and deploy the mod, hot-reload SpireLens, commit, and push. This preference
overrides only the automatic test/live-verification parts of repo skills or
workflows, not their build/deploy/reload flow. Explicitly assigned verification
tasks (for example, a Glimmung verification phase) still count as an explicit
request and should follow their own validation contract.

Build, deploy, and reload are **agent-owned, not user-owned**. Never hand the
user an F5 press, a rebuild, or a redeploy as a chore, and never offer one as
an optional follow-up — the commands are in **Useful Commands** below and the
agent runs them. This includes the deploy that follows a merge: the live
install keeps running the feature-branch build until Core is rebuilt from the
merged `main` and reloaded, so that redeploy belongs to the same session that
merged. The only thing a running game
needs from the user is a full process restart, and that is governed by
**Restart Claims** below.

## Restart Claims

Never turn uncertainty about hot reload, Harmony, JIT compilation, or any other
runtime behavior into an instruction for the user to restart the game.

There are exactly three allowed outcomes:

1. If the evidence makes it certain that no restart is needed, say so.
2. If the evidence makes it certain that a restart is required, say so and name
   the evidence.
3. Otherwise, explicitly say that the restart requirement is uncertain and
   propose a concrete way to learn.

A newly introduced Harmony target is not, by itself, evidence that a restart is
required. A patched-method log entry proves installation, not whether existing
compiled callers bypass the patch. Conversely, an observed post-reload behavior
change proves that the relevant live path is already reaching the patch. Do not
give the user precautionary restart chores to cover agent uncertainty.

## Mod Policy

The Slay the Spire 2 install (`D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\`) runs **only** the user's own mods plus their required prereqs. No third-party mods.

Allowed entries:

1. The user's own mods — currently **SpireLens** and **SpirePad** (controller fix: follows the last-used pad under Steam Input; private repo [`romaine-life/spire-pad`](https://github.com/romaine-life/spire-pad), source at `D:\repos\spire-pad\`, kept out of SpireLens so it doesn't ship to SpireLens users).
2. Required prereqs for (1) — currently **BaseLib** (Alchyr), which SpireLens depends on for Harmony patching and node factories.
3. Tooling prereqs required to validate (1) via the automated verify run — currently **`SpireLensMcp`** (in-house fork at [`romaine-life/spire-lens-mcp`](https://github.com/romaine-life/spire-lens-mcp), source at `D:\repos\spire-lens-mcp\`, vendored from `Gennadiyev/STS2MCP` under MIT). Listens on `localhost:15526` exposing `/api/v1/singleplayer` and `/api/v1/multiplayer`; the Python MCP server in the same repo's `mcp/` directory connects to that endpoint and exposes ~50 game-control tools to Claude. Without it the run's bridge-readiness probe fails before Claude launches.

When inspecting `mods/`, treat any non-(SpireLens|SpirePad|BaseLib|SpireLensMcp) entry as a removal candidate. Don't recommend installing third-party mods even for diagnostics — prefer adding the diagnostic to SpireLens itself. Orphaned appdata under `%APPDATA%\SlayTheSpire2\` from removed third-party mods is also fair game to clean up, after inspecting contents (some folders, e.g. save-game backups, may have user value). Expected `Loaded N mods` line in the game log: **4** when SpireLensMcp is installed (BaseLib + SpireLens + SpirePad + SpireLensMcp), **3** otherwise.

## Current Truths

- Runtime is split into a stable loader and a hot-reloaded core.
  - [Loader/LoaderMain.cs](D:/repos/SpireLens/Loader/LoaderMain.cs:14) owns the long-lived bootstrap and `F5` reload flow.
  - [Core/CoreMain.cs](D:/repos/SpireLens/Core/CoreMain.cs:8) owns Harmony patch install/uninstall and re-entry on each reload.
- Persistence is combat-boundary based.
  - [Core/RunTracker.cs](D:/repos/SpireLens/Core/RunTracker.cs:18) buffers live combat data in `_pendingCombat`.
  - Nothing is promoted to the permanent run file until combat ends — with one deliberate exception: a LOST run's end also promotes, because on a loss `RunManager.OnEnded` fires synchronously from the killing action and the fatal combat's `CombatEnded` only fires afterwards, too late for the buffer. Abandon-mid-combat still discards the buffer.
  - The game re-fires `RunStarted` with the same `_startTime` on every main-menu Continue; `OnRunStarted` resumes/adopts the existing run record (matched by `game_start_time`) instead of minting a new one.
  - Reload between combats / between floors is supported and expected.
  - Mid-combat restore is intentionally out of scope.
- The persisted shape evolves additively without an explicit version number.
  - [Core/RunData.cs](D:/repos/SpireLens/Core/RunData.cs:1) is the source of truth for the current shape.
  - [Core/RunStorage.cs](D:/repos/SpireLens/Core/RunStorage.cs:1) detects the historic pooled shape structurally; everything else is current per-instance.
  - [Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs](D:/repos/SpireLens/Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs:1) and the checked-in fixtures pin known shapes that must remain loadable.
- Card identity is per physical card when the card has stable deck identity.
  - Instance numbers never get reused within a run.
  - Combat-generated cards that do not meaningfully exist in the deck may use pooled summaries instead of fake deck-instance identities.
- Attribution prefers observed outcomes over listed card text whenever the game can diverge from the card face.
  - Examples already in tree: actual energy gained, Regent stars spent/gained, forge granted, observed cards drawn, blocked draw attempts/reasons, successful self-summons to hand, Artifact-blocked debuffs, and downstream poison damage.
- Tooltip style is intentionally quiet.
  - Hand view stays compact.
  - Rows should be self-describing without noisy section headers.
  - Inline keyword icons are preferred when they improve scanability without making the layout louder.
  - When the game already has a recognizable asset for the stat, prefer that in-game icon over a generic label.

## Start Here

- Read [README.md](D:/repos/SpireLens/README.md:1) for the product-level overview.
- Read [docs/architecture.md](D:/repos/SpireLens/docs/architecture.md:1) for subsystem layout and data flow.
- Read [docs/sts2-runtime-primer.md](docs/sts2-runtime-primer.md) before changing card/relic attribution hooks; it captures stable Slay the Spire 2 lifecycle, combat-history, async hook, pile, and attribution timing behavior.
- Read [docs/steam-workshop-publishing.md](docs/steam-workshop-publishing.md) for how SpireLens is published/updated on the Steam Workshop (item 3774710835, workspace at `D:\repos\spirelens-workshop\`).
- For tracking behavior, start in [Core/RunTracker.cs](D:/repos/SpireLens/Core/RunTracker.cs:18).
- For tooltip/UI behavior, start in:
  - [Core/Patches/ViewStatsInjectorPatch.cs](D:/repos/SpireLens/Core/Patches/ViewStatsInjectorPatch.cs:11)
  - [Core/Patches/CardHoverTooltipPatch.cs](D:/repos/SpireLens/Core/Patches/CardHoverTooltipPatch.cs:11)

## When Changing Behavior

- If you add persisted fields:
  - keep them additive so old run files still load via missing-field defaults
  - add a fixture file under [Fixtures/RunSchema](D:/repos/SpireLens/Fixtures/RunSchema/README.md:1) capturing the new shape
  - update [SchemaLoadingTests.cs](D:/repos/SpireLens/Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs:1) to assert the new shape loads and any new fields land where expected
- If you change tooltip presentation:
  - preserve the compact-vs-full distinction
  - keep labels self-describing
  - avoid adding loud headers unless they clearly earn their space
- If you add new attribution:
  - read [docs/sts2-runtime-primer.md](docs/sts2-runtime-primer.md) first
  - prefer empirical results over intent text
  - be explicit when attribution is heuristic, pooled, contributor-ledger based, or case-specific

## Run execution model

Runs are dispatched by `romaine-life/glimmung` against this repo's
registered workflow. Each phase runs as a Glimmung-managed `k8s_job` in the
cluster, but the load-bearing work (Claude invocation, build, deploy, scenario
prep, verification) executes on this gaming laptop because the warm Slay the
Spire 2 install and the SpireLensMcp bridge only exist here.

The cluster-side phase pods open a per-run SSH session to this host over an
ephemeral Tailscale tailnet:

- The SSH user certificate is signed by Glimmung's CA per-run (10-minute TTL,
  `KeyId=glimmung-lease:<project>/<lease_id>`).
- The Tailscale auth key is minted per-run via Glimmung's federation flow
  against `auth.romaine.life`; the resulting tailnet node is `ephemeral` and
  dies when the orchestrator pod disconnects.

The harness is a single Go binary, `cmd/glimmung-spirelens`, built on the
`github.com/romaine-life/glimmung/harness` run-harness SDK. It has two faces:
the **pod face** (`glimmung-spirelens pod <slug>`) runs on the cluster-side k8s
job pods — `main()` builds a `harness/step.Registry` and calls `step.Main`,
which dispatches `GLIMMUNG_STEP_SLUG`; and the **host face**
(`glimmung-spirelens <subcmd>`) is the same binary cross-compiled for Windows
and run on this laptop over ssh by the pod (replacing the retired pwsh-over-ssh
here-docs). The pod reaches the laptop through the SDK's `harness/remotehost`
venue (mint ssh cert + tailscale authkey, userspace `tailscaled`, `tailscale nc`
ssh proxy). `git_ref` controls the harness end to end: the pod cross-compiles
the host `.exe` from its own checkout and scps it per run, and the `.mcp.json`
template is embedded in the binary. The retired `scripts/glimmung-native/*.sh`
and `.github/scripts/*.ps1` are gone; a Go reintroduction guard
(`internal/migrationguard`) fails if they return.

There is no GitHub Actions self-hosted runner on this host, and no GitHub
Actions workflow file drives these runs. Sign-in remains manual (no
AutoAdminLogon) — if
the laptop is asleep or pre-logon, the orchestrator's `env-prep` phase aborts
with `host_unavailable` and the run requeues until next manual sign-in.

See `romaine-life/glimmung/docs/remote-host-execution.md` for the orchestrator-side
protocol and `docs/glimmung-workflow.md` here for the registered phase shape.

In the verification phase, the unit-test gate is harness-owned and deterministic
(an observed-outcomes-over-claimed-intent application of the attribution
principle above). The host face's `run-phase` runs `dotnet test` on
`Tests/SpireLens.Core.Tests` with a `trx` logger before the verification agent
starts and reads the observed exit code + TRX via the SDK's
`harness/evidence.ObservedUnitTestResult`; `passed` is
`exit_code == 0 && failed == 0`. A failing observed result aborts with
`unit_tests_failed` (carrying the real failing test names) without invoking the
agent; a passing result is stamped into `verification.json` authoritatively. The
verification agent does live-MCP + screenshot evidence only — it does not run or
judge unit tests, and its narration cannot move the unit-test verdict.

## Useful Commands

- Deploy Core to the live game (the build's post-build target copies
  `SpireLens.Core.dll` into `mods/SpireLens/`; the Loader reads it from a temp
  copy so the file stays unlocked while STS2 runs):
  - `dotnet build D:\repos\SpireLens\Core\SpireLens.Core.csproj -c Debug`
  - add `-p:SkipModsDeploy=true` to compile without touching the live install
- Hot-reload the deployed Core into the running game — the agent does this, the
  user is never asked to press F5:
  - MCP tool `reload_spirelens_core` when the SpireLensMcp tools are connected
  - otherwise the equivalent bridge call:
    `Invoke-RestMethod -Uri 'http://localhost:15526/api/v1/singleplayer' -Method Post -ContentType 'application/json' -Body '{"action":"dev_reload_spirelens_core"}'`
  - a successful reload returns `{"status":"ok", ..., "reload_number":N}`
- Confirm the reload took, in `%APPDATA%\SlayTheSpire2\logs\godot.log`:
  - require `--- HOT RELOAD DONE ---` and `Core.Initialize complete`
  - treat `Core.Initialize threw` as a failed reload
  - `Patch install: N classes patched, 0 failed/skipped` should account for any
    patch classes the change added
  - note that `godot.log` is the **live** session; the timestamped
    `godot<ISO-8601>.log` siblings are rotated copies of *earlier* sessions, so
    reading one of those to judge the running process gives a stale answer
  - `[CUS-diag] Harmony patched methods` lists every installed target; grep it to
    prove a specific hook was installed (installation only — not that the live
    path reaches it)
- Loader or config-page changes need the root build/deploy, not the Core project
  alone, and can fail while STS2 holds `SpireLens.dll`. Report that distinction;
  Core-only hot reload may still be deployable.
- Build/tests:
  - `dotnet test D:\repos\SpireLens\Tests\SpireLens.Core.Tests\SpireLens.Core.Tests.csproj -c Debug`
- Focused schema tests:
  - `dotnet test D:\repos\SpireLens\Tests\SpireLens.Core.Tests\SpireLens.Core.Tests.csproj -c Debug --filter SchemaLoadingTests`
- Focused tooltip tests:
  - `dotnet test D:\repos\SpireLens\Tests\SpireLens.Core.Tests\SpireLens.Core.Tests.csproj -c Debug --filter PoisonTooltipTests`
