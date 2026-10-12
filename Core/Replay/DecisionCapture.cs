using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core.Replay;

/// <summary>
/// Reads the game state and the legal options at the instant of a player
/// decision, for the per-decision training records (docs/run-recording.md,
/// "Decision records"). Everything here runs synchronously on the game thread
/// BEFORE the decision mutates anything, and produces only detached data:
/// primitives/strings/lists for the readable form, and the game's own
/// snapshot objects (<c>NetFullCombatState.FromRun</c> /
/// <c>Player.ToSerializable</c>, which copy) for the exact form, so encoding
/// and JSON can run on the background chain.
///
/// Every read is best-effort (a failing field is omitted, never thrown).
/// Only read-only game APIs the UI itself calls every frame are used
/// (CanPlay / IsValidTarget / intent damage previews).
/// </summary>
internal static class DecisionCapture
{
    internal sealed class Capture
    {
        public string Phase = DecisionFormat.PhaseRun;
        public Dictionary<string, object?> State = new();
        public Dictionary<string, object?> Hidden = new();
        public IPacketSerializable? NetSnapshot;
        public string? NetForm;
    }

    /// <summary>One offered option, with its identity (for matching) and readable detail.</summary>
    internal sealed record Option(LegalOption Identity, Dictionary<string, object?> Detail, object? Ref = null)
    {
        public Dictionary<string, object?> ToJson()
        {
            var d = Identity.ToDictionary();
            foreach (var kv in Detail) d[kv.Key] = kv.Value;
            return d;
        }
    }

    public static Player? LocalPlayer(RunState? state)
    {
        if (state == null) return null;
        return state.Players.FirstOrDefault(p => LocalContext.IsMe(p)) ?? state.Players.FirstOrDefault();
    }

    public static bool InCombat(Player player) =>
        CombatManager.Instance?.IsInProgress == true && player.PlayerCombatState != null;

    public static Capture CaptureState(RunState state, Player player)
    {
        var cap = new Capture();
        Try(() => cap.State["run"] = RunBlock(state, player));
        if (InCombat(player))
        {
            cap.Phase = DecisionFormat.PhaseCombat;
            Try(() => cap.State["combat"] = CombatBlock(player, cap.Hidden));
            Try(() =>
            {
                cap.NetSnapshot = NetFullCombatState.FromRun(state, null);
                cap.NetForm = "NetFullCombatState";
            });
        }
        else
        {
            Try(() =>
            {
                cap.NetSnapshot = player.ToSerializable();
                cap.NetForm = "SerializablePlayer";
            });
        }
        return cap;
    }

    // -------- Readable state --------

    private static Dictionary<string, object?> RunBlock(RunState state, Player player)
    {
        var d = new Dictionary<string, object?>();
        Try(() => d["character"] = player.Character?.Id.ToString());
        Try(() => d["hp"] = player.Creature.CurrentHp);
        Try(() => d["max_hp"] = player.Creature.MaxHp);
        Try(() => d["gold"] = player.Gold);
        Try(() => d["floor"] = state.TotalFloor);
        Try(() => d["act"] = state.CurrentActIndex);
        Try(() =>
        {
            var c = state.CurrentMapCoord;
            d["coord"] = c.HasValue ? $"{c.Value.col},{c.Value.row}" : null;
        });
        Try(() => d["room_type"] = state.CurrentRoom?.RoomType.ToString());
        Try(() => d["ascension"] = state.AscensionLevel);
        Try(() => d["deck"] = player.Deck.Cards.Select((c, i) => (object?)CardInfo(c, deckIndex: i)).ToList());
        Try(() => d["relics"] = player.Relics.Select(RelicInfo).ToList());
        Try(() => d["potions"] = player.PotionSlots.Select((p, i) => (object?)PotionInfo(p, i)).ToList());
        Try(() => d["max_energy"] = player.MaxEnergy);
        return d;
    }

    private static Dictionary<string, object?> CombatBlock(Player player, Dictionary<string, object?> hidden)
    {
        var d = new Dictionary<string, object?>();
        var pcs = player.PlayerCombatState!;
        var cs = CombatManager.Instance.DebugOnlyGetState();
        Try(() => d["round"] = cs?.RoundNumber);
        Try(() => d["side"] = cs?.CurrentSide.ToString());
        Try(() => d["turn"] = pcs.TurnNumber);
        Try(() => d["energy"] = pcs.Energy);
        Try(() => d["max_energy"] = pcs.MaxEnergy);
        Try(() => d["stars"] = pcs.Stars);
        Try(() => d["block"] = player.Creature.Block);
        Try(() => d["hp"] = player.Creature.CurrentHp);
        Try(() => d["max_hp"] = player.Creature.MaxHp);
        Try(() => d["powers"] = Powers(player.Creature));
        Try(() => d["orbs"] = pcs.OrbQueue.Orbs.Select(o =>
        {
            var s = NetFullCombatState.OrbState.From(o);
            return (object?)new Dictionary<string, object?>
            {
                ["id"] = o.Id.ToString(),
                ["passive"] = s.passive,
                ["evoke"] = s.evoke,
            };
        }).ToList());
        Try(() => d["hand"] = pcs.Hand.Cards.Select((c, i) =>
        {
            var info = CardInfo(c, combat: true);
            info["hand_index"] = i;
            return (object?)info;
        }).ToList());
        // Draw pile: the composition is visible in game, the ORDER is not.
        Try(() =>
        {
            d["draw_pile_count"] = pcs.DrawPile.Cards.Count;
            d["draw_pile"] = pcs.DrawPile.Cards
                .Select(c => CardInfo(c))
                .OrderBy(c => (string?)c["id"], StringComparer.Ordinal)
                .ThenBy(c => (int?)c["upgrade"] ?? 0)
                .Cast<object?>()
                .ToList();
            hidden["draw_pile_order"] = pcs.DrawPile.Cards.Select(c => (object?)CardInfo(c)).ToList();
        });
        Try(() => d["discard_pile"] = pcs.DiscardPile.Cards.Select(c => (object?)CardInfo(c)).ToList());
        Try(() => d["exhaust_pile"] = pcs.ExhaustPile.Cards.Select(c => (object?)CardInfo(c)).ToList());
        Try(() =>
        {
            if (cs == null) return;
            var playerTargets = cs.PlayerCreatures;
            var moves = new List<object?>();
            d["creatures"] = cs.Creatures.Select(c =>
            {
                var info = new Dictionary<string, object?>
                {
                    ["combat_id"] = c.CombatId,
                    ["side"] = c.Side.ToString(),
                    ["monster"] = c.Monster?.Id.ToString(),
                    ["player"] = c.Player?.NetId,
                    ["hp"] = c.CurrentHp,
                    ["max_hp"] = c.MaxHp,
                    ["block"] = c.Block,
                    ["alive"] = c.IsAlive,
                };
                Try(() => info["powers"] = Powers(c));
                if (c.Monster != null)
                {
                    Try(() => info["intents"] = Intents(c, playerTargets));
                    Try(() => moves.Add(new Dictionary<string, object?>
                    {
                        ["combat_id"] = c.CombatId,
                        ["move_id"] = c.Monster.NextMove?.StateId,
                    }));
                }
                return (object?)info;
            }).ToList();
            hidden["monster_moves"] = moves;
        });
        return d;
    }

    private static List<object?> Intents(Creature monster, IReadOnlyList<Creature> targets)
    {
        var list = new List<object?>();
        foreach (var intent in monster.Monster!.NextMove.Intents)
        {
            var i = new Dictionary<string, object?> { ["type"] = intent.IntentType.ToString() };
            if (intent is AttackIntent attack)
            {
                // The same preview the intent UI shows (Hook.ModifyDamage with no card source).
                Try(() => i["damage"] = attack.GetSingleDamage(targets, monster));
                Try(() => i["hits"] = Math.Max(1, attack.Repeats));
            }
            list.Add(i);
        }
        return list;
    }

    private static List<object?> Powers(Creature c) =>
        c.Powers.Select(p => (object?)new Dictionary<string, object?>
        {
            ["id"] = p.Id.ToString(),
            ["amount"] = p.Amount,
        }).ToList();

    internal static Dictionary<string, object?> CardInfo(CardModel card, bool combat = false, int? deckIndex = null)
    {
        var d = new Dictionary<string, object?> { ["id"] = card.Id.ToString() };
        Try(() => d["upgrade"] = card.CurrentUpgradeLevel);
        Try(() =>
        {
            if (card.Enchantment != null)
                d["enchantment"] = new Dictionary<string, object?>
                {
                    ["id"] = card.Enchantment.Id.ToString(),
                    ["amount"] = card.Enchantment.Amount,
                };
        });
        if (deckIndex.HasValue) d["deck_index"] = deckIndex;
        if (combat)
        {
            Try(() =>
            {
                if (NetCombatCardDb.Instance.TryGetCardId(card, out uint id)) d["combat_card_index"] = id;
            });
            Try(() => d["type"] = card.Type.ToString());
            Try(() => d["target_type"] = card.TargetType.ToString());
            Try(() => d["cost"] = card.EnergyCost.GetWithModifiers(CostModifiers.All));
            Try(() => d["star_cost"] = card.GetStarCostWithModifiers());
            Try(() => d["affliction"] = card.Affliction?.Id.ToString());
            Try(() =>
            {
                bool playable = card.CanPlay(out var reason, out _);
                d["playable"] = playable;
                if (!playable) d["unplayable_reason"] = reason.ToString();
            });
        }
        return d;
    }

    private static object? RelicInfo(RelicModel relic)
    {
        var d = new Dictionary<string, object?> { ["id"] = relic.Id.ToString() };
        Try(() => { if (relic.ShowCounter) d["counter"] = relic.DisplayAmount; });
        Try(() => d["status"] = relic.Status.ToString());
        return d;
    }

    private static object? PotionInfo(PotionModel? potion, int slot)
    {
        if (potion == null) return null;
        var d = new Dictionary<string, object?> { ["slot"] = slot, ["id"] = potion.Id.ToString() };
        Try(() => d["usage"] = potion.Usage.ToString());
        Try(() => d["target_type"] = potion.TargetType.ToString());
        return d;
    }

    // -------- Legal options --------

    /// <summary>
    /// Every legal combat action right now: playable hand cards × valid
    /// targets, usable potions × valid targets, potion discards, end turn.
    /// Out of combat: usable (AnyTime) potions and discards.
    /// </summary>
    public static List<Option> ActionOptions(Player player)
    {
        var options = new List<Option>();
        bool inCombat = InCombat(player);
        var cs = CombatManager.Instance?.DebugOnlyGetState();
        var candidates = new List<Creature?> { null };
        if (inCombat && cs != null) candidates.AddRange(cs.Creatures);
        else candidates.Add(player.Creature);

        if (inCombat)
        {
            var hand = player.PlayerCombatState!.Hand.Cards;
            for (int i = 0; i < hand.Count; i++)
            {
                var card = hand[i];
                bool playable = false;
                Try(() => playable = card.CanPlay());
                if (!playable) continue;
                uint? cid = null;
                Try(() => { if (NetCombatCardDb.Instance.TryGetCardId(card, out uint id)) cid = id; });
                foreach (var t in candidates)
                {
                    bool valid = false;
                    Try(() => valid = card.IsValidTarget(t));
                    if (!valid) continue;
                    options.Add(new Option(
                        new LegalOption("play_card", CombatCardIndex: cid, TargetCombatId: t?.CombatId, Label: card.Id.ToString()),
                        new Dictionary<string, object?> { ["hand_index"] = i, ["target_monster"] = t?.Monster?.Id.ToString() }));
                }
            }
        }

        var slots = player.PotionSlots;
        for (int s = 0; s < slots.Count; s++)
        {
            var potion = slots[s];
            if (potion == null) continue;
            bool usable = inCombat
                ? potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime
                : potion.Usage == PotionUsage.AnyTime;
            if (usable)
            {
                foreach (var t in candidates)
                {
                    bool valid = false;
                    Try(() => valid = potion.IsValidTarget(t));
                    if (!valid) continue;
                    options.Add(new Option(
                        new LegalOption("use_potion", TargetCombatId: t?.CombatId, TargetPlayerId: t?.Player?.NetId,
                            PotionSlot: s, Label: potion.Id.ToString()),
                        new Dictionary<string, object?> { ["target_monster"] = t?.Monster?.Id.ToString() }));
                }
            }
            options.Add(new Option(
                new LegalOption("discard_potion", PotionSlot: s, Label: potion.Id.ToString()),
                new Dictionary<string, object?>()));
        }

        if (inCombat)
            options.Add(new Option(new LegalOption("end_turn"), new Dictionary<string, object?>()));
        return options;
    }

    /// <summary>Map nodes reachable from the current position (start points at an act's beginning).</summary>
    public static List<Option> MapOptions(RunState state)
    {
        var options = new List<Option>();
        var current = state.CurrentMapPoint;
        IEnumerable<MegaCrit.Sts2.Core.Map.MapPoint> next = current != null
            ? current.Children
            : state.Map.startMapPoints;
        foreach (var p in next.OrderBy(p => p.coord.col).ThenBy(p => p.coord.row))
        {
            options.Add(new Option(
                new LegalOption("map_vote", Id: $"{p.coord.col},{p.coord.row}"),
                new Dictionary<string, object?> { ["point_type"] = p.PointType.ToString() }));
        }
        return options;
    }

    public static List<Option> TreasureRelicOptions(IReadOnlyList<RelicModel>? relics)
    {
        var options = new List<Option>();
        if (relics != null)
            for (int i = 0; i < relics.Count; i++)
                options.Add(new Option(new LegalOption("pick_relic", Index: i, Label: relics[i].Id.ToString()), new()));
        options.Add(new Option(new LegalOption("pick_relic", Index: -1, Label: "skip"), new()));
        return options;
    }

    public static List<Option> MerchantOptions(MerchantInventory? inventory)
    {
        var options = new List<Option>();
        if (inventory == null) return options;
        void Add(string category, int slot, MerchantEntry e, string? model)
        {
            var detail = new Dictionary<string, object?>();
            Try(() => detail["cost"] = e.Cost);
            Try(() => detail["stocked"] = e.IsStocked);
            Try(() => detail["affordable"] = e.EnoughGold);
            options.Add(new Option(new LegalOption("shop", Index: slot, Id: category, Label: model), detail, e));
        }
        for (int i = 0; i < inventory.CharacterCardEntries.Count; i++)
            Add("character_card", i, inventory.CharacterCardEntries[i], inventory.CharacterCardEntries[i].CreationResult?.Card?.Id.ToString());
        for (int i = 0; i < inventory.ColorlessCardEntries.Count; i++)
            Add("colorless_card", i, inventory.ColorlessCardEntries[i], inventory.ColorlessCardEntries[i].CreationResult?.Card?.Id.ToString());
        for (int i = 0; i < inventory.RelicEntries.Count; i++)
            Add("relic", i, inventory.RelicEntries[i], inventory.RelicEntries[i].Model?.Id.ToString());
        for (int i = 0; i < inventory.PotionEntries.Count; i++)
            Add("potion", i, inventory.PotionEntries[i], inventory.PotionEntries[i].Model?.Id.ToString());
        if (inventory.CardRemovalEntry != null)
            Add("card_removal", 0, inventory.CardRemovalEntry, null);
        return options;
    }

    public static List<Option> RewardOptions(RewardsSet set)
    {
        var options = new List<Option>();
        for (int i = 0; i < set.Rewards.Count; i++)
        {
            var r = set.Rewards[i];
            var detail = new Dictionary<string, object?> { ["reward_type"] = r.GetType().Name };
            Try(() =>
            {
                switch (r)
                {
                    case CardReward cr:
                        detail["cards"] = cr._cards.Select(c => (object?)CardInfo(c.Card)).ToList();
                        break;
                    case RelicReward rr:
                        detail["relic"] = rr.Relic?.Id.ToString();
                        break;
                    case PotionReward pr:
                        detail["potion"] = pr.Potion?.Id.ToString();
                        break;
                }
            });
            Try(() => detail["text"] = r.ToString());
            options.Add(new Option(new LegalOption("reward", Index: i), detail, r));
        }
        options.Add(new Option(new LegalOption("reward", Index: set.Rewards.Count, Label: "skip"), new()));
        return options;
    }

    // -------- Selection screens (card / relic / bundle choices) --------

    /// <summary>What a selection screen offered, snapshot when it opened (it is gone by the time the choice syncs).</summary>
    internal sealed class Selection
    {
        public string Screen = "";
        public List<Option> Options = new();
        public int? MinSelect;
        public int? MaxSelect;
        public bool? CanSkip;
        public bool FilterKnown = true;

        public Dictionary<string, object?> Describe() => new()
        {
            ["screen"] = Screen,
            ["min_select"] = MinSelect,
            ["max_select"] = MaxSelect,
            ["can_skip"] = CanSkip,
            ["filter_known"] = FilterKnown,
        };
    }

    /// <summary>Snapshot the options of the overlay screen now on top, if it is a selection screen.</summary>
    public static Selection? SnapshotOverlay(IOverlayScreen? screen)
    {
        switch (screen)
        {
            case NCardRewardSelectionScreen r:
            {
                var sel = new Selection { Screen = "card_reward", MinSelect = 0, MaxSelect = 1, CanSkip = true };
                var cards = r._options;
                for (int i = 0; i < cards.Count; i++)
                    sel.Options.Add(CardOption(i, cards[i].Card));
                var extra = r._extraOptions;
                if (extra != null)
                    for (int j = 0; j < extra.Count; j++)
                        sel.Options.Add(new Option(
                            new LegalOption("select", Index: cards.Count + j, Id: "alternative:" + extra[j].OptionId),
                            new Dictionary<string, object?> { ["alternative"] = extra[j].OptionId }));
                return sel;
            }
            case NChooseACardSelectionScreen c:
            {
                var sel = new Selection { Screen = "choose_a_card", MinSelect = c._canSkip ? 0 : 1, MaxSelect = 1, CanSkip = c._canSkip };
                for (int i = 0; i < c._cards.Count; i++) sel.Options.Add(CardOption(i, c._cards[i]));
                return sel;
            }
            case NCardGridSelectionScreen g:
            {
                var sel = new Selection { Screen = GridName(g) };
                CardSelectorPrefs? prefs = g switch
                {
                    NSimpleCardSelectScreen s => s._prefs,
                    NDeckCardSelectScreen s => s._prefs,
                    NDeckUpgradeSelectScreen s => s._prefs,
                    NDeckTransformSelectScreen s => s._prefs,
                    NDeckEnchantSelectScreen s => s._prefs,
                    NCombatPileCardSelectScreen s => s._prefs,
                    _ => null,
                };
                if (prefs.HasValue)
                {
                    sel.MinSelect = prefs.Value.MinSelect;
                    sel.MaxSelect = prefs.Value.MaxSelect;
                    sel.CanSkip = prefs.Value.Cancelable;
                }
                for (int i = 0; i < g._cards.Count; i++) sel.Options.Add(CardOption(i, g._cards[i]));
                return sel;
            }
            case NChooseABundleSelectionScreen b:
            {
                var sel = new Selection { Screen = "choose_a_bundle", MinSelect = 1, MaxSelect = 1 };
                for (int i = 0; i < b._bundles.Count; i++)
                    sel.Options.Add(new Option(
                        new LegalOption("select", Index: i),
                        new Dictionary<string, object?> { ["bundle"] = b._bundles[i].Select(c => (object?)CardInfo(c)).ToList() }));
                return sel;
            }
            case NChooseARelicSelection rs:
            {
                var sel = new Selection { Screen = "choose_a_relic", MinSelect = 1, MaxSelect = 1 };
                for (int i = 0; i < rs._relics.Count; i++)
                    sel.Options.Add(new Option(new LegalOption("select", Index: i, Label: rs._relics[i].Id.ToString()), new(), rs._relics[i]));
                return sel;
            }
            default:
                return null;
        }
    }

    /// <summary>Snapshot an in-hand selection (discard / exhaust / upgrade a card in hand).</summary>
    public static Selection? SnapshotHand(NPlayerHand hand)
    {
        if (hand.CurrentMode is not (NPlayerHand.Mode.SimpleSelect or NPlayerHand.Mode.UpgradeSelect)) return null;
        var player = LocalPlayer(RunManager.Instance.State);
        var cards = player?.PlayerCombatState?.Hand.Cards;
        if (cards == null) return null;
        var filter = hand._currentSelectionFilter;
        var prefs = hand._prefs;
        var sel = new Selection
        {
            Screen = "hand_" + hand.CurrentMode.ToString().ToLowerInvariant(),
            MinSelect = prefs.MinSelect,
            MaxSelect = prefs.MaxSelect,
            CanSkip = prefs.Cancelable,
        };
        int idx = 0;
        foreach (var c in cards)
        {
            bool selectable = true;
            Try(() => selectable = filter?.Invoke(c) ?? true);
            if (selectable) sel.Options.Add(CardOption(idx++, c, combat: true));
        }
        return sel;
    }

    private static string GridName(NCardGridSelectionScreen g) => g switch
    {
        NSimpleCardSelectScreen => "simple_grid",
        NDeckCardSelectScreen => "deck_select",
        NDeckUpgradeSelectScreen => "deck_upgrade",
        NDeckTransformSelectScreen => "deck_transform",
        NDeckEnchantSelectScreen => "deck_enchant",
        NCombatPileCardSelectScreen => "combat_pile",
        _ => g.GetType().Name,
    };

    private static Option CardOption(int index, CardModel card, bool combat = false)
    {
        var info = CardInfo(card, combat: combat);
        Try(() =>
        {
            int deckIndex = card.Owner?.Deck.Cards.ToList().IndexOf(card) ?? -1;
            if (deckIndex >= 0) info["deck_index"] = deckIndex;
        });
        Try(() =>
        {
            if (!info.ContainsKey("combat_card_index") && NetCombatCardDb.Instance.TryGetCardId(card, out uint id))
                info["combat_card_index"] = id;
        });
        return new Option(new LegalOption("select", Index: index, Label: card.Id.ToString()), new() { ["card"] = info }, card);
    }

    /// <summary>
    /// Map a synced choice result onto the snapshot's option indexes. Index
    /// results are option indexes already; card results are matched by
    /// combat index, deck index, reference, then id.
    /// </summary>
    public static List<int> ChosenIndexes(Selection sel, NetPlayerChoiceResult r, Player? player)
    {
        var chosen = new List<int>();
        var used = new HashSet<int>();
        void Mark(Func<Option, bool> match)
        {
            for (int i = 0; i < sel.Options.Count; i++)
            {
                if (used.Contains(i) || !match(sel.Options[i])) continue;
                used.Add(i);
                chosen.Add(sel.Options[i].Identity.Index ?? i);
                return;
            }
        }

        if (r.indexes != null)
        {
            foreach (int i in r.indexes) chosen.Add(i);
            return chosen;
        }
        if (r.combatCards != null)
            foreach (var c in r.combatCards)
            {
                var model = c.ToCardModelOrNull();
                Mark(o => ReferenceEquals(o.Ref, model));
            }
        if (r.deckCards != null && player != null)
            foreach (var c in r.deckCards)
            {
                var model = c.DeckIndex < player.Deck.Cards.Count ? player.Deck.Cards[(int)c.DeckIndex] : null;
                Mark(o => ReferenceEquals(o.Ref, model));
            }
        if (r.canonicalCards != null)
            foreach (var c in r.canonicalCards)
                Mark(o => ReferenceEquals(o.Ref, c) || (o.Ref as CardModel)?.Id == c?.Id);
        if (r.mutableCards != null)
            foreach (var c in r.mutableCards)
                Mark(o => c?.Id != null && (o.Ref as CardModel)?.Id == c.Id);
        return chosen;
    }

    private static void Try(Action body)
    {
        try { body(); }
        catch { /* best-effort field */ }
    }
}
