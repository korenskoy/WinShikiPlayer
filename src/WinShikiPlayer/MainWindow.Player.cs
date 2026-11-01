using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private MediaPlayer? _mediaPlayer;
    private WebView2? _webView;
    private DispatcherTimer? _progressTimer;
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _openPlayerCts;
    private Anime? _playingAnime;
    private KodikEpisode? _playingEpisode;
    private ResolvedVideo? _resolved;
    private WatchEntry? _resume;
    private bool _changingPickers;
    private bool _usingWeb;
    private readonly SemaphoreSlim _progressWrite = new(1, 1);
    private bool _markedWatched;
    private double _pendingPosition;
    private int _playingTranslationId;
    private readonly StreamRecovery _streamRecovery = new();
    private long _streamRevision;
    private string _playerPreferredQuality = "auto";
    private readonly Dictionary<string, EpisodeProgress> _diagnosticProgress = [];

    private async Task StartPlaybackAsync(Anime anime, WatchEntry? resume)
    {
        _openPlayerCts?.Cancel(); _openPlayerCts?.Dispose();
        _openPlayerCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _openPlayerCts.Token;
        PlayerStatus.Text = "Ищем озвучки…";
        var translations = await _services.Kodik.SearchAsync(anime.Id, ct);
        ct.ThrowIfCancellationRequested();
        if (translations.Count == 0) throw new InvalidOperationException("Для этого тайтла в Kodik пока нет доступных серий.");
        resume ??= PlaybackStart.Select(anime, (await _services.History.LoadAsync()).FirstOrDefault(x => x.Anime.Id == anime.Id)).ToWatchEntry(anime);
        var requested = new PlaybackStart(resume.Episode, resume.Season, resume.PositionSeconds, resume.TranslationId);
        var translation = translations.FirstOrDefault(x => x.Id == resume.TranslationId && requested.FindEpisode(x.Episodes) is not null)
            ?? translations.FirstOrDefault(x => requested.FindEpisode(x.Episodes) is not null)
            ?? throw new InvalidOperationException($"Эпизод {requested.Episode} пока недоступен в Kodik.");
        ct.ThrowIfCancellationRequested();
        await StopPlaybackAsync(closeWindow: false);
        _playingAnime = anime; _resume = resume;
        _playerPreferredQuality = _services.Settings.PreferredQuality;
        _changingPickers = true;
        try
        {
            TranslationPicker.ItemsSource = translations;
            TranslationPicker.SelectedItem = translation;
            SetEpisodes();
        }
        finally { _changingPickers = false; }
        _mediaPlayer ??= CreateMediaPlayer();
        NativePlayer.SetMediaPlayer(_mediaPlayer);
        _progressTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _progressTimer.Tick -= Progress_Tick; _progressTimer.Tick += Progress_Tick; _progressTimer.Start();
        OpenPlayerWindow();
        StartPlayerChrome();
        await PlaySelectedAsync();
    }

    private MediaPlayer CreateMediaPlayer()
    {
        var player = new MediaPlayer { AutoPlay = true, Volume = _services.Settings.PlayerVolume, IsMuted = _playerDiagnostic };
        player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_playingEpisode is null || _usingWeb) return;
            var duration = player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (_pendingPosition > 0 && _pendingPosition < duration - 5)
                player.PlaybackSession.Position = TimeSpan.FromSeconds(_pendingPosition);
            _pendingPosition = 0;
            player.PlaybackSession.PlaybackRate = _playerSpeed;
            _preparingPlayback = false; PlayerStatus.Text = "";
        });
        player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(async () => await RunAsync(() => RecoverStreamAsync(args.ErrorMessage)));
        player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(async () => await RunAsync(HandleEpisodeEndedAsync));
        return player;
    }

    private void SetEpisodes()
    {
        if (TranslationPicker.SelectedItem is not KodikTranslation translation) return;
        var current = _playingEpisode;
        var desired = new PlaybackStart(current?.Number ?? _resume?.Episode ?? 1, current?.Season ?? _resume?.Season ?? 1);
        var selected = desired.FindEpisode(translation.Episodes) ?? throw new InvalidOperationException($"Эпизод {desired.Episode} недоступен в озвучке «{translation.Title}».");
        EpisodePicker.ItemsSource = translation.Episodes; EpisodePicker.SelectedItem = selected;
    }

    private async Task PlaySelectedAsync()
    {
        if (_playingAnime is not { } anime || EpisodePicker.SelectedItem is not KodikEpisode episode) return;
        var translation = TranslationPicker.SelectedItem as KodikTranslation;
        var wasPlaying = _mediaPlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        _mediaPlayer?.Pause();
        await SaveProgressAsync();
        _playCts?.Cancel(); _playCts = new(); var ct = _playCts.Token;
        var resume = _resume; _resume = null;
        _preparingPlayback = true;
        PlayerStatus.Text = "Загрузка потока…"; RevealPlayerChrome();
        if (_playingEpisode is null) { PlayerTitle.Text = anime.Title; PlayerEpisodeLabel.Text = $"ЭП {episode.Number:00}"; }
        try
        {
            var position = resume is not null && resume.Episode == episode.Number && resume.Season == episode.Season ? resume.PositionSeconds : 0;
            if (position <= 1)
            {
                var key = $"{anime.Id}:{episode.Season}:{episode.Number}";
                var point = _playerDiagnostic ? _diagnosticProgress.GetValueOrDefault(key) : await _services.Progress.FindAsync(anime.Id, episode.Season, episode.Number);
                if (point is { Completed: false }) position = point.Position;
            }
            var resolved = await (_prefetch?.TakeAsync(episode.Link, ct) ?? Task.FromResult<ResolvedVideo?>(null))
                ?? await _services.Resolver.ResolveAsync(episode.Link, ct);
            ct.ThrowIfCancellationRequested();
            // Commit only after resolution succeeds: a failed studio/episode
            // request must leave the previous picture, position and source intact.
            _mediaPlayer!.Source = null;
            _webView?.Close(); _webView = null; WebPlayerHost.Children.Clear();
            WebPlayerHost.Visibility = Visibility.Collapsed; NativePlayer.Visibility = Visibility.Visible; _usingWeb = false;
            _playingEpisode = episode; _playingTranslationId = translation?.Id ?? 0;
            _markedWatched = false; _pendingPosition = position; _resolved = resolved;
            ResetSubtitles();
            _changingPickers = true;
            try { QualityPicker.ItemsSource = resolved.Streams; QualityPicker.SelectedItem = resolved.Streams.FirstOrDefault(x => x.Quality == _playerPreferredQuality) ?? resolved.Streams[0]; }
            finally { _changingPickers = false; }
            PlayerTitle.Text = anime.Title; PlayerEpisodeLabel.Text = $"ЭП {episode.Number:00}";
            if (_playerWindow is not null) _playerWindow.Title = $"{anime.Title} — {episode.Label} · WinShikiPlayer";
            SetStream((VideoStream)QualityPicker.SelectedItem);
            await InitializeSubtitlesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ct.IsCancellationRequested) return;
            _preparingPlayback = false;
            if (_playingEpisode is { } previous)
            {
                _changingPickers = true;
                try
                {
                    TranslationPicker.SelectedItem = TranslationPicker.Items.Cast<KodikTranslation>().FirstOrDefault(x => x.Id == _playingTranslationId);
                    SetEpisodes(); EpisodePicker.SelectedItem = previous;
                }
                finally { _changingPickers = false; }
                if (wasPlaying && !_usingWeb) _mediaPlayer?.Play();
                PlayerStatus.Text = "Не удалось загрузить выбранный поток. Текущее воспроизведение сохранено.";
            }
            else PlayerStatus.Text = ex is System.Net.Http.HttpRequestException ? "Не удалось подключиться к Kodik. Повторите загрузку в меню «Ещё»." : ex.Message;
        }
    }

    private void SetStream(VideoStream stream, bool resetRecovery = true)
    {
        if (_mediaPlayer is null) return;
        _streamRevision++;
        if (resetRecovery) _streamRecovery.Reset();
        _mediaPlayer.Source = MediaSource.CreateFromUri(stream.Uri);
        _mediaPlayer.Play();
    }
    private async Task RecoverStreamAsync(string error)
    {
        if (_playingAnime is null || _usingWeb || QualityPicker.SelectedItem is not VideoStream current) return;
        _preparingPlayback = false;
        var revision = _streamRevision; var ct = _playCts?.Token ?? default;
        var recovery = _streamRecovery.Next(current, _resolved?.Streams ?? []);
        if (recovery is null) { PlayerStatus.Text = "Поток недоступен. Повторите загрузку или откройте веб-плеер в меню «Ещё»."; ToolTipService.SetToolTip(PlayerStatus, error); RevealPlayerChrome(); return; }
        _pendingPosition = Math.Max(_pendingPosition, _mediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0);
        if (recovery.Delay > TimeSpan.Zero) await Task.Delay(recovery.Delay, ct);
        if (ct.IsCancellationRequested || _streamRevision != revision || _playingAnime is null || _usingWeb) return;
        if (recovery.Fallback)
        {
            _changingPickers = true; try { QualityPicker.SelectedItem = recovery.Stream; } finally { _changingPickers = false; }
            PlayerStatus.Text = $"Пробуем качество {recovery.Stream.Quality}…";
        }
        else PlayerStatus.Text = "Повторное подключение к потоку…";
        SetStream(recovery.Stream, resetRecovery: false);
    }

    private async void Translation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingPickers || _playingAnime is null) return;
        await RunAsync(async () =>
        {
            if (_playingEpisode is { } current && TranslationPicker.SelectedItem is KodikTranslation selected && new PlaybackStart(current.Number, current.Season).FindEpisode(selected.Episodes) is null)
            {
                _changingPickers = true;
                try { TranslationPicker.SelectedItem = (TranslationPicker.ItemsSource as IEnumerable<KodikTranslation>)?.FirstOrDefault(x => x.Id == _playingTranslationId); }
                finally { _changingPickers = false; }
                throw new InvalidOperationException($"Эпизод {current.Number} недоступен в этой озвучке. Текущая озвучка сохранена.");
            }
            await SaveProgressAsync();
            _prefetch?.Reset();
            if (_playingEpisode is { } ep && _mediaPlayer is not null)
                _resume = new(_playingAnime, ep.Number, _mediaPlayer.PlaybackSession.Position.TotalSeconds, 0, DateTimeOffset.UtcNow, Season: ep.Season);
            _changingPickers = true; try { SetEpisodes(); } finally { _changingPickers = false; }
            await PlaySelectedAsync();
        });
    }
    private async void Episode_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_changingPickers && _playingAnime is not null) await RunAsync(PlaySelectedAsync); }
    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingPickers || _usingWeb || QualityPicker.SelectedItem is not VideoStream stream || _mediaPlayer is null) return;
        _pendingPosition = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
        _playerPreferredQuality = stream.Quality;
        if (!_playerDiagnostic) _services.Settings.PreferredQuality = stream.Quality;
        SetStream(stream);
    }

    private async void Progress_Tick(object? sender, object e)
    {
        await RunAsync(async () =>
        {
            if (_usingWeb || _mediaPlayer is null || _playingEpisode is null) return;
            var session = _mediaPlayer.PlaybackSession;
            var position = session.Position.TotalSeconds;
            var duration = session.NaturalDuration.TotalSeconds;
            if (_services.Settings.AutoSkipChapters)
            {
                if (PlayerTimeline.SkipTarget(position, duration, _resolved?.Opening, _resolved?.Ending) is { } target) SeekPlayer(target);
            }
            await SaveProgressAsync();
            if (duration > 0 && position / duration >= 0.9 && _services.Auth.IsSignedIn) await MarkWatchedAsync();
        });
    }

    private async Task SaveProgressAsync()
    {
        if (_usingWeb || _playingAnime is null || _playingEpisode is null || _mediaPlayer is null) return;
        var position = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
        var duration = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        if (position < 1 || duration <= 0) return;
        var entry = new WatchEntry(_playingAnime, _playingEpisode.Number, position, duration, DateTimeOffset.UtcNow,
            _playingTranslationId, _playingEpisode.Season);
        var point = EpisodeProgress.From(entry);
        if (_playerDiagnostic) { _diagnosticProgress[point.Key] = point; return; }
        var completed = _markedWatched;
        await _progressWrite.WaitAsync();
        try { if (!completed) await _services.Progress.SaveAsync(point); await _services.History.SaveAsync(entry); }
        finally { _progressWrite.Release(); }
    }

    private async Task MarkWatchedAsync()
    {
        if (_playerDiagnostic || _markedWatched || _playingAnime is null || _playingEpisode is null) return;
        _markedWatched = true;
        var anime = _playingAnime;
        var episode = _playingEpisode;
        var position = _usingWeb ? 0 : _mediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0;
        var duration = _usingWeb ? 0 : _mediaPlayer?.PlaybackSession.NaturalDuration.TotalSeconds ?? 0;
        var translation = _playingTranslationId;
        try
        {
            if (_services.Auth.IsSignedIn)
            {
                var user = await CurrentUserAsync();
                var watched = Math.Max(anime.UserRate?.Episodes ?? 0, episode.Number);
                var status = anime.Status == "released" && anime.Episodes > 0 && watched >= anime.Episodes ? "completed" : "watching";
                var rate = await _services.Shikimori.SaveRateAsync(user.Id, anime, status, anime.UserRate?.Score ?? 0, watched);
                await _services.Cache.ClearAsync();
                var updated = anime with { UserRate = rate };
                if (_playingAnime?.Id == anime.Id && _playingEpisode == episode) { _playingAnime = updated; PlayerStatus.Text = "Просмотр синхронизирован с Shikimori ✓"; }
                if (_detail?.Id == anime.Id) _detail = updated;
            }
            else PlayerStatus.Text = "Просмотр сохранён на этом компьютере ✓";
            await _services.Progress.SaveAsync(new(anime.Id, episode.Season, episode.Number, Math.Max(2, duration), Math.Max(2, duration), DateTimeOffset.UtcNow));
            await _services.History.SaveAsync(new(anime, episode.Number, position, duration, DateTimeOffset.UtcNow, translation, episode.Season));
        }
        catch { if (_playingAnime?.Id == anime.Id && _playingEpisode == episode) _markedWatched = false; throw; }
    }

    private async Task NextEpisodeAsync()
    {
        if (NextAvailableEpisode() is { } next)
        {
            _changingPickers = true; EpisodePicker.SelectedItem = next; _changingPickers = false;
            await PlaySelectedAsync();
        }
        else PlayerStatus.Text = "Это последняя доступная серия.";
    }
    private async Task HandleEpisodeEndedAsync()
    {
        if (_playingAnime is null || _usingWeb) return;
        var revision = _streamRevision;
        await SaveProgressAsync();
        if (_streamRevision != revision) return;
        try { await MarkWatchedAsync(); }
        catch (Exception) { PlayerStatus.Text = "Не удалось синхронизировать просмотр. Повторите отметку в меню «Ещё»."; }
        if (_streamRevision == revision && _services.Settings.AutoNext) await NextEpisodeAsync();
    }
    private KodikEpisode? NextAvailableEpisode() => _playingEpisode is { } current
        ? EpisodePicker.Items.Cast<KodikEpisode>().FirstOrDefault(x => x.Season == current.Season && x.Number == current.Number + 1)
        : null;
    private async void NextEpisode_Click(object sender, RoutedEventArgs e) => await RunAsync(NextEpisodeAsync);
    private async void MarkWatched_Click(object sender, RoutedEventArgs e) { PlayerMoreFlyout.Hide(); await RunAsync(MarkWatchedAsync); }
    private void SkipOpening_Click(object sender, RoutedEventArgs e)
    { SkipActiveChapter(); }
    private void SkipEnding_Click(object sender, RoutedEventArgs e)
    { SkipActiveChapter(); }
    private void SkipActiveChapter()
    { if (_mediaPlayer is { } player && PlayerTimeline.SkipTarget(player.PlaybackSession.Position.TotalSeconds, player.PlaybackSession.NaturalDuration.TotalSeconds, _resolved?.Opening, _resolved?.Ending) is { } target) SeekPlayer(target); }
    private async void ClosePlayer_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(ReturnToAnimeAsync);
    }
    private async Task ReturnToAnimeAsync()
    {
        var anime = _playingAnime;
        await StopPlaybackAsync();
        Activate();
        if (anime is not null) await ShowDetailAsync(anime); else await LoadSectionAsync();
    }

    private async Task StopPlaybackAsync(bool closeWindow = true)
    {
        _playerChoiceFlyout?.Hide(); PlayerSubtitlesFlyout.Hide(); PlayerMoreFlyout.Hide();
        await SaveProgressAsync();
        _chromeTimer?.Stop();
        _adjustingVolume = false; _volumePointerId = null;
        if (_mediaPlayer is not null)
        {
            _services.Settings.PlayerVolume = _mediaPlayer.Volume;
            _services.Settings.PlayerSpeed = _playerSpeed;
            await PersistPlayerPreferencesAsync();
        }
        ResetSubtitles();
        _playCts?.Cancel(); _progressTimer?.Stop();
        _streamRevision++; _streamRecovery.Reset();
        _prefetch?.Reset(); _preparingPlayback = false; PlayerBuffering.IsActive = false;
        _mediaPlayer?.Pause();
        if (_mediaPlayer is not null) _mediaPlayer.Source = null;
        _webView?.Close(); _webView = null; WebPlayerHost.Children.Clear();
        _playingAnime = null; _playingEpisode = null; _playingTranslationId = 0; _resolved = null; _resume = null; _usingWeb = false;
        PlayerView.Visibility = Visibility.Collapsed;
        if (closeWindow) { _openPlayerCts?.Cancel(); ClosePlayerWindow(); }
    }

    private async void WebPlayer_Click(object sender, RoutedEventArgs e)
    {
        PlayerMoreFlyout.Hide();
        await RunAsync(async () =>
        {
            if (_playingEpisode is not { } episode) return;
            await SaveProgressAsync();
            _playCts?.Cancel();
            _mediaPlayer?.Pause(); _usingWeb = true;
            NativePlayer.Visibility = Visibility.Collapsed; WebPlayerHost.Visibility = Visibility.Visible;
            SkipOpening.Visibility = Visibility.Collapsed;
            SkipEnding.Visibility = Visibility.Collapsed; CenterPlay.Visibility = Visibility.Collapsed;
            if (_webView is not null) return;
            _webView = new WebView2(); WebPlayerHost.Children.Add(_webView);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShikiPlayer", "WebView2"), new CoreWebView2EnvironmentOptions());
            await _webView.EnsureCoreWebView2Async(environment);
            if (!_usingWeb || _playingEpisode != episode) return;
            _webView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            _webView.CoreWebView2.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            _webView.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
            // Kodik explicitly rejects top-level navigation (isIframe()).
            // Host its public embed in a frame, as a regular embedding site does.
            var source = System.Net.WebUtility.HtmlEncode(episode.Link.AbsoluteUri);
            _webView.NavigateToString($"<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>html,body{{margin:0;width:100%;height:100%;background:#000;overflow:hidden}}iframe{{position:absolute;inset:0;width:100%;height:100%;border:0}}</style></head><body><iframe src=\"{source}\" allow=\"autoplay; fullscreen; picture-in-picture\" allowfullscreen></iframe></body></html>");
            PlayerStatus.Text = "Веб-плеер · отметьте просмотр вручную; позиция не синхронизируется";
        });
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(_playerWindow?.AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);
}
