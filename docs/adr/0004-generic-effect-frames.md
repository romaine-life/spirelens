# 0004. Generic Effect Frames For Relics And Powers

Date: 2026-10-12

Status: Accepted

## Context

By October 2026, 180 of the game's 300 relics had stats, each through a
hand-written patch, `RunTracker` recorder, `RelicAggregate` fields, tooltip
builder, fixture and test (about 500 lines per relic). 118 relics and roughly
100 player powers still had none. Power payoff tracking for the ~17 powers
that had it used six different attribution models, and most of its ~6,600
lines were copies of one "arm a window around the power's callback" shape.

Two observers also double-booked: block, energy, stars, draws and power
applications produced by a relic or power callback *while a card was
resolving* fell back to that card (for example Ivory Tile's energy, or Feel
No Pain's block during True Grit).

## Decision

Owner-specific attribution (ADR 0001) is implemented once, generically:

1. `Core/Effects/EffectRegistry` lists enrolled relic and power classes by
   name. Their own async hook overrides are discovered by reflection.
2. `EffectFrameTaskPatch` (one patch class, many targets) pushes an
   `EffectFrame` into an `AsyncLocal` for the duration of each enrolled hook.
   The hook's state machine captures it, so every command it awaits sees it.
3. The existing shared observers (block entry, damage entry, heal, energy,
   stars, draw, power received, orb channel, max HP, gold, generated,
   upgraded, transformed, added and removed cards, forge) ask for a live
   frame first. When one claims the outcome, the resolving-card fallback is
   skipped. A card play that *started inside* the frame shadows it.
4. Outcomes land in one additive shape: `Dictionary<string, decimal>
   Outcomes` keyed by `EffectOutcomeKeys` constants, on `RelicAggregate`,
   the pooled `PowerAggregate`, and `AppliedEffectAggregate` (a source's
   share of a power's payoff).
5. Power payoff is split across sources by a per-instance contributor
   ledger: the stacks each card, relic, or parent power put into the power.
   Proportional splitting is a convention; it is exact only for one source.
6. Tooltips render the bag through `EffectOutcomeRows`, so every enrolled
   owner uses the same vocabulary.

Enrolled owners are, by rule, owners without bespoke tracking. The
hand-written relics and powers keep their windows; migrating them onto the
framework is follow-up work, one owner at a time, and must stop writing the
legacy field in the same change that starts writing the bag.

## Consequences

- Adding a relic or power is one registry line; refinement (a custom label,
  a qualifier, a value-modifier marginal) is added only where the generic
  rows are not good enough.
- Value modifiers that never issue a command (Strength, Vulnerable, damage
  and block multipliers) are not captured by frames. Hand-draw and max-Energy
  modifiers are handled by two dedicated generic patches; damage/block
  marginal contribution needs its own convention and is deferred.
- Reward-shaping relics (Glitter, Lava Lamp, Black Star, Driftwood...) get
  only activations from frames; their offer/take stats need the existing
  reward-screen patterns.
- Work the game queues as a separate action rather than awaiting directly
  loses the frame. Each enrolled owner's rows should be checked once in play.
