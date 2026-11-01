using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.System;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private readonly NavigationHistory _navigation = new();
    private bool _restoringNavigation;
    private async Task RecordNavigationAsync(NavigationEntry entry)
    {
        if (_restoringNavigation) return;
        _navigation.Push(entry); UpdateNavigationButtons();
        await _services.Store.WriteAsync("navigation", _navigation.Snapshot());
    }
    private void UpdateNavigationButtons()
    { BackButton.IsEnabled = _navigation.CanGoBack; ForwardButton.IsEnabled = _navigation.CanGoForward; }
    private async Task NavigateHistoryAsync(bool forward)
    {
        var entry = forward ? _navigation.Forward() : _navigation.Back(); if (entry is null) return;
        _restoringNavigation = true;
        try
        {
            _section = entry.Section; _query = entry.Search;
            if (entry.AnimeId is { } animeId) await ShowDetailAsync(new Anime { Id = animeId });
            else if (entry.TopicId is { } topicId) await ShowTopicAsync(topicId);
            else if (entry.Section == "profile" && _services.Auth.IsSignedIn) await ShowProfileAsync();
            else await LoadSectionAsync();
        }
        finally { _restoringNavigation = false; UpdateNavigationButtons(); await _services.Store.WriteAsync("navigation", _navigation.Snapshot()); }
    }
    private async void Back_Click(object sender, RoutedEventArgs args) => await RunAsync(() => NavigateHistoryAsync(false));
    private async void Forward_Click(object sender, RoutedEventArgs args) => await RunAsync(() => NavigateHistoryAsync(true));
    private async void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; await RunAsync(() => NavigateHistoryAsync(false)); }
    private async void Forward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; await RunAsync(() => NavigateHistoryAsync(true)); }
    private void PlayerShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_playingAnime is null || _mediaPlayer is null || _dialogOpen || _usingWeb ||
            _playerFlyouts > 0 || FocusManager.GetFocusedElement(PlayerView.XamlRoot) is TextBox or NumberBox or ComboBox or Controls.ChapterScrubber or Slider) return;
        var player = _mediaPlayer; var session = player.PlaybackSession;
        switch (sender.Key)
        {
            case VirtualKey.Space:
                if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing) player.Pause(); else player.Play(); break;
            case VirtualKey.Left: SeekDelta(-10); break;
            case VirtualKey.Right: SeekDelta(10); break;
            case VirtualKey.Up: player.Volume = Math.Min(1, player.Volume + 0.05); break;
            case VirtualKey.Down: player.Volume = Math.Max(0, player.Volume - 0.05); break;
            case VirtualKey.M: player.IsMuted = !player.IsMuted; break;
            case VirtualKey.F: Fullscreen_Click(this, new()); break;
            case VirtualKey.S: SkipActiveChapter(); break;
            case VirtualKey.Escape: if (_playerWindow?.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) SetFullscreen(false); else ClosePlayer_Click(this, new()); break;
            default: return;
        }
        args.Handled = true;
        RevealPlayerChrome();
    }
    private void SetFullscreen(bool value)
    {
        if (_playerWindow is null) return;
        _playerWindow.AppWindow.SetPresenter(value ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        FullscreenIcon.Glyph = value ? "\uE73F" : "\uE740";
        SyncPlayerPin(_mediaPlayer?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing);
    }
}
