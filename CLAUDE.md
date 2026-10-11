# CLAUDE.md

**Read [AGENTS.md](AGENTS.md) first.** It is the shared, authoritative brief for
every agent working in this repo — runtime architecture, persistence rules,
attribution principles, mod policy, and the build/deploy/reload commands. Then
read the task-relevant docs it names.

The development workflow doc is [`.codex/skills/dev-work/SKILL.md`](.codex/skills/dev-work/SKILL.md).
The `.codex/` path is historical: **that workflow applies to Claude sessions
too.** Branch/commit/push cadence, the deploy-and-reload loop, and the log checks
in it are all expected here.

## Non-negotiables that are easy to get backwards

- **Build, deploy, and hot-reload are yours to run, not the user's.** After a
  Core change, build to deploy it and then reload the running game through the
  bridge or the `reload_spirelens_core` MCP tool. Telling the user to press F5 is
  a defect. See **Useful Commands** in AGENTS.md.
- **Do not run tests or do live behavioral verification** unless the user asks in
  the current task. AGENTS.md **User-Owned Verification** overrides only the
  test/verification steps of the dev-work skill — it does not excuse you from the
  build/deploy/reload flow in that same skill.
- **Restart claims are evidence-based.** Never hand over a precautionary restart
  to cover your own uncertainty. AGENTS.md **Restart Claims** defines the only
  three allowed outcomes.
- **Recording something that did not happen is a defect, not a documented
  limitation.** Per-card and per-relic attribution *is* the product, so never
  ship run-total corruption with a note explaining it — and price the fix
  before concluding it is expensive. When a correctness precondition is
  missing, prefer blocking the action over letting it run and silently skewing
  the data.
- **New persisted fields are additive**, and need a `Fixtures/RunSchema` fixture
  plus `SchemaLoadingTests` assertions. See **When Changing Behavior** in
  AGENTS.md.
- **Read `docs/sts2-runtime-primer.md`** before touching card/relic attribution
  hooks, and add a note there when you add new attribution.
