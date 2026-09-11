using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TarkovMapLocator.Modules.InGamePrice.Models;

namespace TarkovMapLocator.Modules.InGamePrice.Services;

/// <summary>
/// Locates the EFT top-level window without requiring the game to be the
/// foreground application. It also remembers the last cursor position inside
/// the game so an interactive OCR debug window does not move the scan area onto
/// itself as soon as the user clicks it.
/// </summary>
public static class EftCaptureTargetService
{
    private const string EftProcessName = "EscapeFromTarkov";
    private static readonly object Gate = new();
    private static IntPtr _cachedWindow;
    private static Point? _lastGameCursor;
    private static long _nextWindowProbeAt;

    public static bool IsGameForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0) return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, EftProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public static ScreenCaptureRegion? TryGetWindowBounds()
    {
        lock (Gate)
        {
            if (TryReadUsableBounds(_cachedWindow, out var cachedBounds))
                return cachedBounds;

            _cachedWindow = IntPtr.Zero;
            var now = Environment.TickCount64;
            if (now < _nextWindowProbeAt) return null;
            _nextWindowProbeAt = now + 500;

            var processIds = GetEftProcessIds();
            if (processIds.Count == 0) return null;

            var foreground = GetForegroundWindow();
            if (BelongsTo(foreground, processIds) && TryReadUsableBounds(foreground, out var foregroundBounds))
            {
                _cachedWindow = foreground;
                return foregroundBounds;
            }

            IntPtr bestWindow = IntPtr.Zero;
            ScreenCaptureRegion? bestBounds = null;
            long bestArea = 0;
            EnumWindows((window, _) =>
            {
                if (!BelongsTo(window, processIds) || !TryReadUsableBounds(window, out var bounds))
                    return true;

                var area = (long)bounds.Width * bounds.Height;
                if (area <= bestArea) return true;
                bestArea = area;
                bestWindow = window;
                bestBounds = bounds;
                return true;
            }, IntPtr.Zero);

            _cachedWindow = bestWindow;
            if (_cachedWindow != IntPtr.Zero)
                _nextWindowProbeAt = 0;
            return bestBounds;
        }
    }

    public static Point GetSearchPoint(ScreenCaptureRegion bounds)
    {
        lock (Gate)
        {
            Point? current = null;
            if (GetCursorPos(out var cursor))
                current = new Point(cursor.X, cursor.Y);

            var resolved = ResolveSearchPoint(bounds, current, _lastGameCursor);
            if (current is { } point && Contains(bounds, point))
                _lastGameCursor = point;
            return resolved;
        }
    }

    internal static Point ResolveSearchPoint(ScreenCaptureRegion bounds, Point? current, Point? lastKnown)
    {
        if (current is { } currentPoint && Contains(bounds, currentPoint))
            return currentPoint;
        if (lastKnown is { } lastPoint && Contains(bounds, lastPoint))
            return lastPoint;
        return new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }

    private static bool Contains(ScreenCaptureRegion bounds, Point point) =>
        point.X >= bounds.X &&
        point.X < bounds.X + bounds.Width &&
        point.Y >= bounds.Y &&
        point.Y < bounds.Y + bounds.Height;

    private static bool BelongsTo(IntPtr window, IReadOnlySet<int> processIds)
    {
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && processIds.Contains((int)processId);
    }

    private static HashSet<int> GetEftProcessIds()
    {
        var processIds = new HashSet<int>();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(EftProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return processIds;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    processIds.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                    // The process can exit between enumeration and reading its id.
                }
            }
        }

        return processIds;
    }

    private static bool TryReadUsableBounds(IntPtr window, out ScreenCaptureRegion bounds)
    {
        bounds = null!;
        if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window) ||
            !GetWindowRect(window, out var rectangle))
            return false;

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width < 24 || height < 24) return false;
        bounds = new ScreenCaptureRegion(rectangle.Left, rectangle.Top, width, height);
        return true;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
