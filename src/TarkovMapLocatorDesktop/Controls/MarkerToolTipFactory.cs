using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TarkovMapLocatorDesktop.Models;
using TarkovMapLocatorDesktop.Windows;

namespace TarkovMapLocatorDesktop.Controls;

public static class MarkerToolTipFactory
{
    public static void Attach(FrameworkElement target, MapMarker marker)
    {
        if (marker.Type is not ("extract" or "transit" or "key-room" or "switch" or "season-document") || string.IsNullOrWhiteSpace(marker.ToolTipText))
        {
            target.ToolTip = marker.ToolTipText ?? marker.Label;
            return;
        }

        var accent = marker.Type switch
        {
            "transit" => FindBrush("AmberBrush", Brushes.White),
            "key-room" => FindBrush("KeyRoomBrush", Brushes.White),
            "switch" => FindBrush("SwitchBrush", Brushes.White),
            "season-document" => FindBrush("SeasonDocumentBrush", Brushes.White),
            _ => FindBrush("GreenBrush", Brushes.White)
        };
        var text = FindBrush("TextBrush", Brushes.White);
        var dim = FindBrush("TextDimBrush", Brushes.LightGray);
        var line = FindBrush("LineBrightBrush", Brushes.DimGray);
        var typeLabel = marker.Type switch
        {
            "transit" => "转移点",
            "key-room" => "钥匙房",
            "switch" => "拉闸点",
            "season-document" => "赛季文件",
            _ => "撤离点"
        };

        var title = new TextBlock
        {
            Text = marker.Label,
            Foreground = text,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 245,
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new Border
        {
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(6, 2, 6, 2),
            Background = WithAlpha(accent, 30),
            BorderBrush = WithAlpha(accent, 105),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = typeLabel,
                Foreground = accent,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            }
        };
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);
        header.Children.Add(title);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            Background = WithAlpha(line, 150)
        });
        content.Children.Add(new TextBlock
        {
            Text = marker.ToolTipText,
            Foreground = dim,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 11,
            LineHeight = 18,
            MaxWidth = 310,
            TextWrapping = TextWrapping.Wrap
        });

        AsyncSeasonDocumentImage? preview = null;
        if (marker.Type == "season-document" &&
            !string.IsNullOrWhiteSpace(marker.PreviewImageId) &&
            !string.IsNullOrWhiteSpace(marker.PreviewImageUrl))
        {
            preview = new AsyncSeasonDocumentImage(marker.PreviewImageId, marker.PreviewImageUrl);
            content.Children.Add(preview);
        }

        var card = new Border
        {
            MinWidth = 220,
            MaxWidth = 340,
            Padding = new Thickness(11, 10, 11, 10),
            Background = new SolidColorBrush(Color.FromArgb(250, 14, 18, 17)),
            BorderBrush = WithAlpha(accent, 135),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = .46
            },
            Child = content
        };

        if (preview is not null)
        {
            AttachInteractivePreview(target, marker, card, preview);
            return;
        }

        var translate = new TranslateTransform(0, 4);
        var toolTip = new ToolTip
        {
            Content = card,
            Placement = PlacementMode.MousePoint,
            HorizontalOffset = 13,
            VerticalOffset = 13,
            Style = Application.Current.TryFindResource("MapMarkerToolTipStyle") as Style,
            RenderTransform = translate,
            RenderTransformOrigin = new Point(.5, 0)
        };
        toolTip.Opened += (_, _) =>
        {
            toolTip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(115))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(135))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };

        target.ToolTip = toolTip;
        ToolTipService.SetInitialShowDelay(target, 180);
        ToolTipService.SetBetweenShowDelay(target, 40);
        ToolTipService.SetShowDuration(target, 60_000);
    }

    private static void AttachInteractivePreview(
        FrameworkElement target,
        MapMarker marker,
        Border card,
        AsyncSeasonDocumentImage preview)
    {
        var translate = new TranslateTransform(0, 4);
        card.RenderTransform = translate;
        card.RenderTransformOrigin = new Point(.5, 0);
        var popup = new Popup
        {
            Child = card,
            PlacementTarget = target,
            Placement = PlacementMode.MousePoint,
            HorizontalOffset = 13,
            VerticalOffset = 13,
            AllowsTransparency = true,
            StaysOpen = true
        };
        var closeTimer = new DispatcherTimer(DispatcherPriority.Input, target.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(360)
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            if (target.IsMouseOver || card.IsMouseOver) return;
            popup.IsOpen = false;
        };

        void CancelClose() => closeTimer.Stop();
        void ScheduleClose()
        {
            closeTimer.Stop();
            closeTimer.Start();
        }
        void OpenPopup()
        {
            CancelClose();
            if (popup.IsOpen) return;
            card.Opacity = 0;
            translate.Y = 4;
            popup.IsOpen = true;
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(115))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(135))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        target.MouseEnter += (_, _) => OpenPopup();
        target.MouseLeave += (_, _) => ScheduleClose();
        card.MouseEnter += (_, _) => CancelClose();
        card.MouseLeave += (_, _) => ScheduleClose();
        target.Unloaded += (_, _) =>
        {
            closeTimer.Stop();
            popup.IsOpen = false;
        };
        preview.PreviewClicked += (_, _) =>
        {
            popup.IsOpen = false;
            var window = new SeasonDocumentImageWindow(marker.Label, marker.PreviewImageId!, marker.PreviewImageUrl!);
            if (Window.GetWindow(target) is { } owner) window.Owner = owner;
            window.Show();
            window.Activate();
        };
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;

    private static SolidColorBrush WithAlpha(Brush brush, byte alpha)
    {
        var color = brush is SolidColorBrush solid ? solid.Color : Colors.White;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
