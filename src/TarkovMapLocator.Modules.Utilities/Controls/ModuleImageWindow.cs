using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Utilities.Controls;

internal sealed class ModuleImageWindow : Window
{
    private readonly IFeatureHost _host;
    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly string _imageId;
    private readonly string _imageUrl;
    private bool _loading;

    public ModuleImageWindow(IFeatureHost host, string title, string imageId, string imageUrl)
    {
        _host = host;
        _imageId = imageId;
        _imageUrl = imageUrl;
        Title = $"{title} - 位置截图";
        Width = 1100;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 11, 10));

        _image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(14), Visibility = Visibility.Collapsed };
        _status = new TextBlock
        {
            Text = "位置截图加载中…",
            Foreground = new SolidColorBrush(Color.FromRgb(155, 173, 163)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        var grid = new Grid();
        grid.Children.Add(_status);
        grid.Children.Add(_image);
        Content = grid;

        Loaded += async (_, _) => await LoadImageAsync();
        _status.MouseLeftButtonUp += async (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            await LoadImageAsync();
        };
        Closed += (_, _) => _image.Source = null;
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Escape) return;
            eventArgs.Handled = true;
            Close();
        };
    }

    private async Task LoadImageAsync()
    {
        if (_loading) return;
        _loading = true;
        _status.Text = "位置截图加载中…";
        _status.Visibility = Visibility.Visible;
        ImageSource? source = null;
        try
        {
            var retryDelays = new[] { 350, 1000 };
            for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                source = await _host.LoadImageAsync(_imageId, _imageUrl, fullSize: true);
                if (!IsLoaded) return;
                if (source is not null) break;
                if (attempt >= retryDelays.Length) continue;
                _status.Text = "位置截图正在重试…";
                await Task.Delay(retryDelays[attempt]);
                if (!IsLoaded) return;
            }
        }
        finally
        {
            _loading = false;
        }

        if (source is null)
        {
            _status.Text = "位置截图加载失败，点击重试";
            return;
        }

        _image.Source = source;
        _image.Visibility = Visibility.Visible;
        _status.Visibility = Visibility.Collapsed;
    }
}
