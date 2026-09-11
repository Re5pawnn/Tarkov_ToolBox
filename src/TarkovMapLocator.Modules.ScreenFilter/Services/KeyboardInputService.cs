using System.ComponentModel;
using System.Runtime.InteropServices;
using TarkovMapLocator.Modules.ScreenFilter.Models;

namespace TarkovMapLocator.Modules.ScreenFilter.Services;

public static class KeyboardInputService
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventXButtonDown = 0x0080;
    private const uint MouseEventXButtonUp = 0x0100;
    private const int VirtualKeyXButton1 = 0x05;
    private const int VirtualKeyXButton2 = 0x06;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyLeftWindows = 0x5B;

    public static async Task<bool> WaitForReleaseAsync(
        GlobalHotkeyGesture gesture,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var keys = GetPressedKeys(gesture.Normalize()).Distinct().ToArray();
        if (keys.Length == 0) return true;
        var deadline = DateTime.UtcNow + timeout;
        while (keys.Any(IsPressed))
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(25, cancellationToken);
        }
        return true;
    }

    public static bool TrySend(GlobalHotkeyGesture gesture, out string error)
    {
        var normalized = gesture.Normalize();
        if (!normalized.IsConfigured)
        {
            error = "游戏截图键尚未设置。";
            return false;
        }

        var inputSize = Marshal.SizeOf<NativeInput>();
        var expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
        if (inputSize != expectedInputSize)
        {
            error = $"模拟按键结构尺寸无效（当前 {inputSize}，预期 {expectedInputSize}）。";
            return false;
        }
        var inputs = BuildNativeInputs(normalized);

        var sent = SendInput((uint)inputs.Length, inputs, inputSize);
        if (sent == inputs.Length)
        {
            error = "";
            return true;
        }

        var win32Error = Marshal.GetLastWin32Error();
        if (sent > 0) ReleaseGesture(normalized);
        error = win32Error == 0
            ? "系统拒绝了模拟按键。请确认游戏和工具使用相同的运行权限。"
            : new Win32Exception(win32Error).Message;
        return false;
    }

    internal static IReadOnlyList<(int VirtualKey, bool KeyUp)> BuildSequenceForTest(GlobalHotkeyGesture gesture) =>
        BuildSequence(gesture.Normalize());

    internal static int NativeInputSizeForTest() => Marshal.SizeOf<NativeInput>();

    internal static IReadOnlyList<uint> NativeInputTypesForTest(GlobalHotkeyGesture gesture) =>
        BuildNativeInputs(gesture.Normalize()).Select(input => input.Type).ToArray();

    private static IReadOnlyList<(int VirtualKey, bool KeyUp)> BuildSequence(GlobalHotkeyGesture gesture)
    {
        var result = new List<(int VirtualKey, bool KeyUp)>();
        var modifiers = GetModifierKeys(gesture.Modifiers).ToArray();
        result.AddRange(modifiers.Select(key => (key, false)));
        result.Add((gesture.VirtualKey, false));
        result.Add((gesture.VirtualKey, true));
        result.AddRange(modifiers.Reverse().Select(key => (key, true)));
        return result;
    }

    private static NativeInput[] BuildNativeInputs(GlobalHotkeyGesture gesture)
    {
        var inputs = new List<NativeInput>();
        var modifiers = GetModifierKeys(gesture.Modifiers).ToArray();
        inputs.AddRange(modifiers.Select(key => CreateKeyboardInput(key, keyUp: false)));
        if (IsMouseButton(gesture.VirtualKey))
        {
            inputs.Add(CreateMouseButtonInput(gesture.VirtualKey, keyUp: false));
            inputs.Add(CreateMouseButtonInput(gesture.VirtualKey, keyUp: true));
        }
        else
        {
            inputs.Add(CreateKeyboardInput(gesture.VirtualKey, keyUp: false));
            inputs.Add(CreateKeyboardInput(gesture.VirtualKey, keyUp: true));
        }
        inputs.AddRange(modifiers.Reverse().Select(key => CreateKeyboardInput(key, keyUp: true)));
        return inputs.ToArray();
    }

    private static NativeInput CreateKeyboardInput(int virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)virtualKey,
                Flags = (keyUp ? KeyEventKeyUp : 0) |
                        (IsExtendedKey(virtualKey) ? KeyEventExtendedKey : 0)
            }
        }
    };

    private static NativeInput CreateMouseButtonInput(int virtualKey, bool keyUp) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                MouseData = virtualKey == VirtualKeyXButton1 ? 1u : 2u,
                Flags = keyUp ? MouseEventXButtonUp : MouseEventXButtonDown
            }
        }
    };

    private static IEnumerable<int> GetPressedKeys(GlobalHotkeyGesture gesture)
    {
        if (gesture.IsConfigured) yield return gesture.VirtualKey;
        foreach (var key in GetModifierKeys(gesture.Modifiers)) yield return key;
    }

    private static IEnumerable<int> GetModifierKeys(GlobalHotkeyModifiers modifiers)
    {
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Control)) yield return VirtualKeyControl;
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Alt)) yield return VirtualKeyAlt;
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Shift)) yield return VirtualKeyShift;
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Windows)) yield return VirtualKeyLeftWindows;
    }

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsMouseButton(int virtualKey) => virtualKey is VirtualKeyXButton1 or VirtualKeyXButton2;

    private static bool IsExtendedKey(int virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F;

    private static void ReleaseGesture(GlobalHotkeyGesture gesture)
    {
        var inputs = new List<NativeInput>();
        if (IsMouseButton(gesture.VirtualKey))
            inputs.Add(CreateMouseButtonInput(gesture.VirtualKey, keyUp: true));
        else
            inputs.Add(CreateKeyboardInput(gesture.VirtualKey, keyUp: true));
        inputs.AddRange(GetModifierKeys(gesture.Modifiers).Reverse().Select(key => CreateKeyboardInput(key, keyUp: true)));
        var recoveryInputs = inputs.ToArray();
        if (recoveryInputs.Length > 0)
            SendInput((uint)recoveryInputs.Length, recoveryInputs, Marshal.SizeOf<NativeInput>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // INPUT uses a native union. KEYBDINPUT is only 24 bytes on x64,
        // while MOUSEINPUT is 32 bytes; omitting the largest member shrinks
        // INPUT to 32 bytes and SendInput rejects cbSize with ERROR_INVALID_PARAMETER.
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
