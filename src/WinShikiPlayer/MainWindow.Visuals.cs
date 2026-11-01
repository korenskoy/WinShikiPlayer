using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private readonly UISettings _systemAppearance = new();
    private static T Resource<T>(string key) => App.Resource<T>(key);
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Nav is null) return;
        foreach (var button in Nav.Children.OfType<Button>())
            if (button.Content is StackPanel panel)
                foreach (var text in panel.Children.OfType<TextBlock>()) text.Visibility = e.NewSize.Width < 1140 ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Width = e.NewSize.Width < 1000 ? 190 : 230;
        HeroTitle.FontSize = e.NewSize.Width < 1000 ? 34 : 42;
    }
    private void Browse_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CatalogTools is null) return;
        var inline = e.NewSize.Width >= 900;
        Grid.SetRow(CatalogTools, inline ? 0 : 1); Grid.SetColumn(CatalogTools, inline ? 1 : 0); Grid.SetColumnSpan(CatalogTools, inline ? 1 : 2);
        BrowseHeading.RowSpacing = inline ? 0 : 14;
        CatalogTools.Margin = inline ? new(0, 0, 44, 0) : new(0);
        ConfigurePosterGrid(AnimeGrid);
    }
    private void PosterGrid_SizeChanged(object sender, SizeChangedEventArgs e) { if (sender is GridView grid) ConfigurePosterGrid(grid); }
    private void ConfigurePosterGrid(GridView grid)
    {
        if (grid.ItemsPanelRoot is not ItemsWrapGrid panel || grid.ActualWidth <= 0) return;
        var dense = grid == AnimeGrid && _section is "catalog" or "library" && _services.Settings.CatalogView == "dense";
        var minimum = dense ? 120 : 180;
        var available = Math.Max(1, grid.ActualWidth - 20);
        var columns = Math.Max(1, (int)((available + 14) / (minimum + 14)));
        var width = Math.Floor(available / columns);
        panel.MaximumRowsOrColumns = columns;
        if (Math.Abs(panel.ItemWidth - width) > 0.5 || double.IsNaN(panel.ItemWidth)) panel.ItemWidth = width;
        var height = (width - 14) * 1.5 + (dense ? 66 : 91);
        if (Math.Abs(panel.ItemHeight - height) > 0.5 || double.IsNaN(panel.ItemHeight)) panel.ItemHeight = height;
    }
    private void ApplyDesignTheme()
    {
        var system = _systemAppearance.GetColorValue(UIColorType.Background);
        var palette = AppPalette.Resolve(_services.Settings.Theme, system.R + system.G + system.B < 384);
        Root.RequestedTheme = palette.Dark ? ElementTheme.Dark : ElementTheme.Light;
        static Windows.UI.Color Color(uint rgb, byte alpha = 255) => Windows.UI.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        void Brush(string name, uint rgb, byte alpha = 255) => Resource<SolidColorBrush>(name).Color = Color(rgb, alpha);
        Brush("CanvasBrush", palette.Background); Brush("SurfaceBrush", palette.Card); Brush("TextBrush", palette.Foreground);
        HeroShadeStart.Color = Color(palette.Background);
        HeroShadeMiddle.Color = Color(palette.Background, 237);
        HeroShadeEnd.Color = Color(palette.Background, 32);
        Brush("RaisedBrush", palette.Dark ? (palette.Id == "plum" ? 0x201736u : 0x1D1A22u) : (palette.Id == "slate" ? 0xDDD3EAu : 0xE7E4DCu));
        Brush("SecondaryBrush", palette.Foreground, 184); Brush("MutedBrush", palette.Foreground, 122);
        Brush("LineBrush", palette.Foreground, palette.Dark ? (byte)26 : (byte)32);
        Brush("AccentBrush", palette.Accent); Brush("AccentInkBrush", palette.Dark ? 0x0B0A0Du : 0xFFFFFFu);
        Brush("ScoreBrush", palette.Dark ? 0xFFC857u : 0xB27B15u);
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed", "SliderThumbBackground", "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundFillChecked", "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed", "ComboBoxFocusedBorderBrush" })
        { Root.Resources[key] = Resource<Brush>("AccentBrush"); Application.Current.Resources[key] = Resource<Brush>("AccentBrush"); }
        Root.Resources["TextFillColorPrimaryBrush"] = Resource<Brush>("TextBrush");
        Root.Resources["TextFillColorSecondaryBrush"] = Resource<Brush>("SecondaryBrush");
        Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] = Resource<Brush>("SurfaceBrush");
        Application.Current.Resources["ContentDialogBackground"] = Resource<Brush>("SurfaceBrush");
        AppWindow.TitleBar.ButtonForegroundColor = palette.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Color(palette.Foreground, 122);
        AppWindow.TitleBar.BackgroundColor = Color(palette.Card); AppWindow.TitleBar.InactiveBackgroundColor = Color(palette.Card);
        RefreshCatalogTools();
    }
    private void SystemAppearance_Changed(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (_ready && _services.Settings.Theme is "System" or "auto.otaku" or "auto.plum") ApplyTheme(); });
    private void UpdateNavigationStyle(bool detail = false)
    {
        foreach (var button in Nav.Children.OfType<Button>())
        {
            var active = !detail && Equals(button.Tag, _section);
            button.Background = active ? Resource<Brush>("RaisedBrush") : new SolidColorBrush(Colors.Transparent);
            button.Foreground = Resource<Brush>(active ? "TextBrush" : "SecondaryBrush");
        }
    }
    private List<AnimeCard> RenderCalendar(List<CalendarEntry> entries)
    {
        var today = DateTimeOffset.Now.Date; List<AnimeCard> cards = [];
        var groups = entries.Where(x => x.NextEpisodeAt is { } date && date.LocalDateTime.Date >= today && date.LocalDateTime.Date < today.AddDays(7))
            .OrderBy(x => x.NextEpisodeAt).GroupBy(x => x.NextEpisodeAt!.Value.LocalDateTime.Date);
        foreach (var day in groups)
        {
            var section = new StackPanel { Spacing = 12, Margin = new(0, 12, 0, 6) };
            var kicker = day.Key == today ? "СЕГОДНЯ" : day.Key == today.AddDays(1) ? "ЗАВТРА" : null;
            if (kicker is not null) section.Children.Add(new TextBlock { Text = kicker, Style = Resource<Style>("SectionLabel"), Foreground = Resource<Brush>("AccentBrush") });
            section.Children.Add(Heading(day.Key.ToString("dddd, d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))));
            var dayCards = day.Select(x => new AnimeCard(x.Anime, _services.Settings.ShikimoriHost, $"{x.NextEpisodeAt!.Value.ToLocalTime():HH:mm}", $"ЭП. {x.NextEpisode}")).ToList(); cards.AddRange(dayCards);
            var grid = new GridView { ItemsSource = dayCards, ItemTemplate = Resource<DataTemplate>("ScheduleTemplate"), ItemContainerStyle = Resource<Style>("PosterItem"), IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
            grid.ItemClick += Anime_Click; grid.Loaded += (_, _) => ConfigurePosterGrid(grid); grid.SizeChanged += PosterGrid_SizeChanged; section.Children.Add(grid); ExtraHomeSections.Children.Add(section);
        }
        ExtraHomeSections.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed; return cards;
    }
    private async void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_featured is null) return;
        await RunAsync(async () => { var detail = await _services.Shikimori.AnimeAsync(_featured.Id); await StartPlaybackAsync(detail, null); });
    }
}
