using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinShikiPlayer.Controls;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private TrailerPanel? _detailTrailers;
    private async Task AddDetailExtrasAsync(Anime anime, StackPanel destination, CancellationToken ct)
    {
        _detailTrailers = null;
        var screenshotsTask = _services.Shikimori.ScreenshotsAsync(anime.Id, ct);
        var videosTask = _services.Shikimori.VideosAsync(anime.Id, ct);
        var relatedTask = _services.Shikimori.RelatedAsync(anime.Id, ct);
        var similarTask = _services.Shikimori.SimilarAsync(anime.Id, ct);
        try
        {
            var screenshots = await screenshotsTask; ct.ThrowIfCancellationRequested();
            if (screenshots.Count > 0)
            {
                destination.Children.Add(Heading("Скриншоты"));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                foreach (var screenshot in screenshots)
                {
                    if (RemoteUri(screenshot.Preview ?? screenshot.Original) is not { } uri) continue;
                    var button = new Button { Padding = new(0), CornerRadius = new(8), Content = new Image { Source = new BitmapImage(uri), Width = 230, Height = 135, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill } };
                    button.Click += async (_, _) => await RunAsync(async () =>
                    {
                        if (_dialogOpen || RemoteUri(screenshot.Original) is not { } full) return;
                        _dialogOpen = true;
                        try { await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = anime.Title,
                            Content = new Image { Source = new BitmapImage(full), Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxHeight = 550 }, CloseButtonText = "Закрыть" }.ShowAsync(); }
                        finally { _dialogOpen = false; }
                    });
                    row.Children.Add(button);
                }
                destination.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText.Text = "Скриншоты недоступны"; }
        try
        {
            var videos = await videosTask; ct.ThrowIfCancellationRequested();
            var trailers = TrailerCatalog.Select(videos);
            if (trailers.Count > 0)
            {
                var section = new StackPanel { Spacing = 12 };
                var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                header.Children.Add(Heading("Трейлеры и ролики"));
                if (trailers.Count > 1) { var count = new TextBlock { Text = $"{trailers.Count} шт.", FontSize = 11, Foreground = Resource<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(count, 1); header.Children.Add(count); }
                section.Children.Add(header); _detailTrailers = new TrailerPanel();
                foreach (var trailer in trailers) _detailTrailers.Children.Add(CreateTrailerCard(trailer));
                section.Children.Add(_detailTrailers); destination.Children.Add(section);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText.Text = "Видео недоступны"; }
        try { var related = await relatedTask; ct.ThrowIfCancellationRequested(); await AddAnimeSectionAsync(destination, "Связанное", related.Where(x => x.Anime is not null).Select(x => x.Anime!), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText.Text = "Связанные тайтлы недоступны"; }
        try { var similar = await similarTask; ct.ThrowIfCancellationRequested(); await AddAnimeSectionAsync(destination, "Похожее", similar, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText.Text = "Похожие тайтлы недоступны"; }
    }

    private Button CreateTrailerCard(TrailerCard trailer)
    {
        var content = new Grid { Background = Resource<Brush>("SurfaceBrush"), CornerRadius = new(8) };
        if (trailer.PreviewUri is { } preview)
        {
            var image = new Image { Source = new BitmapImage(preview), Stretch = Stretch.UniformToFill };
            image.ImageFailed += (_, _) => image.Source = null; content.Children.Add(image);
        }
        content.Children.Add(new Border { Width = 42, Height = 42, CornerRadius = new(21), Background = Resource<Brush>("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = "\uE768", FontSize = 22, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } });
        var caption = new StackPanel { Spacing = 2, Padding = new(10), VerticalAlignment = VerticalAlignment.Bottom };
        var fade = new LinearGradientBrush { StartPoint = new(0, 1), EndPoint = new(0, 0) }; fade.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(180, 0, 0, 0), Offset = 0 }); fade.GradientStops.Add(new() { Color = Microsoft.UI.Colors.Transparent, Offset = 1 }); caption.Background = fade;
        caption.Children.Add(new TextBlock { Text = trailer.KindLabel, FontFamily = new FontFamily("Consolas"), FontSize = 9, CharacterSpacing = 155, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(204, 255, 255, 255)) });
        if (!string.IsNullOrWhiteSpace(trailer.Video.Name)) caption.Children.Add(new TextBlock { Text = trailer.Video.Name, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
        content.Children.Add(caption);
        var button = new Button { Content = content, Padding = new(0), CornerRadius = new(8), BorderThickness = new(0.5), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var title = string.IsNullOrWhiteSpace(trailer.Video.Name) ? trailer.KindLabel : trailer.Video.Name;
        AutomationProperties.SetName(button, title); ToolTipService.SetToolTip(button, title);
        button.Click += (_, _) => OpenBrowser(trailer.NavigateUri); return button;
    }
}

