using System;
using System.Runtime.InteropServices;

namespace SpireLens.Core.Pad;

/// <summary>
/// Reads the pad's right-stick click (R3) through Windows GameInput, the one
/// controller interface Steam Input leaves visible inside the game. Under
/// Steam Input the game sees a pad only through its Steam action set, which
/// has no right-stick click, and Steam hides the pad from XInput, raw HID,
/// Windows.Gaming.Input and Godot's joypad input in the game process.
/// Unavailable off Windows or without <c>GameInput.dll</c>; Godot's raw R3
/// event is then the only source.
/// </summary>
internal static unsafe class GameInputPad
{
    private const uint GameInputKindGamepad = 0x00040000;
    private const uint GamepadRightThumbstick = 0x2000;

    // IGameInput (v0): IUnknown 0-2, GetCurrentTimestamp 3, GetCurrentReading 4.
    private const int GetCurrentReadingSlot = 4;

    // IGameInputReading (v0): IUnknown 0-2, then 19 getters before GetGamepadState.
    private const int GetGamepadStateSlot = 22;

    [StructLayout(LayoutKind.Sequential)]
    private struct GamepadState
    {
        public uint Buttons;
        public float LeftTrigger, RightTrigger;
        public float LeftThumbstickX, LeftThumbstickY, RightThumbstickX, RightThumbstickY;
    }

    [DllImport("GameInput.dll")]
    private static extern int GameInputCreate(out IntPtr gameInput);

    private static IntPtr _gameInput;
    private static bool _createAttempted;
    private static bool _down;

    /// <summary>
    /// True on the frame GameInput first reports the right-stick click held.
    /// Called once per frame, from the game's own per-frame pad read.
    /// </summary>
    public static bool PollPress()
    {
        bool down = IsDown() == true;
        bool press = down && !_down;
        _down = down;
        return press;
    }

    /// <summary>
    /// The right-stick click as GameInput reports it now, or null when
    /// GameInput is unavailable or no gamepad has sent it input yet.
    /// </summary>
    public static bool? IsDown()
    {
        if (!EnsureCreated()) return null;

        IntPtr reading;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr*, int>)(*(void***)_gameInput)[GetCurrentReadingSlot])(
            _gameInput, GameInputKindGamepad, IntPtr.Zero, &reading);
        // GAMEINPUT_E_READING_NOT_FOUND until a gamepad has sent input.
        if (hr < 0) return null;

        try
        {
            GamepadState state;
            byte ok = ((delegate* unmanaged[Stdcall]<IntPtr, GamepadState*, byte>)(*(void***)reading)[GetGamepadStateSlot])(
                reading, &state);
            return ok == 0 ? null : (state.Buttons & GamepadRightThumbstick) != 0;
        }
        finally
        {
            Marshal.Release(reading);
        }
    }

    private static bool EnsureCreated()
    {
        if (_gameInput != IntPtr.Zero) return true;
        if (_createAttempted) return false;
        _createAttempted = true;

        if (!OperatingSystem.IsWindows())
        {
            CoreMain.Logger.Info("GameInput: not on Windows; R3 comes only from Godot's joypad input");
            return false;
        }

        try
        {
            int hr = GameInputCreate(out var gameInput);
            if (hr < 0 || gameInput == IntPtr.Zero)
            {
                CoreMain.Logger.Info($"GameInput: unavailable (0x{hr:X8}); R3 comes only from Godot's joypad input");
                return false;
            }
            _gameInput = gameInput;
            CoreMain.Logger.Info("GameInput: reading the right-stick click (R3) through GameInput");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            CoreMain.Logger.Info($"GameInput: unavailable ({e.GetType().Name}); R3 comes only from Godot's joypad input");
            return false;
        }
    }

    /// <summary>Frees the GameInput object; called from Core shutdown before a hot reload.</summary>
    public static void Release()
    {
        if (_gameInput != IntPtr.Zero) Marshal.Release(_gameInput);
        _gameInput = IntPtr.Zero;
    }
}

/// <summary>
/// One right-stick click reaches SpireLens once, whichever source reports it.
/// With Steam Input off both GameInput and Godot's raw joypad event see the
/// same click; under Steam Input only GameInput does; a pad GameInput can't
/// see (or no GameInput at all) leaves only the raw event.
/// </summary>
internal sealed class RightStickClickArbiter
{
    private bool _rawOwnsClick;

    /// <summary>
    /// A raw R3 press arrived. Returns true when the raw path should handle
    /// it: GameInput doesn't see the click held, so it never will.
    /// </summary>
    public bool OnRawPress(bool gameInputSeesDown)
    {
        if (gameInputSeesDown) return false;
        _rawOwnsClick = true;
        return true;
    }

    /// <summary>The raw click ended; a later GameInput press is a new click.</summary>
    public void OnRawRelease() => _rawOwnsClick = false;

    /// <summary>
    /// GameInput reported a press. Returns true when it should be dispatched:
    /// the raw path didn't already handle this click.
    /// </summary>
    public bool OnGameInputPress() => !_rawOwnsClick;
}
