using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private Window? _playerWindow;
    private Panel? _playerOriginalParent;
    private bool _closingPlayerWindow, _mainClosing, _playerDiagnostic;
    private void OpenPlayerWindow()
    {
        if (_playerWindow is not null) { PlayerView.Visibility = Visibility.Visible; _playerWindow.Activate(); return; }
        _playerOriginalParent = (Panel)PlayerView.Parent;
        _playerOriginalParent.Children.Remove(PlayerView);
        var host = new Grid { Background = new SolidColorBrush(Colors.Black), RequestedTheme = ElementTheme.Dark };
        host.Children.Add(PlayerView);
        _playerWindow = new Window { Title = "Плеер · WinShikiPlayer", Content = host };
        _playerWindow.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        _playerWindow.ExtendsContentIntoTitleBar = true;
        var drag = new Grid { Height = 30, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Colors.Transparent), Margin = new(160, 0, 160, 0) };
        host.Children.Add(drag); _playerWindow.SetTitleBar(drag);
        PlayerTopBar.Margin = new(12, 34, 28, 0); PlayerStatus.Margin = new(40, 98, 40, 0);
        var titleBar = _playerWindow.AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.White; titleBar.ButtonInactiveForegroundColor = Colors.Gray;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Min((int)(1280 * scale), area.Width - 80); var height = Math.Min((int)(800 * scale), area.Height - 80);
        if (Environment.GetCommandLineArgs().Contains("--narrow-window")) { width = Math.Min(width, (int)(960 * scale)); height = Math.Min(height, (int)(620 * scale)); }
        _playerWindow.AppWindow.MoveAndResize(new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        _playerWindow.AppWindow.Closing += async (_, args) =>
        {
            if (_closingPlayerWindow) return;
            args.Cancel = true;
            await RunAsync(async () => { await StopPlaybackAsync(); if (!_mainClosing) Activate(); });
        };
        PlayerView.Visibility = Visibility.Visible; _playerWindow.Activate(); PlayerView.Focus(FocusState.Programmatic);
    }
    private void ClosePlayerWindow()
    {
        if (_playerWindow is null) return;
        _closingPlayerWindow = true;
        var window = _playerWindow; _playerWindow = null;
        if (window.Content is Panel host) host.Children.Remove(PlayerView);
        _playerOriginalParent?.Children.Add(PlayerView); _playerOriginalParent = null;
        window.Close(); _closingPlayerWindow = false; _playerFlyouts = 0;
    }
    private void PinPlayer_Click(object sender, RoutedEventArgs e)
    {
        _services.Settings.PlayerAlwaysOnTop = !_services.Settings.PlayerAlwaysOnTop;
        SyncPlayerPin(_mediaPlayer?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing);
    }
    private void SyncPlayerPin(bool playing)
    {
        var enabled = _services.Settings.PlayerAlwaysOnTop;
        if (_playerWindow?.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = enabled && playing;
        PinPlayerIcon.Glyph = enabled ? "\uE840" : "\uE718";
        PinPlayerButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(enabled ? (byte)46 : (byte)15, 255, 255, 255));
        ToolTipService.SetToolTip(PinPlayerButton, enabled ? "Поверх других окон: вкл" : "Поверх других окон: выкл");
    }
}
