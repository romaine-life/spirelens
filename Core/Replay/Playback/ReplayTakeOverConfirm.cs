using System;
using Godot;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// The "Take over this run?" confirmation (docs/replay-player.md, "Taking
/// over"). Says what happens, including the in-progress run that will be
/// abandoned. Enter / T / A confirm, Esc / B cancel, and its two buttons take
/// clicks; input routing is in <see cref="ReplayPlayer"/>. Plain Godot
/// controls on a CanvasLayer above the replay bar; freed when answered, on
/// playback end and at Core shutdown.
/// </summary>
internal static class ReplayTakeOverConfirm
{
    private const int Layer = 1003;

    private static CanvasLayer? _layer;
    private static Control? _panel;
    private static Action? _onConfirm;

    public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

    public static void Open(string body, Action onConfirm)
    {
        Close();
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        _onConfirm = onConfirm;
        _layer = new CanvasLayer { Name = "SpireLensTakeOverConfirm", Layer = Layer };
        tree.Root.AddChild(_layer);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.06f, 0.08f, 0.96f),
            BorderColor = new Color(0.55f, 0.75f, 0.95f, 0.9f),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 18, ContentMarginBottom = 18,
        });
        center.AddChild(panel);
        _panel = panel;

        var rows = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 12);
        panel.AddChild(rows);

        var title = new Label { Text = "Take over this run?", MouseFilter = Control.MouseFilterEnum.Ignore };
        title.AddThemeFontSizeOverride("font_size", 24);
        rows.AddChild(title);

        var text = new Label
        {
            Text = body,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(560, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        text.AddThemeFontSizeOverride("font_size", 17);
        rows.AddChild(text);

        var buttons = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 12);
        rows.AddChild(buttons);
        buttons.AddChild(NewButton("Cancel  (Esc / B)", Cancel));
        buttons.AddChild(NewButton("Take over  (Enter / A)", Confirm));
    }

    private static Button NewButton(string text, Action pressed)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, MouseFilter = Control.MouseFilterEnum.Stop };
        button.AddThemeFontSizeOverride("font_size", 17);
        button.Pressed += () => pressed();
        return button;
    }

    /// <summary>The mouse is over the dialog (its clicks go to its buttons).</summary>
    public static bool IsUnderMouse()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return false;
        var hovered = _panel.GetViewport()?.GuiGetHoveredControl();
        return hovered != null && (hovered == _panel || _panel.IsAncestorOf(hovered));
    }

    public static void Confirm()
    {
        var onConfirm = _onConfirm;
        Close();
        onConfirm?.Invoke();
    }

    public static void Cancel() => Close();

    public static void Close()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
        _layer = null;
        _panel = null;
        _onConfirm = null;
    }
}
