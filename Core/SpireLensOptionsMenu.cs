using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using SpireLens.Core.Patches;

namespace SpireLens.Core;

/// <summary>
/// Global modal options surface. It lives in a high CanvasLayer under the
/// scene root so it remains available above every game screen and consumes
/// normal mouse/controller input while visible.
/// </summary>
public static class SpireLensOptionsMenu
{
    private const int Layer = 1000;

    /// <summary>Row index of the destructive "restart this room" action.</summary>
    private const int RestartRoomIndex = 8;

    private static CanvasLayer? _layer;
    private static readonly List<Button> Checkboxes = new();
    private static readonly List<Button> SelectableButtons = new();
    private static readonly List<Button> CheckboxIndicators = new();
    private static readonly List<Panel> SelectionHighlights = new();
    private static readonly Dictionary<int, Action> RowActions = new();
    private static Button? _restartRoomButton;
    private static Button? _potionHistoryButton;
    private const string PotionHistoryText = "View current-run potion history";
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

        RefreshCheckboxes();
        _restartArmed = false;
        RefreshRestartRoomRow();
        RefreshPotionHistoryRow();
        _layer!.Visible = true;
        _selectedIndex = Math.Clamp(_selectedIndex, 0, SelectableButtons.Count - 1);
        _leftStickVerticalDirection = 0;
        _leftStickHorizontalDirection = 0;
        _leftTriggerPressed = false;
        RefreshSelectionHighlight();
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
        Checkboxes.Clear();
        SelectableButtons.Clear();
        CheckboxIndicators.Clear();
        SelectionHighlights.Clear();
        RowActions.Clear();
        _restartRoomButton = null;
        _potionHistoryButton = null;
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
            CustomMinimumSize = new Vector2(780, 900),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 44);
        margin.AddThemeConstantOverride("margin_right", 44);
        margin.AddThemeConstantOverride("margin_top", 34);
        margin.AddThemeConstantOverride("margin_bottom", 34);
        panel.AddChild(margin);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 18);
        margin.AddChild(rows);

        var title = NewLabel("SpireLens Options", 32);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        rows.AddChild(title);

        var help = NewLabel("Up/Down selects • A toggles or opens • Left/Right or LB/LT jumps • Esc closes", 18);
        help.HorizontalAlignment = HorizontalAlignment.Center;
        help.Modulate = new Color(0.82f, 0.82f, 0.82f);
        rows.AddChild(help);

        AddOption(rows, "SpireLens: on/off", 0);
        AddOption(rows, "SpireLens: card stats", 1);
        AddOption(rows, "Show monster stats", 2);
        AddOption(rows, "Show cards not in deck", 3);
        AddOption(rows, "Show all meta-cards in \"not in deck\" view", 4);

        var relicFilterHeader = NewLabel("Relic bar filter — choose one", 20);
        relicFilterHeader.Modulate = new Color(0.72f, 0.8f, 0.92f);
        rows.AddChild(relicFilterHeader);
        AddOption(rows, "Show combat-only relics at the combat screen", 5);
        AddOption(rows, "Force show only combat relics on all screens", 6);

        var runViewsHeader = NewLabel("Run views", 20);
        runViewsHeader.Modulate = new Color(0.72f, 0.8f, 0.92f);
        rows.AddChild(runViewsHeader);
        _potionHistoryButton = AddAction(rows, PotionHistoryText, 7, OpenPotionHistory);
        RefreshPotionHistoryRow();

        var practiceHeader = NewLabel("Practice", 20);
        practiceHeader.Modulate = new Color(0.72f, 0.8f, 0.92f);
        rows.AddChild(practiceHeader);
        _restartRoomButton = AddAction(
            rows,
            "Restart this room",
            RestartRoomIndex,
            OnRestartRoomActivated);
        RefreshRestartRoomRow();

        var close = new Button
        {
            Text = "Close  —  RS / Left Shift / Esc",
            CustomMinimumSize = new Vector2(0, 56),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        close.AddThemeFontSizeOverride("font_size", 20);
        close.Pressed += () => Close("menu button");
        rows.AddChild(close);
    }

    private static Label NewLabel(string text, int fontSize)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private static void AddOption(
        VBoxContainer parent,
        string text,
        int index)
    {
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
        CheckboxIndicators.Add(indicator);
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
        SelectionHighlights.Add(selectionHighlight);
        optionHost.AddChild(selectionHighlight);

        var checkbox = new Button
        {
            Text = text,
            ToggleMode = true,
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        checkbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        checkbox.AddThemeFontSizeOverride("font_size", 23);
        checkbox.Toggled += enabled =>
        {
            SetSelectedIndex(index);
            UpdateIndicator(index, enabled);
            SetOption(index, enabled, "menu checkbox");
        };
        checkbox.MouseEntered += () => SetSelectedIndex(index);
        Checkboxes.Add(checkbox);
        SelectableButtons.Add(checkbox);
        optionHost.AddChild(checkbox);

    }

    private static Button AddAction(
        VBoxContainer parent,
        string text,
        int index,
        Action action)
    {
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
        SelectionHighlights.Add(selectionHighlight);
        optionHost.AddChild(selectionHighlight);

        var button = new Button
        {
            Text = text,
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddThemeFontSizeOverride("font_size", 23);
        button.MouseEntered += () => SetSelectedIndex(index);
        button.Pressed += () =>
        {
            SetSelectedIndex(index);
            action();
        };
        SelectableButtons.Add(button);
        optionHost.AddChild(button);
        return button;
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
        if (index < 0 || index >= Checkboxes.Count) return;
        RefreshCheckboxes();
        SetOption(index, !Checkboxes[index].ButtonPressed, source);
        RefreshCheckboxes();
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
        SetSelectedIndex((_selectedIndex + delta + SelectableButtons.Count) % SelectableButtons.Count);
    }

    private static void JumpToEdge(bool first)
    {
        if (SelectableButtons.Count == 0) return;
        SetSelectedIndex(first ? 0 : SelectableButtons.Count - 1);
    }

    private static void SetSelectedIndex(int index)
    {
        if (SelectableButtons.Count == 0) return;
        _selectedIndex = Math.Clamp(index, 0, SelectableButtons.Count - 1);

        // Moving off the armed destructive row cancels the confirmation, so a
        // stray Enter/A elsewhere in the menu can never land on it.
        if (_restartArmed && _selectedIndex != RestartRoomIndex)
        {
            _restartArmed = false;
            RefreshRestartRoomRow();
        }

        RefreshSelectionHighlight();
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

    private static void SetOption(int index, bool enabled, string source)
    {
        switch (index)
        {
            case 0:
                ViewStatsInjectorPatch.SetStatsVisibilityEnabled(enabled, source);
                break;
            case 1:
                ViewStatsInjectorPatch.SetCardStatsEnabled(enabled, source);
                break;
            case 2:
                ViewStatsInjectorPatch.SetEnemyStatsEnabled(enabled, source);
                break;
            case 3:
                ViewStatsInjectorPatch.SetShowCardsNotInDeckEnabled(enabled, source);
                break;
            case 4:
                ViewStatsInjectorPatch.SetShowAllMetaCardsInNotInDeckView(
                    enabled,
                    source);
                break;
            case 5:
                ViewStatsInjectorPatch.SetShowCombatOnlyRelicsAtCombatScreen(enabled, source);
                break;
            case 6:
                ViewStatsInjectorPatch.SetHideNonCombatRelicStats(enabled, source);
                break;
        }
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
            _restartRoomButton.Modulate = new Color(0.55f, 0.55f, 0.55f);
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
            : new Color(0.55f, 0.55f, 0.55f);
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

    private static void RefreshCheckboxes()
    {
        if (Checkboxes.Count != 7) return;
        SetCheckboxState(0, ViewStatsInjectorPatch.StatsVisibilityEnabled);
        SetCheckboxState(1, ViewStatsInjectorPatch.CardStatsEnabled);
        SetCheckboxState(2, ViewStatsInjectorPatch.EnemyStatsEnabled);
        SetCheckboxState(3, ViewStatsInjectorPatch.ShowCardsNotInDeckEnabled);
        SetCheckboxState(
            4,
            ViewStatsInjectorPatch.ShowAllMetaCardsInNotInDeckView);
        SetCheckboxState(5, ViewStatsInjectorPatch.ShowCombatOnlyRelicsAtCombatScreen);
        SetCheckboxState(6, ViewStatsInjectorPatch.HideNonCombatRelicStats);
    }

    private static void SetCheckboxState(int index, bool enabled)
    {
        Checkboxes[index].SetPressedNoSignal(enabled);
        UpdateIndicator(index, enabled);
    }

    private static void UpdateIndicator(int index, bool enabled)
    {
        if (index < 0 || index >= CheckboxIndicators.Count) return;
        CheckboxIndicators[index].Text = enabled ? "✓" : "";
    }
}
