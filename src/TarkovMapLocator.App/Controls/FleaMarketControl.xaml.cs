using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using TarkovMapLocator.App.Models;
using TarkovMapLocator.App.Services;
using Windows.Foundation;
using Windows.UI;

namespace TarkovMapLocator.App.Controls;

public sealed partial class FleaMarketControl : UserControl
{
    private const int SearchLimit = 80;
    private readonly DispatcherQueueTimer searchTimer;
    private readonly FleaMarketPriceService marketService = new();
    private LocalPathConfigService? configService;
    private DataTemplate? currentItemTemplate;
    private string currentMode = "pvp";
    private string currentQuery = "";
    private double? updatedAt;
    private string lastError = "";
    private bool backendReady;
    private bool loading;
    private bool refreshing;
    private bool initialized;
    private int resultCount;
    private int requestId;

    public FleaMarketControl()
    {
        InitializeComponent();
        searchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        searchTimer.Interval = TimeSpan.FromMilliseconds(220);
        searchTimer.Tick += OnSearchTimerTick;
        UpdateResponsiveLayout();
        RenderMarketPanel();
    }

    public ObservableCollection<FleaMarketItemView> Items { get; } = [];

    public async Task EnsureLoadedAsync(
        LocalPathConfigService localPathConfigService,
        CancellationToken cancellationToken = default)
    {
        configService = localPathConfigService;
        if (initialized)
        {
            return;
        }

        initialized = true;
        currentMode = ReadMarketMode(localPathConfigService.ReadUiPreferences());
        UpdateMarketModeButtons();
        await LoadMarketStateAsync(cancellationToken);
    }

    private async Task LoadMarketStateAsync(CancellationToken cancellationToken)
    {
        loading = true;
        lastError = "";
        RenderMarketPanel();

        try
        {
            var snapshot = await marketService.GetStateAsync(cancellationToken);
            ApplyState(snapshot);
            await SearchMarketAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            backendReady = false;
            Items.Clear();
            resultCount = 0;
            lastError = ex.Message;
        }
        finally
        {
            loading = false;
            RenderMarketPanel();
        }
    }

    private async Task SearchMarketAsync(CancellationToken cancellationToken = default)
    {
        var localRequestId = ++requestId;
        loading = true;
        lastError = "";
        RenderMarketPanel();

        try
        {
            var payload = await marketService.SearchAsync(
                currentMode,
                currentQuery,
                SearchLimit,
                cancellationToken);
            if (localRequestId != requestId)
            {
                return;
            }

            backendReady = payload?.Available == true;
            updatedAt = payload?.UpdatedAt ?? updatedAt;
            lastError = payload?.Error ?? payload?.LastError ?? "";
            Items.Clear();
            foreach (var item in payload?.Items ?? [])
            {
                Items.Add(FleaMarketItemView.FromItem(item, marketService.GetIconUri(item.Id)));
            }

            resultCount = payload?.Count > 0 ? payload.Count : Items.Count;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (localRequestId != requestId)
            {
                return;
            }

            backendReady = false;
            Items.Clear();
            resultCount = 0;
            lastError = ex.Message;
        }
        finally
        {
            if (localRequestId == requestId)
            {
                loading = false;
                RenderMarketPanel();
            }
        }
    }

    private async Task RefreshMarketAsync()
    {
        refreshing = true;
        lastError = "";
        RenderMarketPanel();

        try
        {
            var snapshot = await marketService.RefreshAsync();
            ApplyState(snapshot);
            await SearchMarketAsync();
        }
        catch (Exception ex)
        {
            backendReady = false;
            lastError = ex.Message;
        }
        finally
        {
            refreshing = false;
            RenderMarketPanel();
        }
    }

    private void ApplyState(MarketStateSnapshot? snapshot)
    {
        backendReady = snapshot?.Available == true;
        updatedAt = snapshot?.UpdatedAt ?? updatedAt;
        lastError = snapshot?.Error ?? snapshot?.LastError ?? "";
    }

    private void RenderMarketPanel()
    {
        UpdateMarketModeButtons();
        RefreshButton.IsEnabled = !refreshing && !loading;
        SearchBox.IsEnabled = !refreshing;

        var modeText = currentMode == "pve" ? "PVE" : "PVP";
        if (refreshing)
        {
            StatusText.Text = $"市场状态: 正在刷新 {modeText} 价格";
            StatusText.Foreground = BrushResource("MarketAccent2Brush");
        }
        else if (loading)
        {
            StatusText.Text = $"市场状态: 正在查询 {modeText}";
            StatusText.Foreground = BrushResource("MarketAccent2Brush");
        }
        else if (!string.IsNullOrWhiteSpace(lastError) && !backendReady)
        {
            StatusText.Text = $"市场状态: {lastError}";
            StatusText.Foreground = BrushResource("MarketDangerBrush");
        }
        else if (!string.IsNullOrWhiteSpace(lastError))
        {
            StatusText.Text = $"市场状态: 使用缓存 / {lastError}";
            StatusText.Foreground = BrushResource("MarketWarnBrush");
        }
        else if (backendReady)
        {
            StatusText.Text = $"市场状态: {modeText} / 更新于 {FormatMarketUpdatedAt(updatedAt)}";
            StatusText.Foreground = BrushResource("MarketAccentBrush");
        }
        else
        {
            StatusText.Text = "市场状态: 未加载";
            StatusText.Foreground = BrushResource("MarketMutedBrush");
        }

        CountText.Text = $"{(resultCount > 0 ? resultCount : Items.Count)} 个结果";
        if (!backendReady && !string.IsNullOrWhiteSpace(lastError))
        {
            ShowEmpty(lastError);
            return;
        }

        if (loading || refreshing)
        {
            ShowEmpty("正在读取市场数据...");
            return;
        }

        if (Items.Count == 0)
        {
            ShowEmpty(string.IsNullOrWhiteSpace(currentQuery) ? "输入物品名称开始查询。" : "没有找到匹配物品。");
            return;
        }

        EmptyView.Visibility = Visibility.Collapsed;
        MarketResultsRepeater.Visibility = Visibility.Visible;
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyView.Visibility = Visibility.Visible;
        MarketResultsRepeater.Visibility = Visibility.Collapsed;
    }

    private void UpdateMarketModeButtons()
    {
        ApplyModeButtonVisual(PvpButton, currentMode == "pvp");
        ApplyModeButtonVisual(PveButton, currentMode == "pve");
    }

    private static void ApplyModeButtonVisual(Button button, bool active)
    {
        if (active)
        {
            button.Foreground = new SolidColorBrush(Color.FromArgb(255, 8, 16, 23));
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(184, 42, 217, 255));
            button.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 105, 230, 255), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 36, 184, 228), Offset = 1 }
                }
            };
            return;
        }

        button.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 172, 186));
        button.BorderBrush = new SolidColorBrush(Colors.Transparent);
        button.Background = new SolidColorBrush(Colors.Transparent);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        currentQuery = SearchBox.Text.Trim();
        if (!initialized)
        {
            return;
        }

        searchTimer.Stop();
        searchTimer.Start();
    }

    private async void OnSearchTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await RunUiAsync(() => SearchMarketAsync());
    }

    private async void OnPvpClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(() => ChangeModeAsync("pvp"));
    }

    private async void OnPveClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(() => ChangeModeAsync("pve"));
    }

    private async Task ChangeModeAsync(string mode)
    {
        if (currentMode == mode || refreshing)
        {
            return;
        }

        currentMode = mode;
        SaveMarketModePreference();
        UpdateMarketModeButtons();
        await SearchMarketAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(RefreshMarketAsync);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        searchTimer.Stop();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        var viewportWidth = RootScrollViewer.ViewportWidth;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = ActualWidth;
        }

        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = 1600;
        }

        var appWidth = Math.Max(0, Math.Min(1600, viewportWidth - 24));
        AppRoot.Width = appWidth + 24;
        AppRoot.Margin = new Thickness(Math.Max(0, (viewportWidth - appWidth - 24) / 2), 0, 0, 0);

        if (ActualHeight > 0)
        {
            MarketPanel.MinHeight = Math.Min(ActualHeight * 0.72, 720);
        }

        var contentWidth = appWidth - 24;
        var compactToolbar = contentWidth <= 900;
        if (compactToolbar)
        {
            Grid.SetRow(SearchField, 0);
            Grid.SetColumn(SearchField, 0);
            Grid.SetColumnSpan(SearchField, 3);
            Grid.SetRow(ModeToggle, 1);
            Grid.SetColumn(ModeToggle, 0);
            Grid.SetColumnSpan(ModeToggle, 3);
            Grid.SetRow(RefreshButton, 2);
            Grid.SetColumn(RefreshButton, 0);
            Grid.SetColumnSpan(RefreshButton, 3);
            SearchBox.MinWidth = 0;
            ModeToggle.HorizontalAlignment = HorizontalAlignment.Stretch;
            RefreshButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Grid.SetRow(SearchField, 0);
            Grid.SetColumn(SearchField, 0);
            Grid.SetColumnSpan(SearchField, 1);
            Grid.SetRow(ModeToggle, 0);
            Grid.SetColumn(ModeToggle, 1);
            Grid.SetColumnSpan(ModeToggle, 1);
            Grid.SetRow(RefreshButton, 0);
            Grid.SetColumn(RefreshButton, 2);
            Grid.SetColumnSpan(RefreshButton, 1);
            SearchBox.MinWidth = 260;
            ModeToggle.HorizontalAlignment = HorizontalAlignment.Left;
            RefreshButton.HorizontalAlignment = HorizontalAlignment.Left;
        }

        var templateKey = contentWidth <= 760
            ? "CompactMarketItemTemplate"
            : contentWidth <= 1320
                ? "MediumMarketItemTemplate"
                : "WideMarketItemTemplate";
        var nextTemplate = (DataTemplate)Resources[templateKey];
        if (!ReferenceEquals(currentItemTemplate, nextTemplate))
        {
            currentItemTemplate = nextTemplate;
            MarketResultsRepeater.ItemTemplate = nextTemplate;
        }
    }

    private void SaveMarketModePreference()
    {
        if (configService is null)
        {
            return;
        }

        var preferences = configService.ReadUiPreferences();
        preferences["marketMode"] = currentMode;
        configService.SaveUiPreferences(preferences);
    }

    private Brush BrushResource(string key)
    {
        return (Brush)Resources[key];
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            backendReady = false;
            loading = false;
            refreshing = false;
            lastError = ex.Message;
            RenderMarketPanel();
        }
    }

    private static string ReadMarketMode(JsonObject preferences)
    {
        try
        {
            return string.Equals(
                preferences["marketMode"]?.GetValue<string>(),
                "pve",
                StringComparison.OrdinalIgnoreCase)
                ? "pve"
                : "pvp";
        }
        catch
        {
            return "pvp";
        }
    }

    private static string FormatMarketUpdatedAt(double? timestampSec)
    {
        if (timestampSec is null or <= 0 || double.IsNaN(timestampSec.Value))
        {
            return "未更新";
        }

        try
        {
            var milliseconds = (long)Math.Round(timestampSec.Value * 1000);
            var date = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
            return date.ToString("yyyy/M/d HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN"));
        }
        catch
        {
            return "未更新";
        }
    }
}

public sealed class FleaMarketItemView
{
    private static readonly Dictionary<string, ImageSource> IconSourceCache = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> MarketSourceNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fleaMarket"] = "Flea Market",
            ["prapor"] = "Prapor",
            ["therapist"] = "Therapist",
            ["fence"] = "Fence",
            ["skier"] = "Skier",
            ["peacekeeper"] = "Peacekeeper",
            ["mechanic"] = "Mechanic",
            ["ragman"] = "Ragman",
            ["jaeger"] = "Jaeger",
            ["lightkeeper"] = "Lightkeeper",
            ["ref"] = "Ref"
        };

    private FleaMarketItemView(
        string displayName,
        string subtitle,
        string typeText,
        string sizeText,
        string fleaPriceText,
        string avg24hPriceText,
        string lowHighPriceText,
        string bestTraderText,
        ImageSource iconSource)
    {
        DisplayName = displayName;
        Subtitle = subtitle;
        TypeText = typeText;
        SizeText = sizeText;
        FleaPriceText = fleaPriceText;
        Avg24hPriceText = avg24hPriceText;
        LowHighPriceText = lowHighPriceText;
        BestTraderText = bestTraderText;
        IconSource = iconSource;
    }

    public string DisplayName { get; }

    public string Subtitle { get; }

    public string TypeText { get; }

    public string SizeText { get; }

    public string FleaPriceText { get; }

    public string Avg24hPriceText { get; }

    public string LowHighPriceText { get; }

    public string BestTraderText { get; }

    public ImageSource IconSource { get; }

    public static FleaMarketItemView FromItem(FleaMarketItem item, Uri iconUri)
    {
        var displayName = SafeText(
            FirstNonEmpty(item.NameZh, item.ShortNameZh, item.Name, item.ShortName),
            "未知物品");
        var subtitlePieces = new[] { item.ShortNameZh, item.Name }
            .Select(static value => value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        var subtitle = subtitlePieces.Length > 0 ? string.Join(" / ", subtitlePieces) : SafeText(item.Id, "--");
        var typeText = item.Types is { Count: > 0 }
            ? string.Join(" / ", item.Types.Take(3).Where(static value => !string.IsNullOrWhiteSpace(value)))
            : "item";
        if (string.IsNullOrWhiteSpace(typeText))
        {
            typeText = "item";
        }

        return new FleaMarketItemView(
            displayName,
            subtitle,
            typeText,
            $"{item.Width?.ToString(CultureInfo.InvariantCulture) ?? "?"}x{item.Height?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            FormatPrice(item.FleaPrice),
            FormatPrice(item.Avg24hPrice),
            $"{FormatPrice(item.Low24hPrice)} / {FormatPrice(item.High24hPrice)}",
            FormatBestTrader(item.BestTrader),
            GetIconSource(iconUri));
    }

    private static ImageSource GetIconSource(Uri iconUri)
    {
        var key = iconUri.AbsoluteUri;
        if (IconSourceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var source = new BitmapImage(iconUri);
        IconSourceCache[key] = source;
        return source;
    }

    private static string FormatBestTrader(MarketTraderOffer? bestTrader)
    {
        if (bestTrader?.Price is not long price)
        {
            return "--";
        }

        var source = MarketSourceNames.TryGetValue(bestTrader.Source, out var known)
            ? known
            : SafeText(bestTrader.VendorName, SafeText(bestTrader.Source, "--"));
        return $"{source} {FormatPrice(price)}";
    }

    private static string FormatPrice(long? value)
    {
        return value is long number
            ? number.ToString("N0", CultureInfo.GetCultureInfo("zh-CN"))
            : "--";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.Select(static value => value?.Trim()).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string SafeText(string? value, string fallback)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }
}
