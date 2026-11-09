using Godot;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// The playback status bar, bottom-left, above the game UI but out of its
/// way. Display only (MouseFilter Ignore): every control is a hotkey, handled
/// in <see cref="ReplayPlayer.HandleInput"/>. Shows the status line, a short
/// note, and a controls table with labeled Keyboard and Controller columns;
/// while the floor picker has the map open, the table lists the picker's
/// controls instead, with Previous / Next act buttons for the mouse (the
/// only part of the bar that takes clicks). H / L3 hides all of it down to a
/// one-line hint for showing it again.
/// Plain Godot controls on a CanvasLayer under the scene root, like the
/// options menu; freed on playback end and at Core shutdown.
/// </summary>
internal static class ReplayPlaybackBar
{
    /// <summary>The act the map shows and the recorded acts before and after it (null at either end).</summary>
    public readonly record struct ActNav(int Act, int? Previous, int? Next);

    private const int Layer = 950;
    private const int TextSize = 13;

    private static readonly (string Action, string Keyboard, string Controller)[] PlaybackControls =
    {
        ("Pause / resume", "Space", "Select"),
        ("Step (paused)", ". or →", "D-pad →"),
        ("Slower / faster", "− / +", "LB / RB"),
        ("Restart floor", "R", "Y"),
        ("Pick a floor on the map", "Map icon or M", "A"),
        ("Previous / next act", "PgUp / PgDn", "D-pad ↓ / ↑"),
        ("Previous / next run", "B / N", "LT / RT"),
        ("Restart run", "Home", "X"),
        ("Hide replay bar", "H", "L3"),
        ("SpireLens menu", "Left Shift", "R3"),
        ("Exit replay", "Esc", "Menu → Stop replay"),
    };

    private static readonly (string Action, string Keyboard, string Controller)[] PickerControls =
    {
        ("Choose a room", "Click or Enter", "A"),
        ("Move between rooms", "↑ / ↓", "D-pad ↑ / ↓"),
        ("Previous / next act", "PgUp / PgDn, or the buttons", "LB / RB"),
        ("Close the map", "Map icon, M or Esc", "B"),
        ("Hide replay bar", "H", "L3"),
    };

    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static Label? _status;
    private static Label? _note;
    private static GridContainer? _playbackTable;
    private static GridContainer? _pickerTable;
    private static Label? _pickerTitle;
    private static Label? _hiddenHint;
    private static HBoxContainer? _actRow;
    private static Button? _previousAct;
    private static Button? _nextAct;
    private static Label? _actLabel;
    private static ActNav? _acts;
    private static bool _picking;
    private static (bool Controls, bool Picking, bool Note)? _shown;

    /// <summary>
    /// Whether the bar shows (false: only the hint to show it again). Kept for
    /// the game session (across replays and playlists), not saved.
    /// </summary>
    public static bool ControlsVisible { get; set; } = true;

    public static void Build(SceneTree tree)
    {
        Destroy();
        _layer = new CanvasLayer { Name = "SpireLensReplayBar", Layer = Layer };
        tree.Root.AddChild(_layer);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AnchorLeft = 0;
        panel.AnchorTop = 1;
        panel.AnchorRight = 0;
        panel.AnchorBottom = 1;
        panel.OffsetLeft = 16;
        panel.OffsetBottom = -16;
        panel.GrowVertical = Control.GrowDirection.Begin;
        panel.Modulate = new Color(1f, 1f, 1f, 0.9f);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.07f, 0.8f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        _layer.AddChild(panel);
        _panel = panel;

        var rows = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 4);
        panel.AddChild(rows);

        _status = NewLabel(17, new Color(0.95f, 0.95f, 0.95f));
        rows.AddChild(_status);
        _note = NewLabel(15, new Color(1f, 0.78f, 0.45f));
        _note.Visible = false;
        rows.AddChild(_note);

        _playbackTable = NewTable(PlaybackControls);
        rows.AddChild(_playbackTable);
        _pickerTitle = NewLabel(TextSize + 1, new Color(0.95f, 0.95f, 0.95f));
        _pickerTitle.Text = "Map: pick a floor to jump to";
        rows.AddChild(_pickerTitle);
        _actRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _actRow.AddThemeConstantOverride("separation", 10);
        _previousAct = NewActButton(-1);
        _actRow.AddChild(_previousAct);
        _actLabel = NewLabel(TextSize + 1, new Color(0.95f, 0.95f, 0.95f));
        _actLabel.VerticalAlignment = VerticalAlignment.Center;
        _actRow.AddChild(_actLabel);
        _nextAct = NewActButton(+1);
        _actRow.AddChild(_nextAct);
        rows.AddChild(_actRow);
        _pickerTable = NewTable(PickerControls);
        rows.AddChild(_pickerTable);
        _hiddenHint = NewLabel(TextSize, new Color(0.6f, 0.6f, 0.62f));
        _hiddenHint.Text = "H / L3  show replay controls";
        rows.AddChild(_hiddenHint);

        _picking = false;
        _acts = null;
        _shown = null;
        ApplyVisibility();
    }

    /// <summary>A three-column table: action, then its Keyboard and Controller input, under a header row.</summary>
    private static GridContainer NewTable((string Action, string Keyboard, string Controller)[] controls)
    {
        var grid = new GridContainer { Columns = 3, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 1);

        var header = new Color(0.55f, 0.75f, 0.95f);
        grid.AddChild(NewLabel(TextSize, header));
        grid.AddChild(Text(NewLabel(TextSize, header), "Keyboard"));
        grid.AddChild(Text(NewLabel(TextSize, header), "Controller"));

        var action = new Color(0.72f, 0.72f, 0.74f);
        var input = new Color(0.92f, 0.92f, 0.92f);
        foreach (var (name, keyboard, controller) in controls)
        {
            grid.AddChild(Text(NewLabel(TextSize, action), name));
            grid.AddChild(Text(NewLabel(TextSize, input), keyboard));
            grid.AddChild(Text(NewLabel(TextSize, input), controller));
        }
        return grid;
    }

    private static Button NewActButton(int direction)
    {
        var button = new Button
        {
            Text = direction < 0 ? "◀ Previous act" : "Next act ▶",
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        button.AddThemeFontSizeOverride("font_size", TextSize);
        button.Pressed += () => ReplayPlayer.RequestAct(direction);
        return button;
    }

    /// <summary>The mouse is over one of the bar's clickable parts (the act buttons).</summary>
    public static bool IsUnderMouse()
    {
        if (_actRow == null || !GodotObject.IsInstanceValid(_actRow) || !_actRow.IsVisibleInTree()) return false;
        var hovered = _actRow.GetViewport()?.GuiGetHoveredControl();
        return hovered != null && _actRow.IsAncestorOf(hovered);
    }

    private static Label Text(Label label, string text)
    {
        label.Text = text;
        return label;
    }

    private static Label NewLabel(int size, Color color)
    {
        var label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.Modulate = color;
        return label;
    }

    /// <summary><paramref name="acts"/> is non-null while the floor picker has the map open.</summary>
    public static void Update(string status, string? note, ActNav? acts)
    {
        if (_status == null || !GodotObject.IsInstanceValid(_status)) return;
        if (_status.Text != status) _status.Text = status;
        if (_note != null && GodotObject.IsInstanceValid(_note))
        {
            _note.Visible = note != null && ControlsVisible;
            if (note != null && _note.Text != note) _note.Text = note;
        }
        _picking = acts != null;
        if (acts != _acts && acts is { } nav && _actLabel != null && _previousAct != null && _nextAct != null)
        {
            _actLabel.Text = $"Act {nav.Act + 1}";
            _previousAct.Disabled = nav.Previous == null;
            _nextAct.Disabled = nav.Next == null;
        }
        _acts = acts;
        ApplyVisibility();
    }

    /// <summary>
    /// Show the parts for the current state. When what shows changes, the
    /// panel is shrunk back to its content (a container only grows on its
    /// own); it stays anchored bottom-left because it grows upward.
    /// </summary>
    private static void ApplyVisibility()
    {
        if (_playbackTable == null || !GodotObject.IsInstanceValid(_playbackTable)) return;
        var shown = (ControlsVisible, _picking, _note?.Visible == true);
        if (_shown == shown) return;
        _shown = shown;
        // Hidden means everything but the hint to bring it back: status line,
        // notes, the controls tables and the map's act buttons.
        if (_status != null) _status.Visible = ControlsVisible;
        _playbackTable.Visible = ControlsVisible && !_picking;
        if (_pickerTitle != null) _pickerTitle.Visible = ControlsVisible && _picking;
        if (_actRow != null) _actRow.Visible = ControlsVisible && _picking;
        if (_pickerTable != null) _pickerTable.Visible = ControlsVisible && _picking;
        if (_hiddenHint != null) _hiddenHint.Visible = !ControlsVisible;
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.CallDeferred(Control.MethodName.ResetSize);
    }

    public static void Destroy()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
        _layer = null;
        _panel = null;
        _status = null;
        _note = null;
        _playbackTable = null;
        _pickerTable = null;
        _pickerTitle = null;
        _hiddenHint = null;
        _actRow = null;
        _previousAct = null;
        _nextAct = null;
        _actLabel = null;
        _acts = null;
    }
}
