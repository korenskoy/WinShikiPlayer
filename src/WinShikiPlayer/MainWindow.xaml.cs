using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinShikiPlayer.Core;
using WinShikiPlayer.Services;

namespace WinShikiPlayer;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services = new();
    private readonly ObservableCollection<AnimeCard> _cards = [];
    private readonly ObservableCollection<Topic> _topics = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _loginCts;
    private bool _ready;
    private bool _loading;
    private string _section = "home";
    private string? _query;
    private int _page = 1;
    private Anime? _featured;
    private Anime? _detail;
    private bool _dialogOpen;
    private readonly CancellationTokenSource _lifetime = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinShikiPlayer";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1800, area.Width - 80), Math.Min(1150, area.Height - 80)));
        if (Environment.GetCommandLineArgs().Contains("--narrow-window")) AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AnimeGrid.ItemsSource = _cards;
        AnimeRows.ItemsSource = _cards;
        AnimeGrid.Loaded += (_, _) => ConfigurePosterGrid(AnimeGrid);
        _systemAppearance.ColorValuesChanged += SystemAppearance_Changed;
        TopicsList.ItemsSource = _topics;
        Root.Loaded += OnLoaded;
        Closed += OnClosed;
        AppWindow.Closing += async (_, args) =>
        {
            if (_mainClosing || _playingAnime is null) return;
            args.Cancel = true; _mainClosing = true;
            try { await StopPlaybackAsync(); } finally { Close(); }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnLoaded;
        _playerDiagnostic = Environment.GetCommandLineArgs().Any(x => x is "--player-ui-smoke" or "--volume-smoke" or "--return-smoke");
        await RunAsync(async () =>
        {
            await _services.InitializeAsync();
            ApplyTheme();
            _ready = true;
            _navigation.Restore(await _services.Store.ReadAsync<NavigationSnapshot>("navigation")); UpdateNavigationButtons();
            _ = ProtocolActivation.ListenAsync(uri => DispatcherQueue.TryEnqueue(() => { _services.Auth.ReceiveProtocolCallback(uri); Activate(); }), _lifetime.Token);
            await LoadSectionAsync();
            if (_services.Auth.IsSignedIn)
            {
                _services.User = await _services.Shikimori.WhoAmIAsync();
                UpdateAccount();
            }
            if (Environment.GetCommandLineArgs().Contains("--oauth-login") && !_services.Auth.IsSignedIn) Account_Click(this, new RoutedEventArgs());
        });
        var startupArguments = Environment.GetCommandLineArgs();
        if (_playerDiagnostic)
        {
            if (startupArguments.Contains("--return-smoke")) await RunReturnSmokeAsync(startupArguments);
            else if (startupArguments.Contains("--volume-smoke")) await RunVolumeSmokeAsync(startupArguments);
            else await RunPlayerUiSmokeAsync(startupArguments);
            return;
        }
        if (startupArguments.Contains("--window-smoke") || startupArguments.Contains("--features-smoke"))
        {
            AppWindow.Hide();
            var output = startupArguments.FirstOrDefault(x => x.StartsWith("--diagnostic-output="))?["--diagnostic-output=".Length..];
            object report;
            try
            {
                if (startupArguments.Contains("--features-smoke"))
                {
                    _section = "library"; await LoadSectionAsync(refresh: true);
                    var libraryCount = _cards.Count;
                    foreach (var mode in new[] { "grid", "dense", "rows" }) { _services.Settings.CatalogView = mode; UpdateCatalogView(); }
                    _services.Settings.CatalogView = "grid"; UpdateCatalogView();
                    var requestedAnime = startupArguments.FirstOrDefault(x => x.StartsWith("--anime-id="))?["--anime-id=".Length..];
                    await ShowDetailAsync(new Anime { Id = int.TryParse(requestedAnime, out var animeId) ? animeId : 16498 });
                    if (_detail is null || DetailContent.Children.Count < 2) throw new InvalidOperationException("Карточка тайтла не построена.");
                    await Task.Delay(150); Root.UpdateLayout();
                    if (_detailHeroElement is null || _detailWatchAction is null || _detailHeroElement.ActualHeight > 500 || _detailWatchAction.ActualHeight < 1)
                        throw new InvalidOperationException("Неверная геометрия hero или невидима кнопка просмотра.");
                    var watchPosition = _detailWatchAction.TransformToVisual(_detailHeroElement).TransformPoint(new(0, 0));
                    if (watchPosition.Y < 0 || watchPosition.Y + _detailWatchAction.ActualHeight > _detailHeroElement.ActualHeight + 1)
                        throw new InvalidOperationException("Действие просмотра вышло за границы hero.");
                    var heroHeight = _detailHeroElement.ActualHeight;
                    var playbackCaption = (_detailWatchAction as Button)?.Content as string;
                    var suggestedEpisode = _detailSuggestedEpisode; var resumeSeconds = _detailResumeSeconds;
                    var watchedEpisodes = _detail.UserRate?.Episodes ?? 0;
                    if (suggestedEpisode <= watchedEpisodes && (_detail.AvailableEpisodes == 0 || watchedEpisodes < _detail.AvailableEpisodes))
                        throw new InvalidOperationException("Основное действие предлагает уже просмотренный эпизод.");
                    if (playbackCaption?.EndsWith($"эпизод {suggestedEpisode}", StringComparison.Ordinal) != true)
                        throw new InvalidOperationException("Подпись просмотра не соответствует выбранному эпизоду.");
                    var expectedTrailers = TrailerCatalog.Select(await _services.Shikimori.VideosAsync(_detail.Id)).Count;
                    var trailerCards = _detailTrailers?.Children.Count ?? 0;
                    if (trailerCards != expectedTrailers) throw new InvalidOperationException("Показаны не все отфильтрованные трейлеры.");
                    var trailerWidth = 0d; var trailerHeight = 0d;
                    if (_detailTrailers is { Children.Count: > 0 } panel)
                    {
                        var card = (Button)panel.Children[0];
                        trailerWidth = card.ActualWidth; trailerHeight = card.ActualHeight;
                        if (trailerWidth < 1 || Math.Abs(trailerWidth * 9 / 16 - trailerHeight) > 1)
                            throw new InvalidOperationException("Карточка трейлера не соответствует пропорции 16:9.");
                        if (Math.Abs(trailerWidth - (panel.ActualWidth - 40) / 5) > 1 || panel.Children.OfType<FrameworkElement>().Any(x => Math.Abs(x.ActualWidth - trailerWidth) > 1 || Math.Abs(x.ActualHeight - trailerHeight) > 1))
                            throw new InvalidOperationException("Карточки трейлеров не заполняют пять одинаковых колонок.");
                        if (card.Content is not FrameworkElement body || body.ActualHeight < trailerHeight - 2)
                            throw new InvalidOperationException("Превью трейлера не заполняет карточку.");
                    }
                    var detailsCount = DetailContent.Children.Count;
                    if (_services.Auth.IsSignedIn) await ShowProfileAsync();
                    var profileCount = FeatureContent.Children.Count;
                    var topics = await _services.Shikimori.TopicsAsync(1);
                    await ShowTopicAsync(topics[0].Id);
                    report = new { success = true, libraryRendered = libraryCount > 0, libraryCards = libraryCount, catalogViewModes = 3, detailsRendered = true, detailSections = detailsCount, heroHeight, watchActionWithinHero = true, watchedEpisodes, suggestedEpisode, resumeSeconds, playbackCaption,
                        trailerCards, trailerWidth, trailerHeight, profileRendered = profileCount > 3, topicRendered = FeatureContent.Children.Count > 4, timestamp = DateTimeOffset.UtcNow };
                }
                else report = new { success = _ready && Root.ActualWidth > 0, windowCreated = true, width = Root.ActualWidth, height = Root.ActualHeight, catalogItems = _cards.Count, timestamp = DateTimeOffset.UtcNow };
            }
            catch (Exception ex) { report = new { success = false, errorType = ex.GetType().Name, message = ex.Message }; }
            if (output is not null) await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(report));
            Close();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _loadCts?.Cancel(); _loginCts?.Cancel(); _playCts?.Cancel();
        _lifetime.Cancel();
        _systemAppearance.ColorValuesChanged -= SystemAppearance_Changed;
        _filterChange?.Cancel(); _chromeTimer?.Stop();
        _progressTimer?.Stop();
        _mediaPlayer?.Dispose();
        _webView?.Close();
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        ApplicationDiagnostics.RecordUiError(ex);
        Notice.Title = "Не удалось выполнить действие";
        Notice.Message = ex is HttpRequestException ? "Нет соединения с сервисом. Проверьте сеть и домен в настройках."
            : ex is TaskCanceledException ? "Сервис не ответил вовремя. Попробуйте ещё раз."
            : ex.Message;
        Notice.Severity = InfoBarSeverity.Error;
        Notice.IsOpen = true;
    }

    private void ApplyTheme() => ApplyDesignTheme();

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not Button { Tag: string tag }) return;
        await RunAsync(async () =>
        {
            _section = tag; _query = null; SearchBox.Text = "";
            await LoadSectionAsync();
        });
    }

    private async Task LoadSectionAsync(bool more = false, bool refresh = false)
    {
        _loadCts?.Cancel();
        _loadCts = new();
        var ct = _loadCts.Token;
        _loading = true;
        if (!more)
        {
            await RecordNavigationAsync(new(_section, Search: _query));
            _page = 1; _cards.Clear(); _topics.Clear();
            _detail = null;
            FeatureView.Visibility = Visibility.Collapsed; FeatureToolbar.Children.Clear(); ExtraHomeSections.Children.Clear(); ExtraHomeSections.Visibility = Visibility.Collapsed;
            DetailView.Visibility = Visibility.Collapsed;
            BrowseView.Visibility = Visibility.Visible;
            Hero.Visibility = ContinueSection.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            Notice.IsOpen = false;
            BrowseScroll.ChangeView(null, 0, null);
        }
        var page = more ? _page + 1 : 1;
        FilterPanel.Visibility = _section is "catalog" or "library" ? Visibility.Visible : Visibility.Collapsed;
        SortOrder.Visibility = _section is "catalog" or "library" ? Visibility.Visible : Visibility.Collapsed;
        LibraryStatus.Visibility = Visibility.Collapsed;
        TopicsList.Visibility = _section == "social" ? Visibility.Visible : Visibility.Collapsed;
        FeatureToolbar.Visibility = _section == "social" ? Visibility.Visible : Visibility.Collapsed;
        UpdateCatalogView();
        LoadMore.Visibility = Visibility.Collapsed;
        Loading.Visibility = Visibility.Visible; Loading.IsActive = true;
        SignInPrompt.Visibility = Visibility.Collapsed;
        GridTitle.Visibility = _section == "home" ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationStyle();
        (PageTitle.Text, PageSubtitle.Text) = _section switch
        {
            "home" => ("Ваша следующая история", "Откройте новое аниме или вернитесь к любимому."),
            "catalog" => (_query is null ? "Весь каталог" : $"Поиск: {_query}", "Тайтлы, которые стоит открыть"),
            "library" => ("Мои списки", "Ваша библиотека Shikimori"),
            "calendar" => ("Расписание онгоингов", "Ближайшие 7 дней · локальное время"),
            "social" => ("Лента", "Обсуждения сообщества Shikimori"),
            "history" => ("История просмотра", "Продолжайте с того места, где остановились"),
            _ => ("WinShikiPlayer", "")
        };
        try
        {
            await EnsureFiltersAsync(ct);
            List<AnimeCard> incoming = [];
            var hasMore = false;
            switch (_section)
            {
                case "home":
                case "catalog":
                    var query = (_filters with { Search = _query }).Query(page);
                    var home = _section == "home" ? await _services.Cache.GetAsync(_services.CacheKey("home:v2"), () => _services.Shikimori.HomeAsync(ct), TimeSpan.FromMinutes(5), refresh) : null;
                    var animes = home?.Trending ?? await _services.Cache.GetAsync(_services.CacheKey("catalog:" + ShikimoriClient.Query(query)),
                        async () => await _services.Shikimori.EnrichPostersAsync(await _services.Shikimori.CatalogAsync(query, ct), ct), TimeSpan.FromMinutes(10), refresh);
                    if (_section == "catalog" && _filters.Ascending) animes = animes.AsEnumerable().Reverse().ToList();
                    ct.ThrowIfCancellationRequested();
                    incoming = animes.Select(Card).ToList(); hasMore = _section == "catalog" && animes.Count == 30;
                    if (_section == "home" && !more)
                    {
                        _featured = animes.FirstOrDefault();
                        if (_featured is not null)
                        {
                            HeroTitle.Text = _featured.Title; HeroMeta.Text = $"★ {_featured.Score}  ·  {_featured.Meta}";
                            HeroImage.Source = Card(_featured).PosterUri is { } poster ? new BitmapImage(poster) : null;
                            HeroBackdrop.Source = HeroImage.Source;
                            Hero.Visibility = Visibility.Visible;
                        }
                        var history = await _services.History.LoadAsync();
                        ct.ThrowIfCancellationRequested();
                        AnimeCard ContinueCard(Anime current, WatchEntry? saved)
                        {
                            var start = PlaybackStart.Select(current, saved);
                            return Card(current, start.CanResume ? $"Эпизод {start.Episode} · {TimeSpan.FromSeconds(start.PositionSeconds):mm\\:ss}" : $"Следующий · эпизод {start.Episode}");
                        }
                        var continuing = history.Take(8).Select(x => ContinueCard(x.Anime, x)).ToList();
                        if (_services.Auth.IsSignedIn)
                        {
                            var watching = await _services.Shikimori.LibraryAsync((await CurrentUserAsync(ct)).Id, "watching", 1, ct);
                            var watchingPosters = (await _services.Shikimori.EnrichPostersAsync(watching.Take(8).Select(x => x.Anime).ToList(), ct)).ToDictionary(x => x.Id);
                            ct.ThrowIfCancellationRequested();
                            var currentWatching = watching.Take(8).Select(x => (watchingPosters.GetValueOrDefault(x.Anime.Id) ?? x.Anime) with { UserRate = new(x.Id, x.Score, x.Status, x.Episodes) }).ToDictionary(x => x.Id);
                            continuing = history.Take(8).Select(x => ContinueCard(currentWatching.GetValueOrDefault(x.Anime.Id) ?? x.Anime, x)).ToList();
                            continuing.AddRange(currentWatching.Values.Select(x => ContinueCard(x, history.FirstOrDefault(saved => saved.Anime.Id == x.Id))));
                        }
                        ContinueGrid.ItemsSource = continuing.DistinctBy(x => x.Anime.Id).Take(8).ToList();
                        ContinueSection.Visibility = continuing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                        if (home is not null)
                        {
                            ExtraHomeSections.Visibility = Visibility.Visible;
                            await AddAnimeSectionAsync(ExtraHomeSections, "Новые серии", home.NewEpisodes, ct);
                            await AddAnimeSectionAsync(ExtraHomeSections, "Для вас", home.Recommendations, ct);
                        }
                        await _services.Store.WriteAsync("home-cache", animes);
                    }
                    break;
                case "library":
                    if (!_services.Auth.IsSignedIn)
                    { ShowEmpty("Ваша коллекция — здесь", "Войдите в Shikimori, чтобы видеть списки, оценки и прогресс.", true); break; }
                    var user = await CurrentUserAsync(ct);
                    var allLibrary = await _services.Cache.GetAsync(_services.CacheKey("library:" + user.Id),
                        () => _services.Shikimori.CompleteLibraryAsync(user.Id, ct), TimeSpan.FromMinutes(15), refresh);
                    var filteredLibrary = (_filters with { Search = _query }).FilterLibrary(allLibrary, SelectedTag(LibraryStatus));
                    _libraryCount = filteredLibrary.Count;
                    BuildLibraryTabs(allLibrary);
                    var library = filteredLibrary.Skip((page - 1) * 60).Take(60).ToList();
                    ct.ThrowIfCancellationRequested();
                    incoming = library.Select(x => Card(x.Anime with { UserRate = new(x.Id, x.Score, x.Status, x.Episodes) }, $"{StatusLabel(x.Status)} · {x.Episodes}/{x.Anime.Episodes} эп.")).ToList();
                    hasMore = filteredLibrary.Count > page * 60;
                    PageSubtitle.Text = $"{filteredLibrary.Count} из {allLibrary.Count} тайтлов · библиотека Shikimori";
                    break;
                case "calendar":
                    var calendar = await _services.Cache.GetAsync(_services.CacheKey("calendar:v3"), async () =>
                    {
                        var schedule = await _services.Shikimori.CalendarAsync(ct);
                        var today = DateTimeOffset.Now.Date;
                        var upcoming = schedule.Where(x => x.NextEpisodeAt is { } date && date.LocalDateTime.Date >= today && date.LocalDateTime.Date < today.AddDays(7)).ToList();
                        var posters = (await _services.Shikimori.EnrichPostersAsync(upcoming.Select(x => x.Anime).DistinctBy(x => x.Id).ToList(), ct)).ToDictionary(x => x.Id);
                        return upcoming.Select(x => posters.TryGetValue(x.Anime.Id, out var match) ? x with { Anime = x.Anime with { Image = match.Image } } : x).ToList();
                    }, TimeSpan.FromMinutes(15), refresh);
                    ct.ThrowIfCancellationRequested();
                    incoming = RenderCalendar(calendar);
                    break;
                case "history":
                    var entries = await _services.History.LoadAsync();
                    var remoteHistory = _services.Auth.IsSignedIn ? await _services.Shikimori.HistoryAsync((await CurrentUserAsync(ct)).Id, page, ct) : [];
                    ct.ThrowIfCancellationRequested();
                    incoming = HistoryMerger.Merge(entries, remoteHistory, _services.Settings.ShikimoriHost);
                    hasMore = remoteHistory.Count == 30;
                    if (incoming.Count == 0) ShowEmpty("Каждая история с чего-то начинается", "Откройте тайтл в каталоге и начните просмотр. Прогресс появится здесь.");
                    break;
                case "social":
                    AddSocialTabs();
                    if (_socialTab == "friends")
                    {
                        if (!_services.Auth.IsSignedIn) { ShowEmpty("Активность друзей", "Войдите в Shikimori, чтобы увидеть, что смотрят ваши друзья.", true); break; }
                        var friends = await _services.Shikimori.FriendActivityAsync((await CurrentUserAsync(ct)).Id, ct);
                        ct.ThrowIfCancellationRequested();
                        incoming = friends.Where(x => x.Event.Target is not null).Select(x => Card(x.Event.Target!, $"{x.User.Nickname} · {TextFormatting.Plain(x.Event.Description)}")).DistinctBy(x => x.Anime.Id).ToList();
                        TopicsList.Visibility = Visibility.Collapsed; AnimeGrid.Visibility = Visibility.Visible;
                        break;
                    }
                    var topics = await _services.Shikimori.ForumTopicsAsync(_socialTab == "reviews" ? "critiques" : "animanga", page, ct);
                    ct.ThrowIfCancellationRequested();
                    foreach (var topic in topics) if (!_topics.Any(x => x.Id == topic.Id)) _topics.Add(topic);
                    hasMore = topics.Count == 20;
                    break;
            }
            ct.ThrowIfCancellationRequested();
            if (_section is "history" or "social" && incoming.Count > 0)
            {
                var posters = (await _services.Shikimori.EnrichPostersAsync(incoming.Select(x => x.Anime).DistinctBy(x => x.Id).ToList(), ct)).ToDictionary(x => x.Id);
                incoming = incoming.Select(x => posters.TryGetValue(x.Anime.Id, out var match) ? x with { Anime = x.Anime with { Image = match.Image } } : x).ToList();
            }
            ct.ThrowIfCancellationRequested();
            var fresh = incoming.Where(card => !_cards.Any(x => x.Anime.Id == card.Anime.Id)).ToList();
            foreach (var card in fresh) _cards.Add(card);
            if (more && fresh.Count == 0 && _section != "social") hasMore = false;
            if (_cards.Count == 0 && _topics.Count == 0 && EmptyState.Visibility != Visibility.Visible)
                ShowEmpty("Ничего не найдено", "Попробуйте изменить запрос или сбросить фильтры.");
            _page = page;
            if (_section == "catalog" && _query is null) PageTitle.Text = $"Весь каталог · {_cards.Count}";
            if (_section == "library") PageTitle.Text = $"Мои списки · {_libraryCount}";
            PageSubtitle.Visibility = _section is "catalog" or "library" ? Visibility.Collapsed : Visibility.Visible;
            RefreshCatalogTools(); ConfigurePosterGrid(AnimeGrid);
            LoadMore.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"WinShikiPlayer · {(_section == "social" ? _topics.Count : _cards.Count)} записей";
            if (_section == "library") StatusText.Text = $"Показано {_cards.Count} из {_libraryCount} тайтлов";
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _section == "home" && !more)
        {
            var cached = await _services.Store.ReadAsync<List<Anime>>("home-cache");
            ct.ThrowIfCancellationRequested();
            if (cached is not null) foreach (var anime in cached) _cards.Add(Card(anime));
            ShowError(ex);
            if (_cards.Count == 0) ShowEmpty("Не удалось загрузить каталог", "Проверьте подключение и нажмите «Обновить». Домен сервиса можно изменить в настройках.");
            else StatusText.Text = "Показана сохранённая копия каталога · нет соединения с сервисом";
        }
        finally
        {
            if (!ct.IsCancellationRequested) { _loading = false; Loading.IsActive = false; Loading.Visibility = Visibility.Collapsed; }
        }
    }

    private AnimeCard Card(Anime anime) => new(anime, _services.Settings.ShikimoriHost);
    private AnimeCard Card(Anime anime, string note) => new(anime, _services.Settings.ShikimoriHost, note);
    private static string? SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    private void ShowEmpty(string title, string text, bool signIn = false)
    {
        EmptyTitle.Text = title; EmptyText.Text = text; EmptyState.Visibility = Visibility.Visible;
        SignInPrompt.Visibility = signIn ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task<User> CurrentUserAsync(CancellationToken ct = default)
    {
        _services.User ??= await _services.Shikimori.WhoAmIAsync(ct);
        UpdateAccount(); return _services.User;
    }
    private void UpdateAccount()
    {
        ToolTipService.SetToolTip(AccountButton, _services.User?.Nickname ?? "Войти в Shikimori");
        AccountInitial.Text = _services.User?.Nickname.FirstOrDefault().ToString().ToUpperInvariant() ?? "?";
        if (RemoteUri(_services.User?.Avatar) is { } avatar) { AccountAvatar.Fill = new ImageBrush { ImageSource = new BitmapImage(avatar), Stretch = Stretch.UniformToFill }; AccountInitial.Visibility = Visibility.Collapsed; }
        else { AccountAvatar.Fill = Resource<Brush>("AccentBrush"); AccountInitial.Visibility = Visibility.Visible; }
    }
    internal void AcceptProtocolCallback(Uri uri)
    {
        if (!_ready) return;
        _services.Auth.ReceiveProtocolCallback(uri); Activate();
    }
    private async void Search_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (!_ready) return;
        await RunAsync(async () => { _section = "catalog"; _query = args.QueryText.Trim(); if (_query.Length == 0) _query = null; await LoadSectionAsync(); });
    }
    private void Search_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SearchBox.Focus(FocusState.Keyboard); args.Handled = true; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (_ready) await RunAsync(() => LoadSectionAsync(refresh: true)); }
    private async void LoadMore_Click(object sender, RoutedEventArgs e) { if (_ready && !_loading) await RunAsync(() => LoadSectionAsync(true)); }
    private async void Sort_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && !_buildingFilters && _section is "catalog" or "library") await RunAsync(async () => { await SaveFiltersAsync(); await LoadSectionAsync(); }); }
    private async void LibraryStatus_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && _section == "library") await RunAsync(() => LoadSectionAsync()); }
    private async void ApplyFilters_Click(object sender, RoutedEventArgs e) { if (_ready) await RunAsync(async () => { await SaveFiltersAsync(); await LoadSectionAsync(); }); }
    private async void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        await RunAsync(async () =>
        {
            await _services.Store.WriteAsync("filters-" + _section, new CatalogFilters());
            _filtersScope = null; _query = null; SearchBox.Text = "";
            await LoadSectionAsync();
        });
    }
    private async void Anime_Click(object sender, ItemClickEventArgs args) { if (args.ClickedItem is AnimeCard card) await RunAsync(() => ShowDetailAsync(card.Anime)); }
    private async void Hero_Click(object sender, RoutedEventArgs e) { if (_featured is not null) await RunAsync(() => ShowDetailAsync(_featured)); }
    private async void Topic_Click(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is Topic topic) await RunAsync(() => ShowTopicAsync(topic.Id));
    }
    private static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    private static string StatusLabel(string status) => status switch
    { "watching" => "Смотрю", "planned" => "Запланировано", "completed" => "Просмотрено", "on_hold" => "Отложено", "dropped" => "Брошено", "rewatching" => "Пересматриваю", _ => status };
}
