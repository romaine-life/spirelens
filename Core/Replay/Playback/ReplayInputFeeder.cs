using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// Feeds one recorded input into the running game the way the player's own
/// click would have: the same local entry points the recorder hooked, the
/// same UI handlers the in-house MCP bridge and the game's AutoSlay use.
///
/// Each input kind has a readiness test (the game is idle / the expected
/// screen is open) and a feed. <see cref="TryFeed"/> is polled once per frame
/// by <see cref="ReplayPlayer"/> until it returns <see cref="FeedResult.Fed"/>
/// or the player's timeout turns <see cref="FeedResult.NotReady"/> into a
/// divergence. Nothing here waits or blocks.
/// </summary>
internal static class ReplayInputFeeder
{
    public enum FeedResult { NotReady, Fed, Impossible }

    /// <summary>Last reason a step was not ready / impossible, for the divergence log.</summary>
    public static string LastReason { get; private set; } = "";

    public static FeedResult TryFeed(PlaybackStep step, ReplayCardSelector selector)
    {
        LastReason = "";
        var state = RunManager.Instance.State;
        var player = state == null ? null : LocalContext.GetMe(state) ?? state.Players.FirstOrDefault();
        if (state == null || player == null) return NotReady("no run in progress");

        using var doc = JsonDocument.Parse(step.Data);
        var data = doc.RootElement;

        return step.InputKind switch
        {
            "play_card" => FeedPlayCard(player, data),
            "use_potion" => FeedUsePotion(player, data),
            "discard_potion" => FeedNetAction(player, data, combatOnly: false),
            "end_turn" => FeedEndTurn(player),
            "undo_end_turn" => FeedNetAction(player, data, combatOnly: true),
            "map_vote" => FeedMapVote(data),
            "pick_relic" => FeedPickRelic(data),
            "vote_next_act" => FeedNextAct(),
            "choice" => FeedChoice(player, state, data, selector),
            "event_option" => FeedEventOption(data),
            "rest_option" => FeedRestOption(data),
            "reward_take" => FeedRewardTake(player, data),
            "reward_skip" => FeedRewardSkip(),
            "shop_buy" or "shop_remove_card" => FeedShop(player, state, data),
            "treasure_chest_open" => FeedTreasureChest(),
            _ => Impossible($"unknown input kind {step.InputKind}"),
        };
    }

    // -------- Combat --------

    /// <summary>The player could act in combat right now, and the action queue is idle.</summary>
    public static bool CombatReadyForInput(out string why)
    {
        var cm = CombatManager.Instance;
        why = "";
        if (cm == null || !cm.IsInProgress) { why = "not in combat"; return false; }
        if (cm.IsEnemyTurnStarted || cm.EndingPlayerTurnPhaseOne || cm.EndingPlayerTurnPhaseTwo || cm.IsPaused)
        { why = "not the player's play phase"; return false; }
        if (cm.PlayerActionsDisabled) { why = "player actions disabled"; return false; }
        var rm = RunManager.Instance;
        if (!rm.ActionQueueSet.IsEmpty || rm.ActionExecutor.CurrentlyRunningAction != null)
        { why = "actions still resolving"; return false; }
        var hand = NPlayerHand.Instance;
        if (hand != null && (hand.InCardPlay || hand.CurrentMode != NPlayerHand.Mode.Play))
        { why = "hand busy"; return false; }
        return true;
    }

    private static FeedResult FeedPlayCard(Player player, JsonElement data)
    {
        if (!CombatReadyForInput(out var why)) return NotReady(why);
        if (DecodeAction(data) is not NetPlayCardAction p) return Impossible("play_card net form undecodable");

        var card = p.card.ToCardModelOrNull();
        if (card == null) return NotReady($"card #{p.card.CombatCardIndex} not in combat yet");
        if (player.PlayerCombatState?.Hand.Cards.Contains(card) != true)
            return NotReady($"{card.Id} not in hand");
        Creature? target = null;
        if (p.targetId.HasValue)
        {
            target = CombatManager.Instance.DebugOnlyGetState()?.GetCreature(p.targetId);
            if (target == null) return Impossible($"target {p.targetId} not found");
        }
        if (!card.CanPlayTargeting(target)) return NotReady($"{card.Id} not playable on that target yet");

        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
        return FeedResult.Fed;
    }

    private static FeedResult FeedUsePotion(Player player, JsonElement data)
    {
        if (DecodeAction(data) is not NetUsePotionAction u) return Impossible("use_potion net form undecodable");
        if (u.enqueuedInCombat && !CombatReadyForInput(out var why)) return NotReady(why);
        if (!u.enqueuedInCombat && !IdleOutOfCombat(out var why2)) return NotReady(why2);

        var potion = player.GetPotionAtSlotIndex((int)u.potionIndex);
        if (potion == null) return Impossible($"no potion in slot {u.potionIndex}");
        if (potion.IsQueued) return NotReady("potion already queued");

        Creature? target = null;
        if (u.targetId.HasValue)
            target = CombatManager.Instance.DebugOnlyGetState()?.GetCreature(u.targetId);
        if (target == null && u.targetPlayerId.HasValue)
            target = RunManager.Instance.State?.Players.FirstOrDefault(pl => pl.NetId == u.targetPlayerId)?.Creature;
        if ((u.targetId.HasValue || u.targetPlayerId.HasValue) && target == null)
            return Impossible("potion target not found");

        potion.EnqueueManualUse(target);
        return FeedResult.Fed;
    }

    private static FeedResult FeedNetAction(Player player, JsonElement data, bool combatOnly)
    {
        var net = DecodeAction(data);
        if (net == null) return Impossible("net form undecodable");
        if (CombatManager.Instance.IsInProgress)
        {
            if (!CombatReadyForInput(out var why)) return NotReady(why);
        }
        else if (combatOnly)
        {
            return NotReady("not in combat");
        }
        else if (!IdleOutOfCombat(out var why))
        {
            return NotReady(why);
        }
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(net.ToGameAction(player));
        return FeedResult.Fed;
    }

    private static FeedResult FeedEndTurn(Player player)
    {
        if (!CombatReadyForInput(out var why)) return NotReady(why);
        PlayerCmd.EndTurn(player, canBackOut: false);
        return FeedResult.Fed;
    }

    private static bool IdleOutOfCombat(out string why)
    {
        why = "";
        var rm = RunManager.Instance;
        if (!rm.ActionQueueSet.IsEmpty || rm.ActionExecutor.CurrentlyRunningAction != null)
        { why = "actions still resolving"; return false; }
        return true;
    }

    // -------- Map, treasure relic, next act --------

    private static FeedResult FeedMapVote(JsonElement data)
    {
        if (DecodeAction(data) is not NetVoteForMapCoordAction v || !v.destination.HasValue)
            return Impossible("map_vote net form undecodable");
        var dest = v.destination.Value.coord;

        var screen = NMapScreen.Instance;
        if (screen == null || !screen.IsOpen)
        {
            // The recorder does not see the click that left the room (a room's
            // proceed button, the terminal rewards screen's proceed); the
            // human made it, so make it too.
            AdvanceTowardMap();
            return NotReady("map not open");
        }

        var point = screen._mapPointDictionary.Values
            .Concat(new NMapPoint?[] { screen._bossPointNode, screen._secondBossPointNode, screen._startingPointNode })
            .FirstOrDefault(n => n != null && GodotObject.IsInstanceValid(n) && n.Point != null
                                 && n.Point.coord.col == dest.col && n.Point.coord.row == dest.row);
        if (point == null) return Impossible($"map point {dest.col},{dest.row} not on this map");
        if (point.State != MapPointState.Travelable) return NotReady($"map point {dest.col},{dest.row} not travelable yet");

        screen.OnMapPointSelectedLocally(point);
        return FeedResult.Fed;
    }

    /// <summary>Click whatever proceed control currently leads out of the room.</summary>
    private static void AdvanceTowardMap()
    {
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewards)
        {
            var proceed = FindFirst<NProceedButton>(rewards);
            if (proceed is { IsEnabled: true }) proceed.ForceClick();
            return;
        }

        var merchant = NMerchantRoom.Instance;
        if (merchant?.Inventory is { IsOpen: true })
        {
            var back = FindFirst<NBackButton>(merchant);
            if (back is { IsEnabled: true }) back.ForceClick();
            return;
        }

        var room = NRun.Instance?._roomContainer?.CurrentScene as IRoomWithProceedButton;
        if (room?.ProceedButton is { IsEnabled: true } button && button.IsVisibleInTree())
            button.ForceClick();
    }

    private static FeedResult FeedPickRelic(JsonElement data)
    {
        if (DecodeAction(data) is not NetPickRelicAction r) return Impossible("pick_relic net form undecodable");
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (sync?.CurrentRelics == null) return NotReady("treasure relics not offered yet");
        sync.PickRelicLocally(r.relicIndex);
        return FeedResult.Fed;
    }

    private static FeedResult FeedNextAct()
    {
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewards)
        {
            var proceed = FindFirst<NProceedButton>(rewards);
            if (proceed is { IsEnabled: true })
            {
                proceed.ForceClick();
                return FeedResult.Fed;
            }
            return NotReady("boss rewards proceed not enabled");
        }
        return NotReady("boss rewards screen not open");
    }

    // -------- Choices --------

    private static FeedResult FeedChoice(Player player, RunState state, JsonElement data, ReplayCardSelector selector)
    {
        if (!data.TryGetProperty("net", out var netEl) || netEl.GetString() is not { } b64)
            return Impossible("choice has no net form");
        var evt = Decode(b64);
        if (evt?.playerChoiceResult is not { } net) return Impossible("choice net form undecodable");
        string? screenName = data.TryGetProperty("selection_screen", out var s) ? s.GetString() : null;

        var top = NOverlayStack.Instance?.Peek();
        if (screenName == "card_reward" || top is NCardRewardSelectionScreen)
        {
            if (top is not NCardRewardSelectionScreen rewardScreen
                || rewardScreen._completionSource == null
                || rewardScreen._completionSource.Task.IsCompleted)
                return NotReady("card reward screen not waiting");
            var result = PlayerChoiceResult.FromNetData(player, state, net);
            int? index = result.ChoiceType == PlayerChoiceType.Index ? result.AsIndexOrNull() : null;
            if (index == null || index < 0)
                rewardScreen._completionSource.SetResult(null);
            else if (index < rewardScreen._options.Count)
                rewardScreen.SelectCard(rewardScreen.GetCardHolder(rewardScreen._options[index.Value].Card));
            else
                rewardScreen.OnAlternateRewardSelected(index.Value - rewardScreen._options.Count);
            return FeedResult.Fed;
        }

        if (top is NChooseARelicSelection relicScreen && !relicScreen._completionSource.Task.IsCompleted)
        {
            var result = PlayerChoiceResult.FromNetData(player, state, net);
            int index = result.AsIndex();
            relicScreen._completionSource.SetResult(index >= 0 && index < relicScreen._relics.Count
                ? new[] { relicScreen._relics[index] }
                : Array.Empty<RelicModel>());
            return FeedResult.Fed;
        }

        if (top is NChooseABundleSelectionScreen bundleScreen && !bundleScreen._completionSource.Task.IsCompleted)
        {
            var result = PlayerChoiceResult.FromNetData(player, state, net);
            int index = result.AsIndex();
            if (index < 0 || index >= bundleScreen._bundles.Count) return Impossible($"bundle {index} not offered");
            bundleScreen._completionSource.SetResult(new[] { bundleScreen._bundles[index] });
            return FeedResult.Fed;
        }

        var pending = selector.Pending;
        if (pending == null) return NotReady("no card selection waiting");

        PlayerChoiceResult decoded;
        try { decoded = PlayerChoiceResult.FromNetData(player, state, net); }
        catch (Exception e) { return Impossible($"choice undecodable here: {e.Message}"); }

        var picked = MapToOptions(decoded, pending.Options);
        if (picked == null) return Impossible($"recorded choice ({decoded}) is not among the {pending.Options.Count} offered cards");
        selector.Complete(picked);
        return FeedResult.Fed;
    }

    /// <summary>The offered cards the recorded result names; null when one of them is not offered.</summary>
    internal static List<CardModel>? MapToOptions(PlayerChoiceResult result, IReadOnlyList<CardModel> options)
    {
        var picked = new List<CardModel>();
        if (result.ChoiceType == PlayerChoiceType.None) return picked;
        if (result.ChoiceType == PlayerChoiceType.Index)
        {
            foreach (int i in result.AsIndexes() ?? new List<int>())
            {
                if (i < 0) continue;
                if (i >= options.Count) return null;
                picked.Add(options[i]);
            }
            return picked;
        }

        var used = new HashSet<CardModel>();
        foreach (var card in result.AsCards(result.ChoiceType))
        {
            var match = options.FirstOrDefault(o => ReferenceEquals(o, card) && !used.Contains(o))
                        ?? options.FirstOrDefault(o => !used.Contains(o) && card != null
                                                       && o.Id == card.Id
                                                       && o.CurrentUpgradeLevel == card.CurrentUpgradeLevel);
            if (match == null) return null;
            used.Add(match);
            picked.Add(match);
        }
        return picked;
    }

    // -------- Events, rest sites, rewards, shop, treasure --------

    private static FeedResult FeedEventOption(JsonElement data)
    {
        int index = data.TryGetProperty("index", out var i) ? i.GetInt32() : -1;
        var room = NEventRoom.Instance;
        if (room == null) return NotReady("event room not open");

        // Ancient dialogue must be clicked through before options appear.
        var dialogue = FindFirst<NAncientEventLayout>(room)?.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
        if (dialogue != null && dialogue.Visible && dialogue.IsEnabled)
        {
            dialogue.ForceClick();
            return NotReady("advancing dialogue");
        }

        EventOption? option = null;
        try
        {
            var options = RunManager.Instance.EventSynchronizer.GetLocalEvent().CurrentOptions;
            if (index >= 0 && index < options.Count) option = options[index];
        }
        catch { }
        if (option == null) return NotReady($"event option {index} not offered yet");

        var button = FindAll<NEventOptionButton>(room)
            .FirstOrDefault(b => ReferenceEquals(b.Option, option));
        if (button == null || !button.IsEnabled || !button.IsVisibleInTree())
            return NotReady("event option button not ready");
        button.ForceClick();
        return FeedResult.Fed;
    }

    private static FeedResult FeedRestOption(JsonElement data)
    {
        int index = data.TryGetProperty("index", out var i) ? i.GetInt32() : -1;
        string? optionId = data.TryGetProperty("option_id", out var o) ? o.GetString() : null;
        var room = NRestSiteRoom.Instance;
        if (room == null) return NotReady("rest site not open");
        var buttons = FindAll<NRestSiteButton>(room);
        var button = (optionId != null ? buttons.FirstOrDefault(b => b.Option?.OptionId == optionId) : null)
                     ?? (index >= 0 && index < buttons.Count ? buttons[index] : null);
        if (button == null) return NotReady("rest option not offered yet");
        if (!button.IsEnabled || !button.IsVisibleInTree()) return NotReady("rest option not enabled");
        button.ForceClick();
        return FeedResult.Fed;
    }

    private static FeedResult FeedRewardTake(Player player, JsonElement data)
    {
        int index = data.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
        if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen) return NotReady("rewards screen not open");

        Reward? target = null;
        try
        {
            var set = RunManager.Instance.RewardsSetSynchronizer
                .GetRewardStateForPlayer(player).rewardsStack.Last().set;
            if (index >= 0 && index < set.Rewards.Count) target = set.Rewards[index];
        }
        catch { }
        if (target == null) return NotReady($"reward {index} not offered yet");

        var button = FindAll<NRewardButton>(screen).FirstOrDefault(b => ReferenceEquals(b.Reward, target));
        if (button == null || !button.IsEnabled) return NotReady("reward button not ready");
        button.ForceClick();
        return FeedResult.Fed;
    }

    private static FeedResult FeedRewardSkip()
    {
        if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen) return NotReady("rewards screen not open");
        var proceed = FindFirst<NProceedButton>(screen);
        if (proceed is not { IsEnabled: true }) return NotReady("rewards proceed not enabled");
        proceed.ForceClick();
        return FeedResult.Fed;
    }

    private static FeedResult FeedShop(Player player, RunState state, JsonElement data)
    {
        if (!IdleOutOfCombat(out var why)) return NotReady(why);
        string? category = data.TryGetProperty("category", out var c) ? c.GetString() : null;
        int slot = data.TryGetProperty("slot", out var sl) && sl.ValueKind == JsonValueKind.Number ? sl.GetInt32() : -1;

        MerchantInventory? inventory = null;
        if (state.CurrentRoom is MerchantRoom merchantRoom)
        {
            inventory = merchantRoom.GetLocalInventory();
            var ui = NMerchantRoom.Instance;
            if (ui?.Inventory != null && !ui.Inventory.IsOpen) ui.OpenInventory();
        }
        else if (state.CurrentRoom is EventRoom eventRoom && eventRoom.LocalMutableEvent is FakeMerchant fake)
        {
            inventory = fake.Inventory;
        }
        if (inventory == null) return NotReady("shop inventory not ready");

        MerchantEntry? entry = category switch
        {
            "card_removal" => inventory.CardRemovalEntry,
            "character_card" => slot >= 0 && slot < inventory.CharacterCardEntries.Count ? inventory.CharacterCardEntries[slot] : null,
            "colorless_card" => slot >= 0 && slot < inventory.ColorlessCardEntries.Count ? inventory.ColorlessCardEntries[slot] : null,
            "relic" => slot >= 0 && slot < inventory.RelicEntries.Count ? inventory.RelicEntries[slot] : null,
            "potion" => slot >= 0 && slot < inventory.PotionEntries.Count ? inventory.PotionEntries[slot] : null,
            _ => null,
        };
        if (entry == null) return Impossible($"shop slot {category}#{slot} not found");

        if (entry is MerchantCardRemovalEntry removal)
        {
            bool cancelable = !data.TryGetProperty("cancelable", out var cn) || cn.ValueKind != JsonValueKind.False;
            TaskHelper.RunSafely(removal.OnTryPurchaseWrapper(inventory, ignoreCost: false, cancelable: cancelable));
        }
        else
        {
            TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inventory));
        }
        return FeedResult.Fed;
    }

    private static FeedResult FeedTreasureChest()
    {
        var room = NRun.Instance?.TreasureRoom;
        if (room == null) return NotReady("treasure room not open");
        if (room._hasChestBeenOpened) return Impossible("chest already open");
        TaskHelper.RunSafely(room.OpenChest());
        return FeedResult.Fed;
    }

    // -------- Decoding and helpers --------

    internal static CombatReplayEvent? Decode(string base64)
    {
        try
        {
            var reader = new PacketReader();
            reader.Reset(Convert.FromBase64String(base64));
            return reader.Read<CombatReplayEvent>();
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"Replay: net form decode failed: {e.Message}");
            return null;
        }
    }

    private static INetAction? DecodeAction(JsonElement data) =>
        data.TryGetProperty("net", out var n) && n.GetString() is { } b64 ? Decode(b64)?.action : null;

    private static FeedResult NotReady(string why)
    {
        LastReason = why;
        return FeedResult.NotReady;
    }

    private static FeedResult Impossible(string why)
    {
        LastReason = why;
        return FeedResult.Impossible;
    }

    internal static List<T> FindAll<T>(Node start) where T : Node
    {
        var found = new List<T>();
        var stack = new Stack<Node>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T t) found.Add(t);
            for (int i = node.GetChildCount() - 1; i >= 0; i--) stack.Push(node.GetChild(i));
        }
        return found;
    }

    internal static T? FindFirst<T>(Node start) where T : Node => FindAll<T>(start).FirstOrDefault();
}
