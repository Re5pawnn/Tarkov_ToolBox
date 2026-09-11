using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.TaskTracking.Models;

namespace TarkovMapLocator.Modules.TaskTracking.Controls;

public partial class TaskTrackingDetailView : UserControl
{
    private static readonly Regex MarkdownImagePattern = new(
        @"!\[(?<alt>[^\]]*)\]\((?<url>https://[^\s\)]+)(?:\s+""[^""]*"")?\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MarkdownLinkPattern = new(
        @"\[(?<text>[^\]]+)\]\(https?://[^\)]+\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmojiPattern = new(
        @"[\u200D\u2600-\u27BF\uFE0F]|\p{Cs}",
        RegexOptions.Compiled);
    private int _renderVersion;
    private IFeatureHost? _host;

    public event EventHandler? BackRequested;
    public event Action<TaskTrackingRelatedTask>? RelatedTaskRequested;

    public TaskTrackingDetailView()
    {
        InitializeComponent();
    }

    internal void AttachHost(IFeatureHost host) => _host = host;

    internal void DetachHost()
    {
        _renderVersion++;
        _host = null;
    }

    public void ShowLoading(TaskTrackingTask task)
    {
        _renderVersion++;
        PageTitleText.Text = task.Name;
        PageStateText.Text = "";
        LoadingPanel.Visibility = Visibility.Visible;
        DetailScrollViewer.Visibility = Visibility.Hidden;
        DetailScrollViewer.ScrollToTop();
    }

    public void ShowError(TaskTrackingTask task, string message)
    {
        _renderVersion++;
        PageTitleText.Text = task.Name;
        PageStateText.Text = "读取失败";
        ClearPanels();
        ObjectivesPanel.Children.Add(Text(message, 13, Brush("#C6D0CB")));
        GuidePanel.Children.Add(Text("暂无可用攻略。", 13, Brush("#7F8C86")));
        RewardsPanel.Children.Add(Text("暂无可用奖励信息。", 13, Brush("#7F8C86")));
        LoadingPanel.Visibility = Visibility.Collapsed;
        DetailScrollViewer.Visibility = Visibility.Visible;
    }

    public Task ShowDetailAsync(TaskTrackingTask task, TaskTrackingDetail detail)
    {
        var version = ++_renderVersion;
        PageTitleText.Text = detail.Name.Length > 0 ? detail.Name : task.Name;
        PageStateText.Text = "";
        ClearPanels();
        MetaText.Text = string.Join("  ·  ", new[]
        {
            detail.TraderName.Length > 0 ? detail.TraderName : task.TraderName,
            task.GroupName,
            NormalizeMapName(detail.MapName.Length > 0 ? detail.MapName : task.MapName)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        DescriptionText.Text = detail.Description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(detail.Description)
            ? Visibility.Collapsed
            : Visibility.Visible;
        AddRelatedTasks(PreviousTasksPanel, detail.PreviousTasks);
        AddRelatedTasks(NextTasksPanel, detail.NextTasks);
        AddObjectives(detail.Objectives);
        RenderGuide(detail.GuideMarkdown, version);
        AddItems(NeededKeysPanel, detail.NeededKeys, version);
        NeededKeysSection.Visibility = detail.NeededKeys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddRewards(detail, version);
        if (version != _renderVersion) return Task.CompletedTask;

        if (!string.IsNullOrWhiteSpace(detail.TaskImageUrl))
        {
            TaskImageColumn.Width = new GridLength(310);
            TaskImageBorder.Visibility = Visibility.Visible;
            _ = SetImageSourceAsync(TaskImage, detail.SourceTaskId, detail.TaskImageUrl, true, version);
        }
        else
        {
            TaskImageColumn.Width = new GridLength(0);
            TaskImageBorder.Visibility = Visibility.Collapsed;
        }

        if (version != _renderVersion) return Task.CompletedTask;
        LoadingPanel.Visibility = Visibility.Collapsed;
        DetailScrollViewer.Visibility = Visibility.Visible;
        return Task.CompletedTask;
    }

    private void ClearPanels()
    {
        TaskImage.Source = null;
        TaskImageBorder.Visibility = Visibility.Collapsed;
        TaskImageColumn.Width = new GridLength(0);
        MetaText.Text = "";
        DescriptionText.Text = "";
        DescriptionText.Visibility = Visibility.Collapsed;
        PreviousTasksPanel.Children.Clear();
        NextTasksPanel.Children.Clear();
        ObjectivesPanel.Children.Clear();
        GuidePanel.Children.Clear();
        NeededKeysPanel.Children.Clear();
        RewardsPanel.Children.Clear();
        NeededKeysSection.Visibility = Visibility.Collapsed;
    }

    private void AddRelatedTasks(Panel panel, IReadOnlyList<TaskTrackingRelatedTask> tasks)
    {
        if (tasks.Count == 0)
        {
            panel.Children.Add(Text("无", 12, Brush("#6F7C76")));
            return;
        }
        foreach (var task in tasks)
        {
            var button = new Button
            {
                Content = task.Name,
                Tag = task,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 7, 7),
                FontSize = 12,
                Foreground = Brush("#C6D0CB"),
                Background = Brush("#151C19"),
                BorderBrush = Brush("#314139"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += RelatedTaskButton_Click;
            panel.Children.Add(button);
        }
    }

    private void RelatedTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaskTrackingRelatedTask task }) RelatedTaskRequested?.Invoke(task);
    }

    private void AddObjectives(IReadOnlyList<TaskTrackingObjective> objectives)
    {
        if (objectives.Count == 0)
        {
            ObjectivesPanel.Children.Add(Text("暂无任务目标说明。", 13.5, Brush("#7F8C86")));
            return;
        }
        for (var index = 0; index < objectives.Count; index++)
        {
            var objective = objectives[index];
            var prefix = objective.Optional ? $"{index + 1}.（可选）" : $"{index + 1}. ";
            ObjectivesPanel.Children.Add(Text($"{prefix}{objective.Summary}", 13.5, Brush("#C6D0CB"), 22, new Thickness(0, 0, 0, 8)));
        }
    }

    private void RenderGuide(string markdown, int version)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            GuidePanel.Children.Add(Text("该任务暂未收录文字攻略。", 13, Brush("#7F8C86")));
            return;
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        foreach (var rawLine in lines)
        {
            if (version != _renderVersion) return;
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var imageMatch = MarkdownImagePattern.Match(line);
            if (imageMatch.Success)
            {
                var url = imageMatch.Groups["url"].Value;
                var image = new Image { MaxWidth = 620, MaxHeight = 360, Stretch = Stretch.Uniform };
                var border = new Border
                {
                    Background = Brush("#0B100F"),
                    BorderBrush = Brush("#26342E"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 4, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = image
                };
                GuidePanel.Children.Add(border);
                _ = SetImageSourceAsync(image, HashId(url), url, true, version);
                var remaining = MarkdownImagePattern.Replace(line, "").Trim();
                if (remaining.Length > 0) GuidePanel.Children.Add(Text(CleanMarkdown(remaining), 13.5, Brush("#C6D0CB"), 22, new Thickness(0, 0, 0, 8)));
                continue;
            }

            var headingLevel = line.TakeWhile(character => character == '#').Count();
            if (headingLevel > 0)
            {
                GuidePanel.Children.Add(Text(
                    CleanMarkdown(line[headingLevel..].Trim()),
                    headingLevel <= 2 ? 18 : 15,
                    Brushes.White,
                    headingLevel <= 2 ? 28 : 23,
                    new Thickness(0, 10, 0, 7),
                    FontWeights.Bold));
                continue;
            }

            var bullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
            if (bullet) line = $"• {line[2..].Trim()}";
            GuidePanel.Children.Add(Text(CleanMarkdown(line), 13.5, Brush("#C6D0CB"), 22, new Thickness(0, 0, 0, 8)));
        }
    }

    private void AddRewards(TaskTrackingDetail detail, int version)
    {
        foreach (var reward in detail.OtherRewards)
            RewardsPanel.Children.Add(Text(reward, 13.5, Brush("#C6D0CB"), 22, new Thickness(0, 0, 0, 7)));
        if (detail.ItemRewards.Count > 0)
        {
            RewardsPanel.Children.Add(SectionCaption("物品奖励"));
            AddItems(RewardsPanel, detail.ItemRewards, version);
        }
        if (detail.OfferUnlocks.Count > 0)
        {
            RewardsPanel.Children.Add(SectionCaption("解锁购买"));
            foreach (var unlock in detail.OfferUnlocks)
            {
                if (version != _renderVersion) return;
                var row = CreateItemRow(unlock.ItemId, unlock.Summary, unlock.IconUrl);
                RewardsPanel.Children.Add(row.Border);
                _ = SetImageSourceAsync(row.Image, unlock.ItemId, unlock.IconUrl, false, version);
            }
        }
        if (RewardsPanel.Children.Count == 0)
        {
            RewardsPanel.Children.Add(Text(
                string.IsNullOrWhiteSpace(detail.RewardSummary) ? "暂无奖励说明。" : detail.RewardSummary,
                13.5,
                Brush("#C6D0CB"),
                22));
        }
    }

    private void AddItems(Panel panel, IReadOnlyList<TaskTrackingRewardItem> items, int version)
    {
        foreach (var item in items)
        {
            if (version != _renderVersion) return;
            var row = CreateItemRow(item.ItemId, item.Summary, item.IconUrl);
            panel.Children.Add(row.Border);
            _ = SetImageSourceAsync(row.Image, item.ItemId, item.IconUrl, false, version);
        }
    }

    private static (Border Border, Image Image) CreateItemRow(string itemId, string summary, string iconUrl)
    {
        var image = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(image);
        var label = Text(summary, 13, Brush("#C6D0CB"));
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return (new Border
        {
            Background = Brush("#0E1412"),
            BorderBrush = Brush("#26342E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
            ToolTip = itemId.Length > 0 && iconUrl.Length == 0 ? itemId : null
        }, image);
    }

    private static TextBlock SectionCaption(string text) => Text(
        text,
        13,
        Brush("#E9AD50"),
        20,
        new Thickness(0, 10, 0, 8),
        FontWeights.SemiBold);

    private static TextBlock Text(
        string text,
        double size,
        Brush foreground,
        double lineHeight = 0,
        Thickness? margin = null,
        FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = lineHeight > 0 ? lineHeight : double.NaN,
        Margin = margin ?? new Thickness(0),
        FontWeight = weight ?? FontWeights.Normal
    };

    internal static string CleanMarkdown(string value)
    {
        var result = MarkdownLinkPattern.Replace(value, match => match.Groups["text"].Value);
        result = result
            .Replace("**", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal)
            .Replace("==", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal)
            .Trim();
        return Regex.Replace(EmojiPattern.Replace(result, ""), @"\s{2,}", " ").Trim();
    }

    private static string NormalizeMapName(string value) =>
        string.Equals(value.Trim(), "Any", StringComparison.OrdinalIgnoreCase) ? "任意地图" : value;

    private async Task<ImageSource?> LoadImageAsync(string id, string url, bool full)
    {
        if (string.IsNullOrWhiteSpace(url) || _host is null) return null;
        var normalizedId = id.Length == 24 && id.All(Uri.IsHexDigit) ? id : HashId(url);
        return await _host.LoadItemIconAsync(normalizedId, url, preferFullImage: full);
    }

    private async Task SetImageSourceAsync(Image image, string id, string url, bool full, int version)
    {
        var source = await LoadImageAsync(id, url, full);
        if (version == _renderVersion) image.Source = source;
    }

    private static string HashId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
