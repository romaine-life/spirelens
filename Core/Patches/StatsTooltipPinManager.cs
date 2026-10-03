using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace SpireLens.Core.Patches;

/// <summary>
/// Pins one card, relic, or run-history tooltip set at a time. The
/// pinned set uses a dedicated native owner so the game's ordinary
/// OnUnfocus/Remove lifecycle
/// can run unchanged without dismissing it.
/// </summary>
internal static class StatsTooltipPinManager
{
    private const float CardCaptureMargin = 10f;
    private const string PinOwnerNodeName = "SpireLensPinnedStatsTooltipOwner";
    private const string LockIconNodeName = "SpireLensStatsTooltipLock";
    private const string LockIconPath =
        "res://images/ui/top_panel/reminder_lock.png";
    private const string HintOwnerNodeName = "SpireLensPinnedStatsHintOwner";
    private const string CopyImageButtonNodeName = "SpireLensCopyStatsImageButton";
    private const string CopyImageButtonTooltip = "Copy image";
    private const string CopyImageIconResourceSuffix =
        "Assets.stat-camera.svg";
    private const float CopyFeedbackDurationSeconds = 1.25f;
    private const float LockIconWidth = 24f;
    private const float LockIconHeight = 28f;
    private const float LockIconRightInset = 3f;
    private const float LockIconTopInset = 2f;
    private const float CardLockIconWidth = 48f;
    private const float CardLockIconHeight = 56f;
    private const float CardLockIconRightInset = 6f;
    private const float CardLockIconTopInset = 4f;

    private sealed class TargetSubscription
    {
        public TargetSubscription(
            Control target,
            Control.GuiInputEventHandler? guiInputHandler,
            Action treeExitingHandler)
        {
            Target = target;
            GuiInputHandler = guiInputHandler;
            TreeExitingHandler = treeExitingHandler;
        }

        public Control Target { get; }
        public Control.GuiInputEventHandler? GuiInputHandler { get; }
        public Action TreeExitingHandler { get; }
    }

    private sealed class RunHistoryContainerSubscription
    {
        public RunHistoryContainerSubscription(
            Control container,
            Node.ChildEnteredTreeEventHandler childEnteredTreeHandler,
            Action treeExitingHandler)
        {
            Container = container;
            ChildEnteredTreeHandler = childEnteredTreeHandler;
            TreeExitingHandler = treeExitingHandler;
        }

        public Control Container { get; }
        public Node.ChildEnteredTreeEventHandler ChildEnteredTreeHandler { get; }
        public Action TreeExitingHandler { get; }
    }

    private static readonly Dictionary<ulong, TargetSubscription> Subscriptions = new();
    private static readonly Dictionary<ulong, RunHistoryContainerSubscription>
        RunHistoryContainerSubscriptions = new();

    private static Control? _pinnedTarget;
    private static Control? _pinOwner;
    private static NHoverTipSet? _pinnedTipSet;
    private static Control? _pinnedStatsControl;
    private static RichTextLabel? _pinnedStatsDescription;
    private static Button? _copyImageButton;
    private static Action? _copyImageButtonHandler;
    private static int _copyImageGeneration;
    private static bool _copyImageInProgress;
    private static Control? _hintOwner;
    private static Control? _lockIconHost;
    private static string? _visibleHintText;
    private static object? _pinnedCardModel;
    private static Texture2D? _lockTexture;
    private static bool _lockLoadAttempted;
    private static ImageTexture? _copyImageIconTexture;
    private static bool _copyImageIconLoadAttempted;
    private static bool _suppressRightPressUntilRelease;

    public static void Attach(NRelicInventoryHolder? holder)
    {
        if (holder != null)
            AttachTarget(holder, subscribeToGuiInput: true);
    }

    public static void Attach(NRelicCollectionEntry? entry)
    {
        if (entry != null)
            AttachTarget(entry, subscribeToGuiInput: true);
    }

    public static void Attach(RunHistoryCampfireButton? button)
    {
        if (button != null)
            AttachTarget(button, subscribeToGuiInput: true);
    }

    public static void AttachRunHistoryHpLabel(Control? label)
    {
        if (label != null)
            AttachTarget(label, subscribeToGuiInput: true);
    }

    public static void AttachRunHistoryGoldLabel(Control? label)
    {
        if (label != null)
            AttachTarget(label, subscribeToGuiInput: true);
    }

    public static void AttachTopBarRunStatsTarget(Control? target)
    {
        if (target is NTopBarHp or NTopBarGold or NTopBarRoomIcon)
            AttachTarget(target, subscribeToGuiInput: true);
    }

    public static void AttachPotionStatsTarget(NPotionHolder? holder)
    {
        if (holder != null)
            AttachTarget(holder, subscribeToGuiInput: true);
    }

    public static void AttachRunTimerStatsTarget(Control? target)
    {
        if (target != null)
            AttachTarget(target, subscribeToGuiInput: true);
    }

    public static void AttachRunHistoryTargets(NRunHistory? runHistory)
    {
        if (!IsLive(runHistory)) return;

        AttachRunHistoryDescendants(runHistory!);

        var deckHistory = FindDescendant<NDeckHistory>(runHistory!);
        var relicHistory = FindDescendant<NRelicHistory>(runHistory!);
        WatchRunHistoryContainer(
            deckHistory?.GetNodeOrNull<Control>("%CardContainer"));
        WatchRunHistoryContainer(
            relicHistory?.GetNodeOrNull<Control>("%RelicsContainer"));
    }

    private static void AttachTarget(
        Control target,
        bool subscribeToGuiInput)
    {
        if (!IsLive(target)) return;

        var instanceId = target.GetInstanceId();
        if (Subscriptions.ContainsKey(instanceId)) return;

        Control.GuiInputEventHandler? guiInputHandler = subscribeToGuiInput
            ? inputEvent => OnGuiInput(target, inputEvent)
            : null;
        Action treeExitingHandler = () =>
            OnTargetTreeExiting(target, instanceId);

        if (guiInputHandler != null)
            target.GuiInput += guiInputHandler;
        target.TreeExiting += treeExitingHandler;
        Subscriptions[instanceId] = new TargetSubscription(
            target,
            guiInputHandler,
            treeExitingHandler);
    }

    public static void ReconcilePinnedState()
    {
        if (_pinnedTarget == null) return;

        if (!IsLive(_pinnedTarget)
            || !_pinnedTarget!.IsVisibleInTree()
            || _pinnedTarget is NCardHolder cardHolder
                && !ReferenceEquals(_pinnedCardModel, cardHolder.CardModel)
            || _pinnedTarget is NDeckHistoryEntry historyEntry
                && !ReferenceEquals(_pinnedCardModel, historyEntry.Card)
            || !IsLive(_pinOwner)
            || !IsLive(_pinnedTipSet)
            || _pinnedTipSet!.IsQueuedForDeletion())
        {
            ClearPin(restoreOrdinaryHover: false);
        }
    }

    internal static void RefreshPinnedRunTimerStats(
        Control target,
        string body)
    {
        // Do not reconcile here: the native factory asks for the stats body
        // while a new pin is still being constructed and before _pinnedTipSet
        // can be assigned. A refresh during that window should simply wait for
        // the next sampler tick.
        if (!ReferenceEquals(_pinnedTarget, target)
            || !IsLive(_pinnedTipSet)
            || _pinnedTipSet!.IsQueuedForDeletion()
            || !IsLive(_pinnedStatsDescription)
            || _pinnedStatsDescription!.IsQueuedForDeletion()
            || string.Equals(
                _pinnedStatsDescription.Text,
                body,
                StringComparison.Ordinal))
        {
            return;
        }

        _pinnedStatsDescription.Text = body;
        RunTimerStatsTooltip.AlignClearOfTarget(target, _pinnedTipSet);
    }

    public static void ClearPin()
        => ClearPin(restoreOrdinaryHover: false);

    public static void UnpinIfHolder(NRelicInventoryHolder holder)
    {
        if (ReferenceEquals(_pinnedTarget, holder))
            ClearPin(restoreOrdinaryHover: false);
    }

    public static void Teardown()
    {
        ClearPin(restoreOrdinaryHover: false);

        foreach (var subscription in Subscriptions.Values)
        {
            if (!IsLive(subscription.Target)) continue;

            if (subscription.GuiInputHandler != null)
                subscription.Target.GuiInput -= subscription.GuiInputHandler;
            subscription.Target.TreeExiting -= subscription.TreeExitingHandler;
        }

        Subscriptions.Clear();

        foreach (var subscription in RunHistoryContainerSubscriptions.Values)
        {
            if (!IsLive(subscription.Container)) continue;

            subscription.Container.ChildEnteredTree -=
                subscription.ChildEnteredTreeHandler;
            subscription.Container.TreeExiting -=
                subscription.TreeExitingHandler;
        }

        RunHistoryContainerSubscriptions.Clear();
        _lockTexture = null;
        _lockLoadAttempted = false;
        _copyImageIconTexture = null;
        _copyImageIconLoadAttempted = false;
        _suppressRightPressUntilRelease = false;
    }

    /// <summary>
    /// A pin exists only to let the pointer travel from the card/relic into its
    /// stats page. Pointer motion is therefore allowed, but the next actual
    /// mouse, keyboard, or controller action dismisses the pin without
    /// consuming the action that the game is about to handle.
    /// </summary>
    internal static void DismissOnGlobalAction(InputEvent inputEvent)
    {
        // Godot may expose different managed InputEvent wrappers to _Input and
        // the later _GuiInput/OnMousePressed dispatch for one native click.
        // Keep the dismissal guard alive for the physical button lifecycle
        // instead of comparing wrapper instance ids.
        if (IsRightRelease(inputEvent))
        {
            _suppressRightPressUntilRelease = false;
            return;
        }

        // If a release was lost while the window changed focus, a later
        // globally-observed right press is necessarily a new physical press.
        // Do not let a stale latch consume it.
        if (_suppressRightPressUntilRelease
            && IsRightPress(inputEvent)
            && _pinnedTarget == null)
        {
            _suppressRightPressUntilRelease = false;
        }

        // The copy control is the only actionable child of a pinned tooltip.
        // Preserve the pin through its press so Godot can deliver the later
        // release and Pressed signal to the button. Every other click keeps
        // the ordinary dismiss-and-continue behavior below.
        if (IsCopyImageButtonPress(inputEvent))
            return;

        if (_pinnedTarget == null || !IsDismissAction(inputEvent))
            return;

        if (IsRightPress(inputEvent))
            _suppressRightPressUntilRelease = true;

        // _Input runs before every lockable surface's control-specific input
        // handler. Remove the pin once here. If this was a right press, the
        // latch above makes the later handler consume the same physical press
        // instead of immediately pinning again.
        ClearPin(restoreOrdinaryHover: false);
    }

    internal static void HandlePinnedHintHover(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseMotion mouseMotion
            || !IsLive(_pinnedStatsDescription)
            || !_pinnedStatsDescription!.IsVisibleInTree())
        {
            return;
        }

        var description = _pinnedStatsDescription;
        if (!description.GetGlobalRect().HasPoint(mouseMotion.Position))
        {
            ClearHintPopup();
            return;
        }

        var localPosition = description.GetLocalMousePosition();
        var tooltip = description.GetTooltip(localPosition) ?? string.Empty;
        if (string.IsNullOrEmpty(tooltip))
        {
            ClearHintPopup();
            return;
        }

        ShowHintPopup(tooltip, mouseMotion.Position);
    }

    internal static bool ShouldSuppressOrdinaryHoverTip(Control owner)
    {
        // A pinned set is created through a surrogate Control owner. During
        // that CreateAndShow call, _pinnedTipSet cannot be assigned until the
        // call returns, so reconciling the surrogate here would mistake the
        // in-progress pin for a dead one and remove the SpireLens page before
        // NativeStatsHoverTipFactory can append it.
        if (ReferenceEquals(owner, _pinOwner)
            || ReferenceEquals(owner, _hintOwner)
            || IsInsidePinnedTooltip(owner))
            return false;

        ReconcilePinnedState();
        if (_pinnedTarget == null) return false;

        // The pinned target's ordinary hover lifecycle continues to fire
        // while its surrogate tooltip remains visible. Never allow that
        // owner to manufacture a duplicate set.
        if (ReferenceEquals(_pinnedTarget, owner)) return true;

        // The room icon shows its ordinary tooltip from its inner Icon
        // TextureRect, while right-clicks land on the NTopBarRoomIcon itself.
        if (_pinnedTarget is NTopBarRoomIcon roomIcon
            && ReferenceEquals(roomIcon._roomIcon, owner))
            return true;

        // Native tooltip pages deliberately ignore mouse input so clicks can
        // dismiss the pin and continue to the game underneath. Keep that
        // click-through behavior, but do not let a covered card, relic, or
        // other hover target raise a second tooltip through the pinned pages.
        return IsPointerOverPinnedTooltip();
    }

    private static bool IsInsidePinnedTooltip(Node node)
    {
        if (!IsLive(_pinnedTipSet)) return false;

        for (Node? current = node;
             current != null;
             current = current.GetParent())
        {
            if (ReferenceEquals(current, _pinnedTipSet))
                return true;
        }

        return false;
    }

    private static bool IsPointerOverPinnedTooltip()
    {
        if (!IsLive(_pinnedTipSet)) return false;

        var viewport = _pinnedTipSet!.GetViewport();
        if (viewport == null) return false;

        var pointerPosition = viewport.GetMousePosition();
        return ContainsVisibleControlAtPoint(
                   _pinnedTipSet._textHoverTipContainer,
                   pointerPosition)
            || ContainsVisibleControlAtPoint(
                _pinnedTipSet._cardHoverTipContainer,
                pointerPosition);
    }

    private static bool ContainsVisibleControlAtPoint(
        Control? root,
        Vector2 point)
    {
        if (!IsLive(root) || !root!.IsVisibleInTree()) return false;
        if (root.GetGlobalRect().HasPoint(point)) return true;

        // Some native containers derive their bounds from children only after
        // layout. Checking descendants as a fallback keeps suppression exact
        // during that first frame without turning it into a screen-wide lock.
        foreach (var child in root.GetChildren())
        {
            if (child is Control control
                && ContainsVisibleControlAtPoint(control, point))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryBuildPinnedStatsTip(
        Control owner,
        out HoverTip tip)
    {
        tip = default;
        if (!ReferenceEquals(owner, _pinOwner) || !IsLive(_pinnedTarget))
            return false;

        return TryBuildStatsTip(_pinnedTarget!, out tip);
    }

    /// <summary>
    /// Handles a card right press before NCardHolder records it as the
    /// pending alternate-click action. Returning true tells the Harmony
    /// prefix to skip the game's handler, which also prevents its matching
    /// release from emitting AltPressed.
    /// </summary>
    internal static bool TryHandleCardRightClick(
        NCardHolder holder,
        InputEvent inputEvent)
    {
        if (!IsRightPress(inputEvent)
            || !IsPassiveCardPileView(holder))
        {
            return false;
        }

        AttachTarget(holder, subscribeToGuiInput: false);
        return TryTogglePin(holder, inputEvent);
    }

    private static bool IsPassiveCardPileView(Node node)
    {
        for (var current = node.GetParent();
             current != null;
             current = current.GetParent())
        {
            if (current is NCardPileScreen or NCardsViewScreen)
                return true;
        }

        return false;
    }

    private static void OnGuiInput(
        Control target,
        InputEvent inputEvent)
    {
        if (!IsRightPress(inputEvent)) return;

        if (TryTogglePin(target, inputEvent))
            target.GetViewport()?.SetInputAsHandled();
    }

    private static bool TryTogglePin(
        Control target,
        InputEvent inputEvent)
    {
        if (_suppressRightPressUntilRelease)
        {
            // Global input has already removed the pin for this physical
            // press. Restore only the target's ordinary hover set and claim
            // the press so neither SpireLens nor the game treats it as a new
            // right-click action.
            RestoreOrdinaryHover(target);
            target.GetViewport()?.SetInputAsHandled();
            return true;
        }

        try
        {
            ReconcilePinnedState();

            if (ReferenceEquals(_pinnedTarget, target))
            {
                ClearPin(restoreOrdinaryHover: true);
            }
            else
            {
                if (!CanPin(target)) return false;
                Pin(target);
            }

            target.GetViewport()?.SetInputAsHandled();
            return true;
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Stats tooltip pin toggle failed: {e}");
            return false;
        }
    }

    private static bool CanPin(Control target)
    {
        return IsLive(target)
               && target.IsVisibleInTree()
               && ViewStatsInjectorPatch.StatsVisibilityEnabled
               && TryBuildStatsTip(target, out _)
               && TryGetNativeHoverTips(target, out _);
    }

    private static void Pin(Control target)
    {
        ClearPin(restoreOrdinaryHover: false);
        if (!CanPin(target)
            || !TryGetNativeHoverTips(target, out var nativeHoverTips))
        {
            return;
        }

        NHoverTipSet.Remove(GetOrdinaryHoverTipOwner(target));

        var pinOwner = new Control
        {
            Name = PinOwnerNodeName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
        };
        GetPinOwnerParent(target).AddChild(pinOwner);

        _pinnedTarget = target;
        _pinOwner = pinOwner;
        _pinnedCardModel = target switch
        {
            NCardHolder cardHolder => cardHolder.CardModel,
            NDeckHistoryEntry historyEntry => historyEntry.Card,
            _ => null,
        };

        try
        {
            var tipSet = NHoverTipSet.CreateAndShow(
                pinOwner,
                nativeHoverTips);
            if (tipSet == null)
            {
                ClearPin(restoreOrdinaryHover: false);
                return;
            }

            _pinnedTipSet = tipSet;
            AlignPinnedTipSet(target, tipSet);
            _pinnedStatsControl =
                NativeStatsHoverTipStyler.GetLastStatsControl(tipSet);
            _pinnedStatsDescription =
                NativeStatsHoverTipStyler.GetLastStatsDescription(tipSet);
            AttachCopyImageButton();
            AddLockIcon(target);
            CoreMain.LogDebug(
                $"Pinned stats tooltip: {GetTargetDebugId(target)}");
        }
        catch
        {
            ClearPin(restoreOrdinaryHover: false);
            throw;
        }
    }

    private static void ClearPin(bool restoreOrdinaryHover)
    {
        var target = _pinnedTarget;
        var pinOwner = _pinOwner;
        DetachCopyImageButton();
        ClearHintPopup();
        RemoveLockIcon(target);

        _pinnedTarget = null;
        _pinOwner = null;
        _pinnedTipSet = null;
        _pinnedStatsControl = null;
        _pinnedStatsDescription = null;
        _pinnedCardModel = null;

        if (IsLive(pinOwner))
        {
            NHoverTipSet.Remove(pinOwner!);
            pinOwner!.GetParent()?.RemoveChild(pinOwner);
            pinOwner.QueueFree();
        }

        if (!restoreOrdinaryHover
            || !IsLive(target)
            || !target!.IsVisibleInTree()
            || !ViewStatsInjectorPatch.StatsVisibilityEnabled)
        {
            return;
        }

        RestoreOrdinaryHover(target);
    }

    private static void AttachCopyImageButton()
    {
        DetachCopyImageButton();
        if (!IsLive(_pinnedStatsControl)) return;

        var button = EnsureCopyImageButton(_pinnedStatsControl!);
        if (!IsLive(button))
        {
            CoreMain.LogDebug(
                "Stats image copy button skipped: tooltip title header was not found.");
            return;
        }

        button!.Disabled = false;
        button.MouseFilter = Control.MouseFilterEnum.Stop;
        button.TooltipText = CopyImageButtonTooltip;
        Action handler = OnCopyImageButtonPressed;
        button.Pressed += handler;

        _copyImageButton = button;
        _copyImageButtonHandler = handler;
        _copyImageGeneration++;
        _copyImageInProgress = false;
    }

    /// <summary>
    /// Gives every rendered SpireLens panel the same final header geometry.
    /// The ordinary hover-tip instance is visual only because it disappears
    /// when the pointer leaves its owner; pinning activates that instance
    /// instead of inserting a new control and remeasuring the panel.
    /// </summary>
    internal static Button? EnsureCopyImageButton(Control statsControl)
    {
        var title = statsControl.GetNodeOrNull<Control>("%Title");
        if (title?.GetParent() is not HBoxContainer header)
            return null;

        var existing = header.GetNodeOrNull<Button>(CopyImageButtonNodeName);
        if (IsLive(existing))
        {
            MoveCopyImageButtonBeforeBrand(header, existing!);
            return existing;
        }

        var icon = GetCopyImageIcon();
        var button = new Button
        {
            Name = CopyImageButtonNodeName,
            Text = icon == null ? "Copy" : string.Empty,
            Icon = icon,
            TooltipText = CopyImageButtonTooltip,
            Flat = true,
            Disabled = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(icon == null ? 68f : 34f, 28f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        button.AddThemeFontSizeOverride("font_size", 14);
        button.AddThemeColorOverride("font_color", Color.FromHtml("#94A0AE"));
        button.AddThemeColorOverride("font_hover_color", Color.FromHtml("#E8EDF4"));
        button.AddThemeColorOverride("font_pressed_color", Color.FromHtml("#A9BCEB"));
        button.AddThemeColorOverride("font_disabled_color", Color.FromHtml("#94A0AE"));
        button.AddThemeColorOverride("icon_normal_color", Color.FromHtml("#94A0AE"));
        button.AddThemeColorOverride("icon_hover_color", Color.FromHtml("#E8EDF4"));
        button.AddThemeColorOverride("icon_pressed_color", Color.FromHtml("#A9BCEB"));
        button.AddThemeColorOverride("icon_disabled_color", Color.FromHtml("#94A0AE"));

        header.AddChild(button);
        MoveCopyImageButtonBeforeBrand(header, button);
        return button;
    }

    private static void MoveCopyImageButtonBeforeBrand(
        HBoxContainer header,
        Button button)
    {
        var brand = header.GetNodeOrNull<Label>(
            NativeStatsHoverTipStyler.BrandNodeName);
        if (IsLive(brand) && button.GetIndex() > brand!.GetIndex())
            header.MoveChild(button, brand.GetIndex());
    }

    private static void DetachCopyImageButton()
    {
        _copyImageGeneration++;
        _copyImageInProgress = false;

        var button = _copyImageButton;
        var handler = _copyImageButtonHandler;
        _copyImageButton = null;
        _copyImageButtonHandler = null;

        if (IsLive(button))
        {
            if (handler != null)
                button!.Pressed -= handler;
            button!.Disabled = true;
            button.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
    }

    private static void OnCopyImageButtonPressed()
    {
        if (_copyImageInProgress) return;
        _ = CopyPinnedStatsImageAsync();
    }

    private static async Task CopyPinnedStatsImageAsync()
    {
        if (_copyImageInProgress
            || !IsLive(_copyImageButton)
            || !IsLive(_pinnedStatsControl)
            || !IsLive(_pinnedTarget)
            || !IsLive(_pinnedTipSet))
        {
            return;
        }

        var generation = _copyImageGeneration;
        var button = _copyImageButton!;
        var statsControl = _pinnedStatsControl!;
        var pinnedTarget = _pinnedTarget!;
        var pinnedTipSet = _pinnedTipSet!;
        var lockIcon = _lockIconHost
            ?.GetNodeOrNull<CanvasItem>(LockIconNodeName);
        var lockIconWasVisible = IsLive(lockIcon) && lockIcon!.Visible;
        var copied = false;
        var feedback = "Copy failed";
        _copyImageInProgress = true;
        button.Disabled = true;
        button.Visible = false;
        if (lockIconWasVisible)
            lockIcon!.Visible = false;

        try
        {
            // Wait until the next completed draw so the hidden button is not
            // present in the viewport texture being captured.
            await statsControl.ToSignal(
                RenderingServer.Singleton,
                RenderingServer.SignalName.FramePostDraw);
            if (generation != _copyImageGeneration
                || !IsLive(statsControl)
                || !IsLive(pinnedTarget)
                || !IsLive(pinnedTipSet))
            {
                return;
            }

            if (!StatsImageCapture.TryCaptureShareImage(
                    statsControl,
                    GetRenderedSubjectRect(pinnedTarget),
                    GetTooltipCaptureGroups(pinnedTipSet),
                    out var image,
                    out var captureError))
            {
                CoreMain.Logger.Error(
                    $"Stats image capture failed: {captureError}");
                feedback = "Capture failed";
                return;
            }

            using (image)
            {
                if (!WindowsImageClipboard.TrySetImage(
                        image,
                        out var clipboardError))
                {
                    CoreMain.Logger.Error(
                        $"Stats image clipboard write failed: {clipboardError}");
                    return;
                }
            }

            copied = true;
            feedback = "Copied";
        }
        catch (Exception exception)
        {
            CoreMain.Logger.Error(
                $"Stats image copy failed: {exception}");
        }
        finally
        {
            if (lockIconWasVisible && IsLive(lockIcon))
                lockIcon!.Visible = true;

            if (generation == _copyImageGeneration && IsLive(button))
            {
                button.TooltipText = feedback;
                button.Visible = true;
                button.Disabled = false;
            }

            if (generation == _copyImageGeneration)
                _copyImageInProgress = false;
        }

        if (!copied
            || generation != _copyImageGeneration
            || !IsLive(button))
        {
            return;
        }

        try
        {
            var tree = button.GetTree();
            if (tree == null) return;

            var timer = tree.CreateTimer(CopyFeedbackDurationSeconds);
            await button.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
            if (generation == _copyImageGeneration && IsLive(button))
                button.TooltipText = CopyImageButtonTooltip;
        }
        catch (Exception exception)
        {
            CoreMain.LogDebug(
                $"Stats image copy feedback reset skipped: {exception.Message}");
        }
    }

    private static IReadOnlyList<Control> GetTooltipCaptureGroups(
        NHoverTipSet tipSet)
    {
        var groups = new List<Control>(2);
        if (IsLive(tipSet._textHoverTipContainer)
            && tipSet._textHoverTipContainer.GetChildCount() > 0)
        {
            groups.Add(tipSet._textHoverTipContainer);
        }

        if (IsLive(tipSet._cardHoverTipContainer)
            && tipSet._cardHoverTipContainer.GetChildCount() > 0)
        {
            groups.Add(tipSet._cardHoverTipContainer);
        }

        return groups;
    }

    private static Rect2 GetRenderedSubjectRect(Control target)
    {
        Control visual = target switch
        {
            NCardHolder holder when IsLive(holder.CardNode)
                => holder.CardNode!,
            NRelicInventoryHolder holder when IsLive(holder.Relic)
                => holder.Relic!,
            NDeckHistoryEntry entry
                when IsLive(entry.GetNodeOrNull<Control>("%Card"))
                => entry.GetNode<Control>("%Card"),
            NRelicBasicHolder holder when IsLive(holder.Relic)
                => holder.Relic!,
            _ => target,
        };

        if (visual is not NCard card)
            return StatsImageCapture.GetViewportRect(visual);

        // NCard draws around a zero-sized Control origin, so its ordinary
        // global rect is empty. Its frame, cost badge, and shadow also extend
        // slightly outside defaultSize; retain a small local-space margin so
        // those details survive the viewport crop at every rendered scale.
        var captureSize = NCard.defaultSize
            + Vector2.One * (CardCaptureMargin * 2f);
        return StatsImageCapture.TransformRect(
            new Rect2(-captureSize / 2f, captureSize),
            card.GetGlobalTransformWithCanvas());
    }

    private static bool TryBuildStatsTip(Control target, out HoverTip tip)
    {
        switch (target)
        {
            case NCardHolder holder:
                return CardHoverShowPatch.TryBuildNativeHoverTip(holder, out tip);

            case NRelicInventoryHolder holder:
                return RelicHoverShowPatch.TryBuildNativeHoverTip(holder, out tip);

            case NRelicCollectionEntry entry:
                return CompendiumRelicStatsContext.TryBuildNativeHoverTip(entry, out tip);

            case NDeckHistoryEntry entry:
                return RunHistoryStatsContext.TryBuildNativeCardHoverTip(entry, out tip);

            case NRelicBasicHolder holder:
                return RunHistoryStatsContext.TryBuildNativeRelicHoverTip(holder, out tip);

            case RunHistoryCampfireButton button:
                return button.TryBuildStatsTip(out tip);

            case NTopBarHp hp:
                return MaxHpHistoryTooltip.TryBuildNativeHoverTip(hp, out tip);

            case NTopBarGold gold:
                return GoldStatsTooltip.TryBuildNativeHoverTip(gold, out tip);

            case NTopBarRoomIcon:
                return EncounterStatsTooltip.TryBuildNativeHoverTip(out tip);

            case NPotionHolder holder:
                return PotionBeltStatsTooltip.TryBuildNativeHoverTip(holder, out tip);

            case Control label when RunHistoryHpTooltip.IsTarget(label):
                return RunHistoryHpTooltip.TryBuildStatsTip(label, out tip);

            case Control label when RunHistoryGoldTooltip.IsTarget(label):
                return RunHistoryGoldTooltip.TryBuildStatsTip(label, out tip);

            case Control timer when RunTimerStatsTooltip.IsTarget(timer):
                return RunTimerStatsTooltip.TryBuildStatsTip(timer, out tip);

            default:
                tip = default;
                return false;
        }
    }

    private static bool TryGetNativeHoverTips(
        Control target,
        out IEnumerable<IHoverTip> nativeHoverTips)
    {
        switch (target)
        {
            case NCardHolder holder when holder.CardModel != null:
                nativeHoverTips = holder.CardModel.HoverTips;
                return true;

            case NRelicInventoryHolder holder:
                nativeHoverTips = holder.Relic.Model.HoverTips;
                return true;

            case NRelicCollectionEntry entry
                when CompendiumRelicStatsContext.TryGetRelicModel(entry, out var relicModel):
                nativeHoverTips = relicModel.HoverTips;
                return true;

            case NDeckHistoryEntry entry when entry.Card != null:
                nativeHoverTips = entry.Card.HoverTips;
                return true;

            case NRelicBasicHolder holder when IsLive(holder.Relic):
                nativeHoverTips = holder.Relic.Model.HoverTips;
                return true;

            case RunHistoryCampfireButton:
                // The campfire summary has no stock tooltip page. The pin
                // surrogate receives its SpireLens page through
                // NativeStatsHoverTipFactory, just like appended card/relic
                // stats, so the native sequence intentionally starts empty.
                nativeHoverTips = Array.Empty<IHoverTip>();
                return true;

            case NTopBarHp:
                nativeHoverTips = new IHoverTip[]
                {
                    CreateStockHoverTip("HIT_POINTS"),
                };
                return true;

            case NTopBarGold:
                nativeHoverTips = new IHoverTip[]
                {
                    CreateStockHoverTip("MONEY_POUCH"),
                };
                return true;

            case NTopBarRoomIcon roomIcon:
                nativeHoverTips = new IHoverTip[]
                {
                    CreateStockHoverTip(roomIcon.GetHoverTipPrefixForRoomType()),
                };
                return true;

            case NPotionHolder holder:
                nativeHoverTips = holder.Potion?.Model.HoverTips
                    ?? new IHoverTip[]
                    {
                        CreateStockHoverTip("POTION_SLOT"),
                    };
                return true;

            case Control label when RunHistoryHpTooltip.IsTarget(label):
                nativeHoverTips = Array.Empty<IHoverTip>();
                return true;

            case Control label when RunHistoryGoldTooltip.IsTarget(label):
                nativeHoverTips = Array.Empty<IHoverTip>();
                return true;

            case Control timer when RunTimerStatsTooltip.IsTarget(timer):
                nativeHoverTips = Array.Empty<IHoverTip>();
                return true;

            default:
                nativeHoverTips = null!;
                return false;
        }
    }

    private static void AlignPinnedTipSet(Control target, NHoverTipSet tipSet)
    {
        switch (target)
        {
            case NCardHolder holder:
                tipSet.SetAlignmentForCardHolder(holder);
                break;

            case NRelicInventoryHolder holder:
                tipSet.SetAlignmentForRelic(holder.Relic);
                break;

            case NRelicCollectionEntry entry:
                tipSet.SetAlignment(entry, HoverTip.GetHoverTipAlignment(entry));
                break;

            case NDeckHistoryEntry entry:
                tipSet.SetAlignment(entry, HoverTip.GetHoverTipAlignment(entry));
                break;

            case NRelicBasicHolder holder:
                tipSet.SetAlignmentForRelic(holder.Relic);
                break;

            case RunHistoryCampfireButton button:
                tipSet.SetAlignment(
                    button,
                    HoverTip.GetHoverTipAlignment(button));
                break;

            case NTopBarHp or NTopBarGold:
                AlignTopBarTipSet(target, tipSet);
                break;

            case NTopBarRoomIcon roomIcon:
                AlignRoomIconTipSet(roomIcon, tipSet);
                break;

            case NPotionHolder holder:
                AlignPotionTipSet(holder, tipSet);
                break;

            case Control label when RunHistoryHpTooltip.IsTarget(label):
                tipSet.SetAlignment(
                    label,
                    HoverTip.GetHoverTipAlignment(label));
                break;

            case Control label when RunHistoryGoldTooltip.IsTarget(label):
                tipSet.SetAlignment(
                    label,
                    HoverTip.GetHoverTipAlignment(label));
                break;

            case Control timer when RunTimerStatsTooltip.IsTarget(timer):
                RunTimerStatsTooltip.AlignClearOfTarget(timer, tipSet);
                break;
        }
    }

    private static void RestoreOrdinaryHover(Control target)
    {
        if (!CanPin(target)
            || !TryGetNativeHoverTips(target, out var nativeHoverTips))
        {
            return;
        }

        switch (target)
        {
            case NCardHolder holder:
                NHoverTipSet.CreateAndShow(target, nativeHoverTips)
                    ?.SetAlignmentForCardHolder(holder);
                break;

            case NRelicInventoryHolder holder:
                NHoverTipSet.CreateAndShow(target, nativeHoverTips)
                    ?.SetAlignmentForRelic(holder.Relic);
                break;

            case NRelicCollectionEntry entry:
                NHoverTipSet.CreateAndShow(
                        target,
                        nativeHoverTips,
                        HoverTip.GetHoverTipAlignment(entry))
                    ?.SetFollowOwner();
                break;

            // Run-history card rows do not create an ordinary hover-tip set.
            // Their pinned set is therefore removed without manufacturing a
            // new transient tooltip when the pin is released.

            case NRelicBasicHolder holder:
                NHoverTipSet.CreateAndShow(target, nativeHoverTips)
                    ?.SetAlignmentForRelic(holder.Relic);
                break;

            case RunHistoryCampfireButton button:
                RunHistoryCampfireSummary.ShowTooltip(button);
                break;

            case NTopBarHp or NTopBarGold:
                var tipSet = NHoverTipSet.CreateAndShow(target, nativeHoverTips);
                if (tipSet != null)
                    AlignTopBarTipSet(target, tipSet);
                break;

            case NTopBarRoomIcon roomIcon:
                var roomTipSet = NHoverTipSet.CreateAndShow(
                    roomIcon._roomIcon,
                    nativeHoverTips);
                if (roomTipSet != null)
                    AlignRoomIconTipSet(roomIcon, roomTipSet);
                break;

            case NPotionHolder holder:
                var potionTipSet = NHoverTipSet.CreateAndShow(
                    target,
                    nativeHoverTips,
                    HoverTipAlignment.Center);
                if (potionTipSet != null)
                    AlignPotionTipSet(holder, potionTipSet);
                break;

            case Control label when RunHistoryHpTooltip.IsTarget(label):
                RunHistoryHpTooltip.ShowTooltip(label);
                break;

            case Control label when RunHistoryGoldTooltip.IsTarget(label):
                RunHistoryGoldTooltip.ShowTooltip(label);
                break;

            case Control timer when RunTimerStatsTooltip.IsTarget(timer):
                RunTimerStatsTooltip.ShowTooltip(timer);
                break;
        }
    }

    private static string GetTargetDebugId(Control target)
    {
        return target switch
        {
            NCardHolder holder when holder.CardModel != null
                => holder.CardModel.Id.ToString(),
            NRelicInventoryHolder holder => holder.Relic.Model.Id.ToString(),
            NRelicCollectionEntry entry
                when CompendiumRelicStatsContext.TryGetRelicModel(entry, out var relicModel)
                => relicModel.Id.ToString(),
            NDeckHistoryEntry entry when entry.Card != null
                => entry.Card.Id.ToString(),
            NRelicBasicHolder holder when IsLive(holder.Relic)
                => holder.Relic.Model.Id.ToString(),
            RunHistoryCampfireButton => "run-history-campfires",
            NTopBarHp => "live-run-hp",
            NTopBarGold => "live-run-gold",
            NTopBarRoomIcon => "live-run-encounter",
            NPotionHolder holder => holder.Potion?.Model.Id.ToString()
                ?? "empty-potion-slot",
            Control label when RunHistoryHpTooltip.IsTarget(label)
                => "run-history-hp",
            Control label when RunHistoryGoldTooltip.IsTarget(label)
                => "run-history-gold",
            Control timer when RunTimerStatsTooltip.IsTarget(timer)
                => "run-timer-stats",
            _ => target.Name,
        };
    }

    private static HoverTip CreateStockHoverTip(string localizationKey)
    {
        return new HoverTip(
            new LocString("static_hover_tips", $"{localizationKey}.title"),
            new LocString("static_hover_tips", $"{localizationKey}.description"));
    }

    private static void AlignTopBarTipSet(
        Control target,
        NHoverTipSet tipSet)
    {
        tipSet.SetGlobalPosition(
            target.GlobalPosition + new Vector2(0f, target.Size.Y + 20f));
    }

    /// <summary>
    /// Mirrors NTopBarRoomIcon.OnFocus, which places its set below the inner
    /// Icon using the outer control's height.
    /// </summary>
    private static void AlignRoomIconTipSet(
        NTopBarRoomIcon roomIcon,
        NHoverTipSet tipSet)
    {
        tipSet.SetGlobalPosition(
            roomIcon._roomIcon.GlobalPosition
            + new Vector2(0f, roomIcon.Size.Y + 20f));
    }

    /// <summary>
    /// The control the game passes as owner for the target's ordinary tooltip.
    /// </summary>
    private static Control GetOrdinaryHoverTipOwner(Control target)
        => target is NTopBarRoomIcon roomIcon && IsLive(roomIcon._roomIcon)
            ? roomIcon._roomIcon
            : target;

    private static void AlignPotionTipSet(
        NPotionHolder holder,
        NHoverTipSet tipSet)
    {
        tipSet.SetGlobalPosition(
            holder.GlobalPosition
            + Vector2.Down
            * holder.Size.Y
            * Mathf.Max(1.5f, holder.Scale.Y));
        tipSet.SetAlignment(holder, HoverTipAlignment.Center);
    }

    private static Node GetPinOwnerParent(Control target)
    {
        if (!UsesLayoutNeutralPinOverlay(target))
            return target;

        var root = target.GetTree()?.Root;
        return root != null ? root : target;
    }

    private static bool UsesLayoutNeutralPinOverlay(Control target)
        => target is NTopBarHp or NTopBarGold or NTopBarRoomIcon or NPotionHolder;

    private static void ShowHintPopup(string tooltip, Vector2 pointerPosition)
    {
        if (string.Equals(tooltip, _visibleHintText, StringComparison.Ordinal)
            && IsLive(_hintOwner))
        {
            return;
        }

        ClearHintPopup();
        if (!IsLive(_pinOwner)) return;

        var hintOwner = new Control
        {
            Name = HintOwnerNodeName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
            Size = Vector2.One,
        };
        _pinOwner!.AddChild(hintOwner);
        hintOwner.GlobalPosition = pointerPosition + new Vector2(18f, 18f);

        _hintOwner = hintOwner;
        _visibleHintText = tooltip;
        try
        {
            var hintSet = NHoverTipSet.CreateAndShow(
                hintOwner,
                StatsTooltip.CreateNativeHint(tooltip),
                HoverTipAlignment.Right);
            if (hintSet == null)
                ClearHintPopup();
        }
        catch
        {
            ClearHintPopup();
            throw;
        }
    }

    private static void ClearHintPopup()
    {
        var hintOwner = _hintOwner;
        _hintOwner = null;
        _visibleHintText = null;
        if (!IsLive(hintOwner)) return;

        NHoverTipSet.Remove(hintOwner!);
        hintOwner!.GetParent()?.RemoveChild(hintOwner);
        hintOwner.QueueFree();
    }

    private static void AddLockIcon(Control target)
    {
        RemoveLockIcon(target);
        var texture = GetLockTexture();
        var host = GetLockIconHost(target);
        if (texture == null || !IsLive(host)) return;

        var isFullCard = target is NCardHolder;
        var width = isFullCard ? CardLockIconWidth : LockIconWidth;
        var height = isFullCard ? CardLockIconHeight : LockIconHeight;
        var rightInset = isFullCard
            ? CardLockIconRightInset
            : LockIconRightInset;
        var topInset = isFullCard
            ? CardLockIconTopInset
            : LockIconTopInset;

        var lockIcon = new TextureRect
        {
            Name = LockIconNodeName,
            Texture = texture,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        var usesLayoutNeutralOverlay = UsesLayoutNeutralPinOverlay(target);
        if (isFullCard)
        {
            // NCard draws its 300x422 card centered around a zero-sized
            // Control origin. Anchoring to NCard's Control rect therefore
            // targets the center of the artwork, not its top-right corner.
            // Position against the visual bounds reported by the game.
            lockIcon.Position = new Vector2(
                (NCard.defaultSize.X / 2f) - rightInset - width,
                (-NCard.defaultSize.Y / 2f) + topInset);
            lockIcon.Size = new Vector2(width, height);
        }
        else if (!usesLayoutNeutralOverlay)
        {
            lockIcon.AnchorLeft = 1f;
            lockIcon.AnchorRight = 1f;
            lockIcon.AnchorTop = 0f;
            lockIcon.AnchorBottom = 0f;
            lockIcon.OffsetLeft = -(rightInset + width);
            lockIcon.OffsetRight = -rightInset;
            lockIcon.OffsetTop = topInset;
            lockIcon.OffsetBottom = topInset + height;
        }

        host!.AddChild(lockIcon);
        if (usesLayoutNeutralOverlay)
        {
            // Top-bar counters and potion holders participate in container
            // layout and may clip children. Keep the badge in the same
            // root-level overlay as the pin surrogate, then place it over the
            // rendered target without affecting any minimum-size calculation.
            var targetRect = GetRenderedSubjectRect(target);
            lockIcon.Size = new Vector2(width, height);
            lockIcon.GlobalPosition = new Vector2(
                targetRect.Position.X + targetRect.Size.X - rightInset - width,
                targetRect.Position.Y + topInset);
            lockIcon.ZIndex = 1000;
        }
        _lockIconHost = host;
    }

    private static Control GetLockIconHost(Control target)
    {
        if (UsesLayoutNeutralPinOverlay(target) && IsLive(_pinOwner))
            return _pinOwner!;

        // Card holders are interaction/layout slots whose bounds can be much
        // larger than the rendered card. NCard owns the visual transform; its
        // centered visual bounds are handled explicitly in AddLockIcon.
        if (target is NCardHolder holder
            && IsLive(holder.CardNode))
        {
            return holder.CardNode!;
        }

        if (target is NDeckHistoryEntry historyEntry
            && IsLive(historyEntry.GetNodeOrNull<Control>("%Card")))
        {
            return historyEntry.GetNode<Control>("%Card");
        }

        if (target is NRelicBasicHolder basicHolder
            && IsLive(basicHolder.Relic))
        {
            return basicHolder.Relic;
        }

        return target;
    }

    private static void AttachRunHistoryDescendants(Node node)
    {
        switch (node)
        {
            case NDeckHistoryEntry entry:
                AttachTarget(entry, subscribeToGuiInput: true);
                break;

            case NRelicBasicHolder holder
                when RunHistoryStatsContext.HasAncestor<NRelicHistory>(holder):
                AttachTarget(holder, subscribeToGuiInput: true);
                break;
        }

        foreach (var child in node.GetChildren())
            AttachRunHistoryDescendants(child);
    }

    private static void WatchRunHistoryContainer(Control? container)
    {
        if (!IsLive(container)) return;

        var instanceId = container!.GetInstanceId();
        if (RunHistoryContainerSubscriptions.ContainsKey(instanceId)) return;

        Node.ChildEnteredTreeEventHandler childEnteredTreeHandler =
            AttachRunHistoryDescendants;
        Action treeExitingHandler = () =>
            OnRunHistoryContainerTreeExiting(container, instanceId);

        container.ChildEnteredTree += childEnteredTreeHandler;
        container.TreeExiting += treeExitingHandler;
        RunHistoryContainerSubscriptions[instanceId] =
            new RunHistoryContainerSubscription(
                container,
                childEnteredTreeHandler,
                treeExitingHandler);
    }

    private static void OnRunHistoryContainerTreeExiting(
        Control container,
        ulong instanceId)
    {
        if (RunHistoryContainerSubscriptions.Remove(
                instanceId,
                out var subscription)
            && IsLive(container))
        {
            container.ChildEnteredTree -= subscription.ChildEnteredTreeHandler;
            container.TreeExiting -= subscription.TreeExitingHandler;
        }
    }

    private static T? FindDescendant<T>(Node node) where T : Node
    {
        if (node is T match) return match;

        foreach (var child in node.GetChildren())
        {
            var descendant = FindDescendant<T>(child);
            if (descendant != null) return descendant;
        }

        return null;
    }

    private static void RemoveLockIcon(Control? target)
    {
        var recordedHost = _lockIconHost;
        _lockIconHost = null;
        RemoveLockIconFromHost(recordedHost);

        if (!IsLive(target)) return;

        var currentHost = GetLockIconHost(target!);
        if (!ReferenceEquals(currentHost, recordedHost))
            RemoveLockIconFromHost(currentHost);
    }

    private static void RemoveLockIconFromHost(Control? host)
    {
        if (!IsLive(host)) return;

        var lockIcon = host!.GetNodeOrNull<TextureRect>(LockIconNodeName);
        if (!IsLive(lockIcon)) return;

        lockIcon!.GetParent()?.RemoveChild(lockIcon);
        lockIcon.QueueFree();
    }

    private static Texture2D? GetLockTexture()
    {
        if (_lockLoadAttempted) return _lockTexture;
        _lockLoadAttempted = true;

        _lockTexture = ResourceLoader.Load<Texture2D>(
            LockIconPath,
            null,
            ResourceLoader.CacheMode.Reuse);
        if (_lockTexture == null)
        {
            CoreMain.Logger.Error(
                $"Could not load stats tooltip lock icon: {LockIconPath}");
        }

        return _lockTexture;
    }

    private static Texture2D? GetCopyImageIcon()
    {
        if (_copyImageIconLoadAttempted) return _copyImageIconTexture;
        _copyImageIconLoadAttempted = true;

        try
        {
            var assembly = typeof(StatsTooltipPinManager).Assembly;
            var resourceName = Array.Find(
                assembly.GetManifestResourceNames(),
                name => name.EndsWith(
                    CopyImageIconResourceSuffix,
                    StringComparison.Ordinal));
            if (resourceName == null)
                throw new InvalidOperationException("embedded camera icon was not found");

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("embedded camera icon could not be opened");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            using var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            var loadError = image.LoadSvgFromBuffer(buffer.ToArray(), 1f);
            if (loadError != Error.Ok)
                throw new InvalidOperationException($"SVG loader returned {loadError}");

            _copyImageIconTexture = ImageTexture.CreateFromImage(image);
        }
        catch (Exception exception)
        {
            CoreMain.Logger.Error(
                $"Could not load stats image camera icon: {exception.Message}");
        }

        return _copyImageIconTexture;
    }

    private static void OnTargetTreeExiting(
        Control target,
        ulong instanceId)
    {
        if (ReferenceEquals(_pinnedTarget, target))
            ClearPin(restoreOrdinaryHover: false);

        Subscriptions.Remove(instanceId);
    }

    private static bool IsDismissAction(InputEvent inputEvent)
    {
        return inputEvent switch
        {
            InputEventMouseButton mouseButton => mouseButton.Pressed,
            InputEventKey key => key.Pressed && !key.Echo,
            InputEventJoypadButton button => button.Pressed,
            InputEventJoypadMotion motion => Math.Abs(motion.AxisValue) >= 0.5f,
            InputEventScreenTouch touch => touch.Pressed,
            InputEventScreenDrag => true,
            InputEventGesture => true,
            InputEventAction action => action.Pressed,
            _ => false,
        };
    }

    private static bool IsRightPress(InputEvent inputEvent)
        => inputEvent is InputEventMouseButton
        {
            ButtonIndex: MouseButton.Right,
            Pressed: true,
        };

    private static bool IsRightRelease(InputEvent inputEvent)
        => inputEvent is InputEventMouseButton
        {
            ButtonIndex: MouseButton.Right,
            Pressed: false,
        };

    private static bool IsCopyImageButtonPress(InputEvent inputEvent)
    {
        return inputEvent is InputEventMouseButton
            {
                ButtonIndex: MouseButton.Left,
                Pressed: true,
            } mouseButton
            && IsLive(_copyImageButton)
            && _copyImageButton!.Visible
            && !_copyImageButton.Disabled
            && _copyImageButton.GetGlobalRect().HasPoint(mouseButton.Position);
    }

    private static bool IsLive(GodotObject? instance)
        => instance != null && GodotObject.IsInstanceValid(instance);
}
