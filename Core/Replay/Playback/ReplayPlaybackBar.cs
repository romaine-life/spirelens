using Godot;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// The playback status bar, bottom-left, above the game UI but out of its
/// way. Display only (MouseFilter Ignore): every control is a hotkey, handled
/// in <see cref="ReplayPlayer.HandleInput"/>. Shows the status line, a short
/// note, and a controls table with labeled Keyboard and Controller columns;
/// while the floor picker has the map open, the table lists the picker's
/// controls instead. H / L3 hides the table down to a one-line hint.
/// Plain Godot controls on a CanvasLayer under the scene root, like the
/// options menu; freed on playback end and at Core shutdown.
/// </summary>
internal static class ReplayPlaybackBar
{
    private const int Layer = 950;
    private const int TextSize = 13;

    private static readonly (string Action, string Keyboard, string Controller)[] PlaybackControls =
    {
        ("Pause / resume", "Space", "Select"),
        ("Step (paused)", ". or →", "D-pad →"),
        ("Slower / faster", "− / +", "LB / RB"),
        ("Restart floor", "R", "Y"),
        ("Pick a floor on the map", "M", "A"),
        ("Previous / next act", "PgUp / PgDn", "D-pad ↓ / ↑"),
        ("Previous / next run", "B / N", "LT / RT"),
        ("Restart run", "Home", "X"),
        ("Hide controls", "H", "L3"),
        ("SpireLens menu", "Left Shift", "R3"),
        ("Exit replay", "Esc", "Menu → Stop replay"),
    };

    private static readonly (string Action, string Keyboard, string Controller)[] PickerControls =
    {
        ("Choose a room", "Click or Enter", "A"),
        ("Move between rooms", "↑ / ↓", "D-pad ↑ / ↓"),
        ("Previous / next act", "PgUp / PgDn", "LB / RB"),
        ("Close the map", "M or Esc", "B"),
        ("Hide controls", "H", "L3"),
    };

    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static Label? _status;
    private static Label? _note;
    private static GridContainer? _playbackTable;
    private static GridContainer? _pickerTable;
    private static Label? _pickerTitle;
    private static Label? _hiddenHint;
    private static bool _picking;
    private static (bool Controls, bool Picking, bool Note)? _shown;

    /// <summary>
    /// Whether the controls table shows. Kept for the game session (across
    /// replays and playlists), not saved.
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
        _pickerTable = NewTable(PickerControls);
        rows.AddChild(_pickerTable);
        _hiddenHint = NewLabel(TextSize, new Color(0.6f, 0.6f, 0.62f));
        _hiddenHint.Text = "H / L3  show controls";
        rows.AddChild(_hiddenHint);

        _picking = false;
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

    public static void Update(string status, string? note, bool picking)
    {
        if (_status == null || !GodotObject.IsInstanceValid(_status)) return;
        if (_status.Text != status) _status.Text = status;
        if (_note != null && GodotObject.IsInstanceValid(_note))
        {
            _note.Visible = note != null;
            if (note != null && _note.Text != note) _note.Text = note;
        }
        _picking = picking;
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
        _playbackTable.Visible = ControlsVisible && !_picking;
        if (_pickerTitle != null) _pickerTitle.Visible = _picking;
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
    }
}
