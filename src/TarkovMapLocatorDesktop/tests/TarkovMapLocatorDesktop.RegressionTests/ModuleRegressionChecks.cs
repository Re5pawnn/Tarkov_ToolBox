using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.Market;
using TarkovMapLocator.Modules.MobileMap;
using TarkovMapLocator.Modules.MobileMap.Services;
using TarkovMapLocator.Modules.TeamSync.Models;
using TarkovMapLocator.Modules.TeamSync.Services;
using TarkovMapLocator.Modules.Utilities.HideoutProfit;
using TarkovMapLocator.Modules.Utilities.StoryGuide;
using TarkovMapLocatorDesktop.Services;

namespace TarkovMapLocatorDesktop.RegressionTests;

internal static partial class Program
{
    private static async Task VerifyMobileMapConnectionLimitAsync()
    {
        await using var server = new MobileMapServer();
        var port = GetFreeTcpPort();
        server.Start(port);
        var clients = new List<TcpClient>();
        try
        {
            // Hold every processing slot with an authorized event stream.
            for (var index = 0; index < 12; index++)
                clients.Add(await OpenMobileMapEventsAsync(server));
            Assert(server.ConnectedClients == 12, "The mobile-map event streams did not fill the connection limit.");

            for (var index = 0; index < 18; index++)
            {
                using var excess = new TcpClient();
                await excess.ConnectAsync(IPAddress.Loopback, port);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var received = await excess.GetStream().ReadAsync(new byte[1], timeout.Token);
                    Assert(received == 0, "An excess mobile-map connection was not rejected.");
                }
                catch (IOException) { /* A reset is also an immediate rejection. */ }
                catch (SocketException) { }
            }

            await server.StopAsync();
            foreach (var client in clients)
                await VerifyClientClosedAsync(client);
            Assert(server.ConnectedClients == 0, "Stopping the mobile map retained active clients.");

            // A stopped server must release its slots as well as its listening port.
            server.Start(port);
            using var restartedClient = await OpenMobileMapEventsAsync(server);
            await server.StopAsync();
            await VerifyClientClosedAsync(restartedClient);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    private static async Task<TcpClient> OpenMobileMapEventsAsync(MobileMapServer server)
    {
        var client = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            var stream = client.GetStream();
            var request = $"GET /api/events HTTP/1.1\r\nHost: localhost\r\nCookie: tarkov_mobile_token={server.Token}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            Assert(await reader.ReadLineAsync(timeout.Token) == "HTTP/1.1 200 OK", "The mobile-map stream was not accepted.");
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
                if (line == "event: snapshot") return client;
            throw new InvalidOperationException("The mobile-map stream closed before its first snapshot.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task VerifyClientClosedAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await client.GetStream().CopyToAsync(Stream.Null, timeout.Token); }
        catch (IOException) { /* Reset and EOF both mean the server closed the socket. */ }
    }

    private static async Task VerifyTeamSyncAuthenticationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TarkovMapLocator-team-sync-{Guid.NewGuid():N}");
        var hostProxy = DispatchProxy.Create<IFeatureHost, IndependentFeatureHost>();
        var clientProxy = DispatchProxy.Create<IFeatureHost, IndependentFeatureHost>();
        var rejectedProxy = DispatchProxy.Create<IFeatureHost, IndependentFeatureHost>();
        ((IndependentFeatureHost)(object)hostProxy).DataDirectory = Path.Combine(root, "host");
        ((IndependentFeatureHost)(object)clientProxy).DataDirectory = Path.Combine(root, "client");
        ((IndependentFeatureHost)(object)rejectedProxy).DataDirectory = Path.Combine(root, "rejected");
        await using var host = new LanPeerSyncService(hostProxy);
        await using var client = new LanPeerSyncService(clientProxy);
        await using var rejected = new LanPeerSyncService(rejectedProxy);
        MobileMapServer? server = null;
        try
        {
            var key = "regression-room-key";
            var port = GetFreeTcpPort();
            var hostConfig = LanSyncConfig.Default with { DisplayName = "Host", RoomKey = key, SyncPort = port };
            await host.StartHostAsync(hostConfig);
            server = new MobileMapServer(teamSyncHandler: host.HandleHostRequest);
            server.Start(port);
            var endpoint = $"http://127.0.0.1:{port}/?token={server.Token}";
            Assert(host.IsHosting && server.IsRunning && server.Port == hostConfig.SyncPort,
                "Team sync and the mobile map did not share one listener port.");
            await host.PublishAsync(hostConfig, new FeatureSharedLocation("customs", "海关", 10, 1, 20, 30));

            var clientConfig = LanSyncConfig.Default with
            {
                Mode = "join", DisplayName = "Client", RoomKey = key, RemoteEndpoint = endpoint, SyncPort = port
            };
            await client.StartJoinAsync(clientConfig);
            await client.PublishAsync(clientConfig, new FeatureSharedLocation("customs", "海关", 11, 1, 21, 31));
            Assert(client.BuildPeerPositions("customs").Any(peer => peer.DisplayName == "Host"),
                "A client with the room key could not receive the authenticated snapshot.");

            var rejectedConfig = clientConfig with { DisplayName = "Rejected", RoomKey = "incorrect-room-key" };
            await rejected.StartJoinAsync(rejectedConfig);
            await rejected.PublishAsync(rejectedConfig, new FeatureSharedLocation("customs", "海关", 99, 1, 99, 0));
            Assert(rejected.BuildUiState("customs").LastError.Contains("密钥", StringComparison.Ordinal) &&
                   host.BuildPeerPositions("customs").All(peer => peer.DisplayName != "Rejected"),
                "Team sync accepted or disclosed its snapshot to a client with the wrong room key.");
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyModuleShutdownAndStoryModes()
    {
        Exception? failure = null;
        var uiThread = new Thread(() =>
        {
            try
            {
                // Load WPF resources without running application startup, migrations or hotkeys.
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var frame = new DispatcherFrame();
                var verification = VerifyModuleViewsAsync();
                _ = verification.ContinueWith(
                    _ => app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                verification.GetAwaiter().GetResult();
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        Assert(uiThread.Join(TimeSpan.FromSeconds(20)), "Module shutdown blocked the WPF dispatcher.");
        if (failure is not null) throw new InvalidOperationException("Module UI regression failed.", failure);
    }

    private static async Task VerifyModuleViewsAsync()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"TarkovMapLocator-module-regression-{Guid.NewGuid():N}");
        var host = DispatchProxy.Create<IFeatureHost, IndependentFeatureHost>();
        ((IndependentFeatureHost)(object)host).DataDirectory = profile;
        try
        {
            VerifyIndependentStoryMode(host);

            var marketView = new MarketView(host);
            var progressBar = (ProgressBar)marketView.FindName("MarketRefreshProgressBar");
            var progressHost = (FrameworkElement)marketView.FindName("MarketRefreshProgressHost");
            var setRefreshing = typeof(MarketView).GetMethod("SetRefreshing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setRefreshing.Invoke(marketView, [true]);
            Assert(progressHost is not null && progressBar.IsIndeterminate &&
                   !((Button)marketView.FindName("MarketRefreshButton")).IsEnabled,
                "Market refresh progress was not configured or duplicate refreshes remained enabled.");
            setRefreshing.Invoke(marketView, [false]);
            Assert(((Button)marketView.FindName("MarketRefreshButton")).IsEnabled,
                "Market refresh button remained disabled after completion.");

            using var hideoutCancellation = new CancellationTokenSource();
            hideoutCancellation.Cancel();
            var hideoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hideoutHost = DispatchProxy.Create<IFeatureHost, IndependentFeatureHost>();
            var hideoutHostState = (IndependentFeatureHost)(object)hideoutHost;
            hideoutHostState.DataDirectory = profile;
            hideoutHostState.ShutdownToken = hideoutCancellation.Token;
            hideoutHostState.MarketFreshTask = hideoutGate.Task;
            var hideoutView = new HideoutProfitView(hideoutHost, () => { });
            var hideoutProgress = (ProgressBar)hideoutView.FindName("HideoutRefreshProgressBar");
            var hideoutProgressHost = (FrameworkElement)hideoutView.FindName("HideoutRefreshProgressHost");
            Assert(hideoutProgressHost is not null && hideoutProgress.IsIndeterminate &&
                   !((Button)hideoutView.FindName("RefreshButton")).IsEnabled,
                "Hideout profit refresh progress was not configured or duplicate refreshes remained enabled.");
            hideoutGate.SetResult();
            await Task.Delay(50);
            Assert(((Button)hideoutView.FindName("RefreshButton")).IsEnabled,
                "Hideout profit refresh button remained disabled after completion.");

            var window = new MainWindow(FeatureModuleCatalog.Empty);
            var activePageField = typeof(MainWindow).GetField("_activePage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            activePageField.SetValue(window, Enum.Parse(activePageField.FieldType, "Settings"));
            typeof(MainWindow).GetField("_requiresInitialPathSetup", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            typeof(MainWindow).GetMethod("CtrlTapService_CtrlTapped", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var airdropStage = typeof(MainWindow).GetField("_airdropLocatorStage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Assert(airdropStage?.ToString() == "Inactive" && activePageField.GetValue(window)?.ToString() == "Settings",
                "A Ctrl tap outside the map page activated airdrop localization.");
            var mobileView = new MobileMapView(host);
            var server = (MobileMapServer)typeof(MobileMapView)
                .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mobileView)!;
            server.Start(GetFreeTcpPort(), ((IFeatureHost)window).ShutdownToken);
            using var client = await OpenMobileMapEventsAsync(server);

            var deferred = new DispatcherCleanupProbe();
            var failing = new DispatcherCleanupProbe(fail: true);
            var lifecycles = (List<(string, IAsyncDisposable)>)typeof(MainWindow)
                .GetField("_featureViewLifecycles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            lifecycles.Add(("手机地图", mobileView));
            lifecycles.Add(("清理失败测试", failing));
            lifecycles.Add(("延迟清理测试", deferred));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.SetResult();

            window.Close();
            Assert(!closed.Task.IsCompleted, "The host closed before asynchronous module cleanup completed.");
            window.Close(); // A second close must neither bypass nor repeat the pending cleanup.
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(deferred.Completed && failing.Completed && deferred.Calls == 1 && failing.Calls == 1,
                "Module cleanup was skipped or repeated during shutdown.");
            Assert(!server.IsRunning, "A failing component prevented the mobile-map component from stopping.");
            await VerifyClientClosedAsync(client);
        }
        finally
        {
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    private static void VerifyIndependentStoryMode(IFeatureHost host)
    {
        var chapter = StoryGuideService.LoadCatalog().First();
        var variant = StoryGuideService.LoadVariants(chapter).First();
        var detail = new StoryGuideDetail(chapter, [],
        [
            new(1, "共同步骤", [], []),
            new(2, "PVP 步骤", [], [], "pvp"),
            new(3, "PVE 步骤", [], [], "pve")
        ]);
        // Seed only the process-local cache so the view exercises the actual loader offline.
        typeof(StoryGuideService).GetMethod("CacheDetail", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [$"{chapter.Id}:{variant.Id}", detail]);

        var view = new StoryGuideView(host, () => { });
        AssertStorySteps(view, "PVE 步骤");
        ((ComboBox)view.FindName("ModeSelector")).SelectedValue = "pvp";
        AssertStorySteps(view, "PVP 步骤");

        var reopened = new StoryGuideView(host, () => { });
        Assert((string)((ComboBox)reopened.FindName("ModeSelector")).SelectedValue == "pvp",
            "The independently selected story mode was not persisted.");
        AssertStorySteps(reopened, "PVP 步骤");
        ((ComboBox)reopened.FindName("ModeSelector")).SelectedValue = "pve";
        AssertStorySteps(reopened, "PVE 步骤");
    }

    private static void AssertStorySteps(StoryGuideView view, string modeStep)
    {
        var steps = ((ListBox)view.FindName("StepsList")).ItemsSource.Cast<StoryGuideStep>().Select(step => step.Title);
        Assert(steps.SequenceEqual(["共同步骤", modeStep]), "The story guide displayed steps from the wrong game mode.");
    }

    private sealed class DispatcherCleanupProbe(bool fail = false) : IAsyncDisposable
    {
        public int Calls { get; private set; }
        public bool Completed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Calls++;
            var dispatcher = Dispatcher.CurrentDispatcher;
            await Task.Delay(60);
            dispatcher.VerifyAccess();
            Completed = true;
            if (fail) throw new InvalidOperationException("Expected isolated cleanup failure.");
        }
    }
}

public class IndependentFeatureHost : DispatchProxy
{
    public string DataDirectory { get; set; } = string.Empty;
    public CancellationToken ShutdownToken { get; set; }
    public Task MarketFreshTask { get; set; } = Task.CompletedTask;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_DataDirectory" => DataDirectory,
        "get_CurrentMarketMode" => "pve",
        "get_ShutdownToken" => ShutdownToken,
        "GetMarketSnapshot" => new FeatureMarketSnapshot([], null, "", null, true, false),
        "GetMarketItems" => new Dictionary<string, FeatureMarketItem>(),
        "EnsureMarketFreshAsync" => MarketFreshTask,
        "WriteLog" or "ShowNotification" => null,
        _ => throw new InvalidOperationException($"Unexpected cross-component dependency: {targetMethod?.Name}")
    };
}
