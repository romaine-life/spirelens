using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using SpireLens.Core.Patches;

namespace SpireLens.Core;

/// <summary>
/// Global modal options surface. It lives in a high CanvasLayer under the
/// scene root so it remains available above every game screen and consumes
/// normal mouse/controller input while visible.
///
/// This is the only place SpireLens settings are edited; the BaseLib Mods page
/// just points here. Settings persist in the Loader-owned BaseLib config and
/// are written through <see cref="RuntimeOptionsProvider"/>. Rows that need
/// the Loader's generic setter stay visible but disabled when the running
/// Loader predates it, because a Core hot reload cannot update the Loader.
/// </summary>
public static class SpireLensOptionsMenu
{
    private const int Layer = 1000;
    private const string RestartNeededNote = "needs a game restart";
    private const string PotionHistoryText = "View current-run potion history";

    /// <summary>
    /// Cycle order for the build-date zone row. These are the Loader's
    /// BuildDisplayTimeZone member names, which the bridge validates.
    /// </summary>
    private static readonly string[] BuildTimeZoneNames =
        ["Pacific", "Eastern", "Central", "Mountain", "UTC", "Local"];

    private static readonly Color HeaderColor = new(0.72f, 0.8f, 0.92f);
    private static readonly Color UnavailableColor = new(0.55f, 0.55f, 0.55f);
    private static readonly Color QuietColor = new(0.62f, 0.62f, 0.62f);

    private sealed class ToggleRow(
        string text,
        Func<bool> get,
        Action<bool, string> set,
        Func<bool>? isAvailable)
    {
        public string Text { get; } = text;
        public Func<bool> Get { get; } = get;
        public Action<bool, string> Set { get; } = set;
        public bool Available => isAvailable?.Invoke() ?? true;
        public Button Checkbox { get; set; } = null!;
        public Button Indicator { get; set; } = null!;
    }

    private static CanvasLayer? _layer;
    private static ScrollContainer? _scroll;

    // Indexed by selection index: every selectable row has one entry in each.
    private static readonly List<Button> SelectableButtons = new();
    private static readonly List<Control> SelectableRows = new();
    private static readonly List<Panel> SelectionHighlights = new();

    private static readonly Dictionary<int, ToggleRow> ToggleRows = new();
    private static readonly Dictionary<int, Action> RowActions = new();
    private static Button? _restartRoomButton;
    private static int _restartRoomIndex = -1;
    private static Button? _potionHistoryButton;
    private static Button? _buildTimeZoneButton;
    private static Button? _replayButton;
    private static Label? _buildInfoLabel;
    private static bool _restartArmed;
    private static int _selectedIndex;
    private static int _leftStickVerticalDirection;
    private static int _leftStickHorizontalDirection;
    private static bool _leftTriggerPressed;

    public static bool IsOpen =>
        _layer != null && GodotObject.IsInstanceValid(_layer) && _layer.Visible;

    public static void Toggle(string source)
    {
        if (IsOpen)
            Close(source);
        else
            Open(source);
    }

    public static void Open(string source)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;

        if (_layer == null || !GodotObject.IsInstanceValid(_layer))
            Build(tree);

        RuntimeOptionsProvider.Refresh();
        RefreshToggleRows();
        _restartArmed = false;
        RefreshRestartRoomRow();
        RefreshPotionHistoryRow();
        RefreshReplayRow();
        RefreshBuildTimeZoneRow();
        RefreshBuildInfo();
        _layer!.Visible = true;
        _selectedIndex = Math.Clamp(_selectedIndex, 0, SelectableButtons.Count - 1);
        _leftStickVerticalDirection = 0;
        _leftStickHorizontalDirection = 0;
        _leftTriggerPressed = false;
        RefreshSelectionHighlight();
        ScrollSelectedIntoView();
        CoreMain.Logger.Info($"SpireLens options menu opened ({source})");
    }

    public static void Close(string source)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
        _layer.Visible = false;
        _restartArmed = false;
        RefreshRestartRoomRow();
        CoreMain.Logger.Info($"SpireLens options menu closed ({source})");
    }

    public static bool HandleShortcut(InputEvent evt)
    {
        if (!IsOpen) return false;

        if (evt is InputEventKey { Pressed: true, Echo: false } key
            && (key.Keycode == Key.Escape || key.PhysicalKeycode == Key.Escape))
        {
            Close("Escape key");
            return true;
        }

        if (evt is InputEventJoypadMotion motion)
            return HandleLeftStick(motion);

        if (evt is InputEventJoypadButton { Pressed: true } button)
        {
            switch (button.ButtonIndex)
            {
                case JoyButton.DpadUp:
                    MoveFocus(-1);
                    return true;
                case JoyButton.DpadDown:
                    MoveFocus(1);
                    return true;
                case JoyButton.DpadLeft:
                    JumpToEdge(first: true);
                    return true;
                case JoyButton.DpadRight:
                    JumpToEdge(first: false);
                    return true;
                case JoyButton.LeftShoulder:
                    JumpToEdge(first: true);
                    return true;
                case JoyButton.A:
                    ActivateSelection(_selectedIndex, "menu confirm");
                    return true;
                default:
                    Close("menu controller button");
                    return true;
            }
        }
        return false;
    }

    public static void Destroy()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _layer = null;
        _scroll = null;
        SelectableButtons.Clear();
        SelectableRows.Clear();
        SelectionHighlights.Clear();
        ToggleRows.Clear();
        RowActions.Clear();
        _restartRoomButton = null;
        _restartRoomIndex = -1;
        _potionHistoryButton = null;
        _buildTimeZoneButton = null;
        _buildInfoLabel = null;
        _replayButton = null;
        _restartArmed = false;
        _selectedIndex = 0;
        _leftStickVerticalDirection = 0;
        _leftStickHorizontalDirection = 0;
        _leftTriggerPressed = false;
    }

    private static void Build(SceneTree tree)
    {
        _layer = new CanvasLayer
        {
            Name = "SpireLensOptionsMenu",
            Layer = Layer,
            Visible = false,
        };
        tree.Root.AddChild(_layer);

        var blocker = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.72f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        blocker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(blocker);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        blocker.AddChild(center);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(780, 0),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 44);
        margin.AddThemeConstantOverride("margin_right", 44);
        margin.AddThemeConstantOverride("margin_top", 34);
        margin.AddThemeConstantOverride("margin_bottom", 34);
        panel.AddChild(margin);

        var frame = new VBoxContainer();
        frame.AddThemeConstantOverride("separation", 18);
        margin.AddChild(frame);

        var title = NewLabel("SpireLens Options", 32);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        frame.AddChild(title);

        var help = NewLabel("Up/Down selects • A toggles or opens • Left/Right or LB/LT jumps • Esc closes", 18);
        help.HorizontalAlignment = HorizontalAlignment.Center;
        help.Modulate = new Color(0.82f, 0.82f, 0.82f);
        frame.AddChild(help);

        // The rows outgrew a 1080p-tall panel once every setting moved here,
        // so they scroll between the fixed title and Close button. Keyboard
        // and controller selection scroll the highlighted row into view.
        _scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 640),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        frame.AddChild(_scroll);

        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 18);
        _scroll.AddChild(rows);

        AddToggle(rows, "SpireLens: on/off",
            () => ViewStatsInjectorPatch.StatsVisibilityEnabled,
            ViewStatsInjectorPatch.SetStatsVisibilityEnabled);
        AddToggle(rows, "SpireLens: card stats",
            () => ViewStatsInjectorPatch.CardStatsEnabled,
            ViewStatsInjectorPatch.SetCardStatsEnabled);
        AddToggle(rows, "Show monster stats",
            () => ViewStatsInjectorPatch.EnemyStatsEnabled,
            ViewStatsInjectorPatch.SetEnemyStatsEnabled);
        AddToggle(rows, "Show cards not in deck",
            () => ViewStatsInjectorPatch.ShowCardsNotInDeckEnabled,
            ViewStatsInjectorPatch.SetShowCardsNotInDeckEnabled);
        AddToggle(rows, "Show all meta-cards in \"not in deck\" view",
            () => ViewStatsInjectorPatch.ShowAllMetaCardsInNotInDeckView,
            ViewStatsInjectorPatch.SetShowAllMetaCardsInNotInDeckView);

        AddHeader(rows, "Cards in hand");
        AddToggle(rows, "Show card stats when hovering the hand",
            () => RuntimeOptionsProvider.Current.ShowHandTooltips,
            SetHandCardStats,
            () => RuntimeOptionsProvider.CanSetOption);
        // Every Loader has the dedicated verbose setter, so this row never
        // needs the generic one.
        AddToggle(rows, "Full card stats in hand (instead of compact)",
            () => RuntimeOptionsProvider.Current.UseVerboseHandStats,
            SetVerboseHandStats);

        AddHeader(rows, "Relic bar filter — choose one");
        AddToggle(rows, "Show combat-only relics at the combat screen",
            () => ViewStatsInjectorPatch.ShowCombatOnlyRelicsAtCombatScreen,
            ViewStatsInjectorPatch.SetShowCombatOnlyRelicsAtCombatScreen);
        AddToggle(rows, "Force show only combat relics on all screens",
            () => ViewStatsInjectorPatch.HideNonCombatRelicStats,
            ViewStatsInjectorPatch.SetHideNonCombatRelicStats);

        AddHeader(rows, "Performance & diagnostics");
        AddToggle(rows, "Pause card tracking during combat",
            () => RuntimeOptionsProvider.Current.DisableCardStatsDuringCombat,
            SetPauseCardTrackingDuringCombat,
            () => RuntimeOptionsProvider.CanSetOption);
        AddToggle(rows, "Debug logging",
            () => RuntimeOptionsProvider.Current.EnableDebugLogging,
            SetDebugLogging,
            () => RuntimeOptionsProvider.CanSetOption);
        _buildTimeZoneButton = AddAction(rows, "", CycleBuildTimeZone);

        AddHeader(rows, "Run views");
        _potionHistoryButton = AddAction(rows, PotionHistoryText, OpenPotionHistory);

        AddHeader(rows, "Replay");
        _replayButton = AddAction(rows, "Replay a run…", OnReplayActivated);

        AddHeader(rows, "Practice");
        _restartRoomIndex = SelectableButtons.Count;
        _restartRoomButton = AddAction(rows, "Restart this room", OnRestartRoomActivated);

        // Quiet, non-selectable footer: which Loader build is running.
        _buildInfoLabel = NewLabel("", 17);
        _buildInfoLabel.Modulate = QuietColor;
        rows.AddChild(_buildInfoLabel);

        RefreshToggleRows();
        RefreshPotionHistoryRow();
        RefreshReplayRow();
        RefreshRestartRoomRow();
        RefreshBuildTimeZoneRow();
        RefreshBuildInfo();

        var close = new Button
        {
            Text = "Close  —  RS / Left Shift / Esc",
            CustomMinimumSize = new Vector2(0, 56),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        close.AddThemeFontSizeOverride("font_size", 20);
        close.Pressed += () => Close("menu button");
        frame.AddChild(close);
    }

    private static Label NewLabel(string text, int fontSize)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private static void AddHeader(VBoxContainer parent, string text)
    {
        var header = NewLabel(text, 20);
        header.Modulate = HeaderColor;
        parent.AddChild(header);
    }

    private static void AddToggle(
        VBoxContainer parent,
        string text,
        Func<bool> get,
        Action<bool, string> set,
        Func<bool>? isAvailable = null)
    {
        var index = SelectableButtons.Count;
        var toggle = new ToggleRow(text, get, set, isAvailable);
        ToggleRows[index] = toggle;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 20);
        parent.AddChild(row);

        var indicator = CreateCheckboxIndicator();
        indicator.MouseEntered += () => SetSelectedIndex(index);
        indicator.Pressed += () =>
        {
            SetSelectedIndex(index);
            ToggleOption(index, "menu checkbox icon");
        };
        toggle.Indicator = indicator;
        row.AddChild(indicator);

        var optionHost = new Control
        {
            CustomMinimumSize = new Vector2(0, 54),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(optionHost);

        var selectionHighlight = new Panel
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        selectionHighlight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        optionHost.AddChild(selectionHighlight);

        var checkbox = new Button
        {
            Text = text,
            ToggleMode = true,
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            // Pass, not Stop, so the mouse wheel reaches the ScrollContainer.
            // Clicks are still consumed by the button itself.
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        checkbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        checkbox.AddThemeFontSizeOverride("font_size", 23);
        checkbox.Toggled += enabled =>
        {
            SetSelectedIndex(index);
            ApplyToggle(toggle, enabled, "menu checkbox");
        };
        checkbox.MouseEntered += () => SetSelectedIndex(index);
        toggle.Checkbox = checkbox;
        optionHost.AddChild(checkbox);

        RegisterSelectable(checkbox, row, selectionHighlight);
    }

    private static Button AddAction(
        VBoxContainer parent,
        string text,
        Action action)
    {
        var index = SelectableButtons.Count;
        RowActions[index] = action;

        var optionHost = new Control
        {
            CustomMinimumSize = new Vector2(0, 56),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        parent.AddChild(optionHost);

        var selectionHighlight = new Panel
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        selectionHighlight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        optionHost.AddChild(selectionHighlight);

        var button = new Button
        {
            Text = text,
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddThemeFontSizeOverride("font_size", 23);
        button.MouseEntered += () => SetSelectedIndex(index);
        button.Pressed += () =>
        {
            SetSelectedIndex(index);
            action();
        };
        optionHost.AddChild(button);

        RegisterSelectable(button, optionHost, selectionHighlight);
        return button;
    }

    private static void RegisterSelectable(Button button, Control row, Panel highlight)
    {
        SelectableButtons.Add(button);
        SelectableRows.Add(row);
        SelectionHighlights.Add(highlight);
    }

    private static Button CreateCheckboxIndicator()
    {
        var border = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.06f, 0.08f, 0.9f),
            BorderColor = new Color(0.9f, 0.9f, 0.9f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
        };

        var indicator = new Button
        {
            Text = "",
            CustomMinimumSize = new Vector2(34, 34),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        indicator.AddThemeFontSizeOverride("font_size", 24);
        indicator.AddThemeStyleboxOverride("normal", border);
        indicator.AddThemeStyleboxOverride("hover", (StyleBoxFlat)border.Duplicate());
        indicator.AddThemeStyleboxOverride("pressed", (StyleBoxFlat)border.Duplicate());
        indicator.AddThemeStyleboxOverride("focus", (StyleBoxFlat)border.Duplicate());
        return indicator;
    }

    private static void ToggleOption(int index, string source)
    {
        if (!ToggleRows.TryGetValue(index, out var toggle)) return;
        ApplyToggle(toggle, !SafeGet(toggle), source);
    }

    /// <summary>
    /// Applies a toggle when its row is available, then redraws every row from
    /// the stored state. The redraw both reverts a click on an unavailable row
    /// and reflects side effects such as the mutually exclusive relic filters.
    /// </summary>
    private static void ApplyToggle(ToggleRow toggle, bool enabled, string source)
    {
        if (toggle.Available)
        {
            try
            {
                toggle.Set(enabled, source);
            }
            catch (Exception e)
            {
                CoreMain.Logger.Error($"SpireLens options: setting '{toggle.Text}' failed: {e}");
            }
        }

        RefreshToggleRows();
    }

    private static bool SafeGet(ToggleRow toggle)
    {
        try
        {
            return toggle.Get();
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"SpireLens options: reading '{toggle.Text}' failed: {e.Message}");
            return false;
        }
    }

    private static void SetHandCardStats(bool enabled, string source)
    {
        if (!SetRuntimeOption(nameof(RuntimeOptions.ShowHandTooltips), "Hand card stats", enabled, source))
            return;
        if (!enabled)
            NHoverTipSet.Clear();
    }

    private static void SetVerboseHandStats(bool enabled, string source)
    {
        RuntimeOptionsProvider.SetVerboseHandStatsEnabled(enabled);
        CoreMain.Logger.Info($"Full hand card stats set to {enabled} ({source})");
    }

    private static void SetPauseCardTrackingDuringCombat(bool enabled, string source) =>
        SetRuntimeOption(
            nameof(RuntimeOptions.DisableCardStatsDuringCombat),
            "Pause card tracking during combat",
            enabled,
            source);

    private static void SetDebugLogging(bool enabled, string source)
    {
        if (SetRuntimeOption(nameof(RuntimeOptions.EnableDebugLogging), "Debug logging", enabled, source))
            CoreMain.RefreshDebugLogging();
    }

    private static bool SetRuntimeOption(string name, string label, bool enabled, string source)
    {
        if (!RuntimeOptionsProvider.TrySetOption(name, enabled)) return false;
        CoreMain.Logger.Info($"{label} set to {enabled} ({source})");
        return true;
    }

    private static void ActivateSelection(int index, string source)
    {
        if (RowActions.TryGetValue(index, out var action))
        {
            action();
            return;
        }
        ToggleOption(index, source);
    }

    private static void MoveFocus(int delta)
    {
        if (SelectableButtons.Count == 0) return;
        SetSelectedIndex(
            (_selectedIndex + delta + SelectableButtons.Count) % SelectableButtons.Count,
            scrollIntoView: true);
    }

    private static void JumpToEdge(bool first)
    {
        if (SelectableButtons.Count == 0) return;
        SetSelectedIndex(first ? 0 : SelectableButtons.Count - 1, scrollIntoView: true);
    }

    /// <summary>
    /// Mouse hover selects without scrolling: scrolling under a hovering
    /// pointer would move a different row beneath it. Keyboard and controller
    /// navigation pass <paramref name="scrollIntoView"/>.
    /// </summary>
    private static void SetSelectedIndex(int index, bool scrollIntoView = false)
    {
        if (SelectableButtons.Count == 0) return;
        _selectedIndex = Math.Clamp(index, 0, SelectableButtons.Count - 1);

        // Moving off the armed destructive row cancels the confirmation, so a
        // stray Enter/A elsewhere in the menu can never land on it.
        if (_restartArmed && _selectedIndex != _restartRoomIndex)
        {
            _restartArmed = false;
            RefreshRestartRoomRow();
        }

        RefreshSelectionHighlight();
        if (scrollIntoView)
            ScrollSelectedIntoView();
    }

    private static void ScrollSelectedIntoView()
    {
        if (_scroll == null || !GodotObject.IsInstanceValid(_scroll)) return;
        if (_selectedIndex < 0 || _selectedIndex >= SelectableRows.Count) return;

        // The last row also reveals the build footer beneath it, which
        // keyboard and controller players have no other way to scroll to.
        if (_selectedIndex == SelectableRows.Count - 1)
        {
            _scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, int.MaxValue);
            return;
        }

        var row = SelectableRows[_selectedIndex];
        if (!GodotObject.IsInstanceValid(row)) return;

        // Deferred so a just-opened or just-relabelled menu has settled layout.
        _scroll.CallDeferred(ScrollContainer.MethodName.EnsureControlVisible, row);
    }

    private static void RefreshSelectionHighlight()
    {
        if (SelectableButtons.Count != SelectionHighlights.Count) return;

        for (var i = 0; i < SelectionHighlights.Count; i++)
        {
            var highlight = SelectionHighlights[i];
            if (!GodotObject.IsInstanceValid(highlight)) continue;

            if (i == _selectedIndex)
            {
                // Reuse the game's Button focus art without giving this modal
                // Godot focus. The gameplay control beneath the overlay keeps
                // its exact focus, highlight, and hover state.
                highlight.AddThemeStyleboxOverride(
                    "panel",
                    SelectableButtons[i].GetThemeStylebox("focus"));
                highlight.Visible = true;
            }
            else
            {
                highlight.Visible = false;
            }
        }
    }

    private static bool HandleLeftStick(InputEventJoypadMotion motion)
    {
        const float deadZone = 0.55f;

        if (motion.Axis == JoyAxis.LeftY)
        {
            var direction = motion.AxisValue > deadZone ? 1 : motion.AxisValue < -deadZone ? -1 : 0;
            if (direction == 0)
            {
                _leftStickVerticalDirection = 0;
            }
            else if (direction != _leftStickVerticalDirection)
            {
                _leftStickVerticalDirection = direction;
                MoveFocus(direction);
            }
            return true;
        }

        if (motion.Axis == JoyAxis.LeftX)
        {
            var direction = motion.AxisValue > deadZone ? 1 : motion.AxisValue < -deadZone ? -1 : 0;
            if (direction == 0)
            {
                _leftStickHorizontalDirection = 0;
            }
            else if (direction != _leftStickHorizontalDirection)
            {
                _leftStickHorizontalDirection = direction;
                JumpToEdge(first: direction < 0);
            }
            return true;
        }

        if (motion.Axis == JoyAxis.TriggerLeft)
        {
            var pressed = motion.AxisValue > deadZone;
            if (pressed && !_leftTriggerPressed)
                JumpToEdge(first: false);
            _leftTriggerPressed = pressed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Two-step confirm. Restarting discards everything done in the room so
    /// far, so the first activation only arms the row and the second commits.
    /// </summary>
    private static void OnRestartRoomActivated()
    {
        var availability = RoomResetter.Describe();
        if (!availability.CanRestart)
        {
            _restartArmed = false;
            RefreshRestartRoomRow();
            CoreMain.Logger.Info(
                $"SpireLens options: restart room unavailable ({availability.BlockedReason})");
            return;
        }

        if (!_restartArmed)
        {
            _restartArmed = true;
            RefreshRestartRoomRow();
            return;
        }

        _restartArmed = false;
        Close("restart room button");
        RoomResetter.Request("options menu");
    }

    /// <summary>
    /// The row names the room it would actually replay — "combat", "shop" or
    /// "event" — because what a restart undoes is the only thing the player
    /// needs to weigh before confirming.
    /// </summary>
    private static void RefreshRestartRoomRow()
    {
        if (_restartRoomButton == null || !GodotObject.IsInstanceValid(_restartRoomButton))
            return;

        var availability = RoomResetter.Describe();
        if (!availability.CanRestart)
        {
            _restartArmed = false;
            _restartRoomButton.Text = $"Restart this room  —  unavailable ({availability.BlockedReason})";
            _restartRoomButton.Modulate = UnavailableColor;
            return;
        }

        var noun = availability.RoomNoun;
        _restartRoomButton.Text = _restartArmed
            ? $"Restart this {noun}  —  select again to confirm"
            // The note comes from the availability, because where a replay
            // lands differs by room: a finished fight returns to its reward
            // screen, not to the top of the fight.
            : $"Restart this {noun}  —  {availability.ReplayNote}";
        _restartRoomButton.Modulate = _restartArmed
            ? new Color(1f, 0.68f, 0.4f)
            : new Color(1f, 1f, 1f);
    }

    /// <summary>
    /// The menu opens outside a run too (main menu, Compendium), where there is
    /// no current run to show, so the row says so instead of opening an empty
    /// view.
    /// </summary>
    private static void RefreshPotionHistoryRow()
    {
        if (_potionHistoryButton == null || !GodotObject.IsInstanceValid(_potionHistoryButton))
            return;

        bool inRun = RunManager.Instance?.IsInProgress == true;
        _potionHistoryButton.Text = inRun
            ? PotionHistoryText
            : $"{PotionHistoryText}  —  unavailable (no run in progress)";
        _potionHistoryButton.Modulate = inRun
            ? new Color(1f, 1f, 1f)
            : UnavailableColor;
    }

    private static void OpenPotionHistory()
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            RefreshPotionHistoryRow();
            return;
        }

        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null) return;

            var capstone = FindNodesOfType<NCapstoneSubmenuStack>(tree.Root)
                .FirstOrDefault(stack => stack.IsVisibleInTree())
                ?? FindNodesOfType<NCapstoneSubmenuStack>(tree.Root).FirstOrDefault();
            if (capstone == null)
            {
                CoreMain.Logger.Warn("SpireLens options: active run submenu stack not found for potion history");
                return;
            }

            PotionCompendiumHistoryUi.SelectCurrentRunMode();
            Close("potion history button");
            var compendium = capstone.ShowScreen(CapstoneSubmenuType.Compendium)
                as NCompendiumSubmenu;
            compendium?.OpenPotionLab(null!);
            CoreMain.Logger.Info("SpireLens options: opened current-run potion history");
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"SpireLens options: opening potion history failed: {e}");
        }
    }

    /// <summary>
    /// A cycling row: each activation moves to the next zone. It only changes
    /// how the build footer's date reads.
    /// </summary>
    private static void CycleBuildTimeZone()
    {
        if (RuntimeOptionsProvider.CanSetOption)
        {
            var current = RuntimeOptionsProvider.Current.ResolveBuildTimeZoneName();
            var next = BuildTimeZoneNames[
                (Array.IndexOf(BuildTimeZoneNames, current) + 1) % BuildTimeZoneNames.Length];
            if (RuntimeOptionsProvider.TrySetOption(nameof(RuntimeOptions.BuildTimeZone), next))
                CoreMain.Logger.Info($"Build date time zone set to {next} (menu)");
        }

        RefreshBuildTimeZoneRow();
        RefreshBuildInfo();
    }

    private static void RefreshBuildTimeZoneRow()
    {
        if (_buildTimeZoneButton == null || !GodotObject.IsInstanceValid(_buildTimeZoneButton))
            return;

        var text = $"Build date time zone: {RuntimeOptionsProvider.Current.ResolveBuildTimeZoneName()}";
        var available = RuntimeOptionsProvider.CanSetOption;
        _buildTimeZoneButton.Text = available ? text : $"{text}  —  {RestartNeededNote}";
        _buildTimeZoneButton.Modulate = available ? new Color(1f, 1f, 1f) : UnavailableColor;
    }

    private static void RefreshBuildInfo()
    {
        if (_buildInfoLabel == null || !GodotObject.IsInstanceValid(_buildInfoLabel))
            return;

        var options = RuntimeOptionsProvider.Current;
        _buildInfoLabel.Text = string.IsNullOrEmpty(options.BuildVersion)
            // The running Loader's snapshot predates the build fields.
            ? $"Build info {RestartNeededNote}"
            : $"Build {options.BuildVersion}  ·  {options.BuildSource}\n"
              + $"Commit {options.BuildCommit}\n"
              + $"Built {options.BuildDate}";
    }

    /// <summary>
    /// Opens the recording list, or stops the replay that is playing. The list
    /// itself explains when a replay can't start (for example mid-run).
    /// </summary>
    private static void OnReplayActivated()
    {
        if (Replay.Playback.ReplayPlayer.IsActive)
        {
            Close("stop replay button");
            Replay.Playback.ReplayPlayer.RequestStop();
            return;
        }
        Close("replay list button");
        Replay.Playback.ReplayPickerMenu.Open();
    }

    private static void RefreshReplayRow()
    {
        if (_replayButton == null || !GodotObject.IsInstanceValid(_replayButton)) return;
        if (Replay.Playback.ReplayPlayer.IsActive)
        {
            _replayButton.Text = "Stop replay  —  back to the main menu, your data restored";
            _replayButton.Modulate = new Color(1f, 1f, 1f);
            return;
        }
        string? blocked = Replay.Playback.ReplayPlayer.StartBlockedReason();
        _replayButton.Text = blocked == null ? "Replay a run…" : $"Replay a run…  —  {blocked}";
        _replayButton.Modulate = new Color(1f, 1f, 1f);
    }

    private static IEnumerable<T> FindNodesOfType<T>(Node? node) where T : Node
    {
        if (node == null) yield break;
        if (node is T match) yield return match;
        for (var i = 0; i < node.GetChildCount(); i++)
        {
            foreach (var childMatch in FindNodesOfType<T>(node.GetChild(i)))
                yield return childMatch;
        }
    }

    private static void RefreshToggleRows()
    {
        foreach (var toggle in ToggleRows.Values)
        {
            if (!GodotObject.IsInstanceValid(toggle.Checkbox)
                || !GodotObject.IsInstanceValid(toggle.Indicator))
                continue;

            var enabled = SafeGet(toggle);
            toggle.Checkbox.SetPressedNoSignal(enabled);
            toggle.Indicator.Text = enabled ? "✓" : "";

            var available = toggle.Available;
            toggle.Checkbox.Text = available ? toggle.Text : $"{toggle.Text}  —  {RestartNeededNote}";
            var color = available ? new Color(1f, 1f, 1f) : UnavailableColor;
            toggle.Checkbox.Modulate = color;
            toggle.Indicator.Modulate = color;
        }
    }
}
