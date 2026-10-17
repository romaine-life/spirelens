using System;
using System.Collections.Generic;
using Godot;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// "Replay a run" list, opened from the SpireLens options menu. The Mods →
/// SpireLens page lists the same recordings inline through
/// <see cref="LoadListings"/> and starts them through <see cref="TryStart"/>
/// (Core/SpireLensSettingsSection.cs). Same modal
/// style and controls as that menu: Up/Down (or D-pad / left stick) select,
/// Enter / A starts, Esc / B closes, mouse works too. Recordings from another
/// game build are listed but greyed out with the reason.
/// </summary>
internal static class ReplayPickerMenu
{
    private const int Layer = 1001;

    private static CanvasLayer? _layer;
    private static readonly List<Button> Rows = new();
    private static readonly List<Panel> Highlights = new();
    private static List<ReplayListing> _listings = new();
    private static int _selected;
    private static Label? _footer;
    private static ScrollContainer? _scroll;
    private static int _stickDirection;

    public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer) && _layer.Visible;

    public static void Open()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;
        Destroy();
        _listings = LoadListings();
        Build(tree);
        _selected = Math.Max(0, _listings.FindIndex(l => l.Playable));
        RefreshHighlight();
    }

    public static void Close()
    {
        Destroy();
    }

    /// <summary>
    /// The recordings, newest first. Shared by this list and the Mods page's
    /// Replay section so both show the same rows.
    /// </summary>
    internal static List<ReplayListing> LoadListings()
    {
        try { return ReplayLibrary.List(); }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay list: reading recordings failed: {e.Message}");
            return new List<ReplayListing>();
        }
    }

    /// <summary>
    /// Starts a recording if it can start now; otherwise returns why not and
    /// starts nothing. <paramref name="beforeStart"/> runs only when the
    /// replay is about to start (this list closes itself there). Shared by
    /// this list and the Mods page's Replay section.
    /// </summary>
    internal static string? TryStart(ReplayListing listing, string source, Action? beforeStart = null)
    {
        if (!listing.Playable) return listing.DisabledReason ?? "this recording can't be played";
        string? blocked = ReplayPlayer.StartBlockedReason();
        if (blocked != null) return blocked;

        beforeStart?.Invoke();
        CoreMain.Logger.Info($"Replay list: starting {listing.GameStartTime} ({source})");
        ReplayPlayer.Start(listing);
        return null;
    }

    public static void Destroy()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
        _layer = null;
        Rows.Clear();
        Highlights.Clear();
        _footer = null;
        _scroll = null;
        _stickDirection = 0;
    }

    public static bool HandleShortcut(InputEvent evt)
    {
        if (!IsOpen) return false;

        if (evt is InputEventKey { Pressed: true } key)
        {
            var k = key.Keycode != Key.None ? key.Keycode : key.PhysicalKeycode;
            switch (k)
            {
                case Key.Escape: Close(); return true;
                case Key.Up: Move(-1); return true;
                case Key.Down: Move(1); return true;
                case Key.Enter:
                case Key.KpEnter:
                case Key.Space: Activate(_selected); return true;
            }
            return true;
        }

        if (evt is InputEventJoypadButton { Pressed: true } button)
        {
            switch (button.ButtonIndex)
            {
                case JoyButton.DpadUp: Move(-1); break;
                case JoyButton.DpadDown: Move(1); break;
                case JoyButton.A: Activate(_selected); break;
                default: Close(); break;
            }
            return true;
        }

        if (evt is InputEventJoypadMotion { Axis: JoyAxis.LeftY } motion)
        {
            int direction = motion.AxisValue > 0.55f ? 1 : motion.AxisValue < -0.55f ? -1 : 0;
            if (direction != 0 && direction != _stickDirection) Move(direction);
            _stickDirection = direction;
            return true;
        }

        return false;
    }

    private static void Build(SceneTree tree)
    {
        _layer = new CanvasLayer { Name = "SpireLensReplayPicker", Layer = Layer };
        tree.Root.AddChild(_layer);

        var blocker = new ColorRect { Color = new Color(0f, 0f, 0f, 0.72f), MouseFilter = Control.MouseFilterEnum.Stop };
        blocker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(blocker);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        blocker.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(980, 820), MouseFilter = Control.MouseFilterEnum.Stop };
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 40);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 30);
        margin.AddThemeConstantOverride("margin_bottom", 30);
        panel.AddChild(margin);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 14);
        margin.AddChild(rows);

        var title = NewLabel("Replay a run", 30);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        rows.AddChild(title);

        var help = NewLabel("SpireLens plays the recorded run in the real game. Your saves and progress are backed up and restored.", 17);
        help.HorizontalAlignment = HorizontalAlignment.Center;
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        help.Modulate = new Color(0.82f, 0.82f, 0.82f);
        rows.AddChild(help);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 560),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        rows.AddChild(scroll);
        _scroll = scroll;
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        string? blocked = ReplayPlayer.StartBlockedReason();
        if (_listings.Count == 0)
        {
            var empty = NewLabel("No recordings yet. Play a run with SpireLens installed to record one.", 19);
            empty.Modulate = new Color(0.7f, 0.7f, 0.7f);
            list.AddChild(empty);
        }

        for (int i = 0; i < _listings.Count; i++)
        {
            int index = i;
            var listing = _listings[i];
            var host = new Control
            {
                CustomMinimumSize = new Vector2(0, 50),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            list.AddChild(host);

            var highlight = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            highlight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            Highlights.Add(highlight);
            host.AddChild(highlight);

            string text = listing.Label;
            if (!listing.Playable) text += $"  —  {listing.DisabledReason}";
            var button = new Button
            {
                Text = text,
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                FocusMode = Control.FocusModeEnum.None,
                ClipText = true,
                MouseDefaultCursorShape = listing.Playable
                    ? Control.CursorShape.PointingHand
                    : Control.CursorShape.Arrow,
            };
            button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            button.AddThemeFontSizeOverride("font_size", 20);
            button.Modulate = listing.Playable ? new Color(1f, 1f, 1f) : new Color(0.5f, 0.5f, 0.5f);
            button.MouseEntered += () => { _selected = index; RefreshHighlight(); };
            button.Pressed += () => Activate(index);
            Rows.Add(button);
            host.AddChild(button);
        }

        _footer = NewLabel(blocked != null
                ? $"Replay unavailable right now: {blocked}"
                : "Up/Down selects • Enter / A plays • Esc / B closes",
            17);
        _footer.HorizontalAlignment = HorizontalAlignment.Center;
        _footer.Modulate = blocked != null ? new Color(1f, 0.7f, 0.45f) : new Color(0.82f, 0.82f, 0.82f);
        rows.AddChild(_footer);
    }

    private static Label NewLabel(string text, int size)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    private static void Move(int delta)
    {
        if (Rows.Count == 0) return;
        _selected = (_selected + delta + Rows.Count) % Rows.Count;
        RefreshHighlight();
    }

    private static void RefreshHighlight()
    {
        for (int i = 0; i < Highlights.Count; i++)
        {
            var h = Highlights[i];
            if (!GodotObject.IsInstanceValid(h)) continue;
            if (i == _selected)
            {
                h.AddThemeStyleboxOverride("panel", Rows[i].GetThemeStylebox("focus"));
                h.Visible = true;
                if (_scroll != null && GodotObject.IsInstanceValid(_scroll)) _scroll.EnsureControlVisible(Rows[i]);
            }
            else
            {
                h.Visible = false;
            }
        }
    }

    private static void Activate(int index)
    {
        if (index < 0 || index >= _listings.Count) return;
        var listing = _listings[index];
        if (!listing.Playable) return;
        string? blocked = TryStart(listing, "replay list", Close);
        if (blocked != null && _footer != null && GodotObject.IsInstanceValid(_footer))
            _footer.Text = $"Replay unavailable right now: {blocked}";
    }
}
