These fixture files pin on-disk run-file shapes that the mod has written. The
`v*-` prefix in each filename is historical — it dates the fixture to the
schema version that existed when it was added. Versions are no longer used at
runtime; the loader detects the per-instance vs. pooled shape structurally.
New fixtures added going forward do not need a `v*-` prefix.

- `defect-orb-family-run.json`
  Adds Plasma energy to exact card-sourced orb outcomes and recurring Defect
  Power orb outcomes for cards such as Storm, Spinner, Trash to Treasure, and
  Lightning Rod.

- `v1-pooled-run.json`
  Pooled shape. Aggregates are keyed by card definition id and do not carry
  the per-instance resume metadata introduced later. Loads as historical-only;
  cannot rebuild live state.
- `v2-per-instance-run.json`
  Earliest per-instance shape. Aggregates are keyed by per-instance card id
  and include the resume-only snapshots (`instance_numbers_by_def`,
  `def_counters`) needed to rebuild numbering after hot reload.
- `v3-per-instance-effects-run.json`
  Per-instance shape extended with applied-effect summaries nested under each
  card aggregate.
- `v4-per-instance-effects-exhaust-run.json`
  Adds the per-card "times exhausted" count.
- `v5-per-instance-block-ledger-run.json`
  Adds absorbed/wasted block aggregates.
- `v6-per-instance-artifact-block-run.json`
  Adds per-effect Artifact-blocked debuff counters.
- `v7-per-instance-poison-damage-run.json`
  Adds per-effect downstream damage and overkill counters so dedicated poison
  rows can report observed poison outcomes, not just poison applied.
- `v8-per-instance-regent-stars-run.json`
  Adds Regent star-resource spend/gain fields alongside the existing energy
  and per-instance attribution data.
- `v9-per-instance-blocked-draw-run.json`
  Adds per-card blocked-draw attribution counts.
- `v9-per-instance-forge-run.json`
  Per-card forge granted tracking from the forge-tracking branch (added in
  parallel with the v9 blocked-draw work).
- `v10-per-instance-forge-run.json`
  Per-card forge granted tracking on top of the v9 blocked-draw fields.
- `v11-per-instance-no-draw-blocked-run.json`
  Adds per-effect downstream blocked-draw counts so powers like No Draw can
  report how many cards they actually prevented from being drawn.
- `v12-per-instance-draw-attempt-gap-run.json`
  Adds per-card attempted draw counts so draw cards can show what they tried
  to draw versus what actually landed.
- `v13-per-instance-blocked-draw-reasons-run.json`
  Adds categorized blocked-draw reasons so draw cards can say why missing
  draws were prevented.
- `v14-per-instance-make-it-so-run.json`
  Adds per-card summon-to-hand tracking for cards like `Make It So`.
- `v15-bag-of-marbles-run.json`
  Adds relic aggregate storage for Bag of Marbles combat-start Vulnerable
  tracking.
- `v16-red-mask-run.json`
  Adds Red Mask Weak tracking to relic aggregates.
- `v17-orichalcum-run.json`
  Adds Orichalcum additional block gained tracking to relic aggregates.
- `v18-pocketwatch-run.json`
  Adds Pocketwatch additional cards drawn tracking to relic aggregates.
- `v19-book-repair-knife-run.json`
  Adds Book Repair Knife confirmed per-enemy Doom trigger, kill-payload, and
  healing attribution tracking to relic aggregates.
- `happy-flower-energy-average-run.json`
  Adds Happy Flower held-combat tracking as the denominator for average energy
  generated per combat.
- `v20-bone-flute-run.json`
  Adds Bone Flute owned-Osty trigger tracking alongside actual block gained in
  relic aggregates.
- `v21-unleash-osty-hp-run.json`
  Adds Unleash-specific Osty current-HP attack bonus tracking to card
  aggregates.
- `v22-osty-summon-body-run.json`
  Adds card-sourced Osty summon HP to card aggregates and run-level Osty
  absorbed damage to meta stats.
- `v23-replay-extra-plays-run.json`
  Adds per-card replay extra-play tracking, where total plays still includes
  all plays and this field counts the subset with nonzero play-series index.
- `v24-replay-source-breakdown-run.json`
  Adds per-card replay extra-play source breakdowns, preserving both the total
  extra-play count and the observed source counts when the game exposes them.
- `meal-ticket-relic-run.json`
  Adds relic activation tracking plus Meal Ticket shop-entry healing
  attribution, including restored healing and full-HP lost healing.
- `burning-blood-relic-run.json`
  Adds Burning Blood combat-victory activation tracking and relic healing
  attribution, including restored healing and full-HP lost healing.
- `chosen-cheese-relic-run.json`
  Adds Chosen Cheese max-HP gain tracking: starting max HP at pickup plus
  observed max HP gained. It intentionally omits a resulting max-HP snapshot
  because unrelated max-HP effects can interleave between Chosen Cheese gains.
- `white-beast-statue-relic-run.json`
  Adds White Beast Statue potion-gained tracking with common, uncommon, and
  rare potion rarity splits, plus skipped White Beast potion reward tracking.
- `alchemize-card-run.json`
  Adds Alchemize potion procurement tracking with successful common, uncommon,
  and rare potion splits plus failed procure results.
- `armaments-card-run.json`
  Adds the count of successful card upgrades caused by each physical
  Armaments, including every hand card actually upgraded by Armaments+.
- `card-orbs-created-run.json`
  Adds successfully channeled orbs to the physical card aggregate, preserves
  exact-orb lifecycle outcomes by type, and keeps Frost block separate from
  the source card's direct block totals.
- `ball-lightning-orb-damage-run.json`
  Adds the observed attempted/dealt/blocked/overkill/kill/target damage split
  from Ball Lightning's exact channeled Lightning orb, kept separate from the
  card's direct Attack damage totals.
- `jack-of-all-trades-card-run.json`
  Adds Jack of All Trades generated colorless-card totals, uncommon/rare and
  attack/skill/power splits, plus the numerator for average added-card cost.
- `discovery-card-run.json`
  Adds Discovery picked-card totals, common/uncommon/rare and
  attack/skill/power splits, plus the numerator for average energy discount.
- `splash-card-run.json`
  Adds Splash selected-Attack totals, common/uncommon/rare splits, and the
  numerator for average observed energy discount.
- `random-card-generation-run.json`
  Adds the shared successful-arrival, exact generated-card utilization,
  rarity/type/cost/discount/destination, and per-definition ledger shape for
  direct random generators and recurring shared Power generators.
- `feed-card-run.json`
  Adds Feed's observed maximum-HP gain to the physical card aggregate after
  its Fatal play callback completes successfully.
- `juggling-power-run.json`
  Adds a power-ID-keyed Juggling aggregate with confirmed Attack copies,
  rarity splits, and active turn/combat denominators. The related Juggling
  card projects this shared power data rather than owning the counters.
- `vicious-power-run.json`
  Adds a power-ID-keyed Vicious aggregate with the cards confirmed by its
  owner-applied-Vulnerable draw commands. Every Vicious card projects this
  shared power total.
- `outbreak-card-run.json`
  Adds per-instance Outbreak effective damage observed from the explicit
  Poison triggers made by that physical card play. Ordinary side-turn Poison
  damage remains separate.
- `stampede-power-run.json`
  Adds a power-ID-keyed Stampede aggregate with confirmed direct Attack
  autoplays, rarity splits, and the resolved energy those free plays saved.
  Every Stampede card projects this shared power total.
- `aggression-power-run.json`
  Adds a power-ID-keyed Aggression aggregate with successful discard-to-hand
  Attack moves and separately confirmed upgrades. Every Aggression card
  projects these shared power totals.
- `rupture-power-run.json`
  Adds a power-ID-keyed Rupture aggregate with observed Strength gained and
  its zero-inclusive active-turn denominator. Every Rupture card projects the
  shared total and per-active-turn average.
- `feel-no-pain-power-run.json`
  Adds a power-ID-keyed Feel No Pain aggregate with observed post-modifier
  block and active-turn denominator data. Every Feel No Pain card projects
  the shared block-per-active-turn value.
- `buffer-power-run.json`
  Adds a power-ID-keyed Buffer aggregate with charges granted, charges the
  power confirmed spending, and the observed post-Block HP loss those charges
  zeroed. Keyed by the canonical `POWER.BUFFER_POWER` id. Every Buffer card
  projects these shared totals; charges that never fired are granted minus
  used.
- `buffer-charge-ledger-run.json`
  Adds per-source Buffer provenance on top of the pooled aggregate: charges
  granted, charges used, and HP loss prevented on both a physical Buffer card
  and a used Lucky Tonic. The pooled power total stays the family sum, so it
  is greater than either contributor alone.
- `entropy-power-run.json`
  Adds a power-ID-keyed Entropy aggregate with confirmed replacement-card
  rarities, Bound cards transformed, and a zero-inclusive active-combat
  denominator. The related Entropy card projects this shared power data.
- `danse-macabre-power-run.json`
  Adds a power-ID-keyed Danse Macabre aggregate with observed triggers,
  post-modifier block gained, and active turn/combat denominators. The related
  Danse Macabre card projects this shared power data.
- `meta-power-registry-run.json`
  Adds the shared meta-power play/application counts, permanent-deck,
  active-turn, and active-application-turn denominators, plus matching
  observation-era outcome numerators. These records back the canonical
  synthetic Power cards in the not-in-deck view.
- `unrelenting-free-attack-power-run.json`
  Adds a power-ID-keyed Free Attack aggregate with charges granted and used,
  observed energy savings, zero-savings uses, and discounted Attack rarity
  splits. Every Unrelenting card projects this shared power data.
- `pounce-free-skill-power-run.json`
  Adds Pounce's symmetric power-ID-keyed Free Skill aggregate with charges
  granted and used, observed energy savings, zero-savings uses, and discounted
  Skill rarity splits. Every Pounce card projects this shared power data.
- `debt-card-run.json`
  Adds Debt end-of-turn trigger tracking with observed gold lost and the
  unaffordable portion blocked by the player being out of gold.
- `normality-card-run.json`
  Adds per-instance Normality turns ended in hand and the unspent-energy
  numerator used for the qualifying-turn average.
- `seal-of-gold-relic-run.json`
  Adds Seal of Gold activation, observed gold-loss outcome, energy generated,
  and held-combat denominator tracking for its energy-per-combat average.
- `phylactery-relic-run.json`
  Adds Bound Phylactery and Phylactery Unbound activation tracking plus actual
  Osty summon HP gained from the shared summon command result.
- `phylactery-osty-body-run.json`
  Adds the shared, upgrade-continuous Osty-body ledger surfaced by both
  Phylactery forms: all successful summon HP, Unleash-play HP, absorbed damage,
  and zero-inclusive held turn/combat denominators.
- `v32-enemy-damage-run.json`
  Adds per-enemy observed damage output aggregates: attempted damage, HP damage
  dealt, and damage blocked by player block.
- `enemy-status-pollution-run.json`
  Adds run-level enemy aggregates for enemy damage dealt to the player and
  status cards that enemies actually add, split by destination pile and status
  card id.
- `open-branch-relic-stats-run.json`
  Adds consolidated relic aggregates rescued from stale open branches: Anchor,
  Letter Opener, Blood Vial, Akabeko, Booming Conch, Pendulum, Parrying
  Shield, Horn Cleat, and Toolbox. Pendulum includes held-combat denominators
  for its cards-drawn average.
- `letter-opener-relic-run.json`
  Adds Letter Opener combat, turn, and skill-play denominators for average
  attempted damage, plus turn-end 1/2 charge buckets.
- `permafrost-combat-average-run.json`
  Adds Permafrost's held-combat denominator for its zero-inclusive average
  trigger rate.
- `bronze-scales-relic-run.json`
  Adds Bronze Scales Thorns damage tracking with observed damage, blocked
  damage, overkill, kills, and target count.
- `candelabra-relic-run.json`
  Adds Candelabra activation tracking, observed energy generated, and count of
  second player turns that ended with excess energy while it was held.
- `turn-energy-relics-run.json`
  Adds shared Lantern, Very Hot Cocoa, Candelabra, and Chandelier turn-energy
  relic tracking: activations, observed energy generated, matching turn-end
  excess-energy counts, and missed-energy combat counts for the turn-2/turn-3
  relics.
- `nunchaku-relic-run.json`
  Adds Nunchaku tracking: attacks played, observed energy gained, completed
  combats held for averages, combat-end 8/9 charge counts, and combat-end
  charge total for average charge.
- `pen-nib-relic-run.json`
  Adds Pen Nib tracking: activations, total base damage added, attack-play
  count for the average base damage per attack, turn-end 8/9 charge counts,
  and turn-end charge samples for average charge.
- `iron-club-relic-run.json`
  Adds Iron Club tracking: actual cards drawn, completed combats held for the
  average cards-drawn rate, combat-end 0/1/2/3 charge counts, and explicit
  combat-end charge samples for average charge.
- `pendulum-combat-end-charge-run.json`
  Adds Pendulum combat-end 0/1/2 charge counts and explicit combat-end charge
  samples for average charge.
- `pendulum-activation-turn-run.json`
  Adds Pendulum activation-turn total and samples for the average turn it
  activated on. Samples count activations, not combats, so combats too short
  for it to activate stay out of the average.
- `paels-wing-sacrifice-relic-run.json`
  Adds Pael's Wing sacrifice tracking: consumed card reward options split by
  common, uncommon, and rare, sacrifices made and skipped, and the total and
  specific relics gained from its completed sacrifice pairs.
- `paels-tooth-relic-run.json`
  Adds Pael's Tooth returned-card history in observed return order, preserving
  duplicate definitions, final display names, post-return upgrade levels, and
  the floors climbed before each card was returned.
- `paels-eye-relic-run.json`
  Adds Pael's Eye activation tracking, observed total/Strike-and-Defend/Status/
  Curse exhaust counts, activation-turn samples, and held-combat denominators
  including combats where it did not activate.
- `strike-dummy-relic-run.json`
  Adds Strike Dummy tracked Strike-card plays since pickup plus current
  permanent-deck counts for base Strikes and non-base Strike-tagged cards.
- `unsettling-lamp-relic-run.json`
  Adds Unsettling Lamp debuff tracking: combats held, fixed Vulnerable and
  Weak totals, and dynamic per-effect buckets for other debuffs it doubles.
- `nutritious-soup-relic-run.json`
  Adds Nutritious Soup tracking for combats held plus finished plays of basic
  Strike-tagged cards carrying the Tezcataras Ember enchantment while the relic
  was held.
- `vajra-relic-run.json`
  Adds Vajra tracking for attack cards played while held plus actual enemy
  damage hits from those attacks, with multi-hit attacks counted per hit.
- `ember-tea-relic-run.json`
  Adds active-charge-only Ember Tea attack plays and observed enemy hits plus
  active turn/combat denominators. Its fifth combat remains active even though
  consuming that charge immediately leaves the visible counter at zero.
- `red-skull-relic-run.json`
  Adds Red Skull attack plays and observed enemy hits while its Strength is
  active, plus distinct active-turn and active-combat denominators.
- `toasty-mittens-relic-run.json`
  Adds Toasty Mittens' completed direct card exhausts, observed Strength
  applications, and zero-inclusive held-combat denominator.
- `paper-phrog-relic-run.json`
  Adds Paper Phrog tracking for bonus damage added by its Vulnerable multiplier,
  vulnerable-enhanced attack counts, and held combat/turn denominators for
  average rows.
- `razor-tooth-relic-run.json`
  Adds Razor Tooth held combat/turn denominators plus later finished plays and
  successful draws of the exact combat cards it was observed upgrading.
- `stone-cracker-play-tracking-run.json`
  Adds Stone Cracker's observed common/uncommon/rare upgrade counts, later
  finished plays of those exact combat cards, and held turn/combat
  denominators for zero-inclusive play averages.
- `storybook-relic-run.json`
  Adds Brightest Flame's observed max-HP-loss card aggregate, which Storybook
  surfaces through a definition-pooled view with play, draw, energy, and
  card-draw stats.
- `brilliant-scarf-relic-run.json`
  Adds Brilliant Scarf discount tracking: energy-discount offers, taken
  discounts, energy saved by those taken discounts, held combats, and
  discounted card cost buckets including dynamic star costs.
- `brilliant-scarf-turn-average-run.json`
  Adds Brilliant Scarf's zero-inclusive held-turn denominator for average
  energy saved per turn.
- `darkstone-periapt-relic-run.json`
  Adds Darkstone Periapt curse acquisition tracking plus observed max HP
  gained from the relic's owner-specific curse-to-deck callback, and
  original/new max-HP snapshots.
- `lucky-fysh-relic-run.json`
  Adds Lucky Fysh permanent-deck additions and the actual gold gained from
  those owner-specific callbacks.
- `lucky-fysh-room-averages-run.json`
  Adds curses among those permanent-deck additions, the additions and the gold
  they paid out split by the room each was observed in, and the once-per-floor
  held-room counts and floor markers that supply the
  per-combat/shop/event/campfire denominators.
- `maw-bank-relic-run.json`
  Adds Maw Bank room-entry activations, observed gold gained, completed shops
  skipped without spending, gold spent outside shops while active, and the
  persisted in-progress shop floor.
- `old-coin-relic-run.json`
  Adds Old Coin's observed gold grant, the amount of that grant later spent,
  and the persisted FIFO gold-provenance ledger used across floors and reloads.
- `membership-card-relic-run.json`
  Adds Membership Card's discounted-purchase activations, the marginal gold its
  discount saved, the balance held immediately after the relic was obtained,
  and the gold earned since then.
- `book-of-five-rings-relic-run.json`
  Adds Book of Five Rings permanent-deck additions, held-floor rate context,
  five-card healing triggers and outcomes, and skipped card rewards.
- `signet-ring-relic-run.json`
  Adds Signet Ring's observed floor distance from pickup to the first merchant
  room reached afterward.
- `shovel-relic-run.json`
  Adds Shovel Dig tracking: total relics acquired plus common, uncommon, and
  rare rarity splits from the actual obtained relic instances, plus campfires
  where Dig was available but not used.
- `tiny-mailbox-relic-run.json`
  Adds Tiny Mailbox rest-heal activations, exact potion offers and selections,
  offer rarity splits, Fruit Juice offers, and campfires where Rest was
  available but not used.
- `juzu-bracelet-relic-run.json`
  Adds Juzu Bracelet tracking for map `?` sites entered while the relic was
  held.
- `dowsing-rod-relic-run.json`
  Adds Dowsing Rod's current `?`-room countdown, derived from the saved Dowsing
  quest card state.
- `cursed-pearl-relic-run.json`
  Adds Cursed Pearl tracking for floors ascended before the first shop while
  held, plus the stats for the Greed curse granted by the relic.
- `gambling-chip-relic-run.json`
  Adds Gambling Chip combat-start discard tracking: combats triggered and
  cards actually discarded so the tooltip can show total and average discarded
  per combat.
- `centennial-puzzle-relic-run.json`
  Adds Centennial Puzzle activation tracking, actual cards drawn, average
  activation turn, active-side buckets, and mutually exclusive Status, Curse,
  and enemy-source activation buckets.
- `regal-pillow-relic-run.json`
  Adds Regal Pillow rest-site bonus healing tracking: activations, effective
  bonus HP healed, and bonus healing lost to full HP.
- `lizard-tail-relic-run.json`
  Adds Lizard Tail pickup and activation floor tracking plus observed HP healed
  by the one-shot revive.
- `pantograph-relic-run.json`
  Adds Pantograph boss-combat healing tracking: activations, effective HP
  healed, and healing wasted to full HP.
- `precarious-shears-relic-run.json`
  Adds Precarious Shears pickup tracking: removed card names plus max HP before
  and after the relic's max-HP cost, stored in the common original/new max-HP
  format as well as the legacy field names.
- `leafy-poultice-relic-run.json`
  Adds Leafy Poultice pickup tracking: original/new max HP after the relic's
  max-HP loss resolves, plus the two source/result card transform pairs.
- `fresnel-lens-relic-run.json`
  Adds Fresnel Lens's Drowning Beacon max-HP loss snapshots, successful Nimble
  card picks, any-Nimble / exact-two / three-or-more reward-screen counts, the
  no-Nimble count, and rewards where Nimble was offered but none was taken.
- `wing-charm-relic-run.json`
  Adds Wing Charm's Swift-card taken/not-taken outcomes and offered-card
  rarity breakdown, sourced from the exact reward option modified by the relic.
- `silver-crucible-relic-run.json`
  Adds Silver Crucible's ordered first, second, and third card-reward screens,
  including every offered card's displayed upgrade state and explicit
  taken/not-taken outcome.
- `orrery-relic-run.json`
  Adds Orrery's five ordered card rewards, including their final skipped,
  obtained-card, Pael-sacrifice, or still-pending handling and the offered-card
  signatures used for same-floor hot-reload recovery.
- `strawberry-relic-run.json`
  Adds Strawberry pickup tracking: activations, observed max HP gained, and
  original/new max-HP snapshots.
- `pear-relic-run.json`
  Adds Pear pickup tracking: activations, observed max HP gained, and
  original/new max-HP snapshots.
- `mango-relic-run.json`
  Adds Mango pickup tracking: activations, observed max HP gained, and
  original/new max-HP snapshots.
- `nutritious-oyster-relic-run.json`
  Adds Nutritious Oyster pickup tracking: activations, observed max HP gained,
  and original/new max-HP snapshots.
- `stone-humidifier-relic-run.json`
  Adds Stone Humidifier rest-site trigger tracking: observed max HP gained and
  an ordered starting/resulting max-HP snapshot for every activation.
- `sturdy-clamp-relic-run.json`
  Adds Sturdy Clamp's observed retained block, pre-cap excess block, and the
  turn/combat denominators used by its four average rows.
- `beating-remnant-relic-run.json`
  Adds Beating Remnant's observed prevented HP loss plus zero-inclusive held
  turn/combat denominators for its total and average rows.
- `ruined-helmet-relic-run.json`
  Adds Ruined Helmet's confirmed activation count, observed bonus Strength, and
  held-combat denominator for its per-activation and per-combat average rows.
- `daughter-of-the-wind-relic-run.json`
  Adds Daughter of the Wind's observed block gain plus held turn/combat
  denominators for its total and average rows.
- `art-of-war-relic-run.json`
  Adds Art of War's observed energy gain plus held turn/combat denominators
  for its total and average rows.
- `cracked-core-relic-run.json`
  Adds lifecycle tracking for the exact Lightning orb Cracked Core channels:
  completed evokes, passive triggers, non-evoke slot-removal fizzles, and the
  observed attempted/dealt/blocked/overkill/kill/target damage split from that
  exact orb.
- `symbiotic-virus-relic-run.json`
  Adds lifecycle tracking for the exact Dark orb Symbiotic Virus channels:
  completed evokes, passive triggers, and non-evoke slot-removal fizzles.
- `bing-bong-relic-run.json`
  Adds Bing Bong's successful permanent-deck duplicate count, split into
  non-Curse Common/Uncommon/Rare cards and a mutually exclusive Curse bucket.
- `gold-plated-cables-relic-run.json`
  Adds Gold-Plated Cables' confirmed activations with an orb, exact activation
  counts by orb type, and player turn ends where no orb was available.
- `reptile-trinket-rates-run.json`
  Adds Reptile Trinket's held turn/combat denominators plus mutually exclusive
  turn buckets for exactly two and more than two activations.
- `paels-claw-relic-run.json`
  Adds Pael's Claw's finished Goopy-card plays, observed earned Goopy
  enhancements, enchanted-card count, and held turn/combat denominators.
- `sand-castle-relic-run.json`
  Adds Sand Castle pickup tracking: the actual cards upgraded by the relic.
- `fragrant-mushroom-relic-run.json`
  Adds Fragrant Mushroom pickup tracking: the actual cards upgraded by the relic
  plus observed current HP before and after its pickup damage.
- `fishing-rod-relic-run.json`
  Adds Fishing Rod tracking: every card actually upgraded at its three-combat
  interval, retained in upgrade order.
- `fishing-rod-floor-averages-run.json`
  Adds Fishing Rod's completed floor-distance samples from acquisition and
  between qualifying normal combats and successful card upgrades.
- `war-hammer-relic-run.json`
  Adds War Hammer's Elite-victory activations, observed permanent-deck
  upgrades, stable upgraded-card instance IDs, later upgraded-card plays, and
  held turn/combat denominators.
- `sword-in-the-stone-relic-run.json`
  Adds Sword in the Stone's acquisition floor, ordered Elite-victory history,
  and the observed Strength activations/gains retained after Sword of Jade
  replaces it.
- `sword-in-the-stone-strength-rates-run.json`
  Adds Sword of Jade's observation-era Strength numerator and zero-inclusive
  held-combat denominator for the shared Strength-relic family presentation.
- `egg-relic-offers-run.json`
  Adds Molten, Toxic, and Frozen Egg tracking: every matching choosable card
  option the egg actually upgraded across rewards, shops, and other offers,
  plus the successfully taken options, including Common, Uncommon, and Rare
  breakdowns for both.
- `hefty-tablet-relic-run.json`
  Adds Hefty Tablet rare-card choice tracking: cards granted by id/name and
  skipped pickup choices.
- `arcane-scroll-relic-run.json`
  Adds Arcane Scroll rare-card grant tracking: the rare card added by id/name.
- `large-capsule-relic-run.json`
  Adds Large Capsule relic grant tracking: relics added by id/name.
- `neows-bones-relic-run.json`
  Adds Neow's Bones tracking: the two relics obtained by id/name plus the
  random curse added by id/name and its card stats.
- `vambrace-relic-run.json`
  Adds Vambrace activation tracking plus extra block gained from the block
  packets its 2x multiplier actually modified.
- `regalite-relic-run.json`
  Adds Regalite tracking for owner-created combat cards, observed block gained,
  and held turn/combat denominators for average block rows.
- `intimidating-helmet-relic-run.json`
  Adds Intimidating Helmet qualifying 2+-Energy card plays, observed block
  gained, and held turn/combat denominators for average block rows.
- `kunai-relic-run.json`
  Adds Kunai attack-counter tracking: owner attack plays, activations, observed
  Dexterity gained, and 1/2 charge turn-end buckets with average charge samples.
- `unlimited-attack-charge-relics-run.json`
  Adds the remaining unlimited three-Attack turn counters: Kusarigama,
  Ornamental Fan, and Shuriken, including observed damage/block/Strength
  outcomes and 1/2 charge turn-end buckets with average charge samples.
  Ornamental Fan additionally preserves turns that ended at 0 charges.
- `three-attack-scaling-rates-run.json`
  Adds the shared Kunai/Shuriken activation-rate window: matching observed
  activations plus zero-inclusive held turn and combat denominators. Both
  relic aggregates deliberately use the same persisted rate fields.
- `ornamental-fan-block-attribution-run.json`
  Adds Ornamental Fan's relic-owned effective/wasted block outcomes, held
  turn/combat denominators, and matching observation-era block numerator.
- `tuning-fork-relic-run.json`
  Adds Tuning Fork owner Skill-play count, trigger count, observed block
  gained, held combat/turn denominators, and turn-end charge buckets.
- `mummified-hand-relic-run.json`
  Adds Mummified Hand trigger costs, observed card discounts, energy-spend to
  discounted-cost ratios, held combat/turn denominators, and recipient
  card-type and rarity counts.
- `dark-embrace-power-run.json`
  Adds Dark Embrace's observed immediate and deferred cards drawn, its active
  turn and active-combat denominators, and all turns in combats where active.
- `consuming-shadow-power-run.json`
  Adds Consuming Shadow's completed end-of-turn orb evocations to its shared
  Power aggregate.
- `burning-sticks-relic-run.json`
  Adds Burning Sticks confirmed duplicate activations, exact generated-card
  plays, duplicate rarity splits, and its held-combat denominator.
- `throwing-axe-relic-run.json`
  Adds Throwing Axe's confirmed extra card plays, play-time energy values,
  rarity splits, and zero-inclusive held-combat denominator.
- `gnarled-hammer-relic-run.json`
  Adds Gnarled Hammer's observed list of permanent deck cards whose Sharp
  enchantment was applied or increased by its pickup effect.
- `silken-tress-relic-run.json`
  Adds the card successfully taken from a reward after Silken Tress applied
  Glam, using the reward option's native modifying-relic provenance.
- `tri-boomerang-relic-run.json`
  Adds Tri-Boomerang's observed Instinct-enchanted permanent-card ledger,
  later plays of those same card instances, and held-combat denominator.
- `ripple-basin-relic-run.json`
  Adds Ripple Basin no-attack turn-end activation tracking plus observed block
  gained and held turn/combat denominators for its block averages.
- `war-paint-relic-run.json`
  Adds War Paint pickup tracking: the actual skill cards upgraded by the relic.
- `unmovable-power-meta-run.json`
  Adds run-level Unmovable power tracking for the extra block produced by the
  power's block multiplier, surfaced on Unmovable card tooltips rather than
  attributed to one physical card instance.
- `drain-power-card-run.json`
  Adds Drain Power's confirmed discard-pile upgrade count, held-turn
  denominator, and later plays of the exact combat cards it upgraded.
- `all-for-one-card-run.json`
  Adds All for One's successfully returned zero-cost discard-pile cards; its
  ordinary play and combat counts provide the two average denominators.
- `soul-pile-card-run.json`
  Adds per-source card tracking for generated or transformed Souls that
  actually arrived in the draw pile, hand, or discard pile.
- `winged-boots-relic-run.json`
  Adds Winged Boots' three numbered off-path destination categories, keyed to
  the relic's own saved use counter.
- `rainbow-ring-relic-run.json`
  Adds Rainbow Ring's confirmed activations plus zero-inclusive held turn and
  combat denominators. Its three current-turn card-type flags remain live
  relic state and are intentionally not persisted.
- `sparkling-rouge-relic-run.json`
  Adds Sparkling Rouge's mutually exclusive completed-combat buckets for turn
  1, turn 2, and turn 3 or later.
- `whispering-earring-relic-run.json`
  Adds Whispering Earring's observed current-HP loss during the first combat
  round plus its zero-inclusive held-combat denominator.
- `tungsten-rod-relic-run.json`
  Adds Tungsten Rod's exact HP-loss prevention, reliable self/Curse/Status/enemy
  source buckets, and zero-inclusive held turn/combat denominators.
- `pocketwatch-turn-stats-run.json`
  Adds Pocketwatch's held turn/combat denominators, turn-end card-count totals,
  missed-threshold turns, and actual-activation card-count samples.
- `pollinous-core-relic-run.json`
  Adds Pollinous Core activations, observed and blocked bonus card draws,
  held turn/combat denominators, and turn-end 0/1/2/3 counter buckets.
- `joss-paper-relic-run.json`
  Adds Joss Paper's observed exhaust count, threshold activations, observed and
  blocked card draws, held turn/combat denominators, and turn-end 0-4 counters.
- `white-star-relic-run.json`
  Adds White Star activations, generated Rare card offers split by
  Attack/Skill/Power, and terminally declined rare-card reward screens.
- `white-star-rare-cards-taken-run.json`
  Adds the Rare cards actually taken from White Star's rewards, split the same
  Attack/Skill/Power way as its offers. Runs written before these counters keep
  only the offer fields, which read as zero taken.
- `oddly-smooth-stone-relic-run.json`
  Adds Oddly Smooth Stone's completed plays of cards the game classifies as
  immediately gaining Dexterity-scaled Block.
- `prayer-wheel-relic-run.json`
  Adds Prayer Wheel's extra reward-screen count, terminal rejections, and
  generated and taken Common/Uncommon/Rare cards.
- `forgotten-soul-relic-run.json`
  Adds Forgotten Soul's same-owner exhaust activations, observed damage
  outcomes, and zero-inclusive held turn/combat denominators.
- `potion-run-history.json`
  Adds ordered per-potion offer, acquisition, use, and held-at-run-end
  provenance for the potion gallery's current-run history view, plus concrete
  rarity and terminal reward-screen rejection for run-wide belt statistics.
- `max-hp-run-history.json`
  Adds chronological observed maximum-HP changes with exact before/after
  values plus floor, room, turn, and best-known source presentation context.
- `hp-run-stats.json`
  Adds run-wide current-HP loss split between combats and events, plus the
  zero-inclusive completed-combat denominator used by HP-loss averages.
- `gold-run-stats.json`
  Adds observed run-wide gold acquisition and game-classified spending,
  context splits, and zero-inclusive shop/event/combat rate denominators.
- `map-legend-run-stats.json`
  Adds per-icon map visit pacing, unknown-site resolutions, combat/elite
  outcomes, and room-attributed HP, gold, card, upgrade, and relic results.
- `run-time-stats.json`
  Adds pause-aware time spent in combats, reward screens, events, and the map,
  plus completed-combat and turn denominators and the resumable sample cursor.
- `eternal-feather-campfire-healing-run.json`
  Adds Eternal Feather's observed restored HP per campfire floor so native
  combined rest-site healing can be split by source in run history.
- `eternal-feather-deck-size-run.json`
  Adds Eternal Feather's observed rest-site deck-size total and its matching
  sample count, the denominator behind the average cards-in-deck-per-rest-site
  row. Samples count activations, so a full-HP rest site still contributes.
- `potion-run-history-turns.json`
  Adds combat-turn timing for potion lifecycle points that occur during
  combat, allowing same-turn acquisitions and uses to share a timeline row.
- `blood-potion-run-history.json`
  Adds Blood Potion's observed current-HP restoration to its exact used
  potion-history entry.
- `swift-potion-run-history.json`
  Adds Swift Potion's observed cards drawn and unfulfilled card draws to its
  exact used potion-history entry.
- `fortifier-potion-run-history.json`
  Adds Fortifier's observed block gain and its later absorbed/wasted
  contributor-ledger outcomes to its exact used potion-history entry.
- `explosive-ampoule-run-history.json`
  Adds Explosive Ampoule's observed attempted, effective, blocked, and
  overkill damage plus kills and targets hit to its exact used potion-history
  entry.
- `potion-slot-relic-combat-start-run.json`
  Adds zero-inclusive combat-start potion-count totals and samples for Potion
  Belt, Alchemical Coffer, and Phial Holster.
- `screaming-flagon-hand-size-run.json`
  Adds Screaming Flagon's turn-end hand-size total plus zero-inclusive held
  turn and combat denominators.
- `petrified-toad-relic-run.json`
  Adds Petrified Toad's successful Potion Shaped Rocks and attempts rejected
  specifically because the potion belt was full.
- `pumpkin-candle-relic-run.json`
  Adds Pumpkin Candle's generated Ancient energy, zero-inclusive combat-start
  charge samples, and selected Kindle campfire options.
- `spiked-gauntlets-relic-run.json`
  Adds Spiked Gauntlets' taxed Power plays, resolved Power costs, actual
  energy spent, zero-inclusive held turn/combat denominators, and generated
  Ancient energy.
- `small-capsule-relic-run.json`
  Adds Small Capsule's concrete rolled relic plus the terminal taken/skipped
  outcome for that exact reward.
- `toy-box-wax-relic-run.json`
  Adds Toy Box's ordered wax-relic ledger with observed bestowed and melted
  floors while preserving each wax relic's ordinary effect aggregate by id.
- `music-box-relic-run.json`
  Adds Musical Box's successfully created Attacks, Common/Uncommon/Rare splits,
  Ethereal exhausts, and zero-inclusive held turn/combat denominators.
- `meat-on-the-bone-pre-trigger-hp-run.json`
  Adds Meat on the Bone's qualifying combat-end signed HP difference from 50%,
  normalized HP percentage used for the signed percentage-point difference,
  and matching pre-trigger sample counts.
- `crossbow-relic-run.json`
  Adds Crossbow's successfully gained Attacks, rarity splits, observed energy
  discount, and zero-inclusive held turn/combat denominators.
- `game-piece-relic-run.json`
  Adds Game Piece's qualifying Power plays, observed and blocked draws, and
  zero-inclusive held turn/combat denominators.
- `card-removal-source-run.json`
  Adds per-physical-card removal source attribution plus the exact gold charge
  for cards removed by the shopkeeper.
- `golden-pearl-relic-run.json`
  Adds Golden Pearl's floor distance from acquisition to the first observed
  positive gold loss classified by the game as spending.
- `girya-relic-run.json`
  Adds Girya's observed Strength, eligible-combat denominator, time-weighted
  floor counter samples, successful-Lift floor pacing, and Attack plays/hits
  while its Strength is active.
- `lasting-candy-relic-run.json`
  Adds Lasting Candy's exact Power offers and terminal taken/rejected outcomes,
  split into Uncommon/Rare rarity buckets and Elite/Boss activation contexts.
- `gremlin-horn-relic-run.json`
  Adds Gremlin Horn's tracking-window activation numerator and zero-inclusive
  held turn/combat denominators alongside its observed Energy and card draws.
- `dingy-rug-relic-run.json`
  Adds Dingy Rug's affected card rewards and final visible card offers split
  across the five character pools and the Colorless pool.
- `card-reward-pool-taken-run.json`
  Adds the per-pool taken counter alongside the existing offer counter for both
  card-pool relics, so each pool row reports an observed offered/taken pair.
  Pools written before the counter existed keep only `count`, which reads as
  zero taken.
- `orichalcum-block-missed-run.json`
  Adds Orichalcum's leftover-Block shortfall: undercut turns, the missed-Block
  total scored against the relic's own trigger amount, and the zero-inclusive
  held turn/combat denominators for its two averages.
- `mercury-hourglass-relic-run.json`
  Adds Mercury Hourglass's zero-inclusive held combat denominator alongside its
  raw per-turn-start activation count, which together back its per-combat
  activation and damage averages.
- `discovery-cards-offered-run.json`
  Adds Discovery's observed choose-a-card options as the denominator for its
  existing pick counters, split by rarity and card type. Runs written before
  the offer counters existed keep only the picks, which read as zero offered.

- `exhausted-other-card-types-run.json`
  Adds the curse and status-card subsets of the per-card "exhausted others"
  count, so exhaust-enabler cards can say what kind of card they removed.

- `panache-power-damage-run.json`
  Damage recorded against a pooled power aggregate. Panache is the only power
  in the game that deals damage itself, so these are the first damage fields
  PowerAggregate has carried.

- `energy-wasted-ledger-run.json`
  The card-side and relic-side halves of the energy provenance ledger, so the
  new `total_energy_wasted` / `energy_wasted` counters have a checked-in shape
  alongside the `*_generated` totals they pair with.

Why these exist:

- new shape work should be validated against real checked-in examples, not memory
- pooled vs. per-instance is not a lossless migration, so the old pooled shape
  needs to stay visible when changing loader behavior
- additive follow-on shapes still need fixture coverage so "old but resumable"
  behavior stays intentional
- future tests can read these files directly without having to reconstruct old
  JSON by hand
