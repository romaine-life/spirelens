using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// Floor navigation during a replay, on the game's own map
/// (docs/replay-player.md, "Jumping to a floor"). Opens the current act's map
/// with only the rooms the recording entered selectable; choosing one asks
/// <see cref="ReplayPlayer"/> to jump to that floor's start.
///
/// The map keeps the game's own look and behavior: every room stays a plain,
/// non-clickable map room (the game pulses the rooms it lets you click), and
/// the game's travel is switched off while picking, so no click can move
/// the replayed run. The whole recorded path is drawn as already traveled,
/// rooms and the path dots between them, as a look back over the run; the
/// game's marker shows the current room. The choice is read by
/// <see cref="ReplayPlayer"/>'s input handling instead: a left click on a
/// recorded room (<see cref="TryChooseUnderMouse"/>), or A / Enter on the
/// focused one (<see cref="TryChooseFocused"/>). Rooms are focusable while not
/// clickable (the game allows it), and pad focus is rewired so up/down steps
/// from one recorded room to the next regardless of the map's paths, with the
/// game's selection reticle on the focused room. Closing the picker puts
/// every one of those back: the game recomputes the rooms' states from the
/// run, travel returns to what it was, and the saved path dots and focus
/// neighbors are restored.
/// </summary>
internal static class ReplayMapFloorPicker
{
    private sealed record SavedFocus(NodePath Top, NodePath Bottom, NodePath Left, NodePath Right);

    private static NMapScreen? _screen;
    private static bool _openedMap;
    private static bool _travelWasEnabled;
    private static int _act;
    private static readonly Dictionary<MapCoord, RecordedRoom> Selectable = new();
    private static readonly Dictionary<NMapPoint, SavedFocus> SavedNeighbors = new();
    private static readonly List<(NMapPoint Point, StringName Signal, Callable Callback)> FocusHooks = new();
    private static readonly Dictionary<TextureRect, (Color Modulate, Vector2 Scale)> SavedTicks = new();

    public static bool IsOpen => _screen != null;

    /// <summary>
    /// Open the picker on the current act's map. Returns why it could not,
    /// or null. <paramref name="currentKeyframe"/> is the room-start keyframe
    /// the replay is in (-1 if none); its room gets focus.
    /// </summary>
    public static string? Open(ReplayPlaybackPlan plan, int currentKeyframe)
    {
        if (IsOpen) return null;
        var screen = NMapScreen.Instance;
        var state = RunManager.Instance?.State;
        if (screen == null || !GodotObject.IsInstanceValid(screen) || state == null) return "the map is not ready";
        if (screen.IsTraveling) return "the map is moving to a room";

        _act = state.CurrentActIndex;
        var rooms = plan.RoomsInAct(_act);
        if (rooms.Count == 0) return $"the recording has no room starts in act {_act + 1}";

        var points = AllPoints(screen);
        Selectable.Clear();
        foreach (var room in rooms)
        {
            var coord = new MapCoord { col = room.Coord.Col, row = room.Coord.Row };
            if (points.ContainsKey(coord)) Selectable[coord] = room;
        }
        if (Selectable.Count == 0) return "none of the recorded rooms are on this map";

        _screen = screen;
        _openedMap = !screen.IsOpen;
        if (_openedMap) screen.Open(isOpenedFromTopBar: true);
        screen.Closed += OnScreenClosed;

        // With travel off, every room refreshes as not clickable (no pulse,
        // no travel); then the recorded path takes the traveled look and the
        // rest the untravelable one.
        _travelWasEnabled = screen.IsTravelEnabled;
        screen.SetTravelEnabled(false);
        foreach (var (coord, point) in points)
            point.State = Selectable.ContainsKey(coord) ? MapPointState.Traveled : MapPointState.Untravelable;

        var ordered = rooms
            .Select(r => new MapCoord { col = r.Coord.Col, row = r.Coord.Row })
            .Where(points.ContainsKey)
            .Select(c => points[c])
            .ToList();
        PaintPath(screen, ordered, state.Act.MapTraveledColor);
        WireFocus(ordered);

        var focus = ordered.FirstOrDefault(p => Selectable[p.Point.coord].KeyframeIndex == currentKeyframe)
                    ?? ordered.FirstOrDefault(p => Selectable[p.Point.coord].KeyframeIndex > currentKeyframe)
                    ?? ordered[^1];
        focus.GrabFocus();
        ScrollTo(focus);
        CoreMain.Logger.Info($"Replay: floor picker opened on act {_act + 1} ({Selectable.Count} recorded rooms)");
        return null;
    }

    /// <summary>
    /// Close the picker and restore the map. A map the picker opened is
    /// closed; a map the game already had open (choosing the next room)
    /// stays open with its own state.
    /// </summary>
    public static void Close()
    {
        var screen = _screen;
        if (screen == null) return;
        _screen = null;
        try
        {
            if (GodotObject.IsInstanceValid(screen))
            {
                screen.Closed -= OnScreenClosed;
                UnwireFocus();
                RestoreTicks();
                screen.RecalculateTravelability();
                screen.SetTravelEnabled(_travelWasEnabled);
                if (_openedMap) screen.Close();
            }
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Replay: restoring the map after the floor picker failed: {e.Message}");
        }
        finally
        {
            SavedNeighbors.Clear();
            FocusHooks.Clear();
            SavedTicks.Clear();
            Selectable.Clear();
            ReplayPlayer.OnFloorPickerClosed();
        }
    }

    /// <summary>The game closed the map itself (its back button): end picking.</summary>
    private static void OnScreenClosed()
    {
        _openedMap = false;
        Close();
    }

    private static Dictionary<MapCoord, NMapPoint> AllPoints(NMapScreen screen)
    {
        var all = new Dictionary<MapCoord, NMapPoint>();
        foreach (var point in screen._mapPointDictionary.Values
                     .Concat(new NMapPoint?[] { screen._bossPointNode, screen._secondBossPointNode, screen._startingPointNode }))
        {
            if (point == null || !GodotObject.IsInstanceValid(point) || point.Point == null) continue;
            all[point.Point.coord] = point;
        }
        return all;
    }

    /// <summary>
    /// Color the path dots between consecutive recorded rooms as the game
    /// colors a traveled path (its act's traveled color, the traveled dot
    /// size). Rooms reached without a path between them (Winged Boots) have
    /// no dots to color.
    /// </summary>
    private static void PaintPath(NMapScreen screen, List<NMapPoint> ordered, Color traveled)
    {
        for (int i = 0; i + 1 < ordered.Count; i++)
        {
            if (!screen._paths.TryGetValue((ordered[i].Point.coord, ordered[i + 1].Point.coord), out var ticks)) continue;
            foreach (var tick in ticks)
            {
                if (!GodotObject.IsInstanceValid(tick)) continue;
                SavedTicks.TryAdd(tick, (tick.Modulate, tick.Scale));
                tick.Modulate = traveled;
                tick.Scale = NMapScreen._tickTraveledScale;
            }
        }
    }

    private static void RestoreTicks()
    {
        foreach (var (tick, saved) in SavedTicks)
        {
            if (!GodotObject.IsInstanceValid(tick)) continue;
            tick.Modulate = saved.Modulate;
            tick.Scale = saved.Scale;
        }
    }

    /// <summary>Up / down step through the recorded rooms in play order; left / right stay put.</summary>
    private static void WireFocus(List<NMapPoint> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            var point = ordered[i];
            SavedNeighbors[point] = new SavedFocus(
                point.FocusNeighborTop, point.FocusNeighborBottom, point.FocusNeighborLeft, point.FocusNeighborRight);
            var self = point.GetPath();
            point.FocusNeighborTop = i + 1 < ordered.Count ? ordered[i + 1].GetPath() : self;
            point.FocusNeighborBottom = i > 0 ? ordered[i - 1].GetPath() : self;
            point.FocusNeighborLeft = self;
            point.FocusNeighborRight = self;

            var target = point;
            var entered = Callable.From(() => OnRoomFocused(target));
            var exited = Callable.From(() => OnRoomUnfocused(target));
            point.Connect(Control.SignalName.FocusEntered, entered);
            point.Connect(Control.SignalName.FocusExited, exited);
            FocusHooks.Add((point, Control.SignalName.FocusEntered, entered));
            FocusHooks.Add((point, Control.SignalName.FocusExited, exited));
        }
    }

    private static void UnwireFocus()
    {
        foreach (var (point, saved) in SavedNeighbors)
        {
            if (!GodotObject.IsInstanceValid(point)) continue;
            point.FocusNeighborTop = saved.Top;
            point.FocusNeighborBottom = saved.Bottom;
            point.FocusNeighborLeft = saved.Left;
            point.FocusNeighborRight = saved.Right;
        }
        foreach (var (point, signal, callback) in FocusHooks)
        {
            if (!GodotObject.IsInstanceValid(point)) continue;
            if (point.IsConnected(signal, callback)) point.Disconnect(signal, callback);
            point._controllerSelectionReticle?.OnDeselect();
        }
    }

    /// <summary>
    /// Pad focus on a room: show the game's selection reticle on it (the game
    /// shows it only on rooms it lets you click) and scroll it into view, as
    /// the game does for the current room.
    /// </summary>
    private static void OnRoomFocused(NMapPoint point)
    {
        if (!IsOpen || NControllerManager.Instance?.IsUsingDirectionalNavigation != true) return;
        point._controllerSelectionReticle?.OnSelect();
        ScrollTo(point);
    }

    private static void OnRoomUnfocused(NMapPoint point)
    {
        if (GodotObject.IsInstanceValid(point)) point._controllerSelectionReticle?.OnDeselect();
    }

    private static void ScrollTo(NMapPoint point)
    {
        var screen = _screen;
        if (screen == null || !GodotObject.IsInstanceValid(screen)) return;
        screen._targetDragPos = new Vector2(0f, -600f + point.Point.coord.row * screen._distY);
    }

    /// <summary>A left click: true (and the jump is requested) when it is on a recorded room.</summary>
    public static bool TryChooseUnderMouse()
    {
        var screen = _screen;
        if (screen == null || !GodotObject.IsInstanceValid(screen)) return false;
        Node? node = screen.GetViewport()?.GuiGetHoveredControl();
        while (node != null && node is not NMapPoint) node = node.GetParent();
        return node is NMapPoint point && Choose(point);
    }

    /// <summary>A / Enter: true (and the jump is requested) when a recorded room has focus.</summary>
    public static bool TryChooseFocused()
    {
        var screen = _screen;
        if (screen == null || !GodotObject.IsInstanceValid(screen)) return false;
        return screen.GetViewport()?.GuiGetFocusOwner() is NMapPoint point && Choose(point);
    }

    /// <summary>A recorded room was chosen: close the map and ask the player to jump there.</summary>
    private static bool Choose(NMapPoint point)
    {
        if (point.Point == null || !Selectable.TryGetValue(point.Point.coord, out var room)) return false;
        Close();
        ReplayPlayer.RequestJumpToRoom(room);
        return true;
    }
}
