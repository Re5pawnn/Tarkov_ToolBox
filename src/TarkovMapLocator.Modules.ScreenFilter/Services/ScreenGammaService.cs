using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TarkovMapLocator.Modules.ScreenFilter.Models;

namespace TarkovMapLocator.Modules.ScreenFilter.Services;

/// <summary>
/// Native display gamma-ramp implementation copied from the original desktop
/// tool. It captures the unmodified ramp before changing a display and restores
/// it whenever the filter is reset or the application exits.
/// </summary>
public sealed class ScreenGammaService : IDisposable
{
    private const uint MonitorInfoPrimary = 1;
    private const int TransitionIntervalMilliseconds = 75;
    private readonly object _sync = new();
    private readonly HashSet<string> _adjustedDisplays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GammaRamp> _originalRamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GammaRamp> _currentRamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GammaRamp> _transitionFromRamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GammaRamp> _transitionToRamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _recoveryPath = Path.Combine(RecoveryDirectory, $"gamma-recovery-{Environment.ProcessId}.json");
    private Timer? _transitionTimer;
    private int _transitionStep;
    private int _transitionSteps;
    private bool _watchdogStarted;
    private int _protectedDisplayCount;
    private bool _disposed;

    private static string RecoveryDirectory => Path.Combine(
        ApplicationIdentity.ApplicationDataDirectory,
        "gamma-recovery");

    public bool HasActiveAdjustments
    {
        get { lock (_sync) return _adjustedDisplays.Count > 0; }
    }

    public IReadOnlyList<ScreenDisplayInfo> GetDisplays()
    {
        var displays = new List<ScreenDisplayInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect rect, IntPtr data)
        {
            var info = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(hMonitor, ref info) && !string.IsNullOrWhiteSpace(info.szDevice))
            {
                var index = displays.Count + 1;
                var isPrimary = (info.dwFlags & MonitorInfoPrimary) == MonitorInfoPrimary;
                var label = isPrimary ? $"屏幕 {index} 主屏 ({info.szDevice})" : $"屏幕 {index} ({info.szDevice})";
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
        => ApplyRamp(BuildRamp(preset), displayName, "已应用屏幕滤镜", 0);

    public ScreenGammaResult ApplyCurve(
        IReadOnlyList<ushort> red,
        IReadOnlyList<ushort> green,
        IReadOnlyList<ushort> blue,
        string displayName)
        => ApplyCurve(red, green, blue, displayName, TimeSpan.Zero);

    public ScreenGammaResult ApplyCurve(
        IReadOnlyList<ushort> red,
        IReadOnlyList<ushort> green,
        IReadOnlyList<ushort> blue,
        string displayName,
        TimeSpan transitionDuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        if (red.Count != 256 || green.Count != 256 || blue.Count != 256)
            throw new ArgumentException("Gamma 曲线的每个颜色通道必须包含 256 个值。");

        var ramp = new GammaRamp
        {
            Red = red.ToArray(),
            Green = green.ToArray(),
            Blue = blue.ToArray()
        };
        return ApplyRamp(
            ramp,
            displayName,
            "已应用自动滤镜",
            (int)Math.Clamp(transitionDuration.TotalMilliseconds, 0, 2000));
    }

    private ScreenGammaResult ApplyRamp(GammaRamp ramp, string displayName, string successMessage, int transitionMilliseconds)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopTransition();
            var displays = ResolveDisplays(displayName);
            var targetNames = displays.Select(display => display.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var obsoleteDisplay in _adjustedDisplays.Where(device => !targetNames.Contains(device)).ToArray())
                Reset(obsoleteDisplay);
            var unrestoredDisplays = _adjustedDisplays.Where(device => !targetNames.Contains(device)).ToArray();
            if (unrestoredDisplays.Length > 0)
                return new ScreenGammaResult(0, unrestoredDisplays.Length, $"屏幕滤镜失败: 无法恢复先前屏幕 {string.Join("、", unrestoredDisplays)}");

            CaptureOriginalRamps(displays);
            var restorableDisplays = displays
                .Where(display => _originalRamps.ContainsKey(display.DeviceName))
                .ToArray();
            if (restorableDisplays.Length == 0)
            {
                return new ScreenGammaResult(
                    0,
                    Math.Max(1, displays.Count),
                    "屏幕滤镜失败: 无法备份当前屏幕颜色，未做任何修改。");
            }

            if (!EnsureRecoveryProtection())
            {
                return new ScreenGammaResult(
                    0,
                    restorableDisplays.Length,
                    "屏幕滤镜失败: 无法建立强退恢复保护，未做任何修改。");
            }

            var result = transitionMilliseconds > 0
                ? StartTransition(restorableDisplays, ramp, transitionMilliseconds)
                : SetRamp(restorableDisplays, ramp);
            var skippedDisplays = displays.Count - restorableDisplays.Length;
            if (skippedDisplays > 0)
            {
                result = result with
                {
                    FailedCount = result.FailedCount + skippedDisplays,
                    LastError = string.IsNullOrWhiteSpace(result.LastError)
                        ? $"{skippedDisplays} 个屏幕无法备份原始颜色，已跳过"
                        : result.LastError
                };
            }
            foreach (var deviceName in result.TouchedDisplays)
            {
                _adjustedDisplays.Add(deviceName);
                if (transitionMilliseconds <= 0) _currentRamps[deviceName] = CloneRamp(ramp);
            }
            return BuildResult(result, successMessage);
        }
    }

    private bool EnsureRecoveryProtection()
    {
        if (!_watchdogStarted || _protectedDisplayCount != _originalRamps.Count || !File.Exists(_recoveryPath))
        {
            if (!PersistRecoverySnapshot()) return false;
            _protectedDisplayCount = _originalRamps.Count;
        }

        return EnsureWatchdogStarted();
    }

    public ScreenGammaResult Reset(string displayName)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopTransition();
            var result = RestoreRamp(displayName);
            foreach (var deviceName in result.TouchedDisplays)
            {
                _adjustedDisplays.Remove(deviceName);
                _originalRamps.Remove(deviceName);
                _currentRamps.Remove(deviceName);
            }
            PersistRecoverySnapshot();

            return result.AppliedCount == 0 && result.FailedCount == 0
                ? new ScreenGammaResult(0, 0, result.LastError)
                : BuildResult(result, "已恢复屏幕颜色");
        }
    }

    public ScreenGammaResult ResetAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Reset("");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StopTransition();
            ResetAll();
            if (_adjustedDisplays.Count == 0) DeleteRecoveryFile(_recoveryPath);
            _disposed = true;
        }
    }

    public static int RestoreAbandonedSessions() => RestorePersistedSessions(includeLiveOwners: false);

    public static int RestoreAllPersistedSessions() => RestorePersistedSessions(includeLiveOwners: true);

    public static int RunRecoveryWatchdog(int ownerProcessId, string recoveryPath)
    {
        try
        {
            using var owner = Process.GetProcessById(ownerProcessId);
            owner.WaitForExit();
        }
        catch (ArgumentException)
        {
            // The owner already exited before the watchdog attached.
        }
        catch (InvalidOperationException)
        {
            // The process handle disappeared; recovery is still required.
        }

        return RestoreRecoveryFile(recoveryPath) ? 0 : 1;
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
            if (_originalRamps.ContainsKey(display.DeviceName)) continue;
            if (TryGetCurrentRamp(display.DeviceName, out var currentRamp)) _originalRamps[display.DeviceName] = currentRamp;
        }
    }

    private bool PersistRecoverySnapshot()
    {
        if (_originalRamps.Count == 0)
        {
            DeleteRecoveryFile(_recoveryPath);
            return true;
        }

        var snapshot = new GammaRecoverySnapshot
        {
            OwnerProcessId = Environment.ProcessId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Displays = _originalRamps.Select(pair => new GammaRecoveryDisplay
            {
                DeviceName = pair.Key,
                Red = pair.Value.Red,
                Green = pair.Value.Green,
                Blue = pair.Value.Blue
            }).ToArray()
        };
        return TryWriteRecoverySnapshot(_recoveryPath, snapshot);
    }

    private bool EnsureWatchdogStarted()
    {
        if (_watchdogStarted) return true;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return false;
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--gamma-watchdog");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(_recoveryPath);
            _watchdogStarted = Process.Start(startInfo) is not null;
            return _watchdogStarted;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            RuntimeLogService.Error("屏幕滤镜", "启动强退恢复守护进程失败", exception);
            return false;
        }
    }

    private static int RestorePersistedSessions(bool includeLiveOwners)
    {
        try
        {
            if (!Directory.Exists(RecoveryDirectory)) return 0;
            var restored = 0;
            foreach (var path in Directory.EnumerateFiles(RecoveryDirectory, "gamma-recovery-*.json", SearchOption.TopDirectoryOnly))
            {
                if (!includeLiveOwners && TryReadRecovery(path, out var snapshot) && IsProcessAlive(snapshot.OwnerProcessId))
                    continue;
                if (RestoreRecoveryFile(path)) restored++;
            }
            return restored;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("屏幕滤镜", "扫描强退恢复文件失败", exception.ToString());
            return 0;
        }
    }

    private static bool RestoreRecoveryFile(string path) =>
        RestoreRecoveryFileCore(path, TryRestoreRecoveryDisplay);

    internal static bool RestoreRecoveryFileForTest(string path, Func<string, bool> restoreDisplay)
    {
        ArgumentNullException.ThrowIfNull(restoreDisplay);
        return RestoreRecoveryFileCore(path, display => restoreDisplay(display.DeviceName));
    }

    private static bool RestoreRecoveryFileCore(string path, Func<GammaRecoveryDisplay, bool> restoreDisplay)
    {
        if (!File.Exists(path)) return true;
        if (!TryReadRecovery(path, out var snapshot))
        {
            DeleteRecoveryFile(path);
            return false;
        }

        var restoredAll = true;
        var remainingDisplays = new List<GammaRecoveryDisplay>();
        foreach (var display in snapshot.Displays)
        {
            if (!IsValidRecoveryDisplay(display))
            {
                restoredAll = false;
                continue;
            }

            if (restoreDisplay(display)) continue;
            restoredAll = false;
            remainingDisplays.Add(display);
        }

        if (remainingDisplays.Count == 0)
        {
            DeleteRecoveryFile(path);
            return restoredAll;
        }

        // Successful displays are removed immediately. A disconnected or failed
        // display remains for the next startup/watchdog retry and is not restored
        // repeatedly together with displays that have already succeeded.
        var remainingSnapshot = new GammaRecoverySnapshot
        {
            OwnerProcessId = snapshot.OwnerProcessId,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            Displays = remainingDisplays.ToArray()
        };
        TryWriteRecoverySnapshot(path, remainingSnapshot);
        return false;
    }

    private static bool IsValidRecoveryDisplay(GammaRecoveryDisplay? display) =>
        display is not null &&
        !string.IsNullOrWhiteSpace(display.DeviceName) &&
        display.Red is { Length: 256 } &&
        display.Green is { Length: 256 } &&
        display.Blue is { Length: 256 };

    private static bool TryRestoreRecoveryDisplay(GammaRecoveryDisplay display)
    {
        var ramp = new GammaRamp { Red = display.Red, Green = display.Green, Blue = display.Blue };
        var hdc = CreateDCW("DISPLAY", display.DeviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(hdc, ref ramp); }
        finally { DeleteDC(hdc); }
    }

    private static bool TryReadRecovery(string path, out GammaRecoverySnapshot snapshot)
    {
        snapshot = new GammaRecoverySnapshot();
        try
        {
            using var stream = File.OpenRead(path);
            var parsed = JsonSerializer.Deserialize<GammaRecoverySnapshot>(stream);
            if (parsed?.Displays is not { Length: > 0 }) return false;
            snapshot = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            RuntimeLogService.Warning("屏幕滤镜", "读取强退恢复文件失败", $"文件: {path}\n{exception}");
            return false;
        }
    }

    private static bool TryWriteRecoverySnapshot(string path, GammaRecoverySnapshot snapshot)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            using (var stream = File.Create(temporaryPath)) JsonSerializer.Serialize(stream, snapshot);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            RuntimeLogService.Error("屏幕滤镜", "写入强退恢复文件失败", exception, $"恢复文件: {path}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogService.Warning("屏幕滤镜", "清理强退恢复临时文件失败", $"文件: {temporaryPath}\n{exception.Message}");
                }
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void DeleteRecoveryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var temporaryPath = path + ".tmp";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("屏幕滤镜", "清理强退恢复文件失败", $"文件: {path}\n{exception.Message}");
        }
    }

    private RampApplyResult RestoreRamp(string displayName)
    {
        var normalizedDisplayName = displayName?.Trim() ?? "";
        var deviceNames = _adjustedDisplays
            .Where(deviceName => string.IsNullOrWhiteSpace(normalizedDisplayName) ||
                string.Equals(deviceName, normalizedDisplayName, StringComparison.OrdinalIgnoreCase))
            .Where(_originalRamps.ContainsKey)
            .ToArray();
        if (deviceNames.Length == 0)
            return new RampApplyResult(0, 0, "当前会话没有可恢复的屏幕颜色", []);

        var appliedCount = 0;
        var failedCount = 0;
        var lastError = "";
        var touchedDisplays = new List<string>();

        foreach (var deviceName in deviceNames)
        {
            var ramp = _originalRamps[deviceName];
            var result = SetRamp([new ScreenDisplayInfo(deviceName, deviceName, false)], ramp);
            appliedCount += result.AppliedCount;
            failedCount += result.FailedCount;
            if (!string.IsNullOrWhiteSpace(result.LastError)) lastError = result.LastError;
            touchedDisplays.AddRange(result.TouchedDisplays);
        }

        if (string.IsNullOrWhiteSpace(lastError) && appliedCount == 0) lastError = "未找到可恢复屏幕";
        return new RampApplyResult(appliedCount, failedCount, lastError, touchedDisplays);
    }

    private static bool TryGetCurrentRamp(string deviceName, out GammaRamp ramp)
    {
        ramp = CreateEmptyRamp();
        var hdc = CreateDCW("DISPLAY", deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            return GetDeviceGammaRamp(hdc, ref ramp);
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    private RampApplyResult StartTransition(
        IReadOnlyList<ScreenDisplayInfo> displays,
        GammaRamp target,
        int durationMilliseconds)
    {
        StopTransition();
        _transitionSteps = Math.Max(1, durationMilliseconds / TransitionIntervalMilliseconds);
        _transitionStep = 1;
        foreach (var display in displays)
        {
            var from = _currentRamps.TryGetValue(display.DeviceName, out var current)
                ? current
                : _originalRamps[display.DeviceName];
            _transitionFromRamps[display.DeviceName] = CloneRamp(from);
            _transitionToRamps[display.DeviceName] = CloneRamp(target);
        }

        var firstResult = ApplyTransitionStep(displays);
        if (firstResult.FailedCount > 0 || _transitionStep >= _transitionSteps)
        {
            StopTransition();
            return firstResult;
        }

        _transitionTimer = new Timer(
            _ => TickTransition(),
            null,
            TransitionIntervalMilliseconds,
            TransitionIntervalMilliseconds);
        return firstResult;
    }

    private void TickTransition()
    {
        lock (_sync)
        {
            if (_disposed || _transitionTimer is null) return;
            _transitionStep++;
            var displays = _transitionToRamps.Keys
                .Select(deviceName => new ScreenDisplayInfo(deviceName, deviceName, false))
                .ToArray();
            var result = ApplyTransitionStep(displays);
            if (result.FailedCount > 0)
            {
                RuntimeLogService.Warning("屏幕滤镜", "平滑切换中断", result.LastError);
                StopTransition();
                return;
            }

            if (_transitionStep >= _transitionSteps) StopTransition();
        }
    }

    private RampApplyResult ApplyTransitionStep(IReadOnlyList<ScreenDisplayInfo> displays)
    {
        var amount = Math.Clamp(_transitionStep / (double)_transitionSteps, 0.0, 1.0);
        amount = amount * amount * (3.0 - 2.0 * amount);
        var applied = 0;
        var failed = 0;
        var lastError = "";
        var touched = new List<string>();
        foreach (var display in displays)
        {
            if (!_transitionFromRamps.TryGetValue(display.DeviceName, out var from) ||
                !_transitionToRamps.TryGetValue(display.DeviceName, out var target))
                continue;
            var ramp = LerpRamp(from, target, amount);
            var result = SetRamp([display], ramp);
            applied += result.AppliedCount;
            failed += result.FailedCount;
            if (!string.IsNullOrWhiteSpace(result.LastError)) lastError = result.LastError;
            touched.AddRange(result.TouchedDisplays);
            if (result.AppliedCount > 0) _currentRamps[display.DeviceName] = ramp;
        }
        return new RampApplyResult(applied, failed, lastError, touched);
    }

    private void StopTransition()
    {
        _transitionTimer?.Dispose();
        _transitionTimer = null;
        _transitionFromRamps.Clear();
        _transitionToRamps.Clear();
        _transitionStep = 0;
        _transitionSteps = 0;
    }

    private static GammaRamp CloneRamp(GammaRamp source) => new()
    {
        Red = (ushort[])source.Red.Clone(),
        Green = (ushort[])source.Green.Clone(),
        Blue = (ushort[])source.Blue.Clone()
    };

    private static GammaRamp LerpRamp(GammaRamp from, GammaRamp to, double amount)
    {
        var result = CreateEmptyRamp();
        for (var index = 0; index < 256; index++)
        {
            result.Red[index] = LerpChannel(from.Red[index], to.Red[index], amount);
            result.Green[index] = LerpChannel(from.Green[index], to.Green[index], amount);
            result.Blue[index] = LerpChannel(from.Blue[index], to.Blue[index], amount);
        }
        return result;
    }

    private static ushort LerpChannel(ushort from, ushort to, double amount) =>
        (ushort)Math.Clamp(Math.Round(from + (to - from) * amount), ushort.MinValue, ushort.MaxValue);

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

        if (string.IsNullOrWhiteSpace(lastError) && appliedCount == 0) lastError = "未找到可用屏幕";
        return new RampApplyResult(appliedCount, failedCount, lastError, touchedDisplays);
    }

    private IReadOnlyList<ScreenDisplayInfo> ResolveDisplays(string displayName)
    {
        var displays = GetDisplays();
        var normalized = displayName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized)) return displays;
        return displays.Where(display => string.Equals(display.DeviceName, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // Recovered from Gamma Panel 1.0 (gapa.exe, fcn.0043527c). Parameter
    // conversion is also identical: integer Gamma/Contrast values are first
    // converted to 32-bit floats by multiplying by 0.01.
    private static GammaRamp BuildRamp(ScreenFilterPreset preset)
    {
        var normalized = preset.Normalize();
        var ramp = CreateEmptyRamp();

        for (var i = 0; i < 256; i++)
        {
            ramp.Red[i] = BuildGammaPanelValue(i, normalized.Red);
            ramp.Green[i] = BuildGammaPanelValue(i, normalized.Green);
            ramp.Blue[i] = BuildGammaPanelValue(i, normalized.Blue);
        }

        return ramp;
    }

    internal static ushort[] BuildGammaPanelChannelForTest(ScreenFilterChannel channel)
    {
        var result = new ushort[256];
        var normalized = channel.Normalize();
        for (var i = 0; i < result.Length; i++) result[i] = BuildGammaPanelValue(i, normalized);
        return result;
    }

    internal static (ushort[] Red, ushort[] Green, ushort[] Blue) BuildGammaPanelRampForTest(ScreenFilterPreset preset)
    {
        var ramp = BuildRamp(preset);
        return (ramp.Red, ramp.Green, ramp.Blue);
    }

    internal static bool IsValidCurveForTest(
        IReadOnlyList<ushort>? red,
        IReadOnlyList<ushort>? green,
        IReadOnlyList<ushort>? blue) =>
        red?.Count == 256 && green?.Count == 256 && blue?.Count == 256;

    internal static ushort InterpolateChannelForTest(ushort from, ushort to, double amount) =>
        LerpChannel(from, to, Math.Clamp(amount, 0.0, 1.0));

    private static GammaRamp CreateEmptyRamp() => new()
    {
        Red = new ushort[256],
        Green = new ushort[256],
        Blue = new ushort[256]
    };

    private static ushort BuildGammaPanelValue(int input, ScreenFilterChannel channel)
    {
        var gamma = (float)(channel.Gamma * 0.01);
        var contrast = (float)(channel.Contrast * 0.01);
        var value = Math.Pow((input + 1) * 0.00390625, 1.0 / gamma);
        value = ((value * 65536.0) - 32768.0 - 1.0 + (channel.Brightness * 512.0)) * contrast + 32768.0;
        value = Math.Clamp(value, 0.0, 65535.0);

        // Delphi's FISTP uses the default x87 round-to-nearest-even mode. The
        // original then clears the low byte, yielding an 8-bit DAC ramp.
        var rounded = (int)Math.Round(value, MidpointRounding.ToEven);
        return (ushort)(rounded & 0xFF00);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDCW(string driver, string device, string? output, IntPtr initData);

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

    private sealed record RampApplyResult(int AppliedCount, int FailedCount, string LastError, IReadOnlyList<string> TouchedDisplays);

    private sealed class GammaRecoverySnapshot
    {
        public int OwnerProcessId { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public GammaRecoveryDisplay[] Displays { get; set; } = [];
    }

    private sealed class GammaRecoveryDisplay
    {
        public string DeviceName { get; set; } = "";
        public ushort[] Red { get; set; } = [];
        public ushort[] Green { get; set; } = [];
        public ushort[] Blue { get; set; } = [];
    }
}
