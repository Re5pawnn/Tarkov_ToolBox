using System.Runtime.InteropServices;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Passively observes a short, standalone Ctrl press without consuming keyboard or mouse input.
/// Any keyboard or mouse-button input used while Ctrl is held cancels the gesture so game
/// shortcuts such as Ctrl+left-click and Ctrl+right-click continue to behave normally.
/// </summary>
public sealed class GlobalCtrlTapService : IDisposable
{
    private const int WhKeyboardLowLevel = 13;
    private const int WhMouseLowLevel = 14;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseHorizontalWheel = 0x020E;

    private const int VkControl = 0x11;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int VkMiddleButton = 0x04;
    private const int VkXButton1 = 0x05;
    private const int VkXButton2 = 0x06;
    private const long MaximumTapDurationMilliseconds = 700;

    private readonly LowLevelHookProcedure _keyboardProcedure;
    private readonly LowLevelHookProcedure _mouseProcedure;
    private readonly HashSet<int> _pressedControlKeys = [];
    private readonly HashSet<int> _pressedNonControlKeys = [];
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private SynchronizationContext? _eventContext;
    private bool _tapCandidate;
    private long _controlPressedAt;
    private bool _disposed;

    public GlobalCtrlTapService()
    {
        _keyboardProcedure = KeyboardHookCallback;
        _mouseProcedure = MouseHookCallback;
    }

    public event Action? CtrlTapped;

    public bool IsRunning => _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

    public int LastError { get; private set; }

    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return true;

        StopHooks();
        LastError = 0;
        _eventContext = SynchronizationContext.Current;
        var moduleHandle = GetModuleHandle(null);

        _keyboardHook = SetWindowsHookEx(WhKeyboardLowLevel, _keyboardProcedure, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseProcedure, moduleHandle, 0);
        if (_mouseHook != IntPtr.Zero) return true;

        // Mouse observation is mandatory: without it Ctrl+click could be mistaken for a tap.
        LastError = Marshal.GetLastWin32Error();
        StopHooks();
        return false;
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        if (code >= 0)
        {
            var message = messagePointer.ToInt32();
            var virtualKey = Marshal.ReadInt32(dataPointer);
            if (message is WmKeyDown or WmSysKeyDown)
            {
                HandleKeyDown(virtualKey);
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                HandleKeyUp(virtualKey);
            }
        }

        return CallNextHookEx(_keyboardHook, code, messagePointer, dataPointer);
    }

    private IntPtr MouseHookCallback(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        if (code >= 0 && _pressedControlKeys.Count > 0 && IsMouseCombinationInput(messagePointer.ToInt32()))
        {
            _tapCandidate = false;
        }

        return CallNextHookEx(_mouseHook, code, messagePointer, dataPointer);
    }

    private void HandleKeyDown(int virtualKey)
    {
        if (!IsControlKey(virtualKey))
        {
            _pressedNonControlKeys.Add(virtualKey);
            if (_pressedControlKeys.Count > 0) _tapCandidate = false;
            return;
        }

        var normalizedKey = NormalizeControlKey(virtualKey);
        if (!_pressedControlKeys.Add(normalizedKey)) return;
        if (_pressedControlKeys.Count != 1) return;

        _controlPressedAt = Environment.TickCount64;
        _tapCandidate = _pressedNonControlKeys.Count == 0 && !IsAnyMouseButtonPressed();
    }

    private void HandleKeyUp(int virtualKey)
    {
        if (!IsControlKey(virtualKey))
        {
            _pressedNonControlKeys.Remove(virtualKey);
            return;
        }

        _pressedControlKeys.Remove(NormalizeControlKey(virtualKey));
        if (_pressedControlKeys.Count > 0) return;

        var elapsed = Environment.TickCount64 - _controlPressedAt;
        var shouldRaise = _tapCandidate && elapsed is >= 0 and <= MaximumTapDurationMilliseconds;
        _tapCandidate = false;
        if (shouldRaise) QueueCtrlTapped();
    }

    private void QueueCtrlTapped()
    {
        var handler = CtrlTapped;
        if (handler is null) return;

        if (_eventContext is not null)
        {
            _eventContext.Post(_ => InvokeSafely(handler), null);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => InvokeSafely(handler));
    }

    private static void InvokeSafely(Action handler)
    {
        try { handler(); }
        catch { }
    }

    private static bool IsControlKey(int virtualKey) =>
        virtualKey is VkControl or VkLeftControl or VkRightControl;

    private static int NormalizeControlKey(int virtualKey) =>
        virtualKey == VkRightControl ? VkRightControl : VkLeftControl;

    private static bool IsMouseCombinationInput(int message) => message is
        WmLeftButtonDown or WmLeftButtonUp or
        WmRightButtonDown or WmRightButtonUp or
        WmMiddleButtonDown or WmMiddleButtonUp or
        WmXButtonDown or WmXButtonUp or
        WmMouseWheel or WmMouseHorizontalWheel;

    private static bool IsAnyMouseButtonPressed() =>
        IsPressed(VkLeftButton) ||
        IsPressed(VkRightButton) ||
        IsPressed(VkMiddleButton) ||
        IsPressed(VkXButton1) ||
        IsPressed(VkXButton2);

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void StopHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _pressedControlKeys.Clear();
        _pressedNonControlKeys.Clear();
        _tapCandidate = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHooks();
        CtrlTapped = null;
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr LowLevelHookProcedure(int code, IntPtr messagePointer, IntPtr dataPointer);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelHookProcedure procedure,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr messagePointer,
        IntPtr dataPointer);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
