using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.TaskTracking.Models;
using TarkovMapLocator.Modules.TaskTracking.Services;
using TarkovMapLocator.Modules.TaskTracking.Windows;

namespace TarkovMapLocator.Modules.TaskTracking.Controls;

public partial class TaskTrackingView : UserControl, ITaskTrackingFeature, IFeatureNavigationHandler
{
    private const double MinimumZoom = .5;
    private const double MaximumZoom = 1.7;
    private const double DefaultZoom = .9;

    private static readonly SolidColorBrush DefaultNodeBrush = Brush("#151C19");
    private static readonly SolidColorBrush AcceptedNodeBrush = Brush("#15242D");
    private static readonly SolidColorBrush CompletedNodeBrush = Brush("#14261B");
    private static readonly SolidColorBrush DefaultBorderBrush = Brush("#314139");
    private static readonly SolidColorBrush AcceptedBorderBrush = Brush("#3E7A9D");
    private static readonly SolidColorBrush CompletedBorderBrush = Brush("#4E8D61");
    private static readonly SolidColorBrush SelectedBorderBrush = Brush("#E9AD50");
    private static readonly SolidColorBrush PinnedBorderBrush = Brush("#B69CFF");
    private static readonly SolidColorBrush PinnedButtonBrush = Brush("#463A63");
    private static readonly SolidColorBrush DefaultDotBrush = Brush("#77847E");
    private static readonly SolidColorBrush AcceptedDotBrush = Brush("#5AADE0");
    private static readonly SolidColorBrush CompletedDotBrush = Brush("#72C88D");
    private static readonly SolidColorBrush GraphLineBrush = Brush("#409EFF");

    private static readonly string[] TraderOrder =
    [
        "prapor", "therapist", "fence", "skier", "peacekeeper", "mechanic",
        "ragman", "jaeger", "lightkeeper", "ref", "btr"
    ];
    private static readonly TaskTrackingModeOption[] TaskModes =
    [
        new("pvp", "PVP"),
        new("pve", "PVE"),
        new("season", "赛季服")
    ];

    private readonly IFeatureHost _host;
    private readonly IReadOnlyDictionary<string, string[]> _trackingTaskIdsByGameId;
    private readonly Dictionary<string, Dictionary<string, TaskTrackingStatus>> _statusesByMode;
    private IReadOnlyDictionary<string, string[]> _prerequisiteIdsByTaskId = new Dictionary<string, string[]>(StringComparer.Ordinal);
    private readonly HashSet<string> _pinnedTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeVisual> _nodeVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect> _taskPositions = new(StringComparer.Ordinal);
    private readonly ScaleTransform _graphScaleTransform = new();
    private readonly DispatcherTimer _zoomInputTimer;
    private TaskTrackingCatalog? _catalog;
    private TaskTrackingTask? _selectedTask;
    private TaskTrackingTask? _detailPageTask;
    private readonly Stack<TaskTrackingTask> _detailNavigationHistory = new();
    private int _summaryLoadVersion;
    private int _detailPageLoadVersion;
    private bool _initialized;
    private double _logicalWidth;
    private double _logicalHeight;
    private double _zoom = DefaultZoom;
    private double _pendingZoom = DefaultZoom;
    private Point _pendingZoomAnchor;
    private bool _hasPendingZoom;
    private DispatcherOperation? _zoomScrollOperation;
    private int _zoomScrollVersion;
    private bool _isPanning;
    private Point _panStart;
    private double _panHorizontalOffset;
    private double _panVerticalOffset;
    private double? _overlayLeft;
    private double? _overlayTop;
    private double? _overlayWidth;
    private double? _overlayHeight;
    private bool _isOverlayResizeMode;
    private bool _linkMapTaskPoints;
    private bool _suppressLinkTaskPointChange;
    private bool _suppressAutomaticTaskRecognitionChange;
    private bool _suppressAutoCompletePrerequisitesChange;
    private string _selectedMode = "pve";
    private TaskTrackingOverlayWindow? _taskOverlayWindow;
    private Window? _hostWindow;
    private bool _disposed;

    public event EventHandler? AutomaticTaskRecognitionChanged;
    public event EventHandler? AutoCompletePrerequisitesChanged;
    public event EventHandler? TaskRecognitionRequested;
    public event EventHandler? TaskModeChanged;

    private Dictionary<string, TaskTrackingStatus> CurrentStatuses => _statusesByMode[_selectedMode];

    public TaskTrackingView(IFeatureHost host)
    {
        _host = host;
        TaskTrackingRuntime.Attach(host);
        _trackingTaskIdsByGameId = TaskPrerequisiteCatalogService.LoadGameTaskMap();
        _statusesByMode = TaskTrackingProgressService.LoadByMode();
        InitializeComponent();
        _graphScaleTransform.ScaleX = _zoom;
        _graphScaleTransform.ScaleY = _zoom;
        TaskGraphCanvas.RenderTransform = _graphScaleTransform;
        _zoomInputTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _zoomInputTimer.Tick += ZoomInputTimer_Tick;
        var pinState = TaskTrackingPinService.Load();
        _overlayLeft = pinState.Left;
        _overlayTop = pinState.Top;
        _overlayWidth = pinState.Width;
        _overlayHeight = pinState.Height;
        _linkMapTaskPoints = pinState.LinkMapTaskPoints;
        _suppressLinkTaskPointChange = true;
        LinkTaskPointsCheckBox.IsChecked = _linkMapTaskPoints;
        _suppressLinkTaskPointChange = false;
        TaskDetailView.BackRequested += TaskDetailView_BackRequested;
        TaskDetailView.RelatedTaskRequested += TaskDetailView_RelatedTaskRequested;
        TaskDetailView.AttachHost(host);
        TaskModeComboBox.ItemsSource = TaskModes;
        TaskModeComboBox.SelectedItem = TaskModes.Single(mode => mode.Key == _selectedMode);
    }

    public void EnsureInitialized()
    {
        if (_disposed) return;
        AttachHostWindow();
        if (_initialized) return;
        _initialized = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, InitializeCatalog);
    }

    public bool AutomaticTaskRecognitionEnabled
    {
        get => AutomaticTaskRecognitionCheckBox.IsChecked == true;
        set
        {
            _suppressAutomaticTaskRecognitionChange = true;
            AutomaticTaskRecognitionCheckBox.IsChecked = value;
            _suppressAutomaticTaskRecognitionChange = false;
        }
    }

    public bool AutoCompletePrerequisitesEnabled
    {
        get => AutoCompletePrerequisitesCheckBox.IsChecked == true;
        set
        {
            _suppressAutoCompletePrerequisitesChange = true;
            AutoCompletePrerequisitesCheckBox.IsChecked = value;
            _suppressAutoCompletePrerequisitesChange = false;
        }
    }

    public string SelectedTaskMode => _selectedMode;

    public IReadOnlyDictionary<string, string[]> TrackingTaskIdsByGameId => _trackingTaskIdsByGameId;

    public void SetTaskRecognitionBusy(bool isBusy)
    {
        SyncTaskStatusButton.IsEnabled = !isBusy;
        SyncTaskStatusButton.Content = isBusy ? "同步中…" : "立即同步";
    }

    public int ApplyDetectedTaskStatuses(IReadOnlyList<FeatureDetectedTaskStatusChange> changes)
    {
        if (_disposed) return 0;
        if (changes.Count == 0) return 0;
        changes = changes.Where(change => IsTrackingTaskInSelectedMode(change.TrackingTaskId)).ToArray();
        if (changes.Count == 0) return 0;
        var changed = 0;
        foreach (var change in changes)
        {
            var detectedStatus = change.Status switch
            {
                FeatureTaskTrackingStatus.Accepted => TaskTrackingStatus.Accepted,
                FeatureTaskTrackingStatus.Completed => TaskTrackingStatus.Completed,
                _ => TaskTrackingStatus.NotStarted
            };
            var current = GetStatus(change.TrackingTaskId);
            if (current == detectedStatus) continue;
            if (detectedStatus == TaskTrackingStatus.NotStarted)
                CurrentStatuses.Remove(change.TrackingTaskId);
            else
                CurrentStatuses[change.TrackingTaskId] = detectedStatus;
            changed++;
        }

        changed += CompletePrerequisites(changes
            .Where(change => change.Status is FeatureTaskTrackingStatus.Accepted or FeatureTaskTrackingStatus.Completed)
            .Select(change => change.TrackingTaskId));
        if (changed == 0) return 0;
        TaskTrackingProgressService.SaveByMode(_statusesByMode);
        UpdateStatusButtons();
        UpdateNodeVisuals();
        UpdateProgressText();
        return changed;
    }

    public int ApplyPrerequisiteCompletionToExistingStatuses()
    {
        if (_disposed) return 0;
        if (!AutoCompletePrerequisitesEnabled) return 0;
        var changed = CompletePrerequisites(CurrentStatuses
            .Where(pair => pair.Value is TaskTrackingStatus.Accepted or TaskTrackingStatus.Completed)
            .Select(pair => pair.Key)
            .ToArray());
        if (changed == 0) return 0;
        TaskTrackingProgressService.SaveByMode(_statusesByMode);
        UpdateStatusButtons();
        UpdateNodeVisuals();
        UpdateProgressText();
        return changed;
    }

    private void AutomaticTaskRecognitionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutomaticTaskRecognitionChange) return;
        AutomaticTaskRecognitionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AutoCompletePrerequisitesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoCompletePrerequisitesChange) return;
        AutoCompletePrerequisitesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncTaskStatusButton_Click(object sender, RoutedEventArgs e) =>
        TaskRecognitionRequested?.Invoke(this, EventArgs.Empty);

    private void InitializeCatalog()
    {
        if (_disposed) return;
        var result = TaskTrackingCatalogService.Load();
        if (!result.IsAvailable || result.Catalog is null)
        {
            EmptyText.Text = result.ErrorMessage ?? "任务树不可用。";
            return;
        }

        _catalog = result.Catalog;
        _prerequisiteIdsByTaskId = TaskPrerequisiteCatalogService.Load();
        ApplyPrerequisiteCompletionToExistingStatuses();
        BuildTraderOptions();
        UpdateProgressText();
        RemoveMissingPins();
        UpdatePinnedOverlay();
        ApplyTaskPointLink();
        EmptyText.Visibility = Visibility.Collapsed;
    }

    private void AttachHostWindow()
    {
        if (_hostWindow is not null) return;
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.Closed += HostWindow_Closed;
    }

    private void HostWindow_Closed(object? sender, EventArgs e)
    {
        if (_hostWindow is not null) _hostWindow.Closed -= HostWindow_Closed;
        _hostWindow = null;
        _taskOverlayWindow?.ClosePermanently();
        _taskOverlayWindow = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _summaryLoadVersion++;
        _detailPageLoadVersion++;
        CancelQueuedZoom();
        if (_zoomScrollOperation is not null && _zoomScrollOperation.Status == DispatcherOperationStatus.Pending)
            _zoomScrollOperation.Abort();
        _zoomScrollOperation = null;

        if (_hostWindow is not null)
            _hostWindow.Closed -= HostWindow_Closed;
        _hostWindow = null;

        if (_taskOverlayWindow is not null)
        {
            _taskOverlayWindow.BoundsCommitted -= TaskOverlayWindow_BoundsCommitted;
            _taskOverlayWindow.Closed -= TaskOverlayWindow_Closed;
            _taskOverlayWindow.ClosePermanently();
            _taskOverlayWindow = null;
        }

        TaskDetailView.BackRequested -= TaskDetailView_BackRequested;
        TaskDetailView.RelatedTaskRequested -= TaskDetailView_RelatedTaskRequested;
        TaskDetailView.DetachHost();
        AutomaticTaskRecognitionChanged = null;
        AutoCompletePrerequisitesChanged = null;
        TaskRecognitionRequested = null;
        TaskModeChanged = null;
        return ValueTask.CompletedTask;
    }

    private void BuildTraderOptions()
    {
        if (_catalog is null) return;
        var selectedTraderKey = (TraderComboBox.SelectedItem as TaskTrackingTraderOption)?.Key;
        var byTrader = _catalog.Tasks
            .Where(IsTaskInSelectedMode)
            .GroupBy(TaskSectionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var options = new List<TaskTrackingTraderOption>();
        foreach (var traderKey in TraderOrder)
        {
            if (!byTrader.TryGetValue(traderKey, out var tasks) || tasks.Length == 0) continue;
            var traderName = tasks.FirstOrDefault(task =>
                string.Equals(task.TraderKey, traderKey, StringComparison.OrdinalIgnoreCase))?.TraderName ?? tasks[0].TraderName;
            options.Add(new TaskTrackingTraderOption(traderKey, traderName, tasks.Length));
        }
        foreach (var pair in byTrader.Where(pair => !TraderOrder.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
        {
            var traderName = pair.Value.FirstOrDefault(task =>
                string.Equals(task.TraderKey, pair.Key, StringComparison.OrdinalIgnoreCase))?.TraderName ?? pair.Value[0].TraderName;
            options.Add(new TaskTrackingTraderOption(pair.Key, traderName, pair.Value.Length));
        }

        TraderComboBox.ItemsSource = options;
        TraderComboBox.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.Key, selectedTraderKey, StringComparison.OrdinalIgnoreCase));
        if (TraderComboBox.SelectedItem is null) TraderComboBox.SelectedIndex = 0;
    }

    private void RenderSelectedTraderGraph()
    {
        if (_catalog is null) return;
        var option = TraderComboBox.SelectedItem as TaskTrackingTraderOption;
        if (option is null) return;
        var tasks = _catalog.Tasks
            .Where(IsTaskInSelectedMode)
            .Where(task => string.Equals(TaskSectionKey(task), option.Key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(task => task.GroupOrder)
            .ThenBy(task => task.Sequence)
            .ThenBy(task => task.Name, StringComparer.CurrentCulture)
            .ToArray();

        TaskGraphCanvas.Children.Clear();
        _nodeVisuals.Clear();
        _taskPositions.Clear();
        CancelQueuedZoom();
        _zoomScrollOperation?.Abort();
        _zoomScrollVersion++;
        if (tasks.Length == 0)
        {
            EmptyText.Text = "该商人暂无任务。";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        var layout = TaskGraphLayoutService.Build(tasks, _catalog.Lines);
        foreach (var pair in layout.Positions) _taskPositions[pair.Key] = pair.Value;
        _logicalWidth = layout.Width;
        _logicalHeight = layout.Height;
        TaskGraphCanvas.Width = _logicalWidth;
        TaskGraphCanvas.Height = _logicalHeight;
        AddTraderGraphLines(layout.Segments);
        AddTaskGroupHeaders(layout.Groups);
        foreach (var task in tasks)
            AddTaskNode(task);

        ApplyZoom(_zoom, null);
        UpdateNodeVisuals();
        EmptyText.Visibility = Visibility.Collapsed;
    }

    private void AddTraderGraphLines(IReadOnlyList<TaskGraphLineSegment> segments)
    {
        if (segments.Count == 0) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var segment in segments)
            {
                context.BeginFigure(segment.Start, false, false);
                context.LineTo(segment.End, true, false);
            }
        }
        geometry.Freeze();
        TaskGraphCanvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = GraphLineBrush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Miter,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            IsHitTestVisible = false
        });
    }

    private void AddTaskGroupHeaders(IReadOnlyList<TaskGraphGroupHeader> groups)
    {
        foreach (var group in groups)
        {
            var count = new TextBlock
            {
                Text = $"{group.Count:N0} 项",
                Foreground = (Brush)FindResource("TextFaintBrush"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var title = new TextBlock
            {
                Text = group.Name,
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var grid = new Grid { Margin = new Thickness(12, 0, 12, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(title);
            Grid.SetColumn(count, 1);
            grid.Children.Add(count);
            var header = new Border
            {
                Width = group.Bounds.Width,
                Height = group.Bounds.Height,
                Background = Brush("#17201C"),
                BorderBrush = DefaultBorderBrush,
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(7),
                Child = grid,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(header, group.Bounds.Left);
            Canvas.SetTop(header, group.Bounds.Top);
            TaskGraphCanvas.Children.Add(header);
        }
    }

    private void AddTaskNode(TaskTrackingTask task)
    {
        var statusDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = DefaultDotBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        var title = new TextBlock
        {
            Text = task.Name,
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var subtitle = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(task.MapName)
                ? task.GroupName
                : $"{task.GroupName} · {task.MapName}",
            Foreground = (Brush)FindResource("TextFaintBrush"),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(14, 5, 0, 0)
        };

        var content = new Grid { Margin = new Thickness(11, 7, 11, 7) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(statusDot);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        content.Children.Add(header);
        Grid.SetRow(subtitle, 1);
        content.Children.Add(subtitle);

        var border = new Border
        {
            Width = TaskGraphLayoutService.NodeWidth,
            Height = TaskGraphLayoutService.NodeHeight,
            Background = DefaultNodeBrush,
            BorderBrush = DefaultBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Child = content,
            Tag = task,
            ToolTip = BuildTaskToolTip(task)
        };
        border.MouseLeftButtonUp += TaskNode_MouseLeftButtonUp;
        var position = _taskPositions[task.Id];
        Canvas.SetLeft(border, position.Left);
        Canvas.SetTop(border, position.Top);
        TaskGraphCanvas.Children.Add(border);
        _nodeVisuals[task.Id] = new NodeVisual(task, border, statusDot);
    }

    private void TaskNode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: TaskTrackingTask task }) return;
        e.Handled = true;
        SelectTask(task, center: false);
    }

    private void SelectTask(TaskTrackingTask task, bool center)
    {
        _selectedTask = task;
        DetailTraderText.Text = task.TraderName;
        DetailNameText.Text = task.Name;
        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.GroupName)) detailParts.Add(task.GroupName);
        if (!string.IsNullOrWhiteSpace(task.MapName)) detailParts.Add(task.MapName);
        if (task.Level > 0) detailParts.Add($"需要等级 {task.Level}");
        if (task.Aliases.Count > 0) detailParts.Add($"旧任务名：{string.Join(" / ", task.Aliases)}");
        DetailLevelText.Text = string.Join(" · ", detailParts);
        ObjectiveItems.ItemsSource = task.Objectives.Count > 0 ? task.Objectives : ["正在读取简略任务目标…"];
        NotStartedButton.IsEnabled = AcceptedButton.IsEnabled = CompletedButton.IsEnabled = true;
        OpenWikiButton.IsEnabled = true;
        UpdateStatusButtons();
        UpdatePinButton();
        UpdateNodeVisuals();
        _ = UpdateSelectedTaskSummaryAsync(task, ++_summaryLoadVersion);
        if (center) CenterTask(task);
    }

    private async Task UpdateSelectedTaskSummaryAsync(TaskTrackingTask task, int version)
    {
        var sourceTaskId = DetailSourceTaskId(task);
        if (sourceTaskId.Length == 0)
        {
            if (version == _summaryLoadVersion && _selectedTask?.Id == task.Id)
                ObjectiveItems.ItemsSource = task.Objectives.Count > 0 ? task.Objectives : ["暂无任务目标说明"];
            return;
        }

        var result = await KaedeoriTaskDetailService.GetAsync(sourceTaskId, task.Mode);
        if (version != _summaryLoadVersion || _selectedTask?.Id != task.Id) return;
        ObjectiveItems.ItemsSource = result.Detail?.Objectives.Count > 0
            ? result.Detail.Objectives.Select(objective => objective.Summary).ToArray()
            : task.Objectives.Count > 0
                ? task.Objectives
                : [result.ErrorMessage ?? "暂无任务目标说明"];
    }

    private TaskTrackingStatus GetStatus(string taskId) =>
        CurrentStatuses.TryGetValue(taskId, out var status) ? status : TaskTrackingStatus.NotStarted;

    private void SetSelectedStatus(TaskTrackingStatus status)
    {
        if (_selectedTask is null) return;
        if (status == TaskTrackingStatus.NotStarted)
            CurrentStatuses.Remove(_selectedTask.Id);
        else
            CurrentStatuses[_selectedTask.Id] = status;
        if (status is TaskTrackingStatus.Accepted or TaskTrackingStatus.Completed)
            CompletePrerequisites([_selectedTask.Id]);
        TaskTrackingProgressService.SaveByMode(_statusesByMode);
        UpdateStatusButtons();
        UpdateNodeVisuals();
        UpdateProgressText();
    }

    private int CompletePrerequisites(IEnumerable<string> taskIds)
    {
        if (!AutoCompletePrerequisitesEnabled || _prerequisiteIdsByTaskId.Count == 0) return 0;

        var changed = 0;
        foreach (var prerequisiteId in TaskPrerequisiteCatalogService.Expand(_prerequisiteIdsByTaskId, taskIds))
        {
            if (GetStatus(prerequisiteId) != TaskTrackingStatus.Completed)
            {
                CurrentStatuses[prerequisiteId] = TaskTrackingStatus.Completed;
                changed++;
            }
        }
        return changed;
    }

    private void NotStartedButton_Click(object sender, RoutedEventArgs e) => SetSelectedStatus(TaskTrackingStatus.NotStarted);
    private void AcceptedButton_Click(object sender, RoutedEventArgs e) => SetSelectedStatus(TaskTrackingStatus.Accepted);
    private void CompletedButton_Click(object sender, RoutedEventArgs e) => SetSelectedStatus(TaskTrackingStatus.Completed);

    private void UpdateStatusButtons()
    {
        var status = _selectedTask is null ? TaskTrackingStatus.NotStarted : GetStatus(_selectedTask.Id);
        SetStatusButton(NotStartedButton, status == TaskTrackingStatus.NotStarted, DefaultBorderBrush);
        SetStatusButton(AcceptedButton, status == TaskTrackingStatus.Accepted, AcceptedBorderBrush);
        SetStatusButton(CompletedButton, status == TaskTrackingStatus.Completed, CompletedBorderBrush);
    }

    private static void SetStatusButton(Button button, bool active, Brush accent)
    {
        button.Background = active ? accent : Brushes.Transparent;
        button.BorderBrush = active ? accent : DefaultBorderBrush;
        button.Foreground = active ? Brushes.White : Brush("#96A39D");
    }

    private void UpdateProgressText()
    {
        if (_catalog is null) return;
        var taskIds = _catalog.Tasks.Where(IsTaskInSelectedMode).Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var accepted = CurrentStatuses.Count(pair => taskIds.Contains(pair.Key) && pair.Value == TaskTrackingStatus.Accepted);
        var completed = CurrentStatuses.Count(pair => taskIds.Contains(pair.Key) && pair.Value == TaskTrackingStatus.Completed);
        ProgressText.Text = $"进行中 {accepted:N0} · 已完成 {completed:N0} / {taskIds.Count:N0}";
    }

    private void UpdateNodeVisuals()
    {
        var query = SearchBox.Text.Trim();
        foreach (var visual in _nodeVisuals.Values)
        {
            var searchMatch = string.IsNullOrWhiteSpace(query) || MatchesQuery(visual.Task, query);
            visual.Border.Opacity = searchMatch ? 1 : .12;
            visual.Border.IsHitTestVisible = searchMatch;

            var status = GetStatus(visual.Task.Id);
            visual.Border.Background = status switch
            {
                TaskTrackingStatus.Accepted => AcceptedNodeBrush,
                TaskTrackingStatus.Completed => CompletedNodeBrush,
                _ => DefaultNodeBrush
            };
            visual.Dot.Fill = status switch
            {
                TaskTrackingStatus.Accepted => AcceptedDotBrush,
                TaskTrackingStatus.Completed => CompletedDotBrush,
                _ => DefaultDotBrush
            };
            var selected = _selectedTask?.Id == visual.Task.Id;
            var pinned = _pinnedTaskIds.Contains(visual.Task.Id);
            visual.Border.BorderBrush = selected
                ? SelectedBorderBrush
                : pinned
                    ? PinnedBorderBrush
                : status switch
                {
                    TaskTrackingStatus.Accepted => AcceptedBorderBrush,
                    TaskTrackingStatus.Completed => CompletedBorderBrush,
                    _ => DefaultBorderBrush
                };
            visual.Border.BorderThickness = selected
                ? new Thickness(2)
                : pinned ? new Thickness(1.6) : new Thickness(1);
        }
    }

    private static bool MatchesQuery(TaskTrackingTask task, string query) =>
        task.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        task.Aliases.Any(alias => alias.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
        task.TraderName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        task.Objectives.Any(objective => objective.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private static string BuildTaskToolTip(TaskTrackingTask task)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.GroupName)) lines.Add(task.GroupName);
        if (task.Aliases.Count > 0) lines.Add($"旧任务名：{string.Join(" / ", task.Aliases)}");
        lines.AddRange(task.Objectives);
        return lines.Count > 0 ? string.Join("\n", lines) : task.Name;
    }

    private static string TaskSectionKey(TaskTrackingTask task) =>
        string.IsNullOrWhiteSpace(task.SectionKey) ? task.TraderKey : task.SectionKey;

    private bool IsTaskInSelectedMode(TaskTrackingTask task) =>
        string.Equals(NormalizeMode(task.Mode), _selectedMode, StringComparison.Ordinal);

    public bool IsTrackingTaskInSelectedMode(string trackingTaskId) =>
        _catalog?.Tasks.Any(task => task.Id == trackingTaskId && IsTaskInSelectedMode(task)) == true;

    private static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "pvp" => "pvp",
        "season" => "season",
        _ => "pve"
    };

    private void TaskModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskModeComboBox.SelectedItem is not TaskTrackingModeOption option) return;
        var nextMode = NormalizeMode(option.Key);
        if (nextMode == _selectedMode && _catalog is null) return;
        var changed = nextMode != _selectedMode;
        _selectedMode = nextMode;
        _selectedTask = null;
        _summaryLoadVersion++;
        _detailPageLoadVersion++;
        _detailPageTask = null;
        _detailNavigationHistory.Clear();
        TaskDetailView.Visibility = Visibility.Collapsed;
        if (_catalog is not null)
        {
            BuildTraderOptions();
            ApplyPrerequisiteCompletionToExistingStatuses();
            UpdateProgressText();
            UpdatePinnedOverlay();
            ApplyTaskPointLink();
        }
        if (changed) TaskModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TraderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null) return;
        RenderSelectedTraderGraph();
        var traderKey = (TraderComboBox.SelectedItem as TaskTrackingTraderOption)?.Key ?? "";
        var first = _catalog.Tasks
            .Where(IsTaskInSelectedMode)
            .Where(task => string.Equals(TaskSectionKey(task), traderKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(task => task.GroupOrder)
            .ThenBy(task => task.Sequence)
            .ThenBy(task => task.Name, StringComparer.CurrentCulture)
            .FirstOrDefault();
        if (first is not null) SelectTask(first, center: true);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_catalog is not null) UpdateNodeVisuals();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _catalog is null) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        var traderKey = (TraderComboBox.SelectedItem as TaskTrackingTraderOption)?.Key ?? "";
        var match = _catalog.Tasks.FirstOrDefault(task =>
            IsTaskInSelectedMode(task) &&
            string.Equals(TaskSectionKey(task), traderKey, StringComparison.OrdinalIgnoreCase) &&
            MatchesQuery(task, query));
        if (match is not null) SelectTask(match, center: true);
        e.Handled = true;
    }

    private void CenterTask(TaskTrackingTask task)
    {
        if (!_taskPositions.TryGetValue(task.Id, out var position)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var x = (position.Left + position.Width / 2) * _zoom - TaskGraphViewport.ViewportWidth / 2;
            var y = (position.Top + position.Height / 2) * _zoom - TaskGraphViewport.ViewportHeight / 2;
            TaskGraphViewport.ScrollToHorizontalOffset(Math.Max(0, x));
            TaskGraphViewport.ScrollToVerticalOffset(Math.Max(0, y));
        });
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        CancelQueuedZoom();
        ApplyZoom(_zoom - .12, ViewportCenter());
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        CancelQueuedZoom();
        ApplyZoom(_zoom + .12, ViewportCenter());
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        CancelQueuedZoom();
        ApplyZoom(DefaultZoom, null);
        TaskGraphViewport.ScrollToHorizontalOffset(0);
        TaskGraphViewport.ScrollToVerticalOffset(0);
    }

    private void TaskGraphViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_catalog is null) return;
        var baseZoom = _hasPendingZoom ? _pendingZoom : _zoom;
        _pendingZoom = Math.Clamp(baseZoom * (e.Delta > 0 ? 1.08 : 1 / 1.08), MinimumZoom, MaximumZoom);
        _pendingZoomAnchor = e.GetPosition(TaskGraphViewport);
        _hasPendingZoom = true;
        if (!_zoomInputTimer.IsEnabled) _zoomInputTimer.Start();
        e.Handled = true;
    }

    private void ZoomInputTimer_Tick(object? sender, EventArgs e)
    {
        if (!_hasPendingZoom)
        {
            _zoomInputTimer.Stop();
            return;
        }

        var zoom = _pendingZoom;
        var anchor = _pendingZoomAnchor;
        _hasPendingZoom = false;
        _zoomInputTimer.Stop();
        ApplyZoom(zoom, anchor);
    }

    private void CancelQueuedZoom()
    {
        _zoomInputTimer.Stop();
        _hasPendingZoom = false;
        _pendingZoom = _zoom;
    }

    private Point ViewportCenter() => new(TaskGraphViewport.ViewportWidth / 2, TaskGraphViewport.ViewportHeight / 2);

    private void ApplyZoom(double zoom, Point? anchor)
    {
        if (_logicalWidth <= 0 || _logicalHeight <= 0) return;
        zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        var oldZoom = _zoom;
        var focus = anchor ?? new Point(0, 0);
        var logicalX = (TaskGraphViewport.HorizontalOffset + focus.X) / Math.Max(.01, oldZoom);
        var logicalY = (TaskGraphViewport.VerticalOffset + focus.Y) / Math.Max(.01, oldZoom);
        _zoom = zoom;
        _pendingZoom = zoom;
        _graphScaleTransform.ScaleX = _zoom;
        _graphScaleTransform.ScaleY = _zoom;
        TaskGraphScaleHost.Width = _logicalWidth * _zoom;
        TaskGraphScaleHost.Height = _logicalHeight * _zoom;
        ZoomText.Text = $"{_zoom * 100:0}%";
        if (anchor is not null)
        {
            var horizontalOffset = Math.Max(0, logicalX * _zoom - focus.X);
            var verticalOffset = Math.Max(0, logicalY * _zoom - focus.Y);
            var version = ++_zoomScrollVersion;
            _zoomScrollOperation?.Abort();
            _zoomScrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (version != _zoomScrollVersion) return;
                TaskGraphScaleHost.UpdateLayout();
                TaskGraphViewport.ScrollToHorizontalOffset(horizontalOffset);
                TaskGraphViewport.ScrollToVerticalOffset(verticalOffset);
                _zoomScrollOperation = null;
            });
        }
    }

    private void TaskGraphCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != TaskGraphCanvas) return;
        _isPanning = true;
        _panStart = e.GetPosition(TaskGraphViewport);
        _panHorizontalOffset = TaskGraphViewport.HorizontalOffset;
        _panVerticalOffset = TaskGraphViewport.VerticalOffset;
        TaskGraphCanvas.CaptureMouse();
        TaskGraphCanvas.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void TaskGraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(TaskGraphViewport);
        TaskGraphViewport.ScrollToHorizontalOffset(_panHorizontalOffset - (current.X - _panStart.X));
        TaskGraphViewport.ScrollToVerticalOffset(_panVerticalOffset - (current.Y - _panStart.Y));
    }

    private void TaskGraphCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => StopPanning();
    private void TaskGraphCanvas_LostMouseCapture(object sender, MouseEventArgs e) => StopPanning();

    private void StopPanning()
    {
        if (!_isPanning) return;
        _isPanning = false;
        TaskGraphCanvas.ReleaseMouseCapture();
        TaskGraphCanvas.Cursor = Cursors.Arrow;
    }

    private async void OpenWikiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTask is null) return;
        _detailNavigationHistory.Clear();
        await OpenTaskDetailPageAsync(_selectedTask, addHistory: false);
    }

    private async Task OpenTaskDetailPageAsync(TaskTrackingTask task, bool addHistory)
    {
        if (addHistory && _detailPageTask is not null && _detailPageTask.Id != task.Id)
            _detailNavigationHistory.Push(_detailPageTask);
        _detailPageTask = task;
        TaskDetailView.Visibility = Visibility.Visible;
        TaskDetailView.ShowLoading(task);
        var version = ++_detailPageLoadVersion;
        var sourceTaskId = DetailSourceTaskId(task);
        if (sourceTaskId.Length == 0)
        {
            if (version == _detailPageLoadVersion)
                TaskDetailView.ShowError(task, "该任务缺少可读取的详情标识。");
            return;
        }

        var result = await KaedeoriTaskDetailService.GetAsync(sourceTaskId, task.Mode);
        if (version != _detailPageLoadVersion || _detailPageTask?.Id != task.Id) return;
        if (result.Detail is null)
        {
            TaskDetailView.ShowError(task, result.ErrorMessage ?? "任务详情读取失败。");
            return;
        }
        await TaskDetailView.ShowDetailAsync(task, result.Detail);
    }

    private void TaskDetailView_BackRequested(object? sender, EventArgs e)
    {
        if (_detailNavigationHistory.TryPop(out var previous))
        {
            _ = OpenTaskDetailPageAsync(previous, addHistory: false);
            return;
        }
        _detailPageLoadVersion++;
        _detailPageTask = null;
        TaskDetailView.Visibility = Visibility.Collapsed;
    }

    public bool NavigateBack()
    {
        if (TaskDetailView.Visibility != Visibility.Visible) return false;
        TaskDetailView_BackRequested(this, EventArgs.Empty);
        return true;
    }

    public bool NavigateForward() => false;

    private void TaskDetailView_RelatedTaskRequested(TaskTrackingRelatedTask related)
    {
        if (_catalog is null) return;
        var task = _catalog.Tasks.FirstOrDefault(candidate =>
            IsTaskInSelectedMode(candidate) &&
            string.Equals(candidate.SourceTaskId, related.SourceTaskId, StringComparison.OrdinalIgnoreCase));
        if (task is not null) _ = OpenTaskDetailPageAsync(task, addHistory: true);
    }

    private static string DetailSourceTaskId(TaskTrackingTask task) =>
        !string.IsNullOrWhiteSpace(task.SourceTaskId)
            ? task.SourceTaskId
            : task.GameTaskIds.FirstOrDefault() ?? "";

    private void PinTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTask is null) return;
        if (!_pinnedTaskIds.Add(_selectedTask.Id)) _pinnedTaskIds.Remove(_selectedTask.Id);
        SavePinnedState();
        UpdatePinButton();
        UpdateNodeVisuals();
        UpdatePinnedOverlay();
        ApplyTaskPointLink();
    }

    private void UpdatePinButton()
    {
        var pinned = _selectedTask is not null && _pinnedTaskIds.Contains(_selectedTask.Id);
        PinTaskButton.IsEnabled = _selectedTask is not null;
        PinTaskButton.Content = pinned ? "取消标记" : "标记任务";
        PinTaskButton.Background = pinned ? PinnedButtonBrush : Brushes.Transparent;
        PinTaskButton.BorderBrush = pinned ? PinnedBorderBrush : DefaultBorderBrush;
        PinTaskButton.Foreground = pinned ? Brushes.White : Brush("#96A39D");
    }

    private void RemoveMissingPins()
    {
        if (_catalog is null) return;
        var validIds = _catalog.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var removed = _pinnedTaskIds.RemoveWhere(id => !validIds.Contains(id));
        if (removed > 0) SavePinnedState();
    }

    private void UpdatePinnedOverlay()
    {
        if (_catalog is null) return;
        var tasks = _catalog.Tasks
            .Where(task => IsTaskInSelectedMode(task) && _pinnedTaskIds.Contains(task.Id))
            .OrderBy(task => Array.IndexOf(TraderOrder, task.TraderKey))
            .ThenBy(task => task.GroupOrder)
            .ThenBy(task => task.Sequence)
            .ToArray();
        if (tasks.Length == 0)
        {
            _isOverlayResizeMode = false;
            _taskOverlayWindow?.SetEditMode(false);
            _taskOverlayWindow?.Hide();
            UpdateOverlayResizeButton();
            return;
        }

        if (_taskOverlayWindow is null)
        {
            _taskOverlayWindow = new TaskTrackingOverlayWindow(
                _overlayLeft,
                _overlayTop,
                _overlayWidth,
                _overlayHeight);
            _taskOverlayWindow.BoundsCommitted += TaskOverlayWindow_BoundsCommitted;
            _taskOverlayWindow.Closed += TaskOverlayWindow_Closed;
        }
        _taskOverlayWindow.UpdateTasks(tasks);
        _taskOverlayWindow.SetEditMode(_isOverlayResizeMode);
        if (!_taskOverlayWindow.IsVisible) _taskOverlayWindow.Show();
        _taskOverlayWindow.Topmost = true;
        UpdateOverlayResizeButton();
    }

    private void TaskOverlayWindow_BoundsCommitted(object? sender, EventArgs e)
    {
        if (_taskOverlayWindow is null) return;
        _overlayLeft = _taskOverlayWindow.Left;
        _overlayTop = _taskOverlayWindow.Top;
        _overlayWidth = _taskOverlayWindow.PersistedWidth;
        _overlayHeight = _taskOverlayWindow.PersistedHeight;
        SavePinnedState();
    }

    private void TaskOverlayWindow_Closed(object? sender, EventArgs e)
    {
        if (_taskOverlayWindow is not null)
        {
            _taskOverlayWindow.BoundsCommitted -= TaskOverlayWindow_BoundsCommitted;
            _taskOverlayWindow.Closed -= TaskOverlayWindow_Closed;
        }
        _taskOverlayWindow = null;
        _isOverlayResizeMode = false;
        UpdateOverlayResizeButton();
    }

    private void OverlayResizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPinnedTasksInSelectedMode()) return;
        UpdatePinnedOverlay();
        if (_taskOverlayWindow is null) return;
        _isOverlayResizeMode = !_isOverlayResizeMode;
        _taskOverlayWindow.SetEditMode(_isOverlayResizeMode);
        if (!_isOverlayResizeMode) TaskOverlayWindow_BoundsCommitted(_taskOverlayWindow, EventArgs.Empty);
        UpdateOverlayResizeButton();
    }

    private void UpdateOverlayResizeButton()
    {
        var available = HasPinnedTasksInSelectedMode();
        OverlayResizeButton.IsEnabled = available;
        OverlayResizeButton.Content = _isOverlayResizeMode ? "完成调整" : "调整框体";
        OverlayResizeButton.Background = _isOverlayResizeMode ? PinnedButtonBrush : Brushes.Transparent;
        OverlayResizeButton.BorderBrush = _isOverlayResizeMode ? SelectedBorderBrush : DefaultBorderBrush;
        OverlayResizeButton.Foreground = _isOverlayResizeMode ? Brushes.White : Brush("#96A39D");
    }

    private void LinkTaskPointsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressLinkTaskPointChange) return;
        _linkMapTaskPoints = LinkTaskPointsCheckBox.IsChecked == true;
        SavePinnedState();
        ApplyTaskPointLink();
    }

    private void ApplyTaskPointLink()
    {
        if (_catalog is null) return;
        var taskNames = _catalog.Tasks
            .Where(task => IsTaskInSelectedMode(task) && _pinnedTaskIds.Contains(task.Id))
            .Select(task => task.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _host.ApplyTaskTrackingPinSelection(_linkMapTaskPoints, taskNames);
    }

    private bool HasPinnedTasksInSelectedMode() =>
        _catalog?.Tasks.Any(task => IsTaskInSelectedMode(task) && _pinnedTaskIds.Contains(task.Id)) == true;

    private void SavePinnedState() => TaskTrackingPinService.Save(
        _overlayLeft,
        _overlayTop,
        _overlayWidth,
        _overlayHeight,
        _linkMapTaskPoints);

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed record NodeVisual(TaskTrackingTask Task, Border Border, Ellipse Dot);
}
