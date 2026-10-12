# Coverage audits

Point-in-time audits (game v0.111.0, October 2026) that drove the generic
effect-frame framework ([ADR 0004](../adr/0004-generic-effect-frames.md)).
They describe what was untracked *before* that work; the proposals in them
are the backlog for refining individual owners.

- [Relic stat coverage](2026-10-relic-stat-coverage.md): the 118 relics that
  had no stats, with proposed stats and hooks. 114 are now enrolled in
  `Core/Effects/EffectRegistry.cs` and get generic outcome rows.
- [Card stat coverage](2026-10-card-stat-coverage.md): per character pool,
  which cards had effects falling through generic attribution. The generic
  card outcome bag now covers discards, created/transformed/retrieved/
  top-decked/auto-played cards, orb evokes, healing and gold; bonus-damage
  splits and multiplayer ally resources are still open.
- [Power handling](2026-10-power-handling.md): how powers were tracked, the
  inconsistencies, and the migration plan. Steps done: framework, Feel No Pain,
  Danse Macabre, Dark Embrace, Vicious, plus new coverage for 85 powers.
