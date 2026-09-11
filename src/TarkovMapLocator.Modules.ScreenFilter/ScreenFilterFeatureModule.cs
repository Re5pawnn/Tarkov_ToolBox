using System.Windows;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.ScreenFilter.Models;
using TarkovMapLocator.Modules.ScreenFilter.Services;

namespace TarkovMapLocator.Modules.ScreenFilter;

public sealed class ScreenFilterFeatureModule :
    IFeatureModule,
    IFeatureModuleSelfTest,
    IFeatureApplicationLifecycle
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.ScreenFilter,
        "屏幕调色",
        "调色",
        80);

    public FrameworkElement CreateView(IFeatureHost host, string route)
    {
        ScreenFilterModuleContext.Initialize(host);
        return new ScreenFilterView(host);
    }

    public bool TryHandleStartup(IReadOnlyList<string> arguments, FeatureApplicationContext context)
    {
        ScreenFilterModuleContext.Initialize(context);
        if (arguments.Count >= 3 &&
            string.Equals(arguments[0], "--gamma-watchdog", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(arguments[1], out var ownerProcessId))
        {
            context.RunBackgroundAndShutdown(() => Task.FromResult(
                ScreenGammaService.RunRecoveryWatchdog(ownerProcessId, arguments[2])));
            return true;
        }

        if (arguments.Any(argument => string.Equals(argument, "--restore-gamma", StringComparison.OrdinalIgnoreCase)))
        {
            ScreenGammaService.RestoreAllPersistedSessions();
            context.Shutdown(0);
            return true;
        }

        return false;
    }

    public void OnApplicationStarting(FeatureApplicationContext context)
    {
        ScreenFilterModuleContext.Initialize(context);
        var restoredSessions = ScreenGammaService.RestoreAbandonedSessions();
        if (restoredSessions > 0)
            context.WriteLog(
                FeatureLogLevel.Info,
                "屏幕滤镜",
                "已恢复上次异常退出遗留的屏幕颜色",
                $"恢复会话: {restoredSessions}");
    }

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.ScreenFilter, StringComparison.Ordinal))
            throw new InvalidOperationException("调色组件路由异常。");
        if (ScreenFilterRequest.Default.Normalize().Mode != ScreenFilterMode.GammaPanel)
            throw new InvalidOperationException("调色组件默认配置异常。");
        var ramp = ScreenGammaService.BuildGammaPanelChannelForTest(ScreenFilterChannel.Default);
        if (ramp.Length != 256 || ramp[0] != 0)
            throw new InvalidOperationException("调色组件 Gamma 曲线异常。");
    }
}
