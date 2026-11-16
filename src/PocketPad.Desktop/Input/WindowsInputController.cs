using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Input;

/// <summary>
/// <see cref="IInputController"/> backed by Win32 <c>SendInput</c>. Failures (secure desktop, locked
/// workstation, blocked input) are logged with rate limiting and reported as false; they never throw.
/// </summary>
public sealed partial class WindowsInputController(ILogger<WindowsInputController> logger, TimeProvider time) : IInputController
{
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(10);
    private long _lastFailureLogTimestamp = long.MinValue;
    private int _suppressedFailures;

    public bool MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return true;
        return Send([CreateMouseInput(NativeMethods.MOUSEEVENTF_MOVE, dx, dy)]);
    }

    public bool Click(MouseButton button, MouseClickAction action) => Send(BuildClick(button, action));

    public bool Wheel(int delta, bool horizontal) => delta == 0 || Send([BuildWheel(delta, horizontal)]);

    /// <summary>MOUSEEVENTF_WHEEL / HWHEEL; mouseData carries the signed delta (two's complement in the DWORD).</summary>
    internal static NativeMethods.INPUT BuildWheel(int delta, bool horizontal)
    {
        var input = CreateMouseInput(horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL, 0, 0);
        input.U.mi.mouseData = unchecked((uint)delta);
        return input;
    }

    public bool TypeText(string text)
    {
        var inputs = BuildText(text);
        return inputs.Length == 0 || Send(inputs);
    }

    public bool Key(KeyCode key, KeyModifiers modifiers, KeyAction action) => Send(BuildKey(key, modifiers, action));

    public bool Media(MediaCommand command) => Send(BuildVirtualKeyPress(MediaVirtualKey(command)));

    // ---- mouse -------------------------------------------------------------------------------

    internal static NativeMethods.INPUT[] BuildClick(MouseButton button, MouseClickAction action)
    {
        var (down, up) = button switch
        {
            MouseButton.Left => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP),
            MouseButton.Right => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };

        uint[] flags = action switch
        {
            MouseClickAction.Click => [down, up],
            // Sent as one batch so the two clicks fall within the system double-click time and distance.
            MouseClickAction.DoubleClick => [down, up, down, up],
            MouseClickAction.Down => [down],
            MouseClickAction.Up => [up],
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        return flags.Select(f => CreateMouseInput(f, 0, 0)).ToArray();
    }

    internal static NativeMethods.INPUT CreateMouseInput(uint flags, int dx, int dy) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        U = new NativeMethods.InputUnion
        {
            mi = new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags },
        },
    };

    // ---- keyboard ----------------------------------------------------------------------------

    /// <summary>
    /// Text as KEYEVENTF_UNICODE events (layout independent, e.g. "ğüşıöç" work on any layout).
    /// Line breaks and tabs become real Enter/Tab presses because many apps ignore them as Unicode.
    /// </summary>
    internal static NativeMethods.INPUT[] BuildText(string text)
    {
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    inputs.AddRange(BuildVirtualKeyPress(NativeMethods.VK_RETURN));
                    break;
                case '\n':
                    inputs.AddRange(BuildVirtualKeyPress(NativeMethods.VK_RETURN));
                    break;
                case '\t':
                    inputs.AddRange(BuildVirtualKeyPress(NativeMethods.VK_TAB));
                    break;
                default:
                    if (char.IsControl(c)) break;
                    // Surrogate pairs are sent as two UTF-16 units, which is what Windows expects.
                    inputs.Add(CreateKeyInput(0, c, NativeMethods.KEYEVENTF_UNICODE));
                    inputs.Add(CreateKeyInput(0, c, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP));
                    break;
            }
        }
        return [.. inputs];
    }

    internal static NativeMethods.INPUT[] BuildKey(KeyCode key, KeyModifiers modifiers, KeyAction action)
    {
        var mods = ModifierKeys(modifiers);
        var vk = VirtualKey(key);
        var inputs = new List<NativeMethods.INPUT>();

        if (action is KeyAction.Press or KeyAction.Down)
        {
            inputs.AddRange(mods.Select(m => VirtualKeyInput(m, keyUp: false)));
            inputs.Add(VirtualKeyInput(vk, keyUp: false));
        }
        if (action is KeyAction.Press or KeyAction.Up)
        {
            inputs.Add(VirtualKeyInput(vk, keyUp: true));
            inputs.AddRange(mods.AsEnumerable().Reverse().Select(m => VirtualKeyInput(m, keyUp: true)));
        }
        return [.. inputs];
    }

    internal static NativeMethods.INPUT[] BuildVirtualKeyPress(ushort vk) =>
        [VirtualKeyInput(vk, keyUp: false), VirtualKeyInput(vk, keyUp: true)];

    internal static ushort[] ModifierKeys(KeyModifiers modifiers)
    {
        var keys = new List<ushort>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) keys.Add(NativeMethods.VK_CONTROL);
        if (modifiers.HasFlag(KeyModifiers.Shift)) keys.Add(NativeMethods.VK_SHIFT);
        if (modifiers.HasFlag(KeyModifiers.Alt)) keys.Add(NativeMethods.VK_MENU);
        if (modifiers.HasFlag(KeyModifiers.Windows)) keys.Add(NativeMethods.VK_LWIN);
        return [.. keys];
    }

    internal static ushort VirtualKey(KeyCode key) => key switch
    {
        KeyCode.Enter => NativeMethods.VK_RETURN,
        KeyCode.Backspace => 0x08,
        KeyCode.Tab => NativeMethods.VK_TAB,
        KeyCode.Escape => 0x1B,
        KeyCode.Space => 0x20,
        KeyCode.PageUp => 0x21,
        KeyCode.PageDown => 0x22,
        KeyCode.End => 0x23,
        KeyCode.Home => 0x24,
        KeyCode.Left => 0x25,
        KeyCode.Up => 0x26,
        KeyCode.Right => 0x27,
        KeyCode.Down => 0x28,
        KeyCode.PrintScreen => 0x2C,
        KeyCode.Insert => 0x2D,
        KeyCode.Delete => 0x2E,
        KeyCode.Windows => NativeMethods.VK_LWIN,
        KeyCode.ContextMenu => 0x5D,
        >= KeyCode.F1 and <= KeyCode.F12 => (ushort)(0x70 + (key - KeyCode.F1)),
        >= KeyCode.A and <= KeyCode.Z => (ushort)('A' + (key - KeyCode.A)),     // VK_A..VK_Z = 'A'..'Z'
        >= KeyCode.D0 and <= KeyCode.D9 => (ushort)('0' + (key - KeyCode.D0)),  // VK_0..VK_9 = '0'..'9'
        KeyCode.Plus => 0xBB,   // VK_OEM_PLUS
        KeyCode.Minus => 0xBD,  // VK_OEM_MINUS
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    internal static ushort MediaVirtualKey(MediaCommand command) => command switch
    {
        MediaCommand.Mute => 0xAD,
        MediaCommand.VolumeDown => 0xAE,
        MediaCommand.VolumeUp => 0xAF,
        MediaCommand.Next => 0xB0,
        MediaCommand.Previous => 0xB1,
        MediaCommand.Stop => 0xB2,
        MediaCommand.PlayPause => 0xB3,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>Navigation keys, Win, Apps, PrintScreen and media keys are "extended"; without the flag arrows act as numpad keys.</summary>
    internal static bool IsExtended(ushort vk) =>
        vk is (>= 0x21 and <= 0x28) or 0x2C or 0x2D or 0x2E or NativeMethods.VK_LWIN or 0x5D or (>= 0xAD and <= 0xB3);

    private static NativeMethods.INPUT VirtualKeyInput(ushort vk, bool keyUp)
    {
        var flags = (IsExtended(vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0) | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0);
        var scan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        return CreateKeyInput(vk, scan, flags);
    }

    internal static NativeMethods.INPUT CreateKeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags },
        },
    };

    // ---- SendInput -----------------------------------------------------------------------------

    private bool Send(NativeMethods.INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent == inputs.Length) return true;

        var error = Marshal.GetLastPInvokeError();
        LogFailure(sent, inputs.Length, error);
        return false;
    }

    private void LogFailure(uint sent, int expected, int error)
    {
        var now = time.GetTimestamp();
        var last = Interlocked.Read(ref _lastFailureLogTimestamp);
        if (last != long.MinValue && time.GetElapsedTime(last, now) < FailureLogInterval)
        {
            Interlocked.Increment(ref _suppressedFailures);
            return;
        }
        Interlocked.Exchange(ref _lastFailureLogTimestamp, now);
        var suppressed = Interlocked.Exchange(ref _suppressedFailures, 0);
        logger.LogWarning(
            "SendInput injected {Sent}/{Expected} events (Win32 error {Error}); {Suppressed} similar failures suppressed. " +
            "Input is blocked e.g. on the lock screen, UAC prompt or another secure desktop.",
            sent, expected, error, suppressed);
    }
}

/// <summary>Win32 declarations. Layouts match the native structs on both x64 (INPUT = 40 bytes) and x86 (28 bytes).</summary>
internal static partial class NativeMethods
{
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const ushort VK_TAB = 0x09;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_LWIN = 0x5B;

    public const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U; // pointer-aligned union: offset 8 on x64, 4 on x86
    }

    /// <summary>All members are declared so the union has the native size (largest = MOUSEINPUT).</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyW(uint uCode, uint uMapType);
}
