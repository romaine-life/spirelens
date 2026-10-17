using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;

namespace SpireLens.Core.Patches;

/// <summary>
/// M4 phase 1: inject a sibling "View Stats" tickbox next to the game's
/// existing "View Upgrades" tickbox on the in-run deck view screen.
///
/// Scene structure (discovered by the DeckViewScreenProbePatch on 2026-04-19):
///   DeckViewScreen
///     ViewUpgrades              (MarginContainer, holds the whole block)
///       MarginContainer
///         Upgrades              (NUpgradePreviewTickbox)
///           ViewUpgradesLabel   (MegaLabel — sits INSIDE the tickbox)
///
/// We hook NCardsViewScreen.ConnectSignals (the post-ready wiring method;
/// _Ready throws on derived types), gate on NDeckViewScreen specifically
/// so we don't inject on unrelated card-view surfaces, duplicate the
/// entire ViewUpgrades subtree, rename the clone's inner nodes, change the
/// label text, force the clone visible even when controller mode hid the
/// source tickbox, offset the position, and connect our own toggle handler.
///
/// Phase-1 scope: checkbox renders and logs when toggled. Tooltip/description
/// override is phase 2.
/// </summary>
[HarmonyPatch(typeof(NCardsViewScreen), "ConnectSignals")]
public static class ViewStatsInjectorPatch
{
    // Track the most recently injected tickbox so phase 2 can read its state.
    public static NTickbox? LastInjectedTickbox { get; private set; }

    public static NTickbox? LastInjectedShowCardsNotInDeckTickbox { get; private set; }

    public static NTickbox? LastInjectedEnemyStatsTickbox { get; private set; }

    public static NTickbox? LastInjectedCardStatsTickbox { get; private set; }

    // Track the active deck-view screen so the toggle handler can trigger
    // a live re-render (via DisplayCards) when the user flips the checkbox.
    // Without this, toggling the mode wouldn't switch the active deck screen
    // between live-deck and not-in-deck contents until it was reopened.
    public static NDeckViewScreen? LastInjectedDeckView { get; private set; }

    // Track the cloned containers so Shutdown can QueueFree() them on hot-reload.
    // Otherwise the injected checkbox stays on screen after unload, referencing
    // a dead assembly and potentially causing exceptions when clicked.
    private static readonly List<Node> _injectedClones = new();
    private static readonly string[] InjectedCloneNames =
    [
        "ViewStatsShortcut",
        "ViewStats",
        "ViewRemovedCards",
        "ViewEnemyStats",
        "ViewCombatCardStats",
        "ViewRelicStatsBeforeCollected",
    ];

    // Persist the user's checkbox choices across deck-view open/close cycles
    // AND across Core hot reloads. Each Core load refreshes them from the
    // loader-backed mod config, and each toggle writes the complete set back
    // so changing one control never resets another.
    private static bool _persistedTicked;
    private static bool _persistedShowRemovedCardsTicked = true;
    private static bool _persistedShowAllMetaCardsInNotInDeckView;
    private static bool _persistedEnemyStatsTicked;
    private static bool _persistedCardStatsTicked;
    private static bool _persistedHideNonCombatRelicStats;
    private static bool _persistedShowCombatOnlyRelicsAtCombatScreen;
    private static bool _prefsLoaded;

    public static bool CardStatsEnabled
    {
        get
        {
            EnsurePrefsLoaded();
            return _persistedCardStatsTicked;
        }
    }

    public static bool StatsVisibilityEnabled
    {
        get
        {
            EnsurePrefsLoaded();
            return _persistedTicked;
        }
    }

    public static bool ShowCardsNotInDeckEnabled
    {
        get
        {
            EnsurePrefsLoaded();
            return _persistedShowRemovedCardsTicked;
        }
    }

    public static bool ShowAllMetaCardsInNotInDeckView
    {
        get
        {
            EnsurePrefsLoaded();
            return _persistedShowAllMetaCardsInNotInDeckView;
        }
    }

    public static bool EnemyStatsEnabled
    {
        get
        {
            EnsurePrefsLoaded();
            return _persistedEnemyStatsTicked;
        }
    }

    public static bool HideNonCombatRelicStats
    {
        get
        {
            EnsurePrefsLoaded();
            return _persistedHideNonCombatRelicStats;
        }
    }

    public static bool ShowCombatOnlyRelicsAtCombatScreen
    {
        get
        {
            EnsurePrefsLoaded();
            return _persistedShowCombatOnlyRelicsAtCombatScreen;
        }
    }

    /// <summary>
    /// Toggle the same persisted global visibility state as the deck-view
    /// checkbox. Input shortcuts and UI controls share this path so the live
    /// checkbox, tooltip state, and loader-backed config cannot drift apart.
    /// </summary>
    public static bool ToggleStatsVisibility(string source)
    {
        EnsurePrefsLoaded();
        SetStatsVisibilityEnabled(!_persistedTicked, source);
        return _persistedTicked;
    }

    public static void SetStatsVisibilityEnabled(bool isEnabled, string source)
    {
        EnsurePrefsLoaded();
        _persistedTicked = isEnabled;
        SetTickboxVisualState(LastInjectedTickbox, isEnabled);
        SavePreferences();

        if (!isEnabled)
        {
            StatsTooltipPinManager.ClearPin();
            NHoverTipSet.Clear();
        }

        CoreMain.Logger.Info($"Stats visibility set to {isEnabled} ({source})");
    }

    public static void SetCardStatsEnabled(bool isEnabled, string source)
    {
        EnsurePrefsLoaded();
        _persistedCardStatsTicked = isEnabled;
        SetTickboxVisualState(LastInjectedCardStatsTickbox, isEnabled);
        SavePreferences();
        if (!isEnabled)
            NHoverTipSet.Clear();
        CoreMain.Logger.Info($"Card stats set to {isEnabled} ({source})");
    }

    public static void SetEnemyStatsEnabled(bool isEnabled, string source)
    {
        EnsurePrefsLoaded();
        _persistedEnemyStatsTicked = isEnabled;
        SetTickboxVisualState(LastInjectedEnemyStatsTickbox, isEnabled);
        SavePreferences();
        if (!isEnabled)
            NHoverTipSet.Clear();
        CoreMain.Logger.Info($"Monster stats set to {isEnabled} ({source})");
    }

    public static void SetShowCardsNotInDeckEnabled(bool isEnabled, string source)
    {
        EnsurePrefsLoaded();
        _persistedShowRemovedCardsTicked = isEnabled;
        SetTickboxVisualState(LastInjectedShowCardsNotInDeckTickbox, isEnabled);
        SavePreferences();
        CoreMain.Logger.Info($"Show cards not in deck set to {isEnabled} ({source})");
        RefreshDeckView();
    }

    public static void SetShowAllMetaCardsInNotInDeckView(
        bool isEnabled,
        string source)
    {
        EnsurePrefsLoaded();
        _persistedShowAllMetaCardsInNotInDeckView = isEnabled;
        SavePreferences();
        CoreMain.Logger.Info(
            $"Show all meta-cards in not-in-deck view set to {isEnabled} ({source})");
        RefreshDeckView();
    }

    public static void SetHideNonCombatRelicStats(bool isEnabled, string source)
    {
        EnsurePrefsLoaded();
        _persistedHideNonCombatRelicStats = isEnabled;
        if (isEnabled)
            _persistedShowCombatOnlyRelicsAtCombatScreen = false;
        SavePreferences();
        if (isEnabled)
            NHoverTipSet.Clear();
        RelicBarFilterPatch.RefreshAll();
        CoreMain.Logger.Info($"Force combat-only relic bar set to {isEnabled} ({source})");
    }

    public static void SetShowCombatOnlyRelicsAtCombatScreen(bool isEnabled, string source)
    {
        EnsurePrefsLoaded();
        _persistedShowCombatOnlyRelicsAtCombatScreen = isEnabled;
        if (isEnabled)
            _persistedHideNonCombatRelicStats = false;
        SavePreferences();
        RelicBarFilterPatch.RefreshAll();
        CoreMain.Logger.Info($"Combat-screen-only relic filter set to {isEnabled} ({source})");
    }

    /// <summary>
    /// Load the checkbox state from disk on first use. Called lazily from
    /// Inject so it happens before the first deck view opens and also
    /// after hot reload (since the static flag resets).
    /// </summary>
    private static void EnsurePrefsLoaded()
    {
        if (_prefsLoaded) return;
        _prefsLoaded = true;
        try
        {
            var prefs = PrefsStorage.Load();
            _persistedTicked = prefs.ViewStatsTicked;
            _persistedShowRemovedCardsTicked = prefs.ShowRemovedCardsTicked;
            _persistedShowAllMetaCardsInNotInDeckView =
                prefs.ShowAllMetaCardsInNotInDeckView;
            _persistedEnemyStatsTicked = prefs.ShowEnemyStatsTicked;
            // Keep the legacy preference field as the on-disk compatibility
            // key even though the setting now applies to every card surface.
            _persistedCardStatsTicked = prefs.ShowCombatCardStatsTicked;
            _persistedHideNonCombatRelicStats = prefs.HideNonCombatRelicStats;
            _persistedShowCombatOnlyRelicsAtCombatScreen = prefs.ShowCombatOnlyRelicsAtCombatScreen;
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"ViewStatsInjector: loading prefs failed: {e}");
        }
    }

    /// <summary>
    /// The Mods page changed the config underneath this cache. Re-read it
    /// without writing anything back (the next toggle saves the complete set,
    /// so a stale cache would silently undo the page's edit), then apply the
    /// same side effects the setters would have.
    /// </summary>
    public static void ReloadFromConfig(string source)
    {
        EnsurePrefsLoaded();
        bool oldVisibility = _persistedTicked;
        bool oldCardStats = _persistedCardStatsTicked;
        bool oldEnemyStats = _persistedEnemyStatsTicked;
        bool oldNotInDeck = _persistedShowRemovedCardsTicked;
        bool oldAllMeta = _persistedShowAllMetaCardsInNotInDeckView;
        bool oldHideNonCombat = _persistedHideNonCombatRelicStats;
        bool oldCombatScreen = _persistedShowCombatOnlyRelicsAtCombatScreen;

        _prefsLoaded = false;
        EnsurePrefsLoaded();

        SetTickboxVisualState(LastInjectedTickbox, _persistedTicked);
        SetTickboxVisualState(LastInjectedCardStatsTickbox, _persistedCardStatsTicked);
        SetTickboxVisualState(LastInjectedEnemyStatsTickbox, _persistedEnemyStatsTicked);
        SetTickboxVisualState(LastInjectedShowCardsNotInDeckTickbox, _persistedShowRemovedCardsTicked);

        if (oldVisibility && !_persistedTicked)
            StatsTooltipPinManager.ClearPin();
        if ((oldVisibility && !_persistedTicked)
            || (oldCardStats && !_persistedCardStatsTicked)
            || (oldEnemyStats && !_persistedEnemyStatsTicked)
            || (!oldHideNonCombat && _persistedHideNonCombatRelicStats))
            NHoverTipSet.Clear();
        if (oldHideNonCombat != _persistedHideNonCombatRelicStats
            || oldCombatScreen != _persistedShowCombatOnlyRelicsAtCombatScreen)
            RelicBarFilterPatch.RefreshAll();
        if (oldNotInDeck != _persistedShowRemovedCardsTicked
            || oldAllMeta != _persistedShowAllMetaCardsInNotInDeckView)
            RefreshDeckView();

        CoreMain.Logger.Info($"SpireLens settings reloaded from config ({source})");
    }

    /// <summary>
    /// On hot-reload Initialize, scan the scene tree for an active
    /// NDeckViewScreen and re-inject into it. Without this, if the user
    /// was viewing the deck at reload time, their Shutdown-freed checkbox
    /// wouldn't come back until they closed and reopened the screen (the
    /// ConnectSignals hook only fires on screen open, not on live screens).
    /// </summary>
    public static void ReinjectIntoActiveDeckView()
    {
        try
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree == null) return;
            var deckView = FindDeckViewRecursive(tree.Root);
            if (deckView == null) return;

            CoreMain.Logger.Info("ViewStatsInjector: re-injecting into already-open deck view after hot reload");
            LastInjectedDeckView = deckView;
            Inject(deckView);
            DeckViewSortMenu.Inject(deckView);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"ReinjectIntoActiveDeckView failed: {e}");
        }
    }

    private static NDeckViewScreen? FindDeckViewRecursive(Node node)
    {
        if (node is NDeckViewScreen dv) return dv;
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            var found = FindDeckViewRecursive(node.GetChild(i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Called by CoreMain.Shutdown before unloading. QueueFrees the injected
    /// clone node so it's removed from the scene tree; Godot will clean up
    /// the underlying resources next frame. After this, the user won't see
    /// our checkbox until they reopen the deck view (which triggers the
    /// re-loaded patch on the next ConnectSignals) — UNLESS the deck view
    /// is still open and Initialize's ReinjectIntoActiveDeckView catches it.
    /// </summary>
    public static void TeardownInjectedUI()
    {
        foreach (var clone in _injectedClones)
        {
            if (clone != null && Godot.GodotObject.IsInstanceValid(clone))
            {
                clone.QueueFree();
            }
        }
        if (_injectedClones.Count > 0)
            CoreMain.Logger.Info($"ViewStatsInjector: {_injectedClones.Count} cloned containers queued for removal");
        _injectedClones.Clear();
        DeckViewSortMenu.Teardown();
        LastInjectedTickbox = null;
        LastInjectedShowCardsNotInDeckTickbox = null;
        LastInjectedEnemyStatsTickbox = null;
        LastInjectedCardStatsTickbox = null;
    }

    [HarmonyPostfix]
    public static void Postfix(NCardsViewScreen __instance)
    {
        // Only inject on the in-run deck-view surface for now. Other CardsView
        // subclasses (compendium, card selection dialogs) aren't in scope until
        // we validate semantics per-screen — e.g. Compendium stats would need
        // lifetime aggregation (#2).
        if (__instance is not NDeckViewScreen deckView) return;

        // The run-history viewer reuses this screen type. The tickboxes below
        // are live-run controls and stay out of it, but the sort menu belongs
        // on both surfaces — it reads archived numbers there.
        if (RunHistoryDeckViewer.IsHistoricalDeckViewer(deckView))
        {
            DeckViewSortMenu.Inject(deckView);
            return;
        }

        // Remember the active deck view so OnStatsToggled can trigger a
        // live re-render (switch card collections instantly on toggle).
        LastInjectedDeckView = deckView;

        try { Inject(__instance); }
        catch (Exception e) { CoreMain.Logger.Error($"ViewStatsInjector failed: {e}"); }

        DeckViewSortMenu.Inject(deckView);
    }

    private static void Inject(NCardsViewScreen screen)
    {
        EnsurePrefsLoaded();

        var viewUpgradesContainer = screen.GetNodeOrNull("ViewUpgrades");
        if (viewUpgradesContainer == null)
        {
            CoreMain.Logger.Warn("ViewStatsInjector: ViewUpgrades container not found — scene structure may have changed.");
            return;
        }

        RemoveExistingInjectedControls(viewUpgradesContainer.GetParent());

        var shortcut = new Button
        {
            Name = "ViewStatsShortcut",
            Text = "Open SpireLens menu\nRS / Left Shift",
            CustomMinimumSize = new Vector2(300, 72),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        shortcut.AddThemeFontSizeOverride("font_size", 20);
        shortcut.Pressed += () => SpireLensOptionsMenu.Toggle("deck-view button");

        var parent = viewUpgradesContainer.GetParent();
        parent.AddChild(shortcut);
        if (viewUpgradesContainer is Control original)
            shortcut.Position = original.Position + new Vector2(0, -80);
        _injectedClones.Add(shortcut);

        CoreMain.Logger.Info("ViewStatsInjector: injected deck-view menu shortcut");
    }

    private static void RemoveExistingInjectedControls(Node? parent)
    {
        if (parent == null) return;

        int removed = 0;
        foreach (var cloneName in InjectedCloneNames)
        {
            for (int i = parent.GetChildCount() - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (!string.Equals(child.Name.ToString(), cloneName, StringComparison.Ordinal))
                    continue;

                parent.RemoveChild(child);
                child.QueueFree();
                _injectedClones.Remove(child);
                removed++;
            }
        }

        if (removed > 0)
            CoreMain.Logger.Info($"ViewStatsInjector: queued {removed} stale injected controls for removal");
    }

    private static void InjectTickbox(
        Node viewUpgradesContainer,
        string cloneName,
        string tickboxName,
        string labelName,
        string labelText,
        Vector2 offset,
        bool isTicked,
        Action<NTickbox> onToggled,
        Action<NTickbox> rememberTickbox)
    {
        // Default Duplicate() copies structure + scripts. Runtime-connected signals
        // (made via .Connect() in ConnectSignals) are per-instance and NOT copied,
        // so our clone starts with no inherited toggle handlers — good.
        var clone = (Node)viewUpgradesContainer.Duplicate();
        clone.Name = cloneName;

        // Rename the inner tickbox so GetNode lookups don't collide with the original %Upgrades.
        var innerTickbox = FindChildOfType<NUpgradePreviewTickbox>(clone);
        if (innerTickbox != null)
        {
            innerTickbox.Name = tickboxName;
            // NCardsViewScreen.ConnectSignals calls OnControllerStateUpdated
            // before this postfix runs. When the deck was opened with a
            // controller (for example, Left Bumper), that hides the source
            // %Upgrades tickbox, so Duplicate() otherwise gives every
            // SpireLens clone Visible=false. Our controls remain useful in
            // controller mode and must not inherit that presentation choice.
            innerTickbox.Visible = true;
            // Connect our toggle handler. Use the Toggled signal defined on NTickbox.
            innerTickbox.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(onToggled));
            rememberTickbox(innerTickbox);
        }
        else
        {
            CoreMain.Logger.Warn($"ViewStatsInjector: inner tickbox not found in {cloneName}");
        }

        // Update the label text. MegaLabel.SetTextAutoSize takes a raw string.
        var label = FindChildOfType<MegaLabel>(clone);
        if (label != null)
        {
            label.Name = labelName;
            label.SetTextAutoSize(labelText);
            // TODO(i18n): pull from localization once we have keys
        }
        else
        {
            CoreMain.Logger.Warn($"ViewStatsInjector: label not found in {cloneName}");
        }

        // Godot runs the clone's _Ready() during AddChild(). NTickbox._Ready()
        // calls ConnectSignals(), which looks up %TickboxVisuals before our
        // post-add field rewiring below. Duplicate() does not reliably preserve
        // the unique-name owner relationship, so restore it before insertion.
        if (innerTickbox != null)
        {
            SetOwnerRecursive(clone, clone);
            var preAddVisuals = innerTickbox.GetNodeOrNull<Control>("TickboxVisuals");
            if (preAddVisuals != null)
                preAddVisuals.UniqueNameInOwner = true;
        }

        // Insert into the same parent as the original (the DeckViewScreen).
        var parent = viewUpgradesContainer.GetParent();
        parent.AddChild(clone);

        // Track for Shutdown (hot-reload cleanup).
        _injectedClones.Add(clone);

        // Rewire the clone's visuals to point at its OWN subtree.
        //
        // NTickbox.ConnectSignals uses %TickboxVisuals unique-name lookup,
        // which scopes to the scene root. After duplication there are two
        // nodes marked %TickboxVisuals under DeckViewScreen — the clone's
        // lookup resolves to the original's, so clicking the clone toggles
        // the original's visuals (or neither, depending on resolution order).
        //
        // Publicizer gives us access to the private _tickedImage /
        // _notTickedImage / _imageContainer fields on NTickbox; we reassign
        // them to the clone's own nodes via relative paths.
        if (innerTickbox != null)
        {
            var cloneVisuals = innerTickbox.GetNodeOrNull<Control>("TickboxVisuals");
            if (cloneVisuals != null)
            {
                // Give the clone its own material instance. Godot's Duplicate()
                // shares Resource references by default (materials are Resources),
                // so without this our hover-tween shader changes would mutate
                // the ORIGINAL checkbox's rendering too. Duplicating the material
                // gives the clone independent shader state.
                if (cloneVisuals.Material != null)
                {
                    cloneVisuals.Material = (Material)cloneVisuals.Material.Duplicate();
                }

                innerTickbox._imageContainer = cloneVisuals;
                innerTickbox._tickedImage = cloneVisuals.GetNodeOrNull<Control>("Ticked");
                innerTickbox._notTickedImage = cloneVisuals.GetNodeOrNull<Control>("NotTicked");
                // Rewire the remaining NTickbox fields that OnFocus/OnRelease
                // tweens animate. Without these, mousing over the clone ran
                // shader/scale tweens against the original's material and
                // produced visual glitches (clone visuals vanishing).
                innerTickbox._hsv = cloneVisuals.Material as ShaderMaterial;
                innerTickbox._baseScale = cloneVisuals.Scale;

                // Initial render: restore the user's last choice. The tickbox
                // clone always constructs unticked, but the player expects
                // their "I want stats on" preference to survive closing and
                // re-opening the deck view within the same session.
                SetTickboxVisualState(innerTickbox, isTicked);
            }
            else
            {
                CoreMain.Logger.Warn("ViewStatsInjector: clone's TickboxVisuals not found — cannot rewire");
            }
        }

        // Offset position — put it above the original ViewUpgrades.
        // MarginContainer is a Control, so Position is set relative to the
        // original. Empirical offset; may need adjustment once we see it.
        if (clone is Control control && viewUpgradesContainer is Control origControl)
        {
            control.Position = origControl.Position + offset;
        }

        CoreMain.Logger.Info($"ViewStatsInjector: injected {cloneName} — tickbox={innerTickbox != null} label={label != null}");
    }

    private static void OnStatsToggled(NTickbox tickbox)
    {
        SetStatsVisibilityEnabled(tickbox.IsTicked, "deck-view checkbox");
    }

    private static void OnCardStatsToggled(NTickbox tickbox)
    {
        SetCardStatsEnabled(tickbox.IsTicked, "deck-view checkbox");
    }

    private static void OnEnemyStatsToggled(NTickbox tickbox)
    {
        SetEnemyStatsEnabled(tickbox.IsTicked, "deck-view checkbox");
    }

    private static void OnShowCardsNotInDeckToggled(NTickbox tickbox)
    {
        SetShowCardsNotInDeckEnabled(tickbox.IsTicked, "deck-view checkbox");
    }

    internal static void RefreshDeckView()
    {
        try
        {
            if (LastInjectedDeckView != null && Godot.GodotObject.IsInstanceValid(LastInjectedDeckView))
                LastInjectedDeckView.DisplayCards();
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"DisplayCards re-render failed: {e.Message}");
        }
    }

    private static void SavePreferences()
    {
        PrefsStorage.Save(new Prefs
        {
            ViewStatsTicked = _persistedTicked,
            ShowRemovedCardsTicked = _persistedShowRemovedCardsTicked,
            ShowAllMetaCardsInNotInDeckView =
                _persistedShowAllMetaCardsInNotInDeckView,
            ShowEnemyStatsTicked = _persistedEnemyStatsTicked,
            // Preserve the legacy wire/storage name for existing installs and
            // for compatibility with the stable Loader across Core reloads.
            ShowCombatCardStatsTicked = _persistedCardStatsTicked,
            HideNonCombatRelicStats = _persistedHideNonCombatRelicStats,
            ShowCombatOnlyRelicsAtCombatScreen = _persistedShowCombatOnlyRelicsAtCombatScreen,
        });
    }

    private static void SetTickboxVisualState(NTickbox? tickbox, bool isTicked)
    {
        if (tickbox == null || !GodotObject.IsInstanceValid(tickbox)) return;

        if (tickbox._tickedImage != null)
            tickbox._tickedImage.Visible = isTicked;
        if (tickbox._notTickedImage != null)
            tickbox._notTickedImage.Visible = !isTicked;
        tickbox.IsTicked = isTicked;
    }

    private static void SetOwnerRecursive(Node node, Node owner)
    {
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            var child = node.GetChild(i);
            child.Owner = owner;
            SetOwnerRecursive(child, owner);
        }
    }

    /// <summary>
    /// Depth-first search for a child of the given type anywhere in the node's
    /// subtree. Same helper pattern STS2MCP uses for its settings injection.
    /// </summary>
    private static T? FindChildOfType<T>(Node parent) where T : class
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is T typed) return typed;
            if (child is Node childNode)
            {
                var found = FindChildOfType<T>(childNode);
                if (found != null) return found;
            }
        }
        return null;
    }
}
