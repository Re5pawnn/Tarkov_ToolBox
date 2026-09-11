using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.TeamSync.Models;
using TarkovMapLocator.Modules.TeamSync.Services;

namespace TarkovMapLocator.Modules.TeamSync;

public partial class TeamSyncView : UserControl, ITeamSyncFeature
{
    private readonly IFeatureHost _host;
    private readonly LanPeerSyncService _syncService;
    private readonly LanSyncPreferencesService _preferences;
    private readonly DispatcherTimer _publishTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly IReadOnlyList<LanColorOption> _colorOptions =
    [
        new("#4fd1ff", "青蓝"),
        new("#7cc38f", "绿色"),
        new("#e9ad50", "琥珀"),
        new("#b69cff", "紫色"),
        new("#dc7a73", "红色")
    ];

    private LanSyncConfig _config = LanSyncConfig.Default;
    private bool _suppressConfigChange;
    private bool _publishing;
    private bool _disposed;

    public TeamSyncView(IFeatureHost host)
    {
        _host = host;
        _syncService = new LanPeerSyncService(host);
        _preferences = new LanSyncPreferencesService(host);
        InitializeComponent();
        InitializeControls();
        _publishTimer.Tick += PublishTimer_Tick;
        IsVisibleChanged += TeamSyncView_IsVisibleChanged;
    }

    public bool IsRunning => _syncService.IsRunning;

    public IReadOnlyList<FeatureTeamPeerPosition> GetPeerPositions(string? mapId) =>
        _syncService.BuildPeerPositions(mapId);

    public string? HandleSyncRequest(string requestJson, string remoteAddress) =>
        _syncService.HandleHostRequest(requestJson, remoteAddress);

    public async Task StopHostingAsync()
    {
        if (_syncService.IsHosting) await StopAsync();
    }

    public async Task PublishCurrentLocationAsync()
    {
        if (!_syncService.IsRunning || _publishing || _disposed) return;

        _publishing = true;
        try
        {
            _config = CaptureConfig(true);
            await _syncService.PublishAsync(_config, _host.GetSharedLocation(), _host.ShutdownToken);
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _host.ShowNotification($"队友共享更新失败：{exception.Message}");
            _host.WriteLog(FeatureLogLevel.Error, "队友共享", "发布位置更新失败", exception.ToString());
        }
        finally
        {
            _publishing = false;
            if (IsVisible) UpdatePage();
            _host.RefreshMapMarkers();
        }
    }

    public async Task StopAsync()
    {
        _publishTimer.Stop();
        await _syncService.StopAsync();
        PersistConfig(false);
        UpdatePage();
        _host.RefreshMapMarkers();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _publishTimer.Stop();
        IsVisibleChanged -= TeamSyncView_IsVisibleChanged;
        await _syncService.DisposeAsync();
    }

    private void InitializeControls()
    {
        _config = _preferences.Load() with { Enabled = false };
        _suppressConfigChange = true;
        try
        {
            NameBox.Text = _config.DisplayName;
            RemoteEndpointBox.Text = _config.RemoteEndpoint;
            RoomKeyBox.Password = _config.RoomKey;
            ColorComboBox.ItemsSource = _colorOptions;
            ColorComboBox.SelectedItem = _colorOptions.FirstOrDefault(option =>
                string.Equals(option.Value, _config.Color, StringComparison.OrdinalIgnoreCase)) ?? _colorOptions[0];
        }
        finally
        {
            _suppressConfigChange = false;
        }

        UpdateModeControls();
        UpdatePage();
    }

    private LanSyncConfig CaptureConfig(bool enabled) => new LanSyncConfig(
        enabled,
        _config.Mode,
        NameBox.Text,
        (ColorComboBox.SelectedItem as LanColorOption)?.Value ?? _config.Color,
        RemoteEndpointBox.Text,
        RoomKeyBox.Password,
        _config.SyncPort).Normalize();

    private void PersistConfig(bool? enabled = null)
    {
        _config = CaptureConfig(enabled ?? _syncService.IsRunning);
        _preferences.Save(_config);
    }

    private async Task SelectModeAsync(string mode)
    {
        var normalizedMode = string.Equals(mode, "join", StringComparison.OrdinalIgnoreCase) ? "join" : "host";
        if (_syncService.IsRunning)
        {
            _publishTimer.Stop();
            await _syncService.StopAsync("同步已停止，请按新的连接方式启动");
            _host.WriteLog(FeatureLogLevel.Info, "队友共享", "因连接方式变化停止共享", $"新模式: {normalizedMode}");
        }

        _config = CaptureConfig(false) with { Mode = normalizedMode, Enabled = false };
        _preferences.Save(_config);
        UpdateModeControls();
        UpdatePage();
        _host.RefreshMapMarkers();
    }

    private void UpdateModeControls()
    {
        var isJoin = string.Equals(_config.Mode, "join", StringComparison.OrdinalIgnoreCase);
        RemoteEndpointPanel.Visibility = isJoin ? Visibility.Visible : Visibility.Collapsed;
        SetToolButton(HostButton, !isJoin);
        SetToolButton(JoinButton, isJoin);
    }

    private void UpdatePage()
    {
        if (_disposed) return;
        var location = _host.GetSharedLocation();
        var state = _syncService.BuildUiState(location.MapId);
        var mode = state.IsRunning ? state.Mode : _config.Mode;
        var isJoin = string.Equals(mode, "join", StringComparison.OrdinalIgnoreCase);

        _suppressConfigChange = true;
        try
        {
            if (ColorComboBox.ItemsSource is null) ColorComboBox.ItemsSource = _colorOptions;
            if (ColorComboBox.SelectedItem is null)
            {
                ColorComboBox.SelectedItem = _colorOptions.FirstOrDefault(option =>
                    string.Equals(option.Value, _config.Color, StringComparison.OrdinalIgnoreCase)) ?? _colorOptions[0];
            }
        }
        finally
        {
            _suppressConfigChange = false;
        }

        UpdateModeControls();
        StartStopButton.Content = state.IsRunning ? "停止共享" : isJoin ? "加入队友" : "开启房间";
        EndpointText.Text = state.IsRunning
            ? isJoin ? $"已连接 {_config.RemoteEndpoint}" : $"房主地址 {state.LocalEndpoint}"
            : isJoin
                ? string.IsNullOrWhiteSpace(_config.RemoteEndpoint) ? "填写房主 IP:端口" : $"目标 {_config.RemoteEndpoint}"
                : $"端口 {_config.SyncPort}";
        StatusText.Text = state.Status;
        ErrorText.Text = state.LastError;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(state.LastError) ? Visibility.Collapsed : Visibility.Visible;

        PeerCountText.Text = state.Peers.Count == 0 ? "0 人" : $"{state.Peers.Count} 人";
        PeerPanel.Children.Clear();
        foreach (var peer in state.Peers) PeerPanel.Children.Add(CreatePeerRow(peer));
        PeerEmptyText.Visibility = state.Peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border CreatePeerRow(LanPeerListItem peer)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(21, 28, 25)),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 7)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var color = ResolveColor(peer.Color);
        grid.Children.Add(new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = color,
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = peer.DisplayName,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var stateText = peer.HasPosition ? peer.IsVisibleOnCurrentMap ? "同图显示" : "其他地图" : "等待坐标";
        details.Children.Add(new TextBlock
        {
            Text = $"{peer.MapName} · {stateText} · {peer.LastSeenText}",
            Foreground = (Brush)FindResource("TextDimBrush"),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        var badge = new TextBlock
        {
            Text = peer.IsVisibleOnCurrentMap ? "地图中" : "",
            Foreground = color,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        row.Child = grid;
        return row;
    }

    private Brush ResolveColor(string colorHex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(colorHex) is Color color) return new SolidColorBrush(color);
        }
        catch (FormatException) { }
        return (Brush)FindResource("CyanBrush");
    }

    private void SetToolButton(Button button, bool selected)
    {
        button.Background = selected ? (Brush)FindResource("RaisedBrush") : Brushes.Transparent;
        button.BorderBrush = selected ? (Brush)FindResource("LineBrightBrush") : (Brush)FindResource("LineBrush");
        button.Foreground = selected ? (Brush)FindResource("AmberBrush") : (Brush)FindResource("TextDimBrush");
    }

    private async void HostButton_Click(object sender, RoutedEventArgs e) => await SelectModeAsync("host");

    private async void JoinButton_Click(object sender, RoutedEventArgs e) => await SelectModeAsync("join");

    private void ColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConfigChange) return;
        PersistConfig();
        UpdatePage();
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressConfigChange) return;
        PersistConfig();
        UpdatePage();
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PersistConfig();
        UpdatePage();
        e.Handled = true;
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        StartStopButton.IsEnabled = false;
        try
        {
            if (_syncService.IsRunning)
            {
                await StopAsync();
                _host.WriteLog(FeatureLogLevel.Info, "队友共享", "已停止位置共享", $"模式: {_config.Mode}\n端口: {_config.SyncPort}");
                return;
            }

            _config = CaptureConfig(true);
            if (_config.RoomKey.Length < 12)
            {
                _host.ShowNotification("房间密钥至少需要 12 位");
                RoomKeyBox.Focus();
                return;
            }
            if (!string.Equals(_config.Mode, "join", StringComparison.OrdinalIgnoreCase))
            {
                var sharedPort = _host.EnsureMobileMapServerRunning();
                if (sharedPort == 0) return;
                _config = _config with { SyncPort = sharedPort };
            }
            _preferences.Save(_config);
            if (string.Equals(_config.Mode, "join", StringComparison.OrdinalIgnoreCase))
                await _syncService.StartJoinAsync(_config, _host.ShutdownToken);
            else
                await _syncService.StartHostAsync(_config, _host.ShutdownToken);

            PersistConfig(_syncService.IsRunning);
            if (_syncService.IsRunning)
            {
                _publishTimer.Start();
                await PublishCurrentLocationAsync();
                var state = _syncService.BuildUiState(_host.GetSharedLocation().MapId);
                _host.WriteLog(
                    FeatureLogLevel.Info,
                    "队友共享",
                    string.Equals(_config.Mode, "join", StringComparison.OrdinalIgnoreCase) ? "已加入队友共享" : "已开启队友共享房间",
                    $"模式: {_config.Mode}\n本机名称: {_config.DisplayName}\n颜色: {_config.Color}\n" +
                    $"本地端点: {state.LocalEndpoint}\n远程端点: {_config.RemoteEndpoint}\n端口: {_config.SyncPort}\n状态: {state.Status}");
            }
        }
        catch (Exception exception)
        {
            _host.ShowNotification($"队友共享启动失败：{exception.Message}");
            _host.WriteLog(FeatureLogLevel.Error, "队友共享", "启动或停止位置共享失败", exception.ToString());
        }
        finally
        {
            StartStopButton.IsEnabled = true;
            UpdatePage();
        }
    }

    private async void PublishTimer_Tick(object? sender, EventArgs e)
    {
        if (!_syncService.IsRunning)
        {
            _publishTimer.Stop();
            UpdatePage();
            return;
        }

        await PublishCurrentLocationAsync();
    }

    private void TeamSyncView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) UpdatePage();
    }
}
