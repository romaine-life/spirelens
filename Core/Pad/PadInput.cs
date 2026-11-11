using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.ControllerInput;

namespace SpireLens.Core.Pad;

/// <summary>
/// Pad buttons matched by the game's controller actions, not raw
/// <see cref="JoyButton"/> values. Under Steam Input (the game's preferred
/// path) a pad press reaches Godot only as an <see cref="InputEventAction"/>
/// the game parses (<c>SteamControllerInputStrategy</c>), never as
/// <see cref="InputEventJoypadButton"/>; with Steam Input off, Godot's joypad
/// events match the same actions through the InputMap. Steam Input's action
/// set has no right-stick click; <see cref="GameInputPad"/> supplies it.
/// </summary>
internal static class PadInput
{
    /// <summary>Every controller action the game defines.</summary>
    public static readonly StringName[] ControllerActions =
    {
        Controller.dPadUp, Controller.dPadDown, Controller.dPadLeft, Controller.dPadRight,
        Controller.faceButtonNorth, Controller.faceButtonSouth, Controller.faceButtonEast, Controller.faceButtonWest,
        Controller.leftBumper, Controller.rightBumper, Controller.leftTrigger, Controller.rightTrigger,
        Controller.startButton, Controller.selectButton, Controller.lStickPress,
        Controller.lStickUp, Controller.lStickDown, Controller.lStickLeft, Controller.lStickRight,
        Controller.rStickUp, Controller.rStickDown, Controller.rStickLeft, Controller.rStickRight,
        Controller.ps4Touchpad,
    };

    /// <summary>The first of <paramref name="actions"/> this pad event is, or null.</summary>
    public static StringName? Action(InputEvent evt, StringName[] actions)
    {
        if (evt is not (InputEventAction or InputEventJoypadButton or InputEventJoypadMotion)) return null;
        foreach (var action in actions)
        {
            // IsAction on an InputMap action that doesn't exist logs an error
            // for every event; a parsed InputEventAction just compares names.
            if (evt is not InputEventAction && !InputMap.HasAction(action)) continue;
            if (evt.IsAction(action)) return action;
        }
        return null;
    }

    /// <summary>
    /// A stick direction. The game also makes d-pad actions from the left
    /// stick (and, under Steam Input, the right), so menus act on those.
    /// </summary>
    public static bool IsStickDirection(StringName action)
        => action == Controller.lStickUp || action == Controller.lStickDown
           || action == Controller.lStickLeft || action == Controller.lStickRight
           || action == Controller.rStickUp || action == Controller.rStickDown
           || action == Controller.rStickLeft || action == Controller.rStickRight;

    /// <summary>
    /// Any input from a pad: a raw joypad event, or a controller action the
    /// game parsed from one.
    /// </summary>
    public static bool IsPadEvent(InputEvent evt)
        => evt is InputEventJoypadButton or InputEventJoypadMotion
           || (evt is InputEventAction && Action(evt, ControllerActions) != null);
}

/// <summary>
/// Press edges of pad actions. A press acts once (triggers are axes that
/// repeat), and its release is remembered so the consumer can consume it too.
/// </summary>
internal sealed class PadPresses
{
    private readonly HashSet<string> _held = new();

    /// <summary>True on the press edge of <paramref name="action"/>; tracks its release.</summary>
    public bool Press(InputEvent evt, StringName action)
    {
        if (evt.IsPressed()) return _held.Add(action.ToString());
        _held.Remove(action.ToString());
        return false;
    }

    /// <summary>True when <paramref name="action"/> was held by a consumed press.</summary>
    public bool Release(StringName action) => _held.Remove(action.ToString());

    public void Clear() => _held.Clear();
}
