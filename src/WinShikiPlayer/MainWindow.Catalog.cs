using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, List<CheckBox>> _facets = [];
    private CatalogFilters _filters = new();
    private string? _filtersScope;
    private bool _buildingFilters;
    private TextBox _seasonFilter = null!;
    private NumberBox _scoreFilter = null!;
    private CheckBox _excludeCensored = null!, _licensed = null!, _ascending = null!;
    private CancellationTokenSource? _filterChange;
    private int _libraryCount;

    private async Task EnsureFiltersAsync(CancellationToken ct)
    {
        if (_section is not ("catalog" or "library") || _filtersScope == _section) return;
        var scope = _section;
        var filters = await _services.Store.ReadAsync<CatalogFilters>("filters-" + scope) ?? new();
        var genres = await _services.Cache.GetAsync(_services.CacheKey("genres"), () => _services.Shikimori.GenresAsync(ct), TimeSpan.FromDays(7));
        var studios = await _services.Cache.GetAsync(_services.CacheKey("studios"), () => _services.Shikimori.StudiosAsync(ct), TimeSpan.FromDays(7));
        ct.ThrowIfCancellationRequested();
        _buildingFilters = true;
        try
        {
            _filters = filters; _filtersScope = scope; FilterContent.Children.Clear(); _facets.Clear();
            Facet("Жанры", "genre", genres.OrderBy(x => x.Russian ?? x.Name).Select(x => (x.Id.ToString(), x.Russian ?? x.Name)), filters.Genres.Select(x => x.ToString()), expanded: false);
            var seasons = filters.Season.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var years = Enumerable.Range(DateTime.Now.Year - 6, 7).Reverse().Select(x => (x.ToString(), x.ToString())).ToArray();
            (string, string)[] eras = [("2023_2024", "2023–2024"), ("2018_2022", "2018–2022"), ("2010_2017", "2010–2017"), ("2000_2010", "2000–2010"), ("1990_1999", "1990-е годы"), ("1980_1989", "1980-е годы"), ("1900_1979", "Более старые")];
            Facet("Год", "year", years, seasons);
            Facet("Раньше", "era", eras, seasons);
            Facet("Статус", "status", [("anons", "Анонсировано"), ("ongoing", "Сейчас выходит"), ("released", "Вышедшее")], filters.Statuses);
            Facet("Тип", "kind", [("tv", "TV Сериал"), ("movie", "Фильм"), ("ova", "OVA"), ("ona", "ONA"), ("special", "Спецвыпуск"), ("tv_13", "TV · до 13 серий"), ("tv_24", "TV · 14–24 серии"), ("tv_48", "TV · 25+ серий"), ("tv_special", "TV-спецвыпуск"), ("music", "Музыкальное"), ("pv", "Проморолик"), ("cm", "Реклама")], filters.Kinds);
            Facet("Мой список", "mylist", new[] { "watching", "planned", "completed", "on_hold", "dropped", "rewatching" }.Select(x => (x, StatusLabel(x))), filters.MyList, expanded: false);
            var advanced = new StackPanel { Spacing = 16 };
            _seasonFilter = new TextBox { Header = "Другие годы и сезоны", Text = string.Join(',', seasons.Except(years.Select(x => x.Item1)).Except(eras.Select(x => x.Item1))), PlaceholderText = "fall_2026, 2020_2024" };
            _seasonFilter.LostFocus += FilterValue_Changed; advanced.Children.Add(_seasonFilter);
            Facet("Возрастной рейтинг", "rating", [("g", "G · все возрасты"), ("pg", "PG · дети"), ("pg_13", "PG-13 · 13+"), ("r", "R · 17+"), ("r_plus", "R+"), ("rx", "Rx")], filters.Ratings, advanced, false);
            Facet("Длительность", "duration", [("S", "До 10 минут"), ("D", "10–30 минут"), ("F", "Больше 30 минут")], filters.Durations, advanced, false);
            if (scope == "catalog") Facet("Первоисточник", "origin", new[] { "original", "manga", "web_manga", "four_koma_manga", "novel", "web_novel", "visual_novel", "light_novel", "game", "card_game", "music", "radio", "book", "picture_book", "mixed_media", "other", "unknown" }.Select(x => (x, x)), filters.Origins, advanced, false);
            Facet("Студии", "studio", studios.OrderBy(x => x.Name).Select(x => (x.Id.ToString(), x.Name)), filters.Studios.Select(x => x.ToString()), advanced, false);
            _scoreFilter = new NumberBox { Header = "Минимальная оценка", Minimum = 0, Maximum = 10, Value = filters.MinimumScore, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            _scoreFilter.LostFocus += FilterValue_Changed;
            _excludeCensored = new CheckBox { Content = "Исключить Rx", IsChecked = filters.ExcludeCensored, FontSize = 12 };
            _licensed = new CheckBox { Content = "Только лицензированное", IsChecked = filters.LicensedOnly, FontSize = 12 };
            _ascending = new CheckBox { Content = "Обратный порядок", IsChecked = filters.Ascending, FontSize = 12 };
            foreach (var box in new[] { _excludeCensored, _licensed, _ascending }) { box.Checked += FilterValue_Changed; box.Unchecked += FilterValue_Changed; }
            foreach (var control in new UIElement[] { _scoreFilter, _excludeCensored, _licensed, _ascending }) advanced.Children.Add(control);
            FilterContent.Children.Add(SidebarSection("Дополнительно", advanced, false));
            var reset = new Button { Content = "Очистить всё", Style = Resource<Style>("NavButton"), FontSize = 11, Foreground = Resource<Brush>("MutedBrush"), Padding = new(0, 6, 0, 6) };
            reset.Click += ResetFilters_Click; FilterContent.Children.Add(reset);
            foreach (var option in SortOrder.Items.Cast<ComboBoxItem>()) option.Visibility = scope == "library" && option.Tag is "popularity" or "ranked_random" ? Visibility.Collapsed : Visibility.Visible;
            var order = scope == "library" && filters.Order is "popularity" or "ranked_random" ? "rate_updated" : filters.Order;
            SortOrder.SelectedItem = SortOrder.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, order)) ?? SortOrder.Items[0];
            _filters = filters with { Order = order };
            ViewMode.SelectedItem = ViewMode.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, _services.Settings.CatalogView)) ?? ViewMode.Items[0];
        }
        finally { _buildingFilters = false; }
        RefreshCatalogTools(); UpdateActiveFilters();
    }
    private UIElement SidebarSection(string title, UIElement body, bool expanded)
    {
        var stack = new StackPanel { Spacing = 10 };
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = Resource<Style>("SectionLabel") });
        var arrow = new FontIcon { Glyph = expanded ? "\uE70D" : "\uE76C", FontSize = 9, Foreground = Resource<Brush>("MutedBrush") }; Grid.SetColumn(arrow, 1); header.Children.Add(arrow);
        var button = new Button { Content = header, Style = Resource<Style>("NavButton"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(0), MinHeight = 18 };
        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        button.Click += (_, _) => { var open = body.Visibility != Visibility.Visible; body.Visibility = open ? Visibility.Visible : Visibility.Collapsed; arrow.Glyph = open ? "\uE70D" : "\uE76C"; };
        stack.Children.Add(button); stack.Children.Add(body); return stack;
    }
    private void Facet(string title, string key, IEnumerable<(string Key, string Label)> options, IEnumerable<string> selected, StackPanel? parent = null, bool expanded = true)
    {
        var chosen = selected.ToHashSet(); var rows = new StackPanel { Spacing = 0 }; List<CheckBox> boxes = [];
        foreach (var item in options)
        {
            var box = new CheckBox { Content = item.Label, Tag = item.Key, IsChecked = chosen.Contains(item.Key), Style = Resource<Style>("FilterCheck") };
            box.Checked += FilterValue_Changed; box.Unchecked += FilterValue_Changed; boxes.Add(box); rows.Children.Add(box);
        }
        _facets[key] = boxes;
        UIElement content = rows;
        if (boxes.Count > 16) content = new ScrollViewer { Content = rows, MaxHeight = 300, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        (parent ?? FilterContent).Children.Add(SidebarSection(title, content, expanded));
    }
    private List<string> FacetValues(string key) => _facets.TryGetValue(key, out var boxes) ? boxes.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToList() : [];
    private async void FilterValue_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _buildingFilters || _filtersScope != _section) return;
        _filterChange?.Cancel(); _filterChange = new(); var ct = _filterChange.Token;
        await RunAsync(async () => { await Task.Delay(250, ct); await SaveFiltersAsync(); ct.ThrowIfCancellationRequested(); UpdateActiveFilters(); await LoadSectionAsync(); });
    }
    private async Task SaveFiltersAsync()
    {
        if (_filtersScope != _section) return;
        _filters = new()
        {
            Kinds = FacetValues("kind"), Statuses = FacetValues("status"), Genres = FacetValues("genre").Select(int.Parse).ToList(),
            Ratings = FacetValues("rating"), Durations = FacetValues("duration"), Origins = FacetValues("origin"), Studios = FacetValues("studio").Select(int.Parse).ToList(), MyList = FacetValues("mylist"),
            Season = string.Join(',', FacetValues("year").Concat(FacetValues("era")).Concat(_seasonFilter.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct()),
            MinimumScore = double.IsFinite(_scoreFilter.Value) ? (int)_scoreFilter.Value : 0, ExcludeCensored = _excludeCensored.IsChecked == true,
            LicensedOnly = _licensed.IsChecked == true, Ascending = _ascending.IsChecked == true, Order = SelectedTag(SortOrder) ?? "popularity"
        };
        await _services.Store.WriteAsync("filters-" + _section, _filters); RefreshCatalogTools();
    }
    private void UpdateActiveFilters()
    {
        ActiveFilters.Children.Clear();
        if (_section is not ("catalog" or "library")) { ActiveFilters.Visibility = Visibility.Collapsed; return; }
        var selected = _facets.Values.SelectMany(x => x).Where(x => x.IsChecked == true).ToList();
        if (selected.Count > 0) ActiveFilters.Children.Add(new TextBlock { Text = "АКТИВНО:", Style = Resource<Style>("SectionLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) });
        foreach (var box in selected)
        {
            var chip = new Button { Content = box.Content + "  ×", Style = Resource<Style>("ChipButton"), Background = Resource<Brush>("AccentBrush"), Foreground = Resource<Brush>("AccentInkBrush"), BorderThickness = new(0) };
            chip.Click += (_, _) => box.IsChecked = false; ActiveFilters.Children.Add(chip);
        }
        if (selected.Count > 0) { var clear = new Button { Content = "очистить всё", Style = Resource<Style>("NavButton"), FontSize = 10 }; clear.Click += ResetFilters_Click; ActiveFilters.Children.Add(clear); }
        ActiveFilters.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshCatalogTools()
    {
        CatalogTools.Visibility = _section is "catalog" or "library" ? Visibility.Visible : Visibility.Collapsed;
        SortChips.Children.Clear();
        (string, string)[] options = _section == "library" ? [("rate_updated", "обновлено"), ("rate_score", "моя оценка"), ("name", "название")] : [("ranked", "по рейтингу"), ("aired_on", "по году"), ("popularity", "по популярности")];
        foreach (var (key, title) in options)
        {
            var active = SelectedTag(SortOrder) == key;
            var button = new Button { Content = title + (active && _filters.Ascending ? " ↑" : ""), Style = Resource<Style>("ChipButton"), Background = Resource<Brush>(active ? "AccentBrush" : "SurfaceBrush"), Foreground = Resource<Brush>(active ? "AccentInkBrush" : "SecondaryBrush") };
            button.Click += (_, _) => SortOrder.SelectedItem = SortOrder.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, key)); SortChips.Children.Add(button);
        }
        foreach (var button in ViewSwitch.Children.OfType<Button>())
        {
            var active = Equals(button.Tag, _services.Settings.CatalogView);
            button.Background = active ? Resource<Brush>("AccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = Resource<Brush>(active ? "AccentInkBrush" : "SecondaryBrush");
        }
    }
    private void BuildLibraryTabs(List<LibraryEntry> entries)
    {
        LibraryTabs.Children.Clear();
        foreach (var item in LibraryStatus.Items.Cast<ComboBoxItem>())
        {
            var status = item.Tag as string ?? ""; var active = (SelectedTag(LibraryStatus) ?? "") == status;
            var count = string.IsNullOrEmpty(status) ? entries.Count : entries.Count(x => x.Status == status);
            var button = new Button { Content = $"{item.Content}  {count}", Style = Resource<Style>("ChipButton"), FontSize = 12, Padding = new(12, 7, 12, 7), Background = Resource<Brush>(active ? "AccentBrush" : "SurfaceBrush"), Foreground = Resource<Brush>(active ? "AccentInkBrush" : "SecondaryBrush") };
            button.Click += (_, _) => LibraryStatus.SelectedItem = item; LibraryTabs.Children.Add(button);
        }
    }
    private void UpdateCatalogView()
    {
        var browsing = _section is "catalog" or "library"; var rows = _section == "history" || browsing && _services.Settings.CatalogView == "rows";
        AnimeRows.Visibility = rows ? Visibility.Visible : Visibility.Collapsed;
        AnimeGrid.Visibility = _section is "social" or "calendar" || rows ? Visibility.Collapsed : Visibility.Visible;
        AnimeGrid.ItemTemplate = Resource<DataTemplate>(browsing && _services.Settings.CatalogView == "dense" ? "AnimeDenseTemplate" : "AnimeTemplate");
        ViewMode.Visibility = LibraryStatus.Visibility = Visibility.Collapsed;
        LibraryTabsHost.Visibility = _section == "library" ? Visibility.Visible : Visibility.Collapsed;
        RefreshCatalogTools(); UpdateActiveFilters(); ConfigurePosterGrid(AnimeGrid);
    }
    private void ViewButton_Click(object sender, RoutedEventArgs e)
    { if (sender is Button button) ViewMode.SelectedItem = ViewMode.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, button.Tag)); }
    private async void ViewMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _buildingFilters) return;
        _services.Settings.CatalogView = SelectedTag(ViewMode) ?? "grid"; UpdateCatalogView();
        await RunAsync(() => _services.Store.WriteAsync("settings", _services.Settings));
    }
}
