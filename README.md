# SpireLens

Per-card attribution stats mod for [Slay the Spire 2](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/). For every card you play, it tracks what actually happened: effective damage vs. overkill, block that absorbed vs. wasted, drawn cards played vs. idle, energy generated vs. unused, and effect-oriented outcomes like poison damage.

**Status:** Dev build - core per-instance card stats are live in-game, including damage/block attribution, observed draw and energy generation, card-caused current/max-HP loss, Regent star-resource spend/gain tracking, forge granted from cards, Alchemize potion outcomes by rarity, Jack of All Trades generated-card totals with rarity/type/cost breakdowns, Discovery picked-card rarity/type/discount outcomes, Consuming Shadow's Power-owned orb evocations, Juggling power-owned Attack-copy totals with rarity and active turn/combat averages, Unrelenting's shared Free Attack charge utilization and energy savings, Prismatic Gem and Dingy Rug's final visible card offers split across the Ironclad, Silent, Regent, Necrobinder, Defect, and Colorless pools, blocked-draw attribution, recurring summon-to-hand tracking, applied-effect summaries, Artifact-blocked debuffs, removed-card viewing, pooled combat-generated card summaries, and dedicated poison application/damage rows. Not yet published to Nexus (M6).

For codebase orientation, start with [AGENTS.md](AGENTS.md), [docs/architecture.md](docs/architecture.md), and [docs/sts2-runtime-primer.md](docs/sts2-runtime-primer.md).

## Why

Existing stats mods answer "how often did I *pick* this card" ([SlayTheStats](https://www.nexusmods.com/slaythespire2/mods/349)) or "how much value did this *relic* provide" ([Relic Stats](https://www.nexusmods.com/slaythespire2/mods/327)). Nothing tracks how much of what each card *attempted* actually mattered. A 6-damage Strike into a 4-HP enemy and a 6-damage Strike into a fresh elite look the same on a play counter, but they have very different value.

## What it tracks (target design)

**Attack cards** - four numbers per play:

- `raw_damage_intended` - damage the card tried to deal (after buffs/debuffs)
- `blocked_by_target` - enemy block that absorbed some
- `overkill` - damage past enemy HP (wasted)
- `effective_damage` - what actually counted

**Block cards** - how much of the generated block actually absorbed incoming damage vs. expired unused. Per-card block attribution uses a heuristic (see [issue #1](https://github.com/romaine-life/spirelens/issues/1)).

**Utility cards** - closure tracking:

- Energy generated: was it spent or end-of-turn wasted?
- Regent stars generated/spent: what did the card actually add to or consume from the star pool?
- Cards drawn: were they played this turn/run or sit in hand?

**Effect cards** - effect-oriented summaries:

- Effect applications credited back to the card instance that applied them
- Artifact-blocked debuffs counted separately so failed debuffs still surface
- Dedicated poison rows for poison applied, observed poison damage, and poison overkill

## How you'd use it

A compact **Open SpireLens menu** shortcut sits beside the game's existing
"View Upgrades" toggle on the in-run deck view. The same modal options window
opens from anywhere the game is taking input — gameplay, the main menu, and the
Pause, Settings, and Compendium screens included — by tapping **Left Shift** on
keyboard or pressing **Right Stick (R3)** on controller. The shortcuts stay out
of the way while you type in a text field, rebind a key, or have the dev
console or a platform overlay open. Shift-based chords such as Steam's
Shift+Tab and Windows+Shift+S are left alone, including chords whose other
modifier was pressed before Left Shift.

This menu is where every SpireLens setting lives. The game's **Mods →
SpireLens** page only points here and shows the installed build. The menu
holds the master stats visibility, card stats, monster stats, and not-in-deck
card controls; the hand-hover options (show card stats over the hand, full
instead of compact hand stats); the relic bar filter; performance and
diagnostics (pause card tracking during combat, debug logging, and the time
zone the build date is shown in); and a quiet build-info footer. Settings are
saved in the BaseLib mod configuration under their long-standing keys, so
existing choices carry over.

The modal blocks normal game input while open. It does not take Godot focus
from the game screen beneath it, so closing the menu returns to the exact
selection and highlight that were already active. The D-pad and left stick
move the menu's independent highlight between rows, scrolling it into view,
and `A` toggles the highlighted option (or steps the time-zone row to the next
zone). Any other non-direction controller button closes the menu. Mouse
selection and the mouse wheel remain fully supported; Escape, Left Shift, or
the window's Close button also closes it.

A few rows need the SpireLens Loader that ships with this menu. Because hot
reload updates only the Core, a game still running an older Loader shows those
rows greyed out with "needs a game restart" until the next full restart.

The same modal includes **View current-run potion history**, which opens the
game's Compendium → Potion Gallery directly in SpireLens's **Current run
stats** mode. The potion gallery dropdown switches between the normal gallery
and a three-column vertical timeline: potions seen but not taken on the left,
acquisitions in the middle, and use or final disposition on the right. A used,
discarded, or held-at-run-end potion repeats with the same per-potion-type
instance number and a native connector back to its acquisition. Timeline
entries remain the gallery's native hoverable potion holders; acquisition,
use, discard, and held-at-run-end details appear in their ordinary hover
tooltips. A used Blood Potion additionally reports the current HP it actually
restored. A used Explosive Ampoule reports its observed damage attempted,
damage dealt, damage blocked, overkill, kills, and targets hit. A used Swift
Potion reports cards actually drawn and card draws blocked. A used Fortifier
reports observed block gained, absorbed, and wasted.

The optional, mutually exclusive relic-bar filters hide already-resolved relics
while leaving them owned, functional, and visible on every other relic surface.
The contextual mode filters during combat and combat pile overlays but restores
the full bar in the deck/library view and on the act map; the forced mode keeps
the filtered bar throughout the active run. The category includes
already-resolved max-HP relics, permanent inventory upgrades, card-reward
upgrades, and other relics whose effects do not need combat-bar attention.

Right-click an owned relic, a card in a passive pile view, a compendium relic,
a card or relic in run history, or the run-history Campfires summary to pin its
complete native tooltip set. The game's compact top-panel lock icon appears on
the pinned item, and the tooltip remains visible and mouse-interactive after the
pointer leaves it. Right-clicking the locked item again always unlocks it. Pointer
movement is allowed so inline help can be inspected. Each pinned panel blocks
covered game elements from raising their own hover tooltips, while uncovered
elements remain normally hoverable. Every SpireLens panel keeps
a camera button in the same header position so pinning never changes the
tooltip's measured size. Once pinned, that button captures the selected card or
relic together with its complete native and SpireLens tooltip set in their exact
visible arrangement, then places that viewport crop directly on the Windows
clipboard without a temporary file or external process. Clicking that button
preserves the pin; any other mouse click or wheel
action, key press, or controller action removes the pin and continues to the
game normally.

Run history also has a deck icon beside its Cards section. It opens the
selected player's final deck in the normal deck viewer, with duplicate cards
kept as separate, inspectable card instances. A Campfires row beneath the act
history uses the native campfire icon; hovering it lists that player's campfire
choices and recorded outcomes in chronological floor order, including actual
HP healed, cards upgraded/removed/cloned/transformed, relics dug up, Max HP
gained, Lift progress, and Pumpkin Candle charges rekindled.

The relic compendium's **Edit combat relevance** mode shows each discovered
relic's classification with the game's enemy-map icon for combat or top-bar
map icon for non-combat. Inspecting a discovered relic opens explicit **Combat**
and **Non-combat** radio buttons on the full relic inspection screen. Combat
relics also have an **Always / Until turn 1 / Until turn 2 / Until turn 3**
dropdown. A finite relic moves to effective non-combat status for the rest of
the combat as soon as its native activation fires; the selected turn is also
an exclusive fallback cutoff for non-flashing activations and Core reloads.
For example, **Until turn 2** shows the relic on turn 1 and no later than the
start of turn 2. Relics with a native terminal combat state likewise leave the
filtered bar as soon as their one available activation fires—or, for Lava
Lamp, as soon as qualifying damage permanently disqualifies its reward upgrade
for that combat—even when their duration remains **Always**. Limited-use relics
become effectively non-combat when the game reports that all uses are spent.
These runtime transitions do not change the saved classification for the next
combat. The same assignment controls appear
when inspecting an owned relic from the in-run relic bar. The duration dropdown
stays available for both categories; choosing a duration also selects Combat.
Changes apply immediately and are saved under
`user://SpireLens/relic-classifications.json`. Run
`scripts/sync-relic-classifications.ps1` to promote the working file into the
repository's shipped default.

The same relic-view mode dropdown includes **Icon glossary**. It replaces the
relic grid with the SpireLens symbol vocabulary and its definitions; the
glossary and inline stat-row symbols both render from the cached definitions in
`Core/Config/stat-concepts.json`. Concept symbols use Godot's native rich-text
hover hints, so pinned relic stats can expose short concept help without
creating another tooltip page.

The relic compendium's charge taxonomy is maintained in
`Core/Config/relic-taxonomy.json`. Its nested objects mirror the category tree,
and its top-level `uncategorized` list contains every relic not assigned to a
leaf category. Every relic ID appears exactly once and each list is alphabetical.
Move IDs between lists, then rebuild and hot-reload the Core to apply changes;
display names remain defined in `Core/RelicTaxonomy.cs`.

Turning stats off closes the current native hover-tip set and skips aggregate
lookup and SpireLens hover-tip construction while stats are hidden; attribution
continues in the background. Controller Left Trigger remains the game's Draw
Pile input, and Left Stick press remains Peek, so neither is claimed by
SpireLens.

A separate, default-off **"SpireLens: card stats"** checkbox controls per-card
native hover-tip entries on every supported card surface, including the deck,
hand, combat piles, and run history. It changes presentation only: card
attribution continues to be recorded while the checkbox is off. Within it,
**"Show card stats when hovering the hand"** (default on) can drop just the
hand hovers, and hand hovers stay compact unless **"Full card stats in hand"**
is enabled.

**"Pause card tracking during combat"** is different: it is a performance
switch that stops recording card attribution while a combat is in progress, so
card stats for those fights are not collected at all.

**"Show cards not in deck"** switches the native deck screen to a separate
SpireLens collection: every current deck card leaves the grid, and removed
physical cards plus pooled meta-cards take their place. Removed cards retain
their individual run stats and removal marker. Meta-cards such as Shiv, Soul,
Sovereign Blade, and encountered Status cards aggregate every observed instance
of that generated card family into one inspectable card. By default a meta-card
enters this view after it appears during the run; **"Show all meta-cards in
\"not in deck\" view"** also renders every supported meta-card and every Status
card in the current game database with zeroed stats before it is encountered.
Both choices persist through the mod configuration.

A separate, default-off **"Show monster stats"** checkbox in the SpireLens menu controls combat monster hover popups when general stats are enabled. Keeping it off bypasses enemy aggregate lookup and tooltip construction on creature focus while leaving card and relic stats enabled.

The deck viewer's menu shortcut is injected only into the in-run deck viewer for now (not Compendium - lifetime aggregation is deferred, see [issue #2](https://github.com/romaine-life/spirelens/issues/2)); the Left Shift / R3 hotkeys reach the same menu everywhere.

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| **M1** | Attack damage attribution - the 4 numbers above | OK [#5](https://github.com/romaine-life/spirelens/issues/5) |
| **M2a** | Intended block (how much this card contributed) | OK |
| **M2b** | Block absorption (effective vs wasted) - needs heuristic | [#14](https://github.com/romaine-life/spirelens/issues/14) |
| **M3** | Utility card closure (energy spent, draw count) | OK [#7](https://github.com/romaine-life/spirelens/issues/7) |
| **M4** | In-game UI: SpireLens stats controls on deck view | OK [#8](https://github.com/romaine-life/spirelens/issues/8) |
| **M5a** | Removed-card viewing in deck view | OK |
| **M5b** | Run History integration - browse past-run stats | [#9](https://github.com/romaine-life/spirelens/issues/9) |
| **M6** | Publish v0.1 to Nexus | - |

Additional shipped: discard count, pile-top placements (from hand / from discard), exhaust-others attribution, self-exhaust count, current/max-HP lost from card costs, cards-drawn attribution, Death March's live non-opening-hand draw count for the current turn, blocked-draw attempt/reason tracking, Regent star-resource tracking, forge granted tracking, observed card-sourced orb creation and exact passive/evoke/fizzle lifecycles with separate Frost block and Lightning damage, observed Alchemize potion gains/failures with rarity splits, observed Jack of All Trades colorless-card additions with rarity/type and average-cost splits, observed Discovery selections with rarity/type and average-energy-discount splits, All for One's successful zero-cost discard-to-hand returns and per-play/combat averages, Debt and Seal of Gold triggers with attempted, actual, and blocked gold loss, Seal of Gold's generated-energy total and per-combat average, Art of War's observed energy total, held-turn/combat averages, and live turn/combat energy, Cracked Core's and Infused Core's exact starting-Lightning passive, evoke, and fizzle lifecycles plus observed damage outcomes, Reptile Trinket's activation rates and exact-two/over-two per-turn distribution, Pendulum's observed draws and held-combat average, Mummified Hand trigger/discount efficiency with discounted-card type and rarity tracking, Ruined Helmet's observed bonus Strength total with per-activation and held-combat averages, Daughter of the Wind's and Ripple Basin's observed block totals with held-turn/combat averages, observed max-HP pickup tracking for Strawberry, Pear, Mango, and Nutritious Oyster, Gnarled Hammer's observed Sharp-enchanted card list, Stone Humidifier's observed max-HP gains with per-activation before/after snapshots, Sturdy Clamp's retained/capped block averages per turn and combat, Pael's Claw's Goopy play rates and earned enhancements per Goopy card, Pael's Eye's observed exhaust totals, per-combat rate, and activation timing, recurring summon-to-hand tracking, effect application summaries, Artifact-blocked debuff tracking, downstream poison damage attribution including stacked Noxious Fumes contributor preservation, Dowsing Rod's live `?`-room countdown, Fishing Rod's ordered list of cards actually upgraded, and Molten/Toxic/Frozen Egg counts for matching upgraded cards offered across rewards and shops.
Ornamental Fan additionally owns its gained, effective, and wasted block
attribution, with zero-inclusive held-turn and held-combat averages.
Kunai and Shuriken share one canonical three-Attack scaling presentation: the
same lifetime, live-turn, live-combat, zero-inclusive average activation, and
turn-end charge rows, differing only in Dexterity versus Strength gained.
Ornamental Fan also shows live activation counts for the current turn and
combat; fixed-value per-activation gain rows are omitted.
Meat on the Bone records the HP it actually restores plus the signed average
raw HP difference and signed average percentage-point difference from 50%
(negative below, positive above) immediately before each qualifying combat-end
trigger.
Lead Paperweight records its pickup floor, both concrete Colorless card
options, and which option actually resulted in a permanent-deck addition.
Screaming Flagon records every empty-hand activation, its observed AOE damage
split, and average turn-end hand size per held turn and combat.
Fishing Rod also records average floor travel to the next qualifying normal
combat and to each successful card upgrade.
Potion Belt, Alchemical Coffer, and Phial Holster record the average number of
potions held at combat start while each relic is owned.
Petrified Toad records Potion Shaped Rocks successfully given and attempts
blocked specifically by a full potion belt.
Pumpkin Candle records its Ancient energy contribution, average combat-start
charges, and campfire rekindles.
Girya records the Strength it actually adds at combat entry, Attack plays and
resolved enemy hits while that Strength is active, the average saved Lift count
across every floor held, and average floor travel from acquisition to the first
Lift and between later Lifts.
Lasting Candy records each exact Power it adds to a combat card reward and
whether that option was taken or rejected, with Uncommon/Rare splits and
activation counts for Elite and Boss combat rewards.
Gremlin Horn records its exact activations, observed Energy and card draws,
plus average activations across zero-inclusive held turns and combats.
Small Capsule records the exact relic rolled on its reward screen and whether
that same reward was taken or left behind, with the relic remaining hoverable
in the SpireLens tooltip.
Toy Box records each wax relic it bestows, the floor where each relic melts,
and average floors until melting. Wax relic hovers retain their ordinary relic
stats and append their own bestowed/melted lifecycle rows.
Musical Box records successfully created Attacks by rarity, averages them over
all held turns and combats, and counts its exact generated Attacks that later
exhaust because of Ethereal.
Crossbow records only Attacks that successfully enter combat, splits them by
rarity, and averages both Attack gains and their observed energy discount over
its zero-inclusive held turns and combats.
Game Piece records qualifying Power plays, cards its direct draw actually adds
to hand, draws that were blocked, and average observed cards drawn per held
turn and combat.

Drain Power additionally tracks its observed discard-pile upgrades and later
plays of the exact combat cards it upgraded, with held-turn and held-combat
averages per physical Drain Power.
War Hammer records each Elite-victory activation, every permanent deck card it
actually upgraded, and later plays of those exact physical cards, with
per-activation upgrade value and held-turn/combat play averages.
Book of Five Rings records permanent-deck additions, cards added per held
floor, five-card healing triggers and observed healing outcomes, and skipped
card rewards.

Run outcome detection (win/loss/abandoned) is implemented ([#10](https://github.com/romaine-life/spirelens/issues/10), closed) via the run-history entry hook.

## Storage

Per-run JSON files at `%APPDATA%/SlayTheSpire2/SpireLens/runs/<run-id>.json` (Godot's `user://` path). Contains aggregated stats (fast for UI), ordered potion provenance, and a full event log (one entry per card-played / damage-received / card-upgraded / block-gained / card-removed event, for future analysis). The on-disk shape evolves additively and is detected structurally on load: files containing `instance_numbers_by_def` or `def_counters` use the per-instance shape, while older pooled-shape files lack both fields. Pooled-shape files are history-only; per-instance files remain resumable under the current loader. Session preferences are stored in the BaseLib-backed mod configuration.

## Requirements

- Slay the Spire 2 (tested against v0.103.2)
- [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) - required dependency

> [!NOTE]
> **Active Environment Reference (August 1, 2026)**
>
> The mod is built against **Slay the Spire 2 (v0.110.1)** on local dev setups with the following mod stack configuration:
> - **BaseLib** (`v3.4.0+`) — Modding utility dependency
> - **SpireLens** (`v0.0.0` loader / `v1.0.0` core) — Per-card stats attribution mod
> - **SpireLens MCP** (`v0.3.4`) — MCP bridge running on port `15526` (`http://localhost:15526/`)

## Install

Drop the mod output into `<game install>/mods/` (including `SpireLens.dll`, `SpireLens.json`, and the `.pck` when present). Requires BaseLib.

## Build from source

**Prereqs:** .NET 9 SDK, Slay the Spire 2 installed locally.

```sh
# If the path discovery in Sts2PathDiscovery.props doesn't find your game,
# create Directory.Build.props with your path:
cat > Directory.Build.props <<'XML'
<Project>
    <PropertyGroup>
        <Sts2Path>D:/SteamLibrary/steamapps/common/Slay the Spire 2</Sts2Path>
    </PropertyGroup>
</Project>
XML

dotnet build -c Release
```

The build's `CopyToModsFolderOnBuild` target auto-deploys the `.dll` and manifest to `<game>/mods/SpireLens/`. No manual copy step.

## Credits

- Scaffolded from [Alchyr/ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2)
- Concept inspired by the gap between pick-rate trackers and actual-impact tracking
- BaseLib by [Alchyr](https://www.nexusmods.com/slaythespire2/mods/103)

## License

MIT.
