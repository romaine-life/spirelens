# Glimmung workflow registration

This document captures the phase shape spirelens registers with Glimmung. The
live workflow shape is the Postgres-backed `spirelens.default` row in Glimmung;
dispatch does not read a workflow file from this repo. Treat the live
registration as the source of truth — this document is the human-readable map of
it.

## Phase shape

Mirrors `romaine-life/ambience`'s registered workflow, with the per-phase shell
scripts swapped for spirelens's SSH-over-Tailscale variants. spirelens is a
`stats-display` feature type: the world (the game) exists before the feature, so
generated test plans are honest and `llm-test-plan` always runs. That is the
spirelens-vs-ambience difference — ambience is `effect`/greenfield, where the
surface does not exist at plan time, so it when-skips the plan leg and sources a
standing case. The verification phase owns its own verdict and recycle policy;
there is no separate evidence-gate phase.

```
prepare         depends_on: []                outputs: ssh_endpoint, tailnet_ip,
                                                       working_dir, bridge_ready
llm-work        depends_on: prepare           jobs: test-plan + implement
                                              outputs: test_plan, implementation,
                                                       branch_name
llm-verify      depends_on: llm-work          verify: true
                                              completion: verification
                                              recycle_policy: 1 attempt on
                                              [verify_fail, verify_malformed],
                                              lands_at=prepare
cleanup_early   depends_on: llm-verify        teardown,
                                              when: run.preserve_test_env == 'false'
touchpoint      depends_on: cleanup_early     primitive=pr_touchpoint
touchpoint_gate depends_on: touchpoint        primitive=pr_merge
cleanup_final   depends_on: touchpoint_gate   always
vars.feature_type: stats-display
pr.recycle_policy: 1 attempt on [pr_review_changes_requested], lands_at=prepare
budget.total: 25
```

`llm-verify` carries the recycle policy directly: Glimmung verification phases
own their verdicts, so the standalone evidence-gate phase (retired platform-wide)
is gone. `cleanup_early`'s `when` is the modern replacement for the retired
`skip_when_preserve_test_env` field — with `preserve_test_env=true` the condition
resolves false at dispatch, the early teardown is skipped (zero compute, a
synthesized skipped leg), and the STS2 environment is left up through the
touchpoint review window. `cleanup_final` always tears the environment down.

There is no pre-implementation issue-contract stage. Public names settle by
declaration: the implementation declares its surface and the verify phase checks
that what was declared actually serves. The test-plan and verify phases derive
their context from the issue and the live game directly.

## Run-harness binary (replaces the shell + pwsh harness)

The harness is a single Go binary, `cmd/glimmung-spirelens`, built from the
run's `git_ref` checkout. It has two faces:

- **pod face** (`glimmung-spirelens pod <slug>`) — the orchestrator-pod side.
  `main()` builds a `harness/step.Registry` (one `step.Handler` per slug below)
  and calls `step.Main`, which reads `GLIMMUNG_STEP_SLUG`, dispatches, and exits
  with the honest code. Cross-compiles for Linux (the k8s job pods). Pod
  handlers reach the laptop through the SDK's `harness/remotehost` venue
  (`MintAndConnect` / `RunSelf` / `ScpPull` / `ScpPushTree` / `SyncCheckout`).
- **host face** (`glimmung-spirelens <subcmd>`) — the gaming-laptop side,
  cross-compiled for Windows and run over ssh by the pod via `RunSelf` (the
  typed replacement for the retired pwsh-over-ssh here-docs). Subcommands:
  `run-phase`, `prepare-scenario`, `prepare-host`, `restart-sts2`,
  `probe-mods`, `probe-bridge`, `build-deploy`, `pack-evidence`.

`git_ref` controls the harness end to end: the pod cross-compiles the Windows
host `.exe` from its own checkout and scps it onto the laptop per run (replacing
the retired `native_stage_harness` scp of `.github/scripts/*.ps1`). The
`.mcp.json` MCP-client template is embedded in the binary, so it too is
`git_ref`-controlled with no separate staging.

The grader honesty contract (the four failure modes the SDK kills) is enforced
in Go: a `throw` cannot become a bare `exit 1` (typed `LayeredError` with a
layer that maps to `suspected_cause`); a harness crash before the model runs is
never billed as a model failure (only `harness/agent.Invoke` may originate a
model-layer error); the verification phase builds **only** the selected phase's
prompt lazily; and step inputs ride as typed flags over `RunSelf` instead of a
per-step env here-doc.

## Step slugs (per phase)

The slugs below are what the registration's `phases[].jobs[].steps[].slug`
field must contain — the binary's pod-face `step.Registry` registers exactly
these (identical to the retired shell dispatch, so the phase shape is
unchanged).

### prepare / env-prep job
- `mint-credentials` — generates ed25519 keypair, mints SSH cert + Tailscale auth key.
- `bring-up-tailnet` — tailscaled in userspace networking mode + `tailscale up`.
- `resolve-host-ip` — looks up the laptop's tailnet IPv4 by `tag:spirelens-host`.
- `probe-ssh` — 30-second SSH reachability deadline.
- `probe-mod-set` — fails closed on any mod outside `{BaseLib, SpireLens, SpireLensMcp}`.
- `install-mcp-start-sts2` — syncs the laptop checkout to the run commit
  (`SyncCheckout`) and runs the host face `prepare-host --install-mcp --start-sts2`.
- `probe-bridge-ready` — runs the host face `probe-bridge` and aborts
  `bridge_not_ready` on miss. It waits ≤90s on a directory change notification
  for the bridge's `bridge-ready.json` (`%APPDATA%\SlayTheSpire2\SpireLensMcpBridge\`),
  which SpireLensMcpBridge writes once `NGame.GameStartupComplete` finishes, then
  confirms once over HTTP. Stopping STS2 waits on the process handles. Neither
  polls.
- `emit-env-outputs` — writes `ssh_endpoint`, `working_dir`.

### llm-work (two jobs)
**test-plan**: `run-test-plan`, `collect-test-plan`.
**implement**: `run-implementation`, `push-branch`, `collect-implementation`.
`push-branch` commits the laptop checkout after the implementation LLM exits and
pushes the run-scoped `glimmung/<run_id>` branch with the per-run GitHub token.

### llm-verify
- `build-and-deploy` — runs the host face `build-deploy --branch <branch>`,
  which checks out the implementation branch and `dotnet build`s the loader
  (compile-only) and core (deploy) into the live `mods/` folder.
- `prepare-scenario` — runs the host face `prepare-scenario` (the
  restart→materialize→stop→install→restart→validate_load MCP flow, with the
  scenario validator embedded as the same verbatim Python the laptop's
  spire-lens-mcp server is driven with).
- `run-verification` — runs the host face `run-phase --phase verification`. The
  unit-test gate is harness-owned and deterministic: before invoking the
  verification agent, the harness runs `dotnet test` on
  `Tests/SpireLens.Core.Tests` with a `trx` logger and reads the **observed**
  exit code + TRX via the SDK's `harness/evidence.ObservedUnitTestResult` —
  `passed` is `exit_code == 0 && failed == 0`. If unit tests fail, it writes a
  determined `unit_tests_failed` verdict carrying the actual failing test
  names/counts and does NOT invoke the agent (tests are a hard gate, so a red
  run never spends model tokens or gets mislabelled as a model failure). If they
  pass, it invokes claude via `harness/agent.Invoke` for live-MCP + screenshot
  evidence only, then stamps the observed `unit_tests` block into
  `verification.json` authoritatively and runs the deterministic evidence guard.
  The retired design, where the agent ran `dotnet test` and the harness inferred
  pass/fail by regex-scanning the agent's prose, is gone end to end.
- `collect-evidence` — runs the host face `pack-evidence` (zips
  `verification.json` + screenshots + the `live-mcp-*.json` evidence), pulls the
  one zip with `ScpPull`, and unpacks it into the exact finalizer tree
  `${GLIMMUNG_WORKING_DIR}/artifacts/{verification.json,screenshots,evidence}`.
- `finalize-verification` — Glimmung-owned `verification_finalize` primitive
  that uploads evidence to the run-owned artifact prefix and writes the typed
  `verification` completion payload. The repo script must not emit
  `verification` as a phase output.

### cleanup_early, touchpoint, touchpoint_gate, cleanup_final
These reuse Glimmung's native primitives directly. See ambience's registered
workflow for the canonical inline shell snippets: `primitive: pr_touchpoint` and
`primitive: pr_merge` resolve to Glimmung-supplied handlers. `cleanup_early`
carries `when: "${{ run.preserve_test_env }} == 'false'"`, so it is skipped (zero
compute, a synthesized skipped leg) when the run preserves its test env.

## Re-registration: step-slug → `run` invocation

Every pod step runs the SAME command; only the slug differs (the runner sets
`GLIMMUNG_STEP_SLUG`, and the explicit `pod <slug>` arg is belt-and-suspenders):

```
go build -o /tmp/glimmung-spirelens ./cmd/glimmung-spirelens && \
  exec /tmp/glimmung-spirelens pod <slug>
```

So each `phases[].jobs[].steps[].run` is that two-liner with `<slug>` set to the
step's slug, and `shell: bash`. The build resolves `glimmung v0.1.0` via the tag
(GOPRIVATE=`github.com/romaine-life/*`); the runner image must have Go ≥1.25 and
network egress to mint the module (the same egress the checkout uses). All slugs
in "Step slugs (per phase)" above map to this one invocation, differing only by
`<slug>`.

**Host distribution (replaces `native_stage_harness`).** The pod handlers that
touch the laptop cross-compile the Windows host binary from the pod checkout and
scp it onto the laptop per run, then invoke it over ssh:

```
GOOS=windows GOARCH=amd64 go build -o <stage>/glimmung-spirelens.exe ./cmd/glimmung-spirelens
# scp <stage>/ -> C:/glimmung-runs/<run-ref>/hostbin/   (via remotehost.ScpPushTree)
# ssh ... C:/glimmung-runs/<run-ref>/hostbin/glimmung-spirelens.exe <host-subcmd> --flags...
```

`git_ref` therefore controls grading: the host `.exe` is built from the run's
checkout, not from anything pre-installed on the laptop. The host-subcommand
mapping per slug:

| pod slug | host subcommand invoked over `RunSelf` |
| --- | --- |
| `install-mcp-start-sts2` | `prepare-host --install-mcp --start-sts2` |
| `probe-mod-set` | `probe-mods --out <hostwd>/mod-probe.json` (pod scp-pulls + decides the abort) |
| `probe-bridge-ready` | `probe-bridge --out <hostwd>/bridge-probe.json` |
| `run-test-plan` | `run-phase --phase test_plan ...` |
| `run-implementation` | `run-phase --phase implementation ...` |
| `build-and-deploy` | `build-deploy --branch <branch> ...` |
| `prepare-scenario` | `prepare-scenario --working-dir <hostwd> [--test-plan-b64 ...]` |
| `run-verification` | `run-phase --phase verification [--test-plan-b64 ...] [--implementation-b64 ...]` |
| `collect-evidence` | `pack-evidence --working-dir <hostwd> --out <hostwd>/evidence.zip` |

`run-phase`/`build-deploy` also take `--issue-number`, `--repo-slug`,
`--repo-root D:/repos/SpireLens`, `--working-dir`, `--run-id`,
`--attempt-index`, and `--github-token-b64` (the GitHub token minted per attempt
on the pod from `GLIMMUNG_GITHUB_TOKEN_URL`). Verify-only runs (llm-work
skipped) hydrate `test-plan.json` / `implementation.json` onto the laptop via
the `--*-b64` flags from the declared `test_plan` / `implementation` inputs.

> SDK gaps surfaced by this distribution that the hub should fold back into
> `glimmung/harness`: `RunSelf` discards the remote process's stdout (so the
> agent's `usage` lines are not priced by the runner across the ssh hop, and
> probes must return data via a pulled file); `MintAndConnect` is not
> step-idempotent within a pod (it re-keygens / re-mints and unconditionally
> starts `tailscaled`, whereas the prepare phase runs several steps in one pod);
> there is no single-file `ScpPush` nor a directory `ScpPullTree`; there is no
> run-callback GitHub-token mint; and `harness/verification.Verification` is too
> narrow to carry spirelens's rich domain verdict (so the producer writes
> `verification.json` itself, reusing the SDK's status enum + artifacts-dir).

## Source of truth

The live shape is the `spirelens.default` registration in Glimmung — read it with
the glimmung MCP `list_workflows project=spirelens`, replace it with
`register_workflow`. It is not edited by hand from this repo. When the registered
shape changes, update the phase-shape map above in the same change so this
document keeps describing the final behavior.
