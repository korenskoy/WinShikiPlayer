using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private DispatcherTimer? _chromeTimer;
    private DateTimeOffset _lastPointer = DateTimeOffset.UtcNow;
    private int _playerFlyouts;
    private bool _preparingPlayback, _timelineConnected;
    private double _playerSpeed = 1;
    private EpisodePrefetch? _prefetch;
    private Flyout? _playerChoiceFlyout;
    private bool _syncingVolume, _adjustingVolume, _volumePointerHandlersConnected;
    private uint? _volumePointerId;
    private LocalStore? _playerPreferenceTestStore;
    private Task PersistPlayerPreferencesAsync() => _playerDiagnostic && _playerPreferenceTestStore is null
        ? Task.CompletedTask : (_playerPreferenceTestStore ?? _services.Store).WriteAsync("settings", _services.Settings);
    private void StartPlayerChrome()
    {
        RevealPlayerChrome(); PlayerChrome.Visibility = Visibility.Visible;
        PlayerSubtitlesFlyout.FlyoutPresenterStyle = PlayerPopoverStyle(12);
        PlayerMoreFlyout.FlyoutPresenterStyle = PlayerPopoverStyle(8);
        VolumeSlider.Value = _services.Settings.PlayerVolume;
        if (!_volumePointerHandlersConnected)
        {
            VolumeSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Volume_PointerPressed), true);
            VolumeSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Volume_PointerFinished), true);
            VolumeSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Volume_PointerFinished), true);
            VolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Volume_PointerFinished), true);
            _volumePointerHandlersConnected = true;
        }
        _playerSpeed = _services.Settings.PlayerSpeed;
        SpeedPicker.SelectedItem = SpeedPicker.Items.Cast<ComboBoxItem>().FirstOrDefault(x => double.Parse((string)x.Tag, CultureInfo.InvariantCulture) == _services.Settings.PlayerSpeed) ?? SpeedPicker.Items[2];
        if (!_timelineConnected) { SeekTimeline.SeekRequested += (_, position) => SeekPlayer(position); _timelineConnected = true; }
        _prefetch ??= new EpisodePrefetch((link, ct) => _services.Resolver.ResolveAsync(link, ct));
        _chromeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _chromeTimer.Tick -= Chrome_Tick; _chromeTimer.Tick += Chrome_Tick; _chromeTimer.Start();
    }
    private void Chrome_Tick(object? sender, object e)
    {
        if (_mediaPlayer is null || _playingAnime is null) return;
        var session = _mediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration.TotalSeconds; var position = session.Position.TotalSeconds;
        SeekTimeline.Update(duration, position, _resolved?.Opening, _resolved?.Ending);
        PlaybackTime.Text = $"{PlayerTimeline.Time(position)} / {PlayerTimeline.Time(duration)}";
        var playing = session.PlaybackState == MediaPlaybackState.Playing;
        var buffering = _preparingPlayback || session.PlaybackState is MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
        PlayerBuffering.IsActive = buffering && !_usingWeb;
        CenterPlay.Visibility = !playing && !buffering && !_usingWeb && duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        AutomationProperties.SetName(PlayPauseButton, playing ? "Пауза" : "Воспроизвести");
        VolumeIcon.Glyph = _mediaPlayer.IsMuted || _mediaPlayer.Volume == 0 ? "\uE74F" : "\uE767";
        DubValue.Text = (TranslationPicker.SelectedItem as KodikTranslation)?.Title ?? "—";
        QualityValue.Text = (QualityPicker.SelectedItem as VideoStream)?.Quality ?? "—";
        SpeedValue.Text = $"×{_playerSpeed.ToString("0.##", CultureInfo.InvariantCulture)}";
        if (!_adjustingVolume && Math.Abs(VolumeSlider.Value - _mediaPlayer.Volume) > 0.001)
        {
            _syncingVolume = true;
            try { VolumeSlider.Value = _mediaPlayer.Volume; }
            finally { _syncingVolume = false; }
        }
        DubButton.IsEnabled = !_preparingPlayback && TranslationPicker.Items.Count > 0;
        QualityButton.IsEnabled = !_usingWeb && !_preparingPlayback && QualityPicker.Items.Count > 0;
        SpeedButton.IsEnabled = !_usingWeb; SeekTimeline.IsHitTestVisible = !_usingWeb && duration > 0;
        PlayPauseButton.IsEnabled = !_usingWeb; EpisodesButton.IsEnabled = !_preparingPlayback;
        SeekTimeline.Visibility = PlayerTransport.Visibility = _usingWeb ? Visibility.Collapsed : Visibility.Visible;
        WebPlayerHost.Margin = _usingWeb ? new Thickness(0, 84, 0, 68) : new Thickness(0);
        SubtitlesButton.IsEnabled = !_usingWeb && !_preparingPlayback;
        var active = PlayerTimeline.Active(position, _resolved?.Opening, _resolved?.Ending);
        SkipOpening.Visibility = !_usingWeb && active is not null && active == _resolved?.Opening ? Visibility.Visible : Visibility.Collapsed;
        SkipEnding.Visibility = !_usingWeb && active is not null && active != _resolved?.Opening ? Visibility.Visible : Visibility.Collapsed;
        var next = NextAvailableEpisode();
        NextEpisodeCard.Visibility = !_usingWeb && PlayerTimeline.ShowNext(position, duration, next is not null) ? Visibility.Visible : Visibility.Collapsed;
        if (next is not null) NextEpisodeTitle.Text = $"Следующий эпизод {next.Number}";
        NextEpisodeCountdown.Text = $"через {PlayerTimeline.Time(Math.Max(0, duration - position))}";
        var hide = playing && !buffering && !_usingWeb && _playerFlyouts == 0 && !SeekTimeline.IsDragging && !_adjustingVolume && DateTimeOffset.UtcNow - _lastPointer > TimeSpan.FromSeconds(3);
        if (PlayerChrome.IsHitTestVisible == hide) { PlayerChrome.Opacity = hide ? 0 : 1; PlayerChrome.IsHitTestVisible = !hide; }
        SyncPlayerPin(playing);
        if (!_usingWeb && duration > 0 && position / duration >= 0.7 && next is not null) _prefetch?.Prepare(next.Link);
    }
    private void RevealPlayerChrome() { _lastPointer = DateTimeOffset.UtcNow; if (PlayerChrome is not null) { PlayerChrome.Opacity = 1; PlayerChrome.IsHitTestVisible = true; } }
    private void Player_PointerMoved(object sender, PointerRoutedEventArgs e) => RevealPlayerChrome();
    private void Player_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PlayerChoices is null) return;
        var narrow = e.NewSize.Width < 1100;
        Grid.SetRow(PlayerChoices, narrow ? 1 : 0); Grid.SetColumn(PlayerChoices, narrow ? 0 : 2);
        Grid.SetColumnSpan(PlayerChoices, narrow ? 3 : 1); PlayerChoices.HorizontalAlignment = HorizontalAlignment.Right;
        PlayerTransport.Spacing = narrow ? 10 : 12;
    }
    private void Video_Tapped(object sender, TappedRoutedEventArgs e) { PlayPause_Click(sender, new()); e.Handled = true; }
    private void Video_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { Fullscreen_Click(sender, new()); e.Handled = true; }
    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null || _usingWeb) return;
        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _mediaPlayer.Pause(); else _mediaPlayer.Play();
        RevealPlayerChrome();
    }
    private void SeekPlayer(double position)
    {
        if (_mediaPlayer is null || _usingWeb) return;
        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(PlayerTimeline.Clamp(position, _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds));
        RevealPlayerChrome();
    }
    private void SeekDelta(double seconds) { if (_mediaPlayer is not null) SeekPlayer(_mediaPlayer.PlaybackSession.Position.TotalSeconds + seconds); }
    private void Rewind_Click(object sender, RoutedEventArgs e) => SeekDelta(-10);
    private void FastForward_Click(object sender, RoutedEventArgs e) => SeekDelta(10);
    private void Skip80_Click(object sender, RoutedEventArgs e) => SeekDelta(80);
    private void Mute_Click(object sender, RoutedEventArgs e) { if (_mediaPlayer is not null) _mediaPlayer.IsMuted = !_mediaPlayer.IsMuted; RevealPlayerChrome(); }
    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVolume || _mediaPlayer is null) return;
        _mediaPlayer.Volume = e.NewValue;
        if (!_playerDiagnostic) _services.Settings.PlayerVolume = e.NewValue;
        RevealPlayerChrome();
    }
    private void Volume_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(VolumeSlider).Properties.IsLeftButtonPressed) return;
        _adjustingVolume = true; _volumePointerId = e.Pointer.PointerId; RevealPlayerChrome();
    }
    private void Volume_PointerFinished(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _volumePointerId) return;
        _adjustingVolume = false; _volumePointerId = null; RevealPlayerChrome();
    }
    private async void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_mediaPlayer is null || SelectedTag(SpeedPicker) is not { } tag) return;
        var speed = double.Parse(tag, CultureInfo.InvariantCulture);
        _playerSpeed = speed; _services.Settings.PlayerSpeed = speed;
        _mediaPlayer.PlaybackSession.PlaybackRate = speed;
        await RunAsync(PersistPlayerPreferencesAsync);
    }
    private void PlayerFlyout_Opening(object? sender, object e) { _playerFlyouts++; RevealPlayerChrome(); SubtitleChoicesScroll.MaxHeight = Math.Clamp(PlayerView.ActualHeight - 230, 120, 380); }
    private void PlayerFlyout_Closed(object? sender, object e) { _playerFlyouts = Math.Max(0, _playerFlyouts - 1); RevealPlayerChrome(); PlayerView.Focus(FocusState.Programmatic); }
    private void ShowPlayerChoices<T>(Button anchor, string heading, IEnumerable<T> options, Func<T, string> title, Func<T, bool> selected, Action<T> choose, bool episodes = false)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxHeight = episodes ? 330 : 320, Background = new SolidColorBrush(Colors.Transparent) };
        ListViewItem? current = null;
        foreach (var option in options)
        {
            var active = selected(option);
            var row = new Grid { Padding = new(8), ColumnSpacing = 10, Tag = option, CornerRadius = new(6), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(active ? (byte)15 : (byte)0, 255, 255, 255)) };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = title(option), FontSize = 12, FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Colors.White) });
            if (active) { var check = new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 77, 94)) }; Grid.SetColumn(check, 1); row.Children.Add(check); }
            var item = new ListViewItem { Content = row, Tag = option, Padding = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, title(option)); list.Items.Add(item); if (active) current = item;
        }
        var body = new StackPanel { Width = episodes ? 280 : 260, Spacing = 8, RequestedTheme = ElementTheme.Dark };
        body.Children.Add(new TextBlock { Text = heading, FontFamily = new FontFamily("Consolas"), FontSize = 10, CharacterSpacing = 140, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)), Margin = new(10, 4, 0, 0) }); body.Children.Add(list);
        var flyout = new Flyout { Content = body, Placement = FlyoutPlacementMode.Top, FlyoutPresenterStyle = PlayerPopoverStyle(6) };
        _playerChoiceFlyout?.Hide(); _playerChoiceFlyout = flyout;
        flyout.Closed += (_, _) => { if (ReferenceEquals(_playerChoiceFlyout, flyout)) _playerChoiceFlyout = null; };
        flyout.Opening += PlayerFlyout_Opening; flyout.Closed += PlayerFlyout_Closed;
        flyout.Opened += (_, _) => { if (current is not null) list.ScrollIntoView(current); };
        list.ItemClick += (_, args) =>
        {
            // WinUI unwraps an explicit item container to its Content here.
            if (args.ClickedItem is FrameworkElement { Tag: T value }) { flyout.Hide(); choose(value); }
            else if (args.ClickedItem is T direct) { flyout.Hide(); choose(direct); }
        };
        flyout.ShowAt(anchor);
    }
    private void Dub_Click(object sender, RoutedEventArgs e) => ShowPlayerChoices(DubButton, "ОЗВУЧКА", TranslationPicker.Items.Cast<KodikTranslation>(), x => x.Title, x => Equals(x, TranslationPicker.SelectedItem), x => TranslationPicker.SelectedItem = x);
    private static Style PlayerPopoverStyle(double padding) => new(typeof(FlyoutPresenter))
    {
        Setters = { new Setter(Control.BackgroundProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 18, 24))), new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.White)), new Setter(FrameworkElement.RequestedThemeProperty, ElementTheme.Dark), new Setter(Control.PaddingProperty, new Thickness(padding)), new Setter(Control.CornerRadiusProperty, new CornerRadius(12)) }
    };
    private void QualityMenu_Click(object sender, RoutedEventArgs e) => ShowPlayerChoices(QualityButton, "КАЧЕСТВО", QualityPicker.Items.Cast<VideoStream>(), x => x.Quality, x => Equals(x, QualityPicker.SelectedItem), x => QualityPicker.SelectedItem = x);
    private void SpeedMenu_Click(object sender, RoutedEventArgs e) => ShowPlayerChoices(SpeedButton, "СКОРОСТЬ", SpeedPicker.Items.Cast<ComboBoxItem>(), x => $"×{x.Tag}", x => Equals(x, SpeedPicker.SelectedItem), x => SpeedPicker.SelectedItem = x);
    private void EpisodesMenu_Click(object sender, RoutedEventArgs e) => ShowPlayerChoices(EpisodesButton, "ЭПИЗОДЫ", EpisodePicker.Items.Cast<KodikEpisode>(), x => $"{x.Number:00}    {x.Label}", x => Equals(x, EpisodePicker.SelectedItem), x => EpisodePicker.SelectedItem = x, true);
    private async void RetryPlayback_Click(object sender, RoutedEventArgs e)
    {
        PlayerMoreFlyout.Hide();
        await RunAsync(async () =>
        {
            if (_playingAnime is null || EpisodePicker.SelectedItem is not KodikEpisode selected) return;
            _services.Resolver.Invalidate(selected.Link);
            if (_playingEpisode is { } ep) _resume = new(_playingAnime, ep.Number, _mediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0, 0, DateTimeOffset.UtcNow, _playingTranslationId, ep.Season);
            await PlaySelectedAsync();
        });
    }
}
