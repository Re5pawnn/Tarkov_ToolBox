using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.MobileMap.Models;
using TarkovMapLocator.Modules.MobileMap.Services;

namespace TarkovMapLocator.Modules.MobileMap;

public partial class MobileMapView : UserControl, IFeatureViewLifecycle, IMobileMapFeature
{
    private readonly IFeatureHost _host;
    private readonly MobileMapServer _server;
    private readonly MobileMapPreferencesService _preferences;
    private readonly DispatcherTimer _snapshotTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _disposed;
    private bool _stopping;

    public MobileMapView(IFeatureHost host)
    {
        _host = host;
        _server = new MobileMapServer(teamSyncHandler: host.HandleTeamSyncRequest);
        _preferences = new MobileMapPreferencesService(host);
        InitializeComponent();
        var preferences = _preferences.Load();
        PortBox.Text = preferences.Port.ToString(CultureInfo.InvariantCulture);
        PublicEndpointBox.Text = preferences.SakuraFrpEndpoint;
        _snapshotTimer.Tick += SnapshotTimer_Tick;
        _server.StateChanged += Server_StateChanged;
        UpdatePage();
    }

    public bool IsRunning => _server.IsRunning;

    public int Port => _server.Port;

    public bool EnsureRunning()
    {
        if (_server.IsRunning) return true;

        if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            _host.ShowNotification("端口必须在 1024 到 65535 之间");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(PublicEndpointBox.Text) && !TryNormalizeSakuraFrpEndpoint(PublicEndpointBox.Text, out _))
        {
            _host.ShowNotification("公网入口必须是无路径、无参数的 HTTPS 地址");
            PublicEndpointBox.Focus();
            return false;
        }

        try
        {
            var preferences = new MobileMapPreferences(port, PublicEndpointBox.Text).Normalize();
            _preferences.Save(preferences);
            _server.Start(preferences.Port, _host.ShutdownToken);
            PushSnapshot();
            _snapshotTimer.Start();
            _host.WriteLog(
                FeatureLogLevel.Info,
                "手机地图",
                "手机地图与队友共享服务已启动",
                $"共用端口: {preferences.Port}\n手机地址: {GetPhoneAccessUrl()}\n局域网地址: {string.Join("\n", _server.AccessUrls)}");
            _host.ShowNotification("手机地图与队友共享服务已启动");
            return true;
        }
        catch (SocketException exception)
        {
            _host.WriteLog(FeatureLogLevel.Error, "手机地图", "共享服务启动失败", exception.ToString());
            _host.ShowNotification(exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"端口 {port} 已被占用，请换一个端口"
                : $"共享服务启动失败：{exception.Message}");
        }
        catch (Exception exception)
        {
            _host.WriteLog(FeatureLogLevel.Error, "手机地图", "共享服务启动失败", exception.ToString());
            _host.ShowNotification($"共享服务启动失败：{exception.Message}");
        }
        finally
        {
            UpdatePage();
        }
        return false;
    }

    public void OnActivated() => UpdatePage();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _snapshotTimer.Stop();
        _snapshotTimer.Tick -= SnapshotTimer_Tick;
        _server.StateChanged -= Server_StateChanged;
        await _server.DisposeAsync();
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || _disposed) return;
        if (_server.IsRunning)
        {
            await StopServerAsync();
            return;
        }

        EnsureRunning();
    }

    private async Task StopServerAsync()
    {
        _stopping = true;
        _snapshotTimer.Stop();
        UpdatePage();
        try
        {
            await _host.StopHostedTeamSyncAsync();
            await _server.StopAsync();
            _host.WriteLog(FeatureLogLevel.Info, "手机地图", "手机地图与队友共享服务已停止");
            _host.ShowNotification("共享服务已停止");
        }
        finally
        {
            _stopping = false;
            UpdatePage();
        }
    }

    private void SnapshotTimer_Tick(object? sender, EventArgs e) => PushSnapshot();

    private void PushSnapshot()
    {
        if (!_server.IsRunning || _disposed) return;
        try
        {
            _server.UpdateSnapshot(_host.GetMobileMapSnapshot());
        }
        catch (Exception exception)
        {
            _host.WriteLog(FeatureLogLevel.Warning, "手机地图", "生成手机地图快照失败", exception.ToString());
        }
    }

    private void Server_StateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdatePage));
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var address = GetPhoneAccessUrl();
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            Clipboard.SetText(address);
            _host.ShowNotification("手机访问地址已复制");
        }
        catch (Exception exception)
        {
            _host.ShowNotification($"复制失败：{exception.Message}");
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        var address = _server.PrimaryAccessUrl;
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _host.ShowNotification($"打开浏览器失败：{exception.Message}");
        }
    }

    private void PortBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsAsciiDigit(character));

    private void PublicEndpointBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePage();

    private void PublicEndpointBox_LostFocus(object sender, RoutedEventArgs e) => SavePreferences();

    private void CopyTunnelConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var port = int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
            ? parsedPort
            : MobileMapPreferences.DefaultPort;
        try
        {
            Clipboard.SetText($"隧道类型：HTTPS\n本地 IP：127.0.0.1\n本地端口：{port}");
            _host.ShowNotification("SakuraFrp 隧道参数已复制");
        }
        catch (Exception exception)
        {
            _host.ShowNotification($"复制失败：{exception.Message}");
        }
    }

    private void OpenSakuraFrpButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.natfrp.com/tunnel/") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _host.ShowNotification($"打开 SakuraFrp 失败：{exception.Message}");
        }
    }

    private void SavePreferences()
    {
        var port = int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
            ? parsedPort
            : MobileMapPreferences.DefaultPort;
        _preferences.Save(new MobileMapPreferences(port, PublicEndpointBox.Text));
    }

    private string GetPhoneAccessUrl()
    {
        if (!_server.IsRunning) return string.Empty;
        if (TryNormalizeSakuraFrpEndpoint(PublicEndpointBox.Text, out var publicEndpoint))
            return $"{publicEndpoint}/?token={Uri.EscapeDataString(_server.Token)}";
        return _server.PrimaryAccessUrl;
    }

    internal static bool TryNormalizeSakuraFrpEndpoint(string? value, out string endpoint)
    {
        endpoint = string.Empty;
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            !string.IsNullOrWhiteSpace(uri.Query) ||
            !string.IsNullOrWhiteSpace(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/"))
            return false;

        endpoint = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    private void UpdatePage()
    {
        if (ServerStateText is null) return;
        var running = _server.IsRunning;
        ServerStateText.Text = _stopping ? "正在停止" : running ? "实时服务运行中" : "未启动";
        ServerStateText.Foreground = (Brush)FindResource(running ? "GreenBrush" : "TextDimBrush");
        ServerStateDot.Fill = (Brush)FindResource(running ? "GreenBrush" : "TextFaintBrush");
        StartStopButton.Content = _stopping ? "正在停止" : running ? "停止共享服务" : "启动共享服务";
        StartStopButton.IsEnabled = !_stopping;
        PortBox.IsEnabled = !running && !_stopping;
        var phoneAddress = GetPhoneAccessUrl();
        EndpointBox.Text = running ? phoneAddress : "启动后生成访问地址";
        CopyButton.IsEnabled = running && !string.IsNullOrWhiteSpace(phoneAddress);
        PreviewButton.IsEnabled = CopyButton.IsEnabled;
        ClientCountText.Text = $"{_server.ConnectedClients} 台设备";
        var hasPublicEndpoint = TryNormalizeSakuraFrpEndpoint(PublicEndpointBox.Text, out var normalizedEndpoint);
        var publicEndpointInvalid = !string.IsNullOrWhiteSpace(PublicEndpointBox.Text) && !hasPublicEndpoint;
        TunnelConfigText.Text = publicEndpointInvalid
            ? "公网入口不安全，请填写完整的 https:// 连接地址"
            : $"SakuraFrp：HTTPS · 127.0.0.1 · {PortBox.Text}{(hasPublicEndpoint ? $" · {normalizedEndpoint}" : string.Empty)}";
        TunnelConfigText.Foreground = (Brush)FindResource(publicEndpointInvalid ? "DangerBrush" : "TextDimBrush");
        DetailText.Text = running
            ? hasPublicEndpoint
                ? "公网地址已生成；SakuraFrp 隧道在线后，手机热点和移动网络均可访问。"
                : "当前为局域网地址；如需手机热点访问，请填写 SakuraFrp 公网入口。"
            : "先启动本地服务，再在 SakuraFrp 启动对应 HTTPS 隧道。";
    }
}
