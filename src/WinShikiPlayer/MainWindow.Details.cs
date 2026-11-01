using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinShikiPlayer.Controls;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private FrameworkElement? _detailHeroElement, _detailWatchAction;
    private int _detailSuggestedEpisode;
    private double _detailResumeSeconds;
    private async Task ShowDetailAsync(Anime anime)
    {
        _loadCts?.Cancel(); _loadCts = new(); var ct = _loadCts.Token;
        StatusText.Text = "Загрузка тайтла…";
        var detail = await _services.Cache.GetAsync(_services.CacheKey("detail:" + anime.Id), async () => (await _services.Shikimori.EnrichPostersAsync([await _services.Shikimori.AnimeAsync(anime.Id, ct)], ct))[0], TimeSpan.FromMinutes(5));
        ct.ThrowIfCancellationRequested(); _detail = detail;
        await RecordNavigationAsync(new(_section, detail.Id, Search: _query));
        DetailContent.Children.Clear(); UpdateNavigationStyle(true);
        var history = (await _services.History.LoadAsync()).FirstOrDefault(x => x.Anime.Id == detail.Id);
        var canvas = Resource<SolidColorBrush>("CanvasBrush").Color;
        var hero = new Grid { Height = Root.ActualWidth < 1100 ? 420 : 400, Background = Resource<Brush>("SurfaceBrush") };
        _detailHeroElement = hero;
        if (Card(detail).PosterUri is { } poster)
            hero.Children.Add(new Image { Source = new BitmapImage(poster), Stretch = Stretch.UniformToFill, Opacity = 0.24 });
        var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(90, canvas.R, canvas.G, canvas.B), Offset = 0 });
        fade.GradientStops.Add(new GradientStop { Color = canvas, Offset = 1 });
        hero.Children.Add(new Border { Background = fade });
        var heroLayout = new Grid { Margin = new(32, 44, 32, 28), ColumnSpacing = 28 };
        var posterWidth = Root.ActualWidth < 1100 ? 164 : 200;
        heroLayout.ColumnDefinitions.Add(new() { Width = new(posterWidth) }); heroLayout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var cover = new Image { Height = posterWidth * 1.5, Stretch = Stretch.UniformToFill };
        if (Card(detail).PosterUri is { } coverUri) cover.Source = new BitmapImage(coverUri);
        heroLayout.Children.Add(new Border { CornerRadius = new(8), BorderBrush = Resource<Brush>("LineBrush"), BorderThickness = new(1), Child = cover, VerticalAlignment = VerticalAlignment.Bottom });
        var info = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom }; Grid.SetColumn(info, 1); heroLayout.Children.Add(info);
        var badges = new FlowPanel { Spacing = 7 };
        badges.Children.Add(Badge(detail.Status switch { "ongoing" => "Выходит", "anons" => "Анонс", _ => "Вышло" }, true));
        badges.Children.Add(Badge(detail.AiredOn?.Split('-')[0] ?? "—")); badges.Children.Add(Badge(detail.Rating?.ToUpperInvariant().Replace('_', '-') ?? "—")); info.Children.Add(badges);
        info.Children.Add(new TextBlock { Text = detail.Name, FontSize = 11, CharacterSpacing = 110, Foreground = Resource<Brush>("MutedBrush"), TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new TextBlock { Text = detail.Title, FontSize = Root.ActualWidth < 1100 ? 34 : 44, FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold, TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
        info.Children.Add(new TextBlock { Text = $"★ {detail.Score}    ·    {(detail.Episodes > 0 ? detail.Episodes : detail.EpisodesAired)} эпизодов    ·    {detail.Duration} мин    ·    {string.Join(" · ", detail.Genres.Take(3).Select(x => x.Russian ?? x.Name))}", FontSize = 13, Foreground = Resource<Brush>("SecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        var actions = new FlowPanel { Spacing = 8, Margin = new(0, 6, 0, 0) };
        var studio = new ComboBox { MinWidth = 150, MaxWidth = 220, Height = 40, PlaceholderText = "Озвучка" };
        ToolTipService.SetToolTip(studio, "Выбор озвучки");
        PlaybackStart CurrentStart() => PlaybackStart.Select(_detail?.Id == detail.Id ? _detail : detail, history);
        var play = new Button { Style = Resource<Style>("PrimaryButton") };
        void UpdatePlayAction()
        {
            var start = CurrentStart(); var selected = studio.SelectedItem as KodikTranslation;
            _detailSuggestedEpisode = start.Episode; _detailResumeSeconds = start.PositionSeconds;
            play.Content = start.CanResume ? $"▶  Продолжить · эпизод {start.Episode}" : $"▶  Смотреть эпизод {start.Episode}";
            play.IsEnabled = detail.Status != "anons" && (studio.ItemsSource is null || selected is not null && start.FindEpisode(selected.Episodes) is not null);
            ToolTipService.SetToolTip(play, selected is not null && !play.IsEnabled ? $"Эпизод {start.Episode} недоступен в этой озвучке" : null);
        }
        UpdatePlayAction();
        _detailWatchAction = play;
        play.Click += async (_, _) => await RunAsync(async () =>
        {
            var selected = studio.SelectedItem as KodikTranslation;
            var current = _detail?.Id == detail.Id ? _detail : detail;
            await StartPlaybackAsync(current, CurrentStart().ToWatchEntry(current, selected));
        });
        actions.Children.Add(play);
        var listLabel = new TextBlock { Text = detail.UserRate is { } rate ? StatusLabel(rate.Status) : "+  В список", VerticalAlignment = VerticalAlignment.Center };
        var listContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        listContent.Children.Add(listLabel);
        listContent.Children.Add(new FontIcon { Glyph = "\uE70D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var list = new Button { Content = listContent, MinHeight = 40 };
        Action? renderEpisodeChoices = null;
        var listMenu = new MenuFlyout();
        foreach (var status in new[] { "watching", "planned", "completed", "on_hold", "dropped", "rewatching" })
        {
            var item = new MenuFlyoutItem { Text = StatusLabel(status) };
            item.Click += async (_, _) => await RunAsync(async () => { await SaveDetailRateAsync(detail, status, _detail?.UserRate?.Score ?? 0, _detail?.UserRate?.Episodes ?? 0); listLabel.Text = StatusLabel(status); UpdatePlayAction(); renderEpisodeChoices?.Invoke(); }); listMenu.Items.Add(item);
        }
        listMenu.Items.Add(new MenuFlyoutSeparator()); var edit = new MenuFlyoutItem { Text = "Изменить оценку и прогресс…" }; edit.Click += async (_, _) => await RunAsync(() => EditDetailRateAsync(_detail ?? detail)); listMenu.Items.Add(edit);
        var remove = new MenuFlyoutItem { Text = "Удалить из списка", IsEnabled = detail.UserRate is not null };
        remove.Click += async (_, _) => await RunAsync(async () => { if (_detail?.UserRate is not { } current) return; await _services.Shikimori.DeleteRateAsync(current.Id); await _services.Cache.ClearAsync(); await ShowDetailAsync(detail); }); listMenu.Items.Add(remove);
        list.Flyout = listMenu; actions.Children.Add(list); if (detail.Status != "anons") actions.Children.Add(studio);
        var favourite = new Button { Content = detail.Favoured ? "♥" : "♡", Width = 40, Height = 40, Padding = new(0), FontSize = 22 };
        ToolTipService.SetToolTip(favourite, "Избранное");
        favourite.Click += async (_, _) => await RunAsync(async () => { if (!_services.Auth.IsSignedIn) { Account_Click(this, new()); return; } var liked = _detail?.Favoured ?? detail.Favoured; await _services.Shikimori.FavouriteAsync(detail.Id, !liked); await _services.Cache.ClearAsync(); _detail = (_detail ?? detail) with { Favoured = !liked }; favourite.Content = !liked ? "♥" : "♡"; }); actions.Children.Add(favourite);
        var external = new Button { Content = new FontIcon { Glyph = "\uE71B", FontSize = 16 }, Width = 40, Height = 40, Padding = new(0) }; ToolTipService.SetToolTip(external, "Открыть на Shikimori"); external.Click += (_, _) => OpenBrowser(new($"{_services.Settings.ShikimoriHost}/animes/{detail.Id}")); actions.Children.Add(external);
        if (detail.TopicId is { } topicId) { var discuss = new Button { Content = new FontIcon { Glyph = "\uE8F2", FontSize = 16 }, Width = 40, Height = 40, Padding = new(0) }; ToolTipService.SetToolTip(discuss, "Обсуждение тайтла"); discuss.Click += async (_, _) => await RunAsync(() => ShowTopicAsync(topicId)); actions.Children.Add(discuss); }
        info.Children.Add(actions); if (detail.Status == "anons") info.Children.Add(new TextBlock { Text = "Серии ещё не вышли", FontSize = 12, Foreground = Resource<Brush>("MutedBrush") });
        hero.Children.Add(heroLayout); DetailContent.Children.Add(hero);
        var body = new Grid { Margin = new(32, 24, 32, 40), ColumnSpacing = 26 };
        var sideWidth = Root.ActualWidth < 1100 ? 244 : 300;
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new(sideWidth) });
        var main = new StackPanel { Spacing = 22 }; var rail = new StackPanel { Spacing = 18 }; Grid.SetColumn(rail, 1); body.Children.Add(main); body.Children.Add(rail); DetailContent.Children.Add(body);
        var scores = new StackPanel { Spacing = 10 }; scores.Children.Add(new TextBlock { Text = "ОЦЕНИТЬ", Style = Resource<Style>("SectionLabel") });
        var scoreButtons = new FlowPanel { Spacing = 3 };
        for (var value = 1; value <= 10; value++)
        {
            var captured = value; var button = new Button { Content = value.ToString(), Width = Math.Floor((sideWidth - 35 - 27) / 10d), Height = 26, MinHeight = 26, Padding = new(0), FontSize = 10, CornerRadius = new(5), Background = Resource<Brush>(detail.UserRate?.Score == value ? "AccentBrush" : "RaisedBrush"), Tag = value };
            button.Click += async (_, _) => await RunAsync(async () => { await SaveDetailRateAsync(detail, _detail?.UserRate?.Status ?? "planned", captured, _detail?.UserRate?.Episodes ?? 0); foreach (var item in scoreButtons.Children.OfType<Button>()) item.Background = Resource<Brush>(Equals(item.Tag, captured) ? "AccentBrush" : "RaisedBrush"); }); scoreButtons.Children.Add(button);
        }
        scores.Children.Add(scoreButtons); scores.Children.Add(new TextBlock { Text = detail.UserRate?.Score is > 0 ? $"Ваша оценка: {detail.UserRate.Score}" : "Оценка не выставлена", FontSize = 11, Foreground = Resource<Brush>("MutedBrush") }); rail.Children.Add(Surface(scores, 16));
        var facts = new StackPanel { Spacing = 2 }; facts.Children.Add(new TextBlock { Text = "О ТАЙТЛЕ", Style = Resource<Style>("SectionLabel"), Margin = new(0, 0, 0, 10) });
        foreach (var (key, value) in new[] { ("Тип", detail.Kind?.ToUpperInvariant()), ("Статус", detail.Status == "ongoing" ? "Выходит" : detail.Status == "anons" ? "Анонс" : "Вышло"), ("Вышло", detail.AiredOn), ("Завершено", detail.ReleasedOn), ("Длительность эп.", $"{detail.Duration} мин"), ("Рейтинг", detail.Rating?.ToUpperInvariant()), ("Студии", string.Join(", ", detail.Studios.Select(x => x.Name))) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue; var row = new Grid { ColumnSpacing = 10, Padding = new(0, 6, 0, 6), BorderBrush = Resource<Brush>("LineBrush"), BorderThickness = new(0, 0, 0, 1) }; row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.Children.Add(new TextBlock { Text = key, FontSize = 11, Foreground = Resource<Brush>("MutedBrush") }); var text = new TextBlock { Text = value, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right }; Grid.SetColumn(text, 1); row.Children.Add(text); facts.Children.Add(row);
        }
        rail.Children.Add(Surface(facts, 16));
        if (detail.ScoreStats.Count > 0) AddStatChart(rail, "Оценки сообщества", detail.ScoreStats.Select(x => new StatBucket(x.Name, x.Value, null, null)));
        var episodesHost = new StackPanel { Spacing = 12 }; main.Children.Add(episodesHost);
        main.Children.Add(Heading("Описание")); main.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(detail.PlainDescription) ? "Описание пока не добавлено." : detail.PlainDescription, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 24, IsTextSelectionEnabled = true, Foreground = Resource<Brush>("SecondaryBrush") });
        BrowseView.Visibility = FeatureView.Visibility = Visibility.Collapsed; DetailView.Visibility = Visibility.Visible; DetailView.ChangeView(null, 0, null); StatusText.Text = $"Shikimori · #{detail.Id}";
        if (detail.Status != "anons")
        {
            episodesHost.Children.Add(Heading("Эпизоды"));
            try
            {
                var translations = await _services.Cache.GetAsync(_services.CacheKey("kodik:" + _services.Settings.KodikHost + ":" + detail.Id), () => _services.Kodik.SearchAsync(detail.Id, ct), TimeSpan.FromMinutes(10)); ct.ThrowIfCancellationRequested();
                studio.ItemsSource = translations;
                var grid = new FlowPanel { Spacing = 7 }; episodesHost.Children.Add(grid);
                void RenderEpisodes()
                {
                    grid.Children.Clear(); if (studio.SelectedItem is not KodikTranslation translation) return;
                    var suggested = CurrentStart().FindEpisode(translation.Episodes);
                    var shown = 0;
                    void Append()
                    {
                        foreach (var ep in translation.Episodes.Skip(shown).Take(100))
                        {
                            var number = translation.Episodes.Select(x => x.Season).Distinct().Skip(1).Any() ? $"{ep.Season}:{ep.Number:00}" : ep.Number.ToString("00");
                            var label = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center }; label.Children.Add(new TextBlock { Text = number, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center }); label.Children.Add(new TextBlock { Text = ep.Number <= (_detail?.UserRate?.Episodes ?? 0) ? "просмотрено" : $"{detail.Duration} мин", FontSize = 9, Foreground = Resource<Brush>("MutedBrush") });
                            var isNext = suggested == ep && ep.Number > (_detail?.UserRate?.Episodes ?? 0);
                            var episodeButton = new Button { Content = label, Width = 88, Height = 58, Padding = new(4), Background = Resource<Brush>(isNext ? "RaisedBrush" : "SurfaceBrush"), BorderBrush = Resource<Brush>(isNext ? "AccentBrush" : "LineBrush") };
                            ToolTipService.SetToolTip(episodeButton, ep.Label); episodeButton.Click += async (_, _) => await RunAsync(() => StartPlaybackAsync(_detail ?? detail, new(detail, ep.Number, history?.Episode == ep.Number && history.Season == ep.Season ? history.PositionSeconds : 0, 0, DateTimeOffset.UtcNow, translation.Id, ep.Season))); grid.Children.Add(episodeButton);
                        }
                        shown += 100;
                        if (translation.Episodes.Count > shown) { var more = new Button { Content = "Ещё…", Width = 80, Height = 58 }; more.Click += (_, _) => { grid.Children.Remove(more); Append(); }; grid.Children.Add(more); }
                    }
                    Append();
                }
                renderEpisodeChoices = RenderEpisodes;
                studio.SelectionChanged += (_, _) => { UpdatePlayAction(); RenderEpisodes(); };
                var start = CurrentStart();
                studio.SelectedItem = translations.FirstOrDefault(x => x.Id == history?.TranslationId && start.FindEpisode(x.Episodes) is not null)
                    ?? translations.FirstOrDefault(x => start.FindEpisode(x.Episodes) is not null) ?? translations.FirstOrDefault();
                UpdatePlayAction();
                if (translations.Count == 0) episodesHost.Children.Add(new TextBlock { Text = "Доступных серий пока нет", FontSize = 12, Foreground = Resource<Brush>("MutedBrush") });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { episodesHost.Children.Add(new TextBlock { Text = ex.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Resource<Brush>("MutedBrush") }); }
        }
        await AddDetailExtrasAsync(detail, main, ct);
    }
    private static Border Badge(string text, bool accent = false) => new() { Background = Resource<Brush>(accent ? "AccentBrush" : "RaisedBrush"), CornerRadius = new(12), Padding = new(9, 4, 9, 4), Child = new TextBlock { Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Resource<Brush>(accent ? "AccentInkBrush" : "SecondaryBrush") } };
    private async Task SaveDetailRateAsync(Anime anime, string status, int score, int episodes)
    {
        if (!_services.Auth.IsSignedIn) { Account_Click(this, new()); return; }
        var current = _detail?.Id == anime.Id ? _detail : anime;
        var user = await CurrentUserAsync(); var rate = await _services.Shikimori.SaveRateAsync(user.Id, current, status, score, episodes); await _services.Cache.ClearAsync();
        if (_detail?.Id == anime.Id) _detail = current with { UserRate = rate }; StatusText.Text = "Список Shikimori обновлён";
    }
    private async Task EditDetailRateAsync(Anime anime)
    {
        if (_dialogOpen) return; _dialogOpen = true;
        try
        {
            var panel = new StackPanel { Spacing = 16, Width = 320 }; var status = new ComboBox { Header = "Статус", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var value in new[] { "planned", "watching", "completed", "on_hold", "dropped", "rewatching" }) status.Items.Add(new ComboBoxItem { Content = StatusLabel(value), Tag = value });
            status.SelectedItem = status.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, anime.UserRate?.Status ?? "planned"));
            var score = new NumberBox { Header = "Оценка", Minimum = 0, Maximum = 10, Value = anime.UserRate?.Score ?? 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var episodes = new NumberBox { Header = "Просмотрено эпизодов", Minimum = 0, Maximum = Math.Max(anime.AvailableEpisodes, 10000), Value = anime.UserRate?.Episodes ?? 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            panel.Children.Add(status); panel.Children.Add(score); panel.Children.Add(episodes);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "В моём списке", Content = panel, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && double.IsFinite(score.Value) && double.IsFinite(episodes.Value)) { await SaveDetailRateAsync(anime, SelectedTag(status) ?? "planned", (int)score.Value, (int)episodes.Value); await ShowDetailAsync(anime); }
        }
        finally { _dialogOpen = false; }
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
    private static Border Surface(UIElement child, double padding = 20) => new() { Child = child, Padding = new(padding), CornerRadius = new(12), Background = Resource<Brush>("SurfaceBrush"), BorderBrush = Resource<Brush>("LineBrush"), BorderThickness = new(1) };
}

