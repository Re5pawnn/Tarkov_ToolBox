#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using TarkovMapLocator.Modules.ScreenFilter.Services;

namespace TarkovAutoShade
{
    [DataContract]
    internal sealed class AppSettings
    {
        [DataMember] public int AlgorithmVersion { get; set; }
        [DataMember] public string ScreenshotFolder { get; set; }
        [DataMember] public bool AutoWatch { get; set; }
        [DataMember] public int ShadowTarget { get; set; }
        [DataMember] public int HighlightProtection { get; set; }
        [DataMember] public int ExposureBias { get; set; }
        [DataMember] public int ContrastBias { get; set; }
        [DataMember] public int MaxStrength { get; set; }
        [DataMember] public int Warmth { get; set; }
        [DataMember] public int ColorCorrection { get; set; }
        [DataMember] public int IndoorComfort { get; set; }
        [DataMember] public int SceneGuard { get; set; }
        [DataMember] public int BlackPoint { get; set; }
        [DataMember] public int SaturationBias { get; set; }
        [DataMember] public int HotkeyKeyCode { get; set; }
        [DataMember] public int HotkeyModifiers { get; set; }
        [DataMember] public int AnalysisHotkeyKeyCode { get; set; }
        [DataMember] public int AnalysisHotkeyModifiers { get; set; }
        [DataMember] public int PresetIndex { get; set; }
        [DataMember] public bool CustomPresetInitialized { get; set; }
        [DataMember] public int CustomShadowTarget { get; set; }
        [DataMember] public int CustomHighlightProtection { get; set; }
        [DataMember] public int CustomExposureBias { get; set; }
        [DataMember] public int CustomContrastBias { get; set; }
        [DataMember] public int CustomMaxStrength { get; set; }
        [DataMember] public int CustomWarmth { get; set; }
        [DataMember] public int CustomColorCorrection { get; set; }
        [DataMember] public int CustomIndoorComfort { get; set; }
        [DataMember] public int CustomSceneGuard { get; set; }
        [DataMember] public int CustomBlackPoint { get; set; }
        [DataMember] public int CustomSaturationBias { get; set; }
        [DataMember] public string DisplayDevice { get; set; }
        [DataMember] public bool MultiDisplayMode { get; set; }
        [DataMember] public List<string> SelectedDisplayDevices { get; set; }
        [DataMember] public bool SmoothTransition { get; set; }
        [DataMember] public bool ProcessWatchEnabled { get; set; }
        [DataMember] public bool ProcessWatchConfigured { get; set; }
        [DataMember] public string WatchedProcessName { get; set; }
        // 0 = ask, 1 = hide to tray, 2 = exit directly.
        [DataMember] public int CloseBehavior { get; set; }

        public static AppSettings CreateDefault()
        {
            return new AppSettings {
                AlgorithmVersion = 11,
                ScreenshotFolder = GetDefaultScreenshotFolder(),
                AutoWatch = false,
                ShadowTarget = 70,
                HighlightProtection = 76,
                ExposureBias = 0,
                ContrastBias = 0,
                MaxStrength = 82,
                Warmth = 0,
                ColorCorrection = 72,
                IndoorComfort = 72,
                SceneGuard = 88,
                BlackPoint = 56,
                SaturationBias = 0,
                HotkeyKeyCode = 0,
                HotkeyModifiers = 0,
                AnalysisHotkeyKeyCode = 0,
                AnalysisHotkeyModifiers = 0,
                PresetIndex = 0,
                CustomPresetInitialized = true,
                CustomShadowTarget = 70,
                CustomHighlightProtection = 76,
                CustomExposureBias = 0,
                CustomContrastBias = 0,
                CustomMaxStrength = 82,
                CustomWarmth = 0,
                CustomColorCorrection = 72,
                CustomIndoorComfort = 72,
                CustomSceneGuard = 88,
                CustomBlackPoint = 56,
                CustomSaturationBias = 0,
                DisplayDevice = "",
                MultiDisplayMode = false,
                SelectedDisplayDevices = new List<string>(),
                SmoothTransition = true,
                ProcessWatchEnabled = false,
                ProcessWatchConfigured = true,
                WatchedProcessName = "EscapeFromTarkov.exe"
            };
        }

        public static string GetDefaultScreenshotFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "Escape from Tarkov", "Screenshots");
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(ScreenshotFolder))
                ScreenshotFolder = GetDefaultScreenshotFolder();

            // Version 1 intentionally shipped with conservative defaults.
            // Migrate once so existing users receive the recalibrated curve.
            if (AlgorithmVersion < 2)
            {
                ShadowTarget = Math.Max(ShadowTarget, 70);
                HighlightProtection = Math.Max(HighlightProtection, 76);
                MaxStrength = Math.Max(MaxStrength, 82);
                AlgorithmVersion = 2;
            }

            if (AlgorithmVersion < 3)
            {
                ExposureBias = 0;
                ContrastBias = 0;

                // Recover the common "everything at zero" state, which makes
                // every generated curve identical to the unfiltered desktop.
                if (ShadowTarget == 0 &&
                    HighlightProtection == 0 &&
                    MaxStrength == 0)
                {
                    ShadowTarget = 70;
                    HighlightProtection = 76;
                    MaxStrength = 82;
                    Warmth = 3;
                }
                AlgorithmVersion = 3;
            }

            if (AlgorithmVersion < 4)
            {
                ColorCorrection = 72;
                IndoorComfort = 72;
                SceneGuard = 88;
                AlgorithmVersion = 4;
            }

            if (AlgorithmVersion < 5)
            {
                // Version 3 used +3 as a recovery default. It is not a
                // deliberate user color preference, so return that legacy
                // value to the new neutral baseline.
                if (Warmth == 3) Warmth = 0;
                BlackPoint = 56;
                AlgorithmVersion = 5;
            }

            if (AlgorithmVersion < 6)
            {
                SaturationBias = 0;
                HotkeyKeyCode = 119;
                HotkeyModifiers = 0;
                AlgorithmVersion = 6;
            }

            if (AlgorithmVersion < 7 || !CustomPresetInitialized)
            {
                CustomShadowTarget = ShadowTarget;
                CustomHighlightProtection = HighlightProtection;
                CustomExposureBias = ExposureBias;
                CustomContrastBias = ContrastBias;
                CustomMaxStrength = MaxStrength;
                CustomWarmth = Warmth;
                CustomColorCorrection = ColorCorrection;
                CustomIndoorComfort = IndoorComfort;
                CustomSceneGuard = SceneGuard;
                CustomBlackPoint = BlackPoint;
                CustomSaturationBias = SaturationBias;
                CustomPresetInitialized = true;
                AlgorithmVersion = 7;
            }

            if (AlgorithmVersion < 8)
                AlgorithmVersion = 8;

            if (AlgorithmVersion < 9)
            {
                SmoothTransition = true;
                AlgorithmVersion = 9;
            }

            // The host application uses these two fields for the in-game
            // screenshot shortcut. Automatic directory-wide analysis was
            // replaced by one-shot capture in version 10.
            if (AlgorithmVersion < 10)
            {
                HotkeyKeyCode = 0;
                HotkeyModifiers = 0;
                AutoWatch = false;
                AlgorithmVersion = 10;
            }

            if (AlgorithmVersion < 11)
            {
                AnalysisHotkeyKeyCode = 0;
                AnalysisHotkeyModifiers = 0;
                AlgorithmVersion = 11;
            }

            if (SelectedDisplayDevices == null)
                SelectedDisplayDevices = new List<string>();

            ShadowTarget = MathUtil.Clamp(ShadowTarget, 0, 100);
            HighlightProtection = MathUtil.Clamp(HighlightProtection, 0, 100);
            ExposureBias = MathUtil.Clamp(ExposureBias, -20, 20);
            ContrastBias = MathUtil.Clamp(ContrastBias, -20, 20);
            MaxStrength = MathUtil.Clamp(MaxStrength, 0, 100);
            Warmth = MathUtil.Clamp(Warmth, -20, 20);
            ColorCorrection = MathUtil.Clamp(ColorCorrection, 0, 100);
            IndoorComfort = MathUtil.Clamp(IndoorComfort, 0, 100);
            SceneGuard = MathUtil.Clamp(SceneGuard, 0, 100);
            BlackPoint = MathUtil.Clamp(BlackPoint, 0, 100);
            SaturationBias = MathUtil.Clamp(SaturationBias, -20, 20);
            if (HotkeyKeyCode < 0 || HotkeyKeyCode > 255) HotkeyKeyCode = 0;
            HotkeyModifiers = HotkeyKeyCode == 0 ? 0 : HotkeyModifiers & 0x000F;
            if (AnalysisHotkeyKeyCode < 0 || AnalysisHotkeyKeyCode > 255) AnalysisHotkeyKeyCode = 0;
            AnalysisHotkeyModifiers = AnalysisHotkeyKeyCode == 0 ? 0 : AnalysisHotkeyModifiers & 0x000F;
            PresetIndex = MathUtil.Clamp(PresetIndex, 0, 5);
            CustomShadowTarget = MathUtil.Clamp(CustomShadowTarget, 0, 100);
            CustomHighlightProtection = MathUtil.Clamp(CustomHighlightProtection, 0, 100);
            CustomExposureBias = MathUtil.Clamp(CustomExposureBias, -20, 20);
            CustomContrastBias = MathUtil.Clamp(CustomContrastBias, -20, 20);
            CustomMaxStrength = MathUtil.Clamp(CustomMaxStrength, 0, 100);
            CustomWarmth = MathUtil.Clamp(CustomWarmth, -20, 20);
            CustomColorCorrection = MathUtil.Clamp(CustomColorCorrection, 0, 100);
            CustomIndoorComfort = MathUtil.Clamp(CustomIndoorComfort, 0, 100);
            CustomSceneGuard = MathUtil.Clamp(CustomSceneGuard, 0, 100);
            CustomBlackPoint = MathUtil.Clamp(CustomBlackPoint, 0, 100);
            CustomSaturationBias = MathUtil.Clamp(CustomSaturationBias, -20, 20);
            if (DisplayDevice == null) DisplayDevice = "";
            var normalizedDisplays = new List<string>();
            foreach (string device in SelectedDisplayDevices)
            {
                if (!string.IsNullOrWhiteSpace(device) &&
                    !normalizedDisplays.Contains(device))
                    normalizedDisplays.Add(device);
            }
            SelectedDisplayDevices = normalizedDisplays;
            if (string.IsNullOrWhiteSpace(WatchedProcessName))
                WatchedProcessName = "EscapeFromTarkov.exe";
            if (!ProcessWatchConfigured)
            {
                ProcessWatchEnabled = false;
                ProcessWatchConfigured = true;
            }
            CloseBehavior = MathUtil.Clamp(CloseBehavior, 0, 2);
        }
    }

    internal static class SettingsStore
    {
        private static readonly object SaveQueueGate = new object();
        private static readonly string Folder = Path.Combine(
            TarkovMapLocator.Modules.ScreenFilter.Services.ApplicationIdentity.ApplicationDataDirectory,
            "auto-shade");
        private static readonly string FilePath = Path.Combine(Folder, "settings.json");
        private static Task<bool> queuedSave = Task.FromResult(true);

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return AppSettings.CreateDefault();
                using (var stream = File.OpenRead(FilePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    var settings = serializer.ReadObject(stream) as AppSettings;
                    if (settings == null) return AppSettings.CreateDefault();
                    settings.Normalize();
                    return settings;
                }
            }
            catch (Exception exception)
            {
                string quarantinedPath = TryQuarantineInvalidSettings();
                RuntimeLogService.Warning(
                    "自动滤镜",
                    "读取自动滤镜配置失败，已使用默认值",
                    $"文件: {FilePath}\n" +
                    (string.IsNullOrWhiteSpace(quarantinedPath)
                        ? "原配置未能备份，请检查文件权限。\n"
                        : $"原配置已备份到: {quarantinedPath}\n") +
                    exception);
                return AppSettings.CreateDefault();
            }
        }

        private static string TryQuarantineInvalidSettings()
        {
            try
            {
                if (!File.Exists(FilePath)) return "";
                string quarantinePath = Path.Combine(
                    Folder,
                    "settings.invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N") + ".json");
                File.Move(FilePath, quarantinePath);
                return quarantinePath;
            }
            catch
            {
                return "";
            }
        }

        public static bool Save(AppSettings settings)
        {
            return QueueSaveAsync(settings).GetAwaiter().GetResult();
        }

        public static Task<bool> QueueSaveAsync(AppSettings settings)
        {
            byte[] payload;
            try
            {
                settings.Normalize();
                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    serializer.WriteObject(stream, settings);
                    payload = stream.ToArray();
                }
            }
            catch (Exception exception)
            {
                RuntimeLogService.Warning(
                    "自动滤镜",
                    "序列化自动滤镜配置失败，本次调整仅在当前会话生效",
                    exception.ToString());
                return Task.FromResult(false);
            }

            lock (SaveQueueGate)
            {
                queuedSave = queuedSave.ContinueWith(
                    _ => WritePayload(payload),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                return queuedSave;
            }
        }

        public static Task<bool> FlushAsync()
        {
            lock (SaveQueueGate) return queuedSave;
        }

        private static bool WritePayload(byte[] payload)
        {
            string temporary = null;
            try
            {
                Directory.CreateDirectory(Folder);
                temporary = FilePath + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, payload);
                File.Move(temporary, FilePath, true);
                return true;
            }
            catch (Exception exception)
            {
                RuntimeLogService.Warning(
                    "自动滤镜",
                    "保存自动滤镜配置失败，本次调整仅在当前会话生效",
                    $"文件: {FilePath}\n{exception}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporary))
                {
                    try
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                    catch (Exception exception)
                    {
                        RuntimeLogService.Warning(
                            "自动滤镜",
                            "清理自动滤镜配置临时文件失败",
                            $"文件: {temporary}\n{exception.Message}");
                    }
                }
            }
        }
    }

    internal sealed class AnalysisResult
    {
        public string FilePath;
        public DateTime CapturedAt;
        public int SourceWidth;
        public int SourceHeight;
        public double P01;
        public double P05;
        public double P10;
        public double P25;
        public double Median;
        public double P75;
        public double P90;
        public double P95;
        public double P99;
        public double DynamicRange;
        public double EdgeEnergy;
        public double MeanRed;
        public double MeanGreen;
        public double MeanBlue;
        public double NightVisionScore;
        public double RedCast;
        public double GreenCast;
        public double BlueCast;
        public double UpperMean;
        public double LowerMean;
        public double BrightFraction;
        public bool IsUsable;
        public string SkipReason;
        public string SceneLabel;
        public int[] Histogram;
        public FilterRecommendation Recommendation;
    }

    internal sealed class FilterRecommendation
    {
        public string ProfileName;
        public double EquivalentGamma;
        public double BrightnessBoost;
        public double ContrastBoost;
        public double StrengthBlend;
        public double Gamma;
        public double ShadowLift;
        public double HighlightCompression;
        public double BlackPointRecovery;
        public double Contrast;
        public double Warmth;
        public double RedBalance;
        public double GreenBalance;
        public double BlueBalance;
        public double ChangeStrength;
        public ushort[] Red;
        public ushort[] Green;
        public ushort[] Blue;
    }

    internal sealed class DisplayTarget
    {
        public string DeviceName;
        public string FriendlyName;
        public bool Primary;

        public override string ToString()
        {
            return FriendlyName + (Primary ? "（主显示器）" : "");
        }
    }

    internal static class MathUtil
    {
        public static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public static double Lerp(double from, double to, double amount)
        {
            return from + (to - from) * amount;
        }

        public static double SmoothStep(double start, double end, double value)
        {
            if (end <= start) return value >= end ? 1.0 : 0.0;
            double t = Clamp((value - start) / (end - start), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
