using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.TaskItems;

internal sealed class HideoutLevelWindow : Window
{
    private readonly IReadOnlyList<FeatureHideoutStation> _stations;
    private readonly Dictionary<string, ComboBox> _selectors = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Levels { get; private set; } = new Dictionary<string, int>();

    public HideoutLevelWindow(string mode, IReadOnlyList<FeatureHideoutStation> stations)
    {
        _stations = stations;
        Title = $"藏身处等级 · {mode.ToUpperInvariant()}";
        Width = 760;
        Height = 650;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = Brush("CanvasBrush", "#0E1111");
        Foreground = Brush("TextBrush", "#EDF2EB");
        Content = BuildContent(mode);
    }

    private UIElement BuildContent(string mode)
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = $"藏身处等级 · {mode.ToUpperInvariant()}",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextBrush", "#EDF2EB")
        });

        var stationGrid = new Grid { Margin = new Thickness(0, 18, 0, 16) };
        stationGrid.ColumnDefinitions.Add(new ColumnDefinition());
        stationGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < (_stations.Count + 1) / 2; row++)
            stationGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < _stations.Count; index++)
        {
            var station = _stations[index];
            var selector = new ComboBox
            {
                Width = 116,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                ItemsSource = Enumerable.Range(0, station.MaxLevel + 1).Select(level => new LevelOption(level, level == 0 ? "未建造" : $"{level} 级")).ToArray(),
                DisplayMemberPath = nameof(LevelOption.Label),
                SelectedValuePath = nameof(LevelOption.Value),
                SelectedValue = station.CurrentLevel,
                Style = Application.Current.TryFindResource("DarkComboBox") as Style
            };
            _selectors[station.Id] = selector;
            var item = new Border
            {
                Margin = new Thickness(index % 2 == 0 ? 0 : 6, 0, index % 2 == 0 ? 6 : 0, 10),
                Padding = new Thickness(12, 10, 10, 10),
                Background = Brush("SurfaceBrush", "#151A19"),
                BorderBrush = Brush("LineBrush", "#2D3632"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7)
            };
            var itemGrid = new Grid();
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition());
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemGrid.Children.Add(new TextBlock
            {
                Text = station.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("TextBrush", "#EDF2EB"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0)
            });
            Grid.SetColumn(selector, 1);
            itemGrid.Children.Add(selector);
            item.Child = itemGrid;
            Grid.SetRow(item, index / 2);
            Grid.SetColumn(item, index % 2);
            stationGrid.Children.Add(item);
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stationGrid
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = CreateButton("全部重置", 94);
        reset.Click += (_, _) => { foreach (var selector in _selectors.Values) selector.SelectedValue = 0; };
        var cancel = CreateButton("取消", 74);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.IsCancel = true;
        var save = CreateButton("保存", 74);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.IsDefault = true;
        save.Click += (_, _) =>
        {
            Levels = _stations.ToDictionary(station => station.Id, station => _selectors[station.Id].SelectedValue is int level ? level : 0, StringComparer.Ordinal);
            DialogResult = true;
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        return root;
    }

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 32,
        Style = Application.Current.TryFindResource("TacticalButton") as Style
    };

    private static Brush Brush(string key, string fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private sealed record LevelOption(int Value, string Label);
}
