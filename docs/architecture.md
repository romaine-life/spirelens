# Architecture

This codebase has two big goals:

1. stay hot-reload friendly during development
2. attribute run outcomes back to the card that actually caused them

For the stable Slay the Spire 2 runtime mental model behind the hook choices here, read [docs/sts2-runtime-primer.md](sts2-runtime-primer.md). That primer is intended to reduce repeated rediscovery of game lifecycle, combat-history, async hook, pile, and attribution timing behavior.

## Runtime Topology

### Loader

- [Loader/LoaderMain.cs](../Loader/LoaderMain.cs) is the stable bootstrap loaded once by the game's mod manager.
- It owns the `F5` workflow:
  - copy the Core DLL to a fresh temp path
  - load that copy
  - call `CoreMain.Initialize()`
  - on reload, call the previous `CoreMain.Shutdown()` first
- It also owns the stable BaseLib boundary:
  - [Config/SpireLensConfig.cs](../Config/SpireLensConfig.cs) defines the BaseLib-backed mod settings UI
  - [Loader/RuntimeOptionsBridge.cs](../Loader/RuntimeOptionsBridge.cs) exposes a stable runtime-options bridge to the hot-reloaded Core
  - [Api/SpireLensApiRegistry.cs](../Api/SpireLensApiRegistry.cs) exposes a small public API surface other mods can call into
- The loader does not try to truly unload old contexts; it relies on explicit cleanup plus process-lifetime tolerance.

### Core

- [Core/CoreMain.cs](../Core/CoreMain.cs) is the hot-reloaded entry point.
- It applies Harmony patches, wires tracker hooks, resumes active run state after reload, and tears all of that back down on `Shutdown()`.
- The Core intentionally does not reference BaseLib directly anymore.
- Loader-owned config is consumed through [Core/RuntimeOptions.cs](../Core/RuntimeOptions.cs), which keeps the hot-reloaded assembly focused on domain logic instead of framework glue.

## Data Flow

### Live Tracking

- [Core/RunTracker.cs](../Core/RunTracker.cs) is the heart of the mod.
- Combat history entries and selected hook patches feed into the tracker.
- During combat, observations accumulate in `_pendingCombat`.
- On combat end, `_pendingCombat` is promoted into the committed run aggregates and saved.
- Potion history uses the same boundary: offers/acquisitions outside combat
  save immediately, while combat-time acquisitions and uses update a pending
  history snapshot that is merged only when the combat resolves.

This combat-boundary rule is important:

- between-combat reload is supported
- between-floor reload is supported
- mid-combat restore is intentionally unsupported

### Persistence

- [Core/RunData.cs](../Core/RunData.cs) defines the serialized run shape.
- [Core/RunStorage.cs](../Core/RunStorage.cs) handles load/save and resumability rules.
- Schema changes are additive when possible. The fixture catalog retains
  historical numbered shapes and adds unversioned per-feature shapes.

Historical compatibility is pinned by:

- [Fixtures/RunSchema](../Fixtures/RunSchema/README.md)
- [Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs](../Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs)

## Attribution Model

The project tries to answer "what actually happened because of this card?" rather than "what did the card text claim?"

Examples already implemented:

- direct attack damage, blocked damage, overkill, kills
- block gained / effective / wasted
- actual energy generated
- actual maximum HP lost to card costs
- Regent stars spent / generated
- forge granted from cards
- Alchemize potions actually procured, failed procurements, and gained rarity splits
- Discovery cards actually selected, including rarity/type and observed energy discount
- observed cards drawn from draw effects
- observed Lightning-orb damage retained on the exact card that channeled it,
  separate from that card's direct Attack damage
- blocked draw attempts, categorized blocked reasons, and effect-side downstream blocked counts
- successful self-summons to hand for recurring cards like Make It So
- Osty summon HP, current-body absorbed damage, Unleash's Osty-HP payoff
  damage, and the upgrade-continuous Phylactery family rates
- effect applications credited back to the source card
- Artifact-blocked debuffs
- downstream poison damage and poison overkill
- stacked merged effects like Noxious Fumes preserve per-source contribution ledgers before their poison fanout is charged back into the poison ownership ledger

When attribution is not naturally one-card-to-one-outcome, the code prefers:

- observed outcomes over listed intent
- pooled summaries for combat-generated cards when they do not have stable deck identity
- run-level meta-stats surfaced on related cards when the value describes all
  instances or occurrences of a mechanic rather than one card's own effect
  (including power-ID-keyed aggregates such as Juggling's confirmed Attack
  copies and active turn/combat denominators)
- explicitly heuristic handling instead of pretending certainty

## UI Surface

- [Core/Patches/ViewStatsInjectorPatch.cs](../Core/Patches/ViewStatsInjectorPatch.cs) injects the deck-view shortcut for the global SpireLens options menu.
- [Core/SpireLensOptionsMenu.cs](../Core/SpireLensOptionsMenu.cs) owns the modal, screen-independent checkbox window and its keyboard/gamepad shortcuts. Its highlighted row is independent of Godot GUI focus so the underlying game selection remains untouched while the modal intercepts input.
- [Core/Patches/PotionCompendiumRunHistoryPatch.cs](../Core/Patches/PotionCompendiumRunHistoryPatch.cs) adds the potion-gallery mode dropdown and replaces the rarity galleries with a two-column vertical timeline of native potion holders. Each holder's run details are appended through the shared native hover-tip augmentation path.
- [Core/Patches/PotionRunHistoryTrackingPatch.cs](../Core/Patches/PotionRunHistoryTrackingPatch.cs) observes visible reward/shop offers plus final belt insertion, use, and discard outcomes.
- [Core/Patches/PotionBeltStatsTooltip.cs](../Core/Patches/PotionBeltStatsTooltip.cs) derives the run-wide offer, rarity, rejection, purchase, activation, and discard summary from that provenance and appends it to every filled or empty native potion-belt holder.
- [Core/Patches/RelicBarFilterPatch.cs](../Core/Patches/RelicBarFilterPatch.cs) optionally hides classified, already-resolved relics from the standard in-run relic bar without changing ownership, effects, or any other relic surface, then rewires top-bar controller navigation across the remaining visible relics. Finite-turn relics enter a transient non-combat state when their native activation fires (with the configured turn as a reload-safe fallback). Native terminal-combat relics do the same without a turn cutoff and reconstruct their state directly from the game after Core reload; Lava Lamp uses irreversible combat disqualification rather than a positive activation. Limited-use relics transition when the game reports `IsUsedUp`; combat reset restores recurring relics for the next fight.
- [Core/RelicClassificationStore.cs](../Core/RelicClassificationStore.cs) loads the embedded combat/non-combat JSON, normalizes it against the current game relic database, persists the editable AppData copy, and applies compendium changes immediately. Normalization is deferred rather than run at load: on a cold start the Core is initialized before the game populates `ModelDb`, so the store probes for the live relic database and reconciles on the first read or write instead — see [Core Loads Before The Game's Model Database Exists](sts2-runtime-primer.md#core-loads-before-the-games-model-database-exists).
- [Core/Patches/RelicCompendiumClassificationPatch.cs](../Core/Patches/RelicCompendiumClassificationPatch.cs) turns compendium mouse/controller presses into classification toggles while edit mode is active and renders the classification badges.
- [Core/StatConceptGlossary.cs](../Core/StatConceptGlossary.cs) validates and caches the embedded [Core/Config/stat-concepts.json](../Core/Config/stat-concepts.json) vocabulary once per Core load, then renders the same native rich-text hint markup for stat rows and the relic compendium's **Icon glossary** mode.
- [Core/Patches/StatsVisibilityHotkeyPatch.cs](../Core/Patches/StatsVisibilityHotkeyPatch.cs) maps a standalone Left Shift tap and Right Stick (R3) press to opening/closing that menu while preserving Shift-based chords regardless of whether another modifier was pressed before or after Shift; Left Trigger remains Draw Pile and Left Stick press remains Peek.
- [Core/Patches/CardHoverTooltipPatch.cs](../Core/Patches/CardHoverTooltipPatch.cs) builds compact and full tooltip bodies.
- [Core/Patches/DeckViewNotInDeckPatch.cs](../Core/Patches/DeckViewNotInDeckPatch.cs) switches the native deck grid between current deck cards and the separate removed/meta-card collection; those two sets are never mixed.
- [Core/Patches/NativeHoverTipAugmentationPatch.cs](../Core/Patches/NativeHoverTipAugmentationPatch.cs) appends owner-specific SpireLens data to the game's `IHoverTip` sequence immediately before `NHoverTipSet` renders it, then applies the SpireLens blue panel tint and brand to only the resulting native stats control.
- [Core/Patches/StatsTooltipPinManager.cs](../Core/Patches/StatsTooltipPinManager.cs) pins one native card, relic, or campfire-summary tooltip set under a dedicated surrogate owner, including card and relic rows rebuilt inside run history, displays the game's top-panel lock icon on its source, and releases the pin on the next non-motion user action.
- [Core/StatsImageCapture.cs](../Core/StatsImageCapture.cs) crops the selected
  item with every rendered native and SpireLens tooltip page in their original
  user-visible scale and relative placement, using logical-to-texture scaling;
  [Core/WindowsImageClipboard.cs](../Core/WindowsImageClipboard.cs) publishes
  that image as an in-memory Windows DIB without a helper process or file.
- [Core/Patches/EncounterStatsTooltip.cs](../Core/Patches/EncounterStatsTooltip.cs) appends the current (or just-finished) combat's round-by-round damage dealt and taken, recorded (reload-safe, not persisted) by [Core/EncounterTurnDamage.cs](../Core/EncounterTurnDamage.cs), to the native top-bar room-icon tooltip. It pins on right-click like the other top-bar counters; the pin target is `NTopBarRoomIcon`, while the game's ordinary tooltip owner is its inner `Icon` TextureRect.
- [Core/RunHistoryDeckViewer.cs](../Core/RunHistoryDeckViewer.cs) adds a deck icon to the run-history Cards section and hosts the game's native deck-view scene over run history. It reconstructs the selected player's final deck from the game's individual `SerializableCard` entries and binds duplicate cards back to their SpireLens per-instance keys by saved deck rank.
- [Core/RunHistoryCampfireSummary.cs](../Core/RunHistoryCampfireSummary.cs) adds one native campfire icon beneath the run-history act rows and presents the selected player's saved `rest_site_choices` plus the same map point's concrete outcome fields as a chronological hover tooltip. Rest and Smith therefore show actual healing and upgraded cards; Dig, Cook, Clone, Hatch, Lift, and Kindle receive action-specific result descriptions. It reads the game's history data directly and owns no additional tracking or persistence.
- [Core/Patches/CardTooltipPinInputPatch.cs](../Core/Patches/CardTooltipPinInputPatch.cs) intercepts right press on every declared `NCardHolder.OnMousePressed` implementation before the game records an alternate-click action, but claims it only for holders inside passive card-pile or cards-view screens.
- [Core/StatsTooltip.cs](../Core/StatsTooltip.cs) creates native `HoverTip` values with the established 20px stats typography and owns no scene-tree nodes or hover lifecycle.
- [Config/SpireLensConfig.cs](../Config/SpireLensConfig.cs) provides the persistent mod-settings UI for runtime display options.

Current UI conventions:

- hand tooltips stay compact
- global visibility gates card, relic, enemy, compendium, and run-history stats before aggregate/markup work
- card tooltips on every surface are display-only opt-in; disabling them does not disable attribution
- deck-view tooltips can be fuller and include lineage/context
- rows should be self-describing
- ordinary card, relic, and enemy scalar rows share a natural-width table: the
  widest semantic label establishes the value column, and every value and
  percentage is left-aligned at its column's shared horizontal position; card
  names, relic names, destinations, and other textual outcomes render inline
  with their label so Godot cannot squeeze them into a narrow scalar column;
  do not use character-count wrapping heuristics
- run-summary rows share one natural-width table: the widest semantic label
  establishes the value column, and every value is left-aligned at that same
  horizontal position
- loud section headers are discouraged unless they add real clarity
- inline icons are preferred for keyword-like effects when they improve scanning
- rows phrased as “per <recognized concept>” render the hinted **Per** (`/`)
  concept immediately before the denominator icon; fully icon-driven “in” and
  “this” scopes render `∈`,
  and an aggregate over every member of a plural scope renders `∈ ∀` before
  the scope icon; mixed prose stays readable instead of forcing relation
  symbols ahead of its remaining words
- every icon-driven stat row can pair a left-side information hint for the full row meaning with a separate semantic concept hint; the central compendium glossary describes those same concept symbols
- card, enemy, relic, potion, and run-summary stat rows all pass through the shared row vocabulary; established concepts render as glossary icons, and equivalent prose is retained only in the information hint rather than repeated visibly beside the icon
- when the game already exposes a recognizable asset, prefer the in-game block/draw/energy/star iconography over generic text-only rows
- native lifecycle does not erase visual ownership: SpireLens stats tips retain
  their larger body text, blue background treatment, and top-right brand while
  the game owns positioning, layering, and removal
- pinned card and relic stats add one flat **Copy** action beside the brand;
  its press is the only click that preserves the pin, and the button hides for
  the rendered frame so it is not included in its own clipboard image

## Generated And Non-Deck Cards

Not every card the player sees should be treated as a stable deck resident.

- stable deck cards use per-instance numbering
- removed cards remain viewable with their accumulated stats
- the not-in-deck view replaces the live deck grid with removed physical cards
  plus supported pooled meta-cards
- Shiv, Soul, Sovereign Blade, and each encountered Status definition render as
  pooled meta-cards; the show-all option also enumerates every Status card in
  the current game database and renders unseen entries with zeroed stats
- some combat-generated cards are better represented as pooled summaries than
  as fake permanent instances

That distinction matters for both tooltip wording and data integrity.

## If You Add A New Stat

Use this checklist:

1. read [docs/sts2-runtime-primer.md](sts2-runtime-primer.md) for hook timing and attribution traps
2. decide whether the stat should be per-instance, pooled, meta-stat, effect-oriented, or relic-oriented
3. record the observed game outcome, not just the requested amount, if those can diverge
4. update [RunData.cs](../Core/RunData.cs) if persistence changes
5. update fixtures under [Fixtures/RunSchema](../Fixtures/RunSchema/README.md)
6. update [SchemaLoadingTests.cs](../Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs)
7. update tooltip rendering in [CardHoverTooltipPatch.cs](../Core/Patches/CardHoverTooltipPatch.cs) if the stat is user-facing
8. keep compact tooltip noise low
