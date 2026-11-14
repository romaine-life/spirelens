using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core.Patches;

/// <summary>
/// Presentation-only filter for the standard in-run relic bar. Relics remain
/// owned and functional, and every non-bar surface continues to show them.
/// </summary>
[HarmonyPatch(typeof(NRelicInventoryHolder), nameof(NRelicInventoryHolder._Ready))]
public static class RelicBarFilterPatch
{
    private static readonly HashSet<RelicModel> ResolvedCombatRelics =
        new(ReferenceEqualityComparer.Instance);
    private static bool _hooksInitialized;
    private static NMapScreen? _hookedMapScreen;
    private static EventInfo? _combatBeganEvent;
    private static Delegate? _combatBeganHandler;

    [HarmonyPostfix]
    public static void Postfix(NRelicInventoryHolder __instance)
    {
        try
        {
            StatsTooltipPinManager.Attach(__instance);
            ApplyToHolder(__instance);
        }
        catch (Exception e) { CoreMain.Logger.Error($"RelicBarFilterPatch failed: {e.Message}"); }
    }

    public static void InitializeHooks()
    {
        if (_hooksInitialized)
        {
            AttachMapScreenHooks();
            return;
        }

        AttachCombatBeganHook();
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        _hooksInitialized = true;
        AttachMapScreenHooks();
        CoreMain.Logger.Info(
            "Relic bar filter hooks wired (CombatSetUp, CombatBegan, CombatEnded, map Opened/Closed).");
    }

    public static void TeardownHooks()
    {
        DetachMapScreenHooks();
        ResolvedCombatRelics.Clear();
        if (!_hooksInitialized) return;

        DetachCombatBeganHook();
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        _hooksInitialized = false;
        CoreMain.Logger.Info("Relic bar filter hooks unwired.");
    }

    public static void RefreshAll(string reason = "requested")
    {
        try
        {
            AttachMapScreenHooks();
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null) return;
            var shouldFilter = ShouldFilterNow();
            var currentTurn = GetCurrentPlayerTurnNumber();
            var holders = 0;
            var classified = 0;
            var hidden = 0;
            var focusedRelic = tree.Root.GetViewport()?.GuiGetFocusOwner()
                as NRelicInventoryHolder;
            RefreshRecursive(
                tree.Root,
                shouldFilter,
                currentTurn,
                ref holders,
                ref classified,
                ref hidden);
            StatsTooltipPinManager.ReconcilePinnedState();
            RefreshVisibleNavigation(focusedRelic);

            var capstone = NCapstoneContainer.Instance?.CurrentCapstoneScreen?.GetType().Name ?? "none";
            CoreMain.Logger.Info(
                $"Relic bar filter refresh ({reason}): combat={CombatManager.Instance?.IsInProgress == true}, " +
                $"turn={currentTurn}, capstone={capstone}, filter={shouldFilter}, holders={holders}, " +
                $"not_relevant={classified}, hidden={hidden}.");
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"RelicBarFilter refresh failed: {e.Message}");
        }
    }

    internal static bool IsNonCombatRelic(RelicModel relicModel)
        => IsEffectivelyNonCombat(
            RelicClassificationStore.IsNonCombat(relicModel),
            relicModel.IsUsedUp,
            ResolvedCombatRelics.Contains(relicModel)
            || HasNativeTerminalCombatState(relicModel));

    internal static bool IsEffectivelyNonCombat(
        bool isClassifiedNonCombat,
        bool isUsedUp,
        bool firedThisCombat)
        => isClassifiedNonCombat || isUsedUp || firedThisCombat;

    internal static void MarkRelicFired(RelicModel? relicModel)
    {
        try
        {
            if (relicModel == null) return;
            if (!RelicClassificationStore.GetCombatRelevantUntilTurn(relicModel).HasValue
                && !IsTerminalCombatRelic(relicModel))
            {
                return;
            }

            if (!ResolvedCombatRelics.Add(relicModel)) return;

            var relicId = RelicClassificationStore.GetRelicId(relicModel);
            CoreMain.LogDebug(
                $"Relic combat relevance resolved for this combat: {relicId} fired.");
            if (_hooksInitialized)
                RefreshAll($"{relicId} fired");
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"Could not mark fired relic non-combat: {e.Message}");
        }
    }

    /// <summary>
    /// Relics whose remaining combat value ends at a native, combat-local
    /// terminal state. Unlike finite-turn relics, these have no turn fallback:
    /// activation or irreversible disqualification is the only resolution
    /// signal, and the game resets the state for the next combat.
    /// </summary>
    internal static bool IsTerminalCombatRelic(RelicModel relicModel)
        => relicModel is BurningSticks
            or CentennialPuzzle
            or LavaLamp
            or PaelsEye
            or Permafrost
            or RuinedHelmet
            or ThrowingAxe
            or UnsettlingLamp
            or Vambrace;

    /// <summary>
    /// Reads the game's authoritative state as well as the transient Flash
    /// ledger so a Core reload during combat reconstructs the same projection.
    /// </summary>
    internal static bool HasNativeTerminalCombatState(RelicModel relicModel)
        => relicModel switch
        {
            BurningSticks relic => relic.WasUsedThisCombat,
            CentennialPuzzle relic => relic.UsedThisCombat,
            LavaLamp relic => relic.TookDamageThisCombat,
            PaelsEye relic => relic.UsedThisCombat,
            Permafrost relic => relic.ActivatedThisCombat,
            RuinedHelmet relic => relic.UsedThisCombat,
            ThrowingAxe relic => relic.UsedThisCombat,
            UnsettlingLamp relic => relic.IsFinishedTriggering,
            Vambrace relic => relic.BlockGainedThisCombat,
            _ => false,
        };

    internal static void RefreshIfRelicUsedUp(RelicModel? relicModel)
    {
        try
        {
            if (relicModel?.IsUsedUp != true || !_hooksInitialized) return;
            RefreshAll($"{RelicClassificationStore.GetRelicId(relicModel)} used up");
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"Could not refresh used-up relic state: {e.Message}");
        }
    }

    internal static bool IsCombatRelevantNow(RelicModel relicModel, int currentTurn)
    {
        if (IsNonCombatRelic(relicModel)) return false;

        var relevantUntilTurn = RelicClassificationStore.GetCombatRelevantUntilTurn(relicModel);
        return IsBeforeCombatRelevanceCutoff(currentTurn, relevantUntilTurn);
    }

    internal static bool IsBeforeCombatRelevanceCutoff(int currentTurn, int? hiddenStartingTurn)
        => !hiddenStartingTurn.HasValue
           || currentTurn <= 0
           || currentTurn < hiddenStartingTurn.Value;

    internal static bool ShouldFilterForContext(
        bool forceCombatRelicsOnly,
        bool showCombatOnlyAtCombatScreen,
        bool isCombatInProgress,
        bool isActMapOpen,
        bool isDeckViewOpen)
    {
        if (forceCombatRelicsOnly) return true;
        if (!showCombatOnlyAtCombatScreen || !isCombatInProgress) return false;
        return !isActMapOpen && !isDeckViewOpen;
    }

    internal static int FindNearestVisibleIndex(
        IReadOnlyList<bool> visible,
        int sourceIndex)
    {
        if (visible == null || visible.Count == 0) return -1;
        if (sourceIndex < 0 || sourceIndex >= visible.Count)
            return visible.ToList().FindIndex(value => value);

        for (var distance = 1; distance < visible.Count; distance++)
        {
            var rightIndex = (sourceIndex + distance) % visible.Count;
            if (visible[rightIndex]) return rightIndex;

            var leftIndex = (sourceIndex - distance + visible.Count) % visible.Count;
            if (visible[leftIndex]) return leftIndex;
        }

        return visible[sourceIndex] ? sourceIndex : -1;
    }

    internal static void RefreshVisibleNavigation(
        NRelicInventoryHolder? focusedRelicBeforeRefresh = null)
    {
        try
        {
            var run = NRun.Instance;
            var globalUi = run?.GlobalUi;
            var relicInventory = globalUi?.RelicInventory;
            var topBar = globalUi?.TopBar;
            if (relicInventory == null
                || topBar == null
                || !GodotObject.IsInstanceValid(relicInventory)
                || !GodotObject.IsInstanceValid(topBar))
            {
                return;
            }

            var relicNodes = relicInventory.RelicNodes
                .Where(node => node != null && GodotObject.IsInstanceValid(node))
                .ToArray();
            var visibleRelics = relicNodes
                .Where(node => node.Visible)
                .ToArray();

            var firstVisibleRelic = visibleRelics.FirstOrDefault();
            var activeScreenProxy = topBar.ActiveScreenProxy;
            var downTarget = (Control?)firstVisibleRelic;
            if (downTarget == null
                && activeScreenProxy != null
                && GodotObject.IsInstanceValid(activeScreenProxy))
            {
                downTarget = activeScreenProxy;
            }

            if (downTarget != null)
            {
                var downPath = downTarget.GetPath();
                foreach (var control in new Control?[]
                         {
                             topBar.Gold,
                             topBar.Hp,
                             topBar.FloorIcon,
                             topBar.RoomIcon,
                             topBar.BossIcon,
                         })
                {
                    if (control != null && GodotObject.IsInstanceValid(control))
                        control.FocusNeighborBottom = downPath;
                }

                SetPotionDownNeighbors(topBar.PotionContainer, downPath);
            }

            Control? firstPotionControl = topBar.PotionContainer?._holders.FirstOrDefault();
            for (var i = 0; i < visibleRelics.Length; i++)
            {
                var relic = visibleRelics[i];
                var left = visibleRelics[(i - 1 + visibleRelics.Length) % visibleRelics.Length];
                var right = visibleRelics[(i + 1) % visibleRelics.Length];
                relic.FocusNeighborLeft = left.GetPath();
                relic.FocusNeighborRight = right.GetPath();
                relic.FocusNeighborTop =
                    firstPotionControl != null && GodotObject.IsInstanceValid(firstPotionControl)
                        ? firstPotionControl.GetPath()
                        : relic.GetPath();
            }

            RepairHiddenRelicFocus(
                relicNodes,
                focusedRelicBeforeRefresh
                ?? relicInventory.GetViewport()?.GuiGetFocusOwner() as NRelicInventoryHolder,
                activeScreenProxy);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Relic bar navigation refresh failed: {e.Message}");
        }
    }

    private static void ApplyToHolder(NRelicInventoryHolder holder)
    {
        if (holder == null || !GodotObject.IsInstanceValid(holder)) return;
        var relicModel = holder.Relic?.Model;
        if (relicModel == null) return;

        var shouldFilter = holder.GetTree() != null && ShouldFilterNow();
        holder.Visible = !shouldFilter
                         || IsCombatRelevantNow(relicModel, GetCurrentPlayerTurnNumber());
        if (!holder.Visible)
            StatsTooltipPinManager.UnpinIfHolder(holder);
    }

    private static void SetPotionDownNeighbors(Node? node, NodePath downPath)
    {
        if (node == null || !GodotObject.IsInstanceValid(node)) return;

        if (node is NPotionHolder holder)
            holder.FocusNeighborBottom = downPath;

        for (var i = 0; i < node.GetChildCount(); i++)
            SetPotionDownNeighbors(node.GetChild(i), downPath);
    }

    private static void RepairHiddenRelicFocus(
        IReadOnlyList<NRelicInventoryHolder> relicNodes,
        NRelicInventoryHolder? focusedRelic,
        Control? activeScreenProxy)
    {
        if (focusedRelic == null
            || !GodotObject.IsInstanceValid(focusedRelic)
            || focusedRelic.Visible)
        {
            return;
        }

        var sourceIndex = -1;
        var visibility = new bool[relicNodes.Count];
        for (var i = 0; i < relicNodes.Count; i++)
        {
            visibility[i] = relicNodes[i].Visible;
            if (ReferenceEquals(relicNodes[i], focusedRelic))
                sourceIndex = i;
        }

        if (sourceIndex < 0) return;

        var targetIndex = FindNearestVisibleIndex(visibility, sourceIndex);
        if (targetIndex >= 0)
        {
            relicNodes[targetIndex].GrabFocus();
            return;
        }

        if (activeScreenProxy != null && GodotObject.IsInstanceValid(activeScreenProxy))
            activeScreenProxy.GrabFocus();
    }

    private static bool ShouldFilterNow()
    {
        return ShouldFilterForContext(
            ViewStatsInjectorPatch.HideNonCombatRelicStats,
            ViewStatsInjectorPatch.ShowCombatOnlyRelicsAtCombatScreen,
            CombatManager.Instance?.IsInProgress == true,
            NMapScreen.Instance?.IsOpen == true,
            NCapstoneContainer.Instance?.CurrentCapstoneScreen is NDeckViewScreen);
    }

    private static void AttachMapScreenHooks()
    {
        var currentMapScreen = NMapScreen.Instance;
        if (ReferenceEquals(_hookedMapScreen, currentMapScreen)
            && (currentMapScreen == null || GodotObject.IsInstanceValid(currentMapScreen)))
        {
            return;
        }

        DetachMapScreenHooks();
        if (currentMapScreen == null || !GodotObject.IsInstanceValid(currentMapScreen)) return;

        currentMapScreen.Opened += OnMapOpened;
        currentMapScreen.Closed += OnMapClosed;
        _hookedMapScreen = currentMapScreen;
    }

    private static void DetachMapScreenHooks()
    {
        var mapScreen = _hookedMapScreen;
        _hookedMapScreen = null;
        if (mapScreen == null || !GodotObject.IsInstanceValid(mapScreen)) return;

        mapScreen.Opened -= OnMapOpened;
        mapScreen.Closed -= OnMapClosed;
    }

    private static void RefreshRecursive(
        Node node,
        bool shouldFilter,
        int currentTurn,
        ref int holders,
        ref int classified,
        ref int hidden)
    {
        if (node is NRelicInventoryHolder holder)
        {
            StatsTooltipPinManager.Attach(holder);
            holders++;
            var relicModel = holder.Relic?.Model;
            if (relicModel != null)
            {
                var isRelevant = IsCombatRelevantNow(relicModel, currentTurn);
                if (!isRelevant) classified++;
                var shouldHide = shouldFilter && !isRelevant;
                holder.Visible = !shouldHide;
                if (shouldHide)
                    StatsTooltipPinManager.UnpinIfHolder(holder);
                if (shouldHide) hidden++;
            }
        }

        for (var i = 0; i < node.GetChildCount(); i++)
        {
            RefreshRecursive(
                node.GetChild(i),
                shouldFilter,
                currentTurn,
                ref holders,
                ref classified,
                ref hidden);
        }
    }

    private static int GetCurrentPlayerTurnNumber()
    {
        if (CombatManager.Instance?.IsInProgress != true) return 0;

        var players = RunManager.Instance?.State?.Players;
        if (players == null || players.Count == 0) return 0;
        return players.Max(player => player.PlayerCombatState?.TurnNumber ?? 0);
    }

    private static void OnCombatSetUp(CombatState _)
    {
        ResolvedCombatRelics.Clear();
        RefreshAll("combat set up");
    }

    private static void OnCombatBegan(CombatState _) => RefreshAll("combat began");

    private static void OnCombatEnded(CombatRoom _)
    {
        ResolvedCombatRelics.Clear();
        RefreshAll("combat ended");
    }

    private static void AttachCombatBeganHook()
    {
        var manager = CombatManager.Instance;
        var eventInfo = manager.GetType().GetEvent(
            "CombatBegan",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var handlerType = eventInfo?.EventHandlerType;
        var handlerMethod = typeof(RelicBarFilterPatch).GetMethod(
            nameof(OnCombatBegan),
            BindingFlags.Static | BindingFlags.NonPublic);
        if (eventInfo == null || handlerType == null || handlerMethod == null)
            return;

        var handler = Delegate.CreateDelegate(handlerType, handlerMethod);
        eventInfo.AddEventHandler(manager, handler);
        _combatBeganEvent = eventInfo;
        _combatBeganHandler = handler;
    }

    private static void DetachCombatBeganHook()
    {
        var eventInfo = _combatBeganEvent;
        var handler = _combatBeganHandler;
        _combatBeganEvent = null;
        _combatBeganHandler = null;
        if (eventInfo == null || handler == null) return;

        eventInfo.RemoveEventHandler(CombatManager.Instance, handler);
    }

    private static void OnMapOpened() => RefreshAll("act map opened");

    private static void OnMapClosed() => RefreshAll("act map closed");
}

/// <summary>
/// A configured finite-turn relic or a native terminal-combat relic becomes
/// presentation-only non-combat as soon as its activation flash fires. Turn
/// cutoffs remain fallback behavior only for relics explicitly given one.
/// </summary>
[HarmonyPatch(
    typeof(RelicModel),
    nameof(RelicModel.Flash),
    new[] { typeof(IEnumerable<Creature>) })]
public static class RelicBarFilterRelicFlashPatch
{
    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance)
        => RelicBarFilterPatch.MarkRelicFired(__instance);
}

/// <summary>
/// Lava Lamp has no positive activation flash. Qualifying damage permanently
/// disqualifies its reward upgrade for the current combat, so the game's saved
/// combat-local flag is its terminal visibility signal.
/// </summary>
[HarmonyPatch(typeof(LavaLamp), nameof(LavaLamp.AfterDamageReceived))]
public static class RelicBarFilterLavaLampDamagePatch
{
    [HarmonyPostfix]
    public static void Postfix(LavaLamp __instance)
    {
        if (__instance.TookDamageThisCombat)
            RelicBarFilterPatch.MarkRelicFired(__instance);
    }
}

/// <summary>
/// Limited-use relics expose their terminal state through IsUsedUp and switch
/// to Disabled through this setter. Refresh the projection at that exact state
/// change so they leave the filtered combat bar immediately.
/// </summary>
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Status), MethodType.Setter)]
public static class RelicBarFilterRelicStatusPatch
{
    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance)
        => RelicBarFilterPatch.RefreshIfRelicUsedUp(__instance);
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class RelicBarFilterAfterPlayerTurnStartPatch
{
    [HarmonyPostfix]
    public static void Postfix() => RelicBarFilterPatch.RefreshAll("player turn started");
}

[HarmonyPatch(typeof(NCapstoneContainer), nameof(NCapstoneContainer.Open))]
public static class RelicBarFilterCapstoneOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix() => RelicBarFilterPatch.RefreshAll("capstone opened");
}

[HarmonyPatch(typeof(NCapstoneContainer), nameof(NCapstoneContainer.Close))]
public static class RelicBarFilterCapstoneClosePatch
{
    [HarmonyPostfix]
    public static void Postfix() => RelicBarFilterPatch.RefreshAll("capstone closed");
}

/// <summary>
/// The game's three navigation passes always target the first owned relic and
/// include every owned relic in the horizontal cycle. Reapply SpireLens's
/// visible-only projection after any of those passes overwrite it.
/// </summary>
[HarmonyPatch(typeof(NRelicInventory), "UpdateNavigation")]
public static class RelicInventoryVisibleNavigationPatch
{
    [HarmonyPostfix]
    public static void Postfix() => RelicBarFilterPatch.RefreshVisibleNavigation();
}

/// <summary>
/// Before STS2 v0.113 the potion container sets its own navigation; from v0.113
/// NTopBar.UpdateNavigation does, which the top-bar patch already covers.
/// </summary>
[HarmonyPatch]
public static class PotionContainerVisibleRelicNavigationPatch
{
    private static MethodBase? TargetMethod()
        => AccessTools.DeclaredMethod(typeof(NPotionContainer), "UpdateNavigation");

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPostfix]
    public static void Postfix() => RelicBarFilterPatch.RefreshVisibleNavigation();
}

[HarmonyPatch(typeof(NTopBar), "UpdateNavigation")]
public static class TopBarVisibleRelicNavigationPatch
{
    [HarmonyPostfix]
    public static void Postfix() => RelicBarFilterPatch.RefreshVisibleNavigation();
}
