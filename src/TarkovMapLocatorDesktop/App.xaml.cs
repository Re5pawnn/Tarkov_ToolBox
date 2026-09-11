using System.Windows;
using System.Threading;
using System.IO;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocatorDesktop.Services;

namespace TarkovMapLocatorDesktop;

public partial class App : Application
{
    private static readonly string[] SingleInstanceMutexNames =
    [
        @"Local\TarkovMapLocatorDesktop",
        @"Local\TarkovMapLocatorDesktop.AutoOcrTest"
    ];
    private readonly List<Mutex> _ownedSingleInstanceMutexes = [];

    public App()
    {
        StartupDiagnosticService.Begin();
        DispatcherUnhandledException += (_, args) =>
        {
            StartupDiagnosticService.Fail("WPF 界面线程未处理异常", args.Exception);
            RuntimeLogService.Error("未处理异常", "WPF 界面线程发生未处理异常", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "未知异常");
            StartupDiagnosticService.Fail(args.IsTerminating ? "进程终止异常" : "后台线程未处理异常", exception);
            RuntimeLogService.Error("未处理异常", args.IsTerminating ? "进程即将因未处理异常终止" : "后台线程发生未处理异常", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            StartupDiagnosticService.Fail("未观察的异步任务异常", args.Exception);
            RuntimeLogService.Error("未处理异常", "发现未观察的异步任务异常", args.Exception);
            args.SetObserved();
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            StartupDiagnosticService.Mark("进入 WPF 启动流程");
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var environment = StartupEnvironmentCheckService.CheckAppLocalVcRuntime();
            StartupDiagnosticService.Mark("原生运行库检查", environment.ToLogText());
            if (!environment.IsReady)
                RuntimeLogService.Warning("启动", "原生运行库不完整，OCR 功能可能不可用", environment.ToLogText());

            StartupDiagnosticService.Mark("迁移本机配置");
            var migration = ApplicationDataMigrationService.MigratePromotedProfile();
            if (migration.CopiedFileCount > 0)
                RuntimeLogService.Info("配置迁移", "已合并自动 OCR 测试版的本机数据", $"迁移文件: {migration.CopiedFileCount}");
            else if (!string.IsNullOrWhiteSpace(migration.ErrorMessage))
                RuntimeLogService.Warning("配置迁移", "自动 OCR 测试版数据迁移失败，继续使用现有正式配置", migration.ErrorMessage);

            StartupDiagnosticService.Mark("加载可选组件启动钩子");
            var featureModules = FeatureModuleCatalog.Discover(Path.Combine(AppContext.BaseDirectory, "Modules"));
            var featureContext = CreateFeatureApplicationContext();
            foreach (var module in featureModules.Modules
                         .OrderBy(item => item.Instance.Descriptor.Order))
            {
                if (module.Instance is not IFeatureApplicationLifecycle lifecycle) continue;
                try
                {
                    if (lifecycle.TryHandleStartup(e.Args, featureContext)) return;
                    lifecycle.OnApplicationStarting(featureContext);
                }
                catch (Exception exception)
                {
                    var descriptor = module.Instance.Descriptor;
                    StartupDiagnosticService.Mark(
                        $"可选组件启动钩子失败：{descriptor.DisplayName}",
                        exception.GetBaseException().Message);
                    RuntimeLogService.Error(
                        "组件",
                        $"可选组件“{descriptor.DisplayName}”启动钩子失败，核心程序继续启动",
                        exception,
                        $"组件 ID: {descriptor.Id}\n程序集: {module.AssemblyPath}");
                }
            }

            if (e.Args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                StartupDiagnosticService.Mark("执行发布后自检");
                var exitCode = await ApplicationSelfTest.RunAsync();
                StartupDiagnosticService.Mark(exitCode == 0 ? "自检完成" : "自检失败", $"退出码 {exitCode}");
                Shutdown(exitCode);
                return;
            }

            StartupDiagnosticService.Mark("建立单实例保护");
            if (!TryAcquireSingleInstance(out var alreadyRunning))
            {
                var message = alreadyRunning
                    ? "塔科夫工具箱已在运行。请使用已打开的窗口。"
                    : "无法建立单实例保护，程序未启动。详细原因已写入运行日志。";
                RuntimeLogService.Warning("应用", alreadyRunning ? "阻止重复启动" : "无法建立单实例保护", message);
                MessageBox.Show(message, "塔科夫工具箱", MessageBoxButton.OK, MessageBoxImage.Information);
                StartupDiagnosticService.Mark("启动已终止", message);
                Shutdown(alreadyRunning ? 0 : 1);
                return;
            }

            StartupDiagnosticService.Mark("导入首次启动数据");
            PackagedBootstrapDataService.ApplyFirstStartSeed();
            StartupDiagnosticService.Mark("创建主窗口");
            MainWindow = new MainWindow(featureModules);
            MainWindow.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            StartupDiagnosticService.Mark("主窗口已显示");
        }
        catch (Exception exception)
        {
            StartupDiagnosticService.Fail("应用启动", exception);
            RuntimeLogService.Error("应用", "主窗口启动失败", exception);
            MessageBox.Show(
                $"程序启动失败。\n\n{exception.GetBaseException().Message}\n\n启动诊断：{StartupDiagnosticService.LogFilePath}\n运行日志：{RuntimeLogService.CurrentLogFilePath}",
                "塔科夫工具箱",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private FeatureApplicationContext CreateFeatureApplicationContext() => new(
        TarkovMapLocatorDesktop.Services.ApplicationIdentity.ApplicationDataDirectory,
        WriteFeatureStartupLog,
        operation =>
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(operation).ContinueWith(task =>
                Dispatcher.Invoke(() => Shutdown(task.Status == TaskStatus.RanToCompletion ? task.Result : 1)));
        },
        exitCode => Shutdown(exitCode));

    private static void WriteFeatureStartupLog(
        FeatureLogLevel level,
        string category,
        string message,
        string? details)
    {
        switch (level)
        {
            case FeatureLogLevel.Trace:
                RuntimeLogService.Trace(category, message, details);
                break;
            case FeatureLogLevel.Info:
                RuntimeLogService.Info(category, message, details);
                break;
            case FeatureLogLevel.Warning:
                RuntimeLogService.Warning(category, message, details);
                break;
            case FeatureLogLevel.Error:
                RuntimeLogService.Error(category, message, new InvalidOperationException(details ?? message));
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { RuntimeLogService.ShutdownAsync().GetAwaiter().GetResult(); }
        catch { }
        finally
        {
            ReleaseSingleInstanceMutex();
            base.OnExit(e);
        }
    }

    private bool TryAcquireSingleInstance(out bool alreadyRunning)
    {
        alreadyRunning = false;
        try
        {
            foreach (var mutexName in SingleInstanceMutexNames)
            {
                var mutex = new Mutex(false, mutexName);
                var ownsMutex = false;
                try
                {
                    ownsMutex = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    alreadyRunning = true;
                    mutex.Dispose();
                    ReleaseSingleInstanceMutex();
                    return false;
                }

                _ownedSingleInstanceMutexes.Add(mutex);
            }
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            RuntimeLogService.Error("应用", "建立单实例保护失败", exception);
            ReleaseSingleInstanceMutex();
            return false;
        }
    }

    private void ReleaseSingleInstanceMutex()
    {
        for (var index = _ownedSingleInstanceMutexes.Count - 1; index >= 0; index--)
        {
            var mutex = _ownedSingleInstanceMutexes[index];
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process no longer owns the mutex; disposal still releases its handle.
            }
            finally
            {
                mutex.Dispose();
            }
        }
        _ownedSingleInstanceMutexes.Clear();
    }
}
