using System.Runtime.InteropServices;
using TarkovMapLocator.App.Models;

namespace TarkovMapLocator.App.Services;

public sealed class ScreenGammaService : IDisposable
{
    private const uint MonitorInfoPrimary = 1;
    private readonly HashSet<string> adjustedDisplays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GammaRamp> originalRamps = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public IReadOnlyList<ScreenDisplayInfo> GetDisplays()
    {
        var displays = new List<ScreenDisplayInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect rect, IntPtr data)
        {
            var info = new MonitorInfoEx
            {
                cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>()
            };

            if (GetMonitorInfoW(hMonitor, ref info) && !string.IsNullOrWhiteSpace(info.szDevice))
            {
                var index = displays.Count + 1;
                var isPrimary = (info.dwFlags & MonitorInfoPrimary) == MonitorInfoPrimary;
                var label = isPrimary
                    ? $"屏幕 {index} 主屏 ({info.szDevice})"
                    : $"屏幕 {index} ({info.szDevice})";
                displays.Add(new ScreenDisplayInfo(info.szDevice, label, isPrimary));
            }

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        return displays.Count > 0
            ? displays
            : [new ScreenDisplayInfo(@"\\.\DISPLAY1", @"屏幕 1 (\\.\DISPLAY1)", true)];
    }

    public ScreenGammaResult Apply(ScreenFilterPreset preset, string displayName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var displays = ResolveDisplays(displayName);
        CaptureOriginalRamps(displays);

        var ramp = BuildRamp(preset);
        var result = SetRamp(displays, ramp);
        foreach (var deviceName in result.TouchedDisplays)
        {
            adjustedDisplays.Add(deviceName);
        }

        return BuildResult(result, "已应用屏幕滤镜");
    }

    public ScreenGammaResult Reset(string displayName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var result = RestoreRamp(displayName);
        foreach (var deviceName in result.TouchedDisplays)
        {
            adjustedDisplays.Remove(deviceName);
            originalRamps.Remove(deviceName);
        }

        return BuildResult(result, "已恢复屏幕颜色");
    }

    public void ResetAll()
    {
        if (disposed || adjustedDisplays.Count == 0)
        {
            return;
        }

        foreach (var deviceName in adjustedDisplays.ToArray())
        {
            Reset(deviceName);
        }
    }

    public void Dispose()
    {
        ResetAll();
        disposed = true;
    }

    private static ScreenGammaResult BuildResult(RampApplyResult result, string successMessage)
    {
        if (result.AppliedCount > 0 && result.FailedCount == 0)
        {
            return new ScreenGammaResult(result.AppliedCount, 0, $"{successMessage} ({result.AppliedCount} 个屏幕)");
        }

        if (result.AppliedCount > 0)
        {
            return new ScreenGammaResult(
                result.AppliedCount,
                result.FailedCount,
                $"{successMessage}，但 {result.FailedCount} 个屏幕失败: {result.LastError}");
        }

        return new ScreenGammaResult(0, result.FailedCount, $"屏幕滤镜失败: {result.LastError}");
    }

    private void CaptureOriginalRamps(IReadOnlyList<ScreenDisplayInfo> displays)
    {
        foreach (var display in displays)
        {
            if (originalRamps.ContainsKey(display.DeviceName))
            {
                continue;
            }

            if (TryGetCurrentRamp(display.DeviceName, out var currentRamp))
            {
                originalRamps[display.DeviceName] = currentRamp;
            }
        }
    }

    private RampApplyResult RestoreRamp(string displayName)
    {
        var displays = ResolveDisplays(displayName);
        var appliedCount = 0;
        var failedCount = 0;
        var lastError = "";
        var touchedDisplays = new List<string>();
        var defaultRamp = BuildRamp(ScreenFilterPreset.Default);

        foreach (var display in displays)
        {
            var ramp = originalRamps.TryGetValue(display.DeviceName, out var originalRamp)
                ? originalRamp
                : defaultRamp;
            var result = SetRamp([display], ramp);
            appliedCount += result.AppliedCount;
            failedCount += result.FailedCount;
            if (!string.IsNullOrWhiteSpace(result.LastError))
            {
                lastError = result.LastError;
            }

            touchedDisplays.AddRange(result.TouchedDisplays);
        }

        if (string.IsNullOrWhiteSpace(lastError) && appliedCount == 0)
        {
            lastError = "未找到可恢复屏幕";
        }

        return new RampApplyResult(appliedCount, failedCount, lastError, touchedDisplays);
    }

    private static bool TryGetCurrentRamp(string deviceName, out GammaRamp ramp)
    {
        ramp = CreateEmptyRamp();
        var hdc = CreateDCW("DISPLAY", deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return GetDeviceGammaRamp(hdc, ref ramp);
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    private static RampApplyResult SetRamp(IReadOnlyList<ScreenDisplayInfo> displays, GammaRamp ramp)
    {
        var appliedCount = 0;
        var failedCount = 0;
        var lastError = "";
        var touchedDisplays = new List<string>();

        foreach (var display in displays)
        {
            var hdc = CreateDCW("DISPLAY", display.DeviceName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                failedCount++;
                lastError = $"CreateDCW {display.DeviceName} failed ({Marshal.GetLastWin32Error()})";
                continue;
            }

            try
            {
                if (SetDeviceGammaRamp(hdc, ref ramp))
                {
                    appliedCount++;
                    touchedDisplays.Add(display.DeviceName);
                }
                else
                {
                    failedCount++;
                    lastError = $"SetDeviceGammaRamp {display.DeviceName} failed ({Marshal.GetLastWin32Error()})";
                }
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        if (string.IsNullOrWhiteSpace(lastError) && appliedCount == 0)
        {
            lastError = "未找到可用屏幕";
        }

        return new RampApplyResult(appliedCount, failedCount, lastError, touchedDisplays);
    }

    private IReadOnlyList<ScreenDisplayInfo> ResolveDisplays(string displayName)
    {
        var displays = GetDisplays();
        var normalized = displayName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return displays;
        }

        return displays
            .Where(display => string.Equals(display.DeviceName, normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static GammaRamp BuildRamp(ScreenFilterPreset preset)
    {
        var gamma = Math.Clamp(preset.Gamma, 0.2, 5.0);
        var brightness = Math.Clamp(preset.Brightness, -100, 100);
        var contrastFactor = Math.Pow(1.0 + Math.Clamp(preset.Contrast, -100, 100) / 100.0, 2.0);
        var redScale = Math.Clamp(preset.Red, 0, 255) / 128.0;
        var greenScale = Math.Clamp(preset.Green, 0, 255) / 128.0;
        var blueScale = Math.Clamp(preset.Blue, 0, 255) / 128.0;

        var ramp = CreateEmptyRamp();

        for (var i = 0; i < 256; i++)
        {
            var corrected = ApplyMiaoMiaoGammaFormula(i, gamma, brightness, contrastFactor);

            ramp.Red[i] = ToRampValue(corrected * redScale);
            ramp.Green[i] = ToRampValue(corrected * greenScale);
            ramp.Blue[i] = ToRampValue(corrected * blueScale);
        }

        NormalizeRampChannel(ramp.Red);
        NormalizeRampChannel(ramp.Green);
        NormalizeRampChannel(ramp.Blue);

        return ramp;
    }

    private static GammaRamp CreateEmptyRamp()
    {
        return new GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
    }

    private static double ApplyMiaoMiaoGammaFormula(
        int input,
        double gamma,
        int brightness,
        double contrastFactor)
    {
        var adjusted = ((input - 127.5) * contrastFactor) + 127.5 + brightness;
        var normalized = Math.Clamp(adjusted / 255.0, 0.0, 1.0);
        return Math.Pow(normalized, 1.0 / gamma);
    }

    private static ushort ToRampValue(double value)
    {
        return (ushort)Math.Round(Math.Clamp(value, 0.0, 1.0) * ushort.MaxValue);
    }

    private static void NormalizeRampChannel(ushort[] channel)
    {
        var previous = channel[0];
        for (var i = 1; i < channel.Length; i++)
        {
            if (channel[i] < previous)
            {
                channel[i] = previous;
            }
            else
            {
                previous = channel[i];
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDCW(
        string driver,
        string device,
        string? output,
        IntPtr initData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref GammaRamp ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ref GammaRamp ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GammaRamp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;
    }

    private sealed record RampApplyResult(
        int AppliedCount,
        int FailedCount,
        string LastError,
        IReadOnlyList<string> TouchedDisplays);
}
