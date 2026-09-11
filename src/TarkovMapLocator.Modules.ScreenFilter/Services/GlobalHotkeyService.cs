using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TarkovMapLocator.Modules.ScreenFilter.Models;

namespace TarkovMapLocator.Modules.ScreenFilter.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WhMouseLowLevel = 14;
    private const int WmXButtonDown = 0x020B;
    private const int VkXButton1 = 0x05;
    private const int VkXButton2 = 0x06;
    private const uint ModNoRepeat = 0x4000;
    private static int _nextRegistrationId = 0x5300;

    private readonly int _registrationId = Interlocked.Increment(ref _nextRegistrationId);
    private readonly LowLevelMouseProcedure _mouseProcedure;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _hookAttached;
    private IntPtr _mouseHook;
    private SynchronizationContext? _eventContext;
    private GlobalHotkeyGesture _mouseGesture = GlobalHotkeyGesture.None;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _mouseProcedure = MouseHookCallback;
    }

    public event Action? Pressed;

    public bool IsRegistered { get; private set; }

    public int LastError { get; private set; }

    public bool Register(Window owner, GlobalHotkeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = gesture.Normalize();
        Unregister();
        LastError = 0;
        if (!normalized.IsConfigured) return true;

        _eventContext = SynchronizationContext.Current;
        if (IsMouseButton(normalized.VirtualKey))
        {
            _mouseGesture = normalized;
            _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseProcedure, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                LastError = Marshal.GetLastWin32Error();
                _mouseGesture = GlobalHotkeyGesture.None;
                return false;
            }

            IsRegistered = true;
            return true;
        }

        EnsureWindowHook(owner);
        if (_windowHandle == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        var modifiers = (uint)normalized.Modifiers | ModNoRepeat;
        if (!RegisterHotKey(_windowHandle, _registrationId, modifiers, (uint)normalized.VirtualKey))
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        IsRegistered = true;
        return true;
    }

    public void Unregister()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseGesture = GlobalHotkeyGesture.None;
        }
        else if (IsRegistered && _windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, _registrationId);
        }
        IsRegistered = false;
    }

    private IntPtr MouseHookCallback(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        try
        {
            if (code >= 0 && messagePointer.ToInt32() == WmXButtonDown && _mouseGesture.IsConfigured)
            {
                var data = Marshal.PtrToStructure<LowLevelMouseData>(dataPointer);
                var button = (ushort)(data.MouseData >> 16) switch
                {
                    1 => VkXButton1,
                    2 => VkXButton2,
                    _ => 0
                };
                if (button == _mouseGesture.VirtualKey && ReadPressedModifiers() == _mouseGesture.Modifiers)
                    QueuePressed();
            }
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("全局热键", "处理鼠标侧键失败", exception);
        }

        return CallNextHookEx(_mouseHook, code, messagePointer, dataPointer);
    }

    private void QueuePressed()
    {
        var handler = Pressed;
        if (handler is null) return;
        if (_eventContext is not null)
            _eventContext.Post(_ => InvokeSafely(handler), null);
        else
            ThreadPool.QueueUserWorkItem(_ => InvokeSafely(handler));
    }

    private static void InvokeSafely(Action handler)
    {
        try { handler(); }
        catch (Exception exception)
        {
            RuntimeLogService.Error("全局热键", "执行热键操作失败", exception);
        }
    }

    private static GlobalHotkeyModifiers ReadPressedModifiers()
    {
        var modifiers = GlobalHotkeyModifiers.None;
        if (IsPressed(0x11)) modifiers |= GlobalHotkeyModifiers.Control;
        if (IsPressed(0x12)) modifiers |= GlobalHotkeyModifiers.Alt;
        if (IsPressed(0x10)) modifiers |= GlobalHotkeyModifiers.Shift;
        if (IsPressed(0x5B) || IsPressed(0x5C)) modifiers |= GlobalHotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    internal static bool IsMouseButtonForTest(int virtualKey) => IsMouseButton(virtualKey);

    private static bool IsMouseButton(int virtualKey) => virtualKey is VkXButton1 or VkXButton2;

    private void EnsureWindowHook(Window owner)
    {
        if (_hookAttached) return;
        _windowHandle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle);
        if (_source is null) return;
        _source.AddHook(WindowMessageHook);
        _hookAttached = true;
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam.ToInt32() != _registrationId) return IntPtr.Zero;

        handled = true;
        var handler = Pressed;
        if (handler is not null) InvokeSafely(handler);
        return IntPtr.Zero;
    }

    public static string Format(GlobalHotkeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (!normalized.IsConfigured) return "未设置";

        var parts = new List<string>(5);
        if (normalized.Modifiers.HasFlag(GlobalHotkeyModifiers.Control)) parts.Add("Ctrl");
        if (normalized.Modifiers.HasFlag(GlobalHotkeyModifiers.Alt)) parts.Add("Alt");
        if (normalized.Modifiers.HasFlag(GlobalHotkeyModifiers.Shift)) parts.Add("Shift");
        if (normalized.Modifiers.HasFlag(GlobalHotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(FormatMainKey(normalized.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string FormatMainKey(int virtualKey)
    {
        if (virtualKey == VkXButton1) return "鼠标侧键 1";
        if (virtualKey == VkXButton2) return "鼠标侧键 2";
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + key - Key.D0)).ToString(),
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.Space => "Space",
            Key.Return => "Enter",
            _ when key != Key.None => key.ToString(),
            _ => $"VK {virtualKey:X2}"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        if (_hookAttached && _source is not null) _source.RemoveHook(WindowMessageHook);
        _hookAttached = false;
        _source = null;
        _windowHandle = IntPtr.Zero;
        Pressed = null;
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    private delegate IntPtr LowLevelMouseProcedure(int code, IntPtr messagePointer, IntPtr dataPointer);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelMouseProcedure procedure,
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
