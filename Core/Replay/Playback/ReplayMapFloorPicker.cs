using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
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
/// The game's map normally lets you pick only the next row's rooms. While
/// picking, the game's debug travel switch makes every room clickable, the
/// rooms the recording never entered are disabled again, the recorded rooms
/// the replay has not reached yet are shown as travelable, and pad focus is
/// rewired so up/down steps from one recorded room to the next regardless of
/// the map's paths. Closing the picker puts every one of those back: the
/// game recomputes the rooms' states from the run, and the saved focus
/// neighbors are restored.
///
/// The selection itself is intercepted in <see cref="SelectionPatch"/>, a
/// prefix on <c>NMapScreen.OnMapPointSelectedLocally</c> (the method a room
/// click and the replay's own map-vote feeding both call); only while
/// picking does it act, so feeding is untouched.
/// </summary>
internal static class ReplayMapFloorPicker
{
    private sealed record SavedFocus(NodePath Top, NodePath Bottom, NodePath Left, NodePath Right);

    private static NMapScreen? _screen;
    private static bool _openedMap;
    private static int _act;
    private static readonly Dictionary<MapCoord, RecordedRoom> Selectable = new();
    private static readonly Dictionary<NMapPoint, SavedFocus> SavedNeighbors = new();
    private static readonly List<(NMapPoint Point, Callable Callback)> FocusHooks = new();

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

        // Debug travel makes every room's IsTravelable true; disable the
        // unrecorded ones again after the refresh that applies it.
        screen.SetDebugTravelEnabled(true);
        var visited = new HashSet<MapCoord>(state.VisitedMapCoords);
        foreach (var (coord, point) in points)
        {
            if (!Selectable.ContainsKey(coord))
            {
                point.State = MapPointState.Untravelable;
                point.Disable();
            }
            else if (!visited.Contains(coord))
            {
                point.State = MapPointState.Travelable;
            }
        }

        var ordered = rooms
            .Select(r => new MapCoord { col = r.Coord.Col, row = r.Coord.Row })
            .Where(points.ContainsKey)
            .Select(c => points[c])
            .ToList();
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
                screen.RecalculateTravelability();
                screen.SetDebugTravelEnabled(false);
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
            var callback = Callable.From(() => OnRoomFocused(target));
            point.Connect(Control.SignalName.FocusEntered, callback);
            FocusHooks.Add((point, callback));
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
        foreach (var (point, callback) in FocusHooks)
        {
            if (GodotObject.IsInstanceValid(point) && point.IsConnected(Control.SignalName.FocusEntered, callback))
                point.Disconnect(Control.SignalName.FocusEntered, callback);
        }
    }

    /// <summary>
    /// Pad focus moving to a room far up or down the map: scroll it into
    /// view, as the game does for the current room. The game only accepts a
    /// pad selection of a room that is on screen.
    /// </summary>
    private static void OnRoomFocused(NMapPoint point)
    {
        if (!IsOpen || NControllerManager.Instance?.IsUsingDirectionalNavigation != true) return;
        ScrollTo(point);
    }

    private static void ScrollTo(NMapPoint point)
    {
        var screen = _screen;
        if (screen == null || !GodotObject.IsInstanceValid(screen)) return;
        screen._targetDragPos = new Vector2(0f, -600f + point.Point.coord.row * screen._distY);
    }

    /// <summary>A recorded room was chosen: close the map and ask the player to jump there.</summary>
    private static void Choose(NMapPoint point)
    {
        if (!Selectable.TryGetValue(point.Point.coord, out var room)) return;
        Close();
        ReplayPlayer.RequestJumpToRoom(room);
    }

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
    internal static class SelectionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(NMapPoint point)
        {
            if (!IsOpen) return true;
            try
            {
                if (point?.Point != null) Choose(point);
            }
            catch (Exception e)
            {
                CoreMain.Logger.Warn($"Replay: floor picker selection failed: {e.Message}");
            }
            return false;
        }
    }
}
