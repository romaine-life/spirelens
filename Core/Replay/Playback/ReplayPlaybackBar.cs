using Godot;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// The small playback status bar, bottom-left, above the game UI but out of
/// its way. Display only (MouseFilter Ignore): every control is a hotkey,
/// handled in <see cref="ReplayPlayer.HandleInput"/>. Plain Godot controls on
/// a CanvasLayer under the scene root, like the options menu; freed on
/// playback end and at Core shutdown.
/// </summary>
internal static class ReplayPlaybackBar
{
    private const int Layer = 950;
    private const string Hints =
        "Space pause · . step · −/+ speed · R restart floor · PgUp/PgDn floor · B/N run · Home restart run · Esc exit\n"
        + "Pad: Select pause · → step · LB/RB speed · Y restart floor · ↑/↓ floor · LT/RT run · X restart run · R3 menu";

    private static CanvasLayer? _layer;
    private static Label? _status;
    private static Label? _note;

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
        panel.Modulate = new Color(1f, 1f, 1f, 0.88f);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.07f, 0.78f),
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

        var rows = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 2);
        panel.AddChild(rows);

        _status = NewLabel(17, new Color(0.95f, 0.95f, 0.95f));
        rows.AddChild(_status);
        _note = NewLabel(15, new Color(1f, 0.78f, 0.45f));
        _note.Visible = false;
        rows.AddChild(_note);
        var hints = NewLabel(13, new Color(0.7f, 0.7f, 0.72f));
        hints.Text = Hints;
        rows.AddChild(hints);
    }

    private static Label NewLabel(int size, Color color)
    {
        var label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.Modulate = color;
        return label;
    }

    public static void Update(string status, string? note)
    {
        if (_status == null || !GodotObject.IsInstanceValid(_status)) return;
        if (_status.Text != status) _status.Text = status;
        if (_note != null && GodotObject.IsInstanceValid(_note))
        {
            _note.Visible = note != null;
            if (note != null && _note.Text != note) _note.Text = note;
        }
    }

    public static void Destroy()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
        _layer = null;
        _status = null;
        _note = null;
    }
}
