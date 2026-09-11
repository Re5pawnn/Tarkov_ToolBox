using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TarkovMapLocatorDesktop.Windows;

public enum MainWindowCloseAction
{
    None,
    Minimize,
    Exit
}

/// <summary>
/// Small explicit close chooser used for the normal Windows title-bar close
/// button. A dedicated window keeps the one-time choice explicit; the same
/// preference is always visible and editable from the Settings page.
/// </summary>
public sealed class MainWindowClosePromptWindow : Window
{
    private readonly CheckBox _dontAskAgainCheckBox;

    public MainWindowCloseAction SelectedAction { get; private set; }

    public bool DontAskAgain => _dontAskAgainCheckBox.IsChecked == true;

    public MainWindowClosePromptWindow()
    {
        Title = "关闭工具";
        Width = 438;
        Height = 246;
        MinWidth = Width;
        MaxWidth = Width;
        MinHeight = Height;
        MaxHeight = Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brush("#101514");
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(22, 19, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "要关闭工具，还是最小化到任务栏？",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });

        var description = new TextBlock
        {
            Text = "最小化后，悬浮地图和窗口化 OCR 调试框会继续保持显示。",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 11,
            Foreground = Brush("#91A89D"),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        _dontAskAgainCheckBox = new CheckBox
        {
            Content = "以后不再提示",
            Margin = new Thickness(0, 17, 0, 0),
            Foreground = Brush("#D8E5DE"),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(_dontAskAgainCheckBox, 2);
        root.Children.Add(_dontAskAgainCheckBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelButton = CreateButton("取消", "#151C19", "#405047");
        cancelButton.IsCancel = true;
        cancelButton.Click += (_, _) => Close();
        var minimizeButton = CreateButton("最小化", "#18231F", "#4C665B");
        minimizeButton.Margin = new Thickness(8, 0, 0, 0);
        minimizeButton.Click += (_, _) => Choose(MainWindowCloseAction.Minimize);
        var exitButton = CreateButton("关闭工具", "#3B241F", "#885047");
        exitButton.Margin = new Thickness(8, 0, 0, 0);
        exitButton.Click += (_, _) => Choose(MainWindowCloseAction.Exit);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(minimizeButton);
        buttons.Children.Add(exitButton);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
    }

    private void Choose(MainWindowCloseAction action)
    {
        SelectedAction = action;
        DialogResult = true;
    }

    private static Button CreateButton(string content, string background, string borderBrush) => new()
    {
        Content = content,
        MinWidth = 82,
        Padding = new Thickness(12, 6, 12, 6),
        Background = Brush(background),
        Foreground = Brushes.White,
        BorderBrush = Brush(borderBrush),
        BorderThickness = new Thickness(1),
        FontSize = 11,
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
