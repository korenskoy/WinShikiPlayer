using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinShikiPlayer.Core;
using WinShikiPlayer.Services;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private AssRenderer? _assRenderer;
    private DispatcherTimer? _subtitleTimer;
    private WriteableBitmap? _subtitleBitmap;
    private CancellationTokenSource? _subtitleCts;
    private bool _changingSubtitles;
    private string? _subtitleOffsetKey;
    private bool _subtitleLoading;
    private int _subtitleGeneration;
    private readonly SemaphoreSlim _subtitleOffsetWrites = new(1, 1);
    private readonly Dictionary<string, double> _diagnosticSubtitleOffsets = [];

    private Task InitializeSubtitlesAsync()
    {
        ResetSubtitles();
        return Task.CompletedTask;
    }
    private async void FindSubtitles_Click(object sender, RoutedEventArgs e) => await RunAsync(FindSubtitlesAsync);
    private async Task FindSubtitlesAsync()
    {
        if (_playingAnime is null || _playingEpisode is null) return;
        _subtitleCts?.Cancel();
        _subtitleCts = new(); var ct = _subtitleCts.Token;
        FindSubtitlesButton.IsEnabled = false;
        SubtitleStatus.Text = "Поиск субтитров…";
        try
        {
            var candidates = await _services.Subtitles.SearchAsync(_playingAnime.Id, _playingEpisode.Number, "auto", ct);
            ct.ThrowIfCancellationRequested();
            _changingSubtitles = true; SubtitlePicker.Items.Clear(); SubtitlePicker.Items.Add("Выключены"); SubtitlePicker.SelectedIndex = 0;
            foreach (var candidate in candidates) SubtitlePicker.Items.Add(candidate);
            _changingSubtitles = false;
            SubtitleStatus.Text = candidates.Count == 0 ? "Для этой серии субтитров нет" : $"Найдено дорожек: {candidates.Count}";
            FindSubtitlesButton.Visibility = candidates.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            FindSubtitlesButton.Content = "Повторить поиск";
            RenderSubtitleChoices();
            var preferred = PreferredSubtitleLanguage();
            if (candidates.FirstOrDefault(x => x.LanguageCode == preferred) is { } match) SubtitlePicker.SelectedItem = match;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { SubtitleStatus.Text = "Источник субтитров недоступен; можно повторить поиск или открыть файл"; FindSubtitlesButton.Content = "Повторить поиск"; }
        finally { if (!ct.IsCancellationRequested) { _changingSubtitles = false; FindSubtitlesButton.IsEnabled = true; } }
    }
    private void RenderSubtitleChoices()
    {
        SubtitleChoices.Children.Clear();
        var candidates = SubtitlePicker.Items.OfType<SubtitleCandidate>().ToList();
        SubtitleChoicesScroll.Visibility = candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitlePopover.Width = candidates.Count > 0 ? 380 : 280;
        void Row(string title, object value, string? tooltip = null)
        {
            var selected = Equals(SubtitlePicker.SelectedItem, value);
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12, FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium });
            if (selected) { var check = new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 77, 94)) }; Grid.SetColumn(check, 1); row.Children.Add(check); }
            var button = new Button { Content = row, IsEnabled = !_subtitleLoading, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(selected ? (byte)15 : (byte)0, 255, 255, 255)), BorderThickness = new(0), FontSize = 12, Padding = new(10, 8, 10, 8) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
            ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(tooltip) ? title : tooltip); button.Click += (_, _) => { if (_subtitleLoading) return; if (Equals(SubtitlePicker.SelectedItem, value)) Subtitle_Changed(SubtitlePicker, null!); else SubtitlePicker.SelectedItem = value; }; SubtitleChoices.Children.Add(button);
        }
        Row("Выкл", SubtitlePicker.Items[0]);
        var preferred = PreferredSubtitleLanguage();
        foreach (var group in candidates.GroupBy(x => x.LanguageCode).OrderByDescending(x => x.Key == preferred))
        {
            SubtitleChoices.Children.Add(new TextBlock { Text = SubtitleLanguages.GroupTitle(group.Key), FontSize = 10, CharacterSpacing = 150, Opacity = 0.55, Margin = new(10, 8, 10, 2) });
            foreach (var candidate in group) Row(candidate.StudioName, candidate, candidate.Title);
        }
    }
    private string PreferredSubtitleLanguage() => SubtitleLanguages.Code(_services.Settings.SubtitleLanguage == "auto" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : _services.Settings.SubtitleLanguage);
    private async void Subtitle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSubtitles) return;
        await RunAsync(async () =>
        {
            _subtitleCts?.Cancel(); _subtitleCts = new(); var ct = _subtitleCts.Token;
            if (SubtitlePicker.SelectedItem is not SubtitleCandidate candidate) { DisposeSubtitleRenderer(); _subtitleOffsetKey = null; SubtitleOffsetBar.Visibility = Visibility.Collapsed; SubtitleStatus.Text = "Субтитры выключены"; RenderSubtitleChoices(); return; }
            _subtitleLoading = true; RenderSubtitleChoices(); SubtitleChoices.Opacity = 0.5;
            SubtitleStatus.Text = "Загрузка дорожки…";
            try
            {
                var bytes = await _services.Subtitles.DownloadAsync(_services.Settings.SubtitleStudioStyle ? candidate.AssUri : candidate.VttUri, ct);
                ct.ThrowIfCancellationRequested();
                if (!_services.Settings.SubtitleStudioStyle) bytes = Encoding.UTF8.GetBytes(SubtitleParser.ToAss(SubtitleParser.ParseVtt(Encoding.UTF8.GetString(bytes)), _services.Settings));
                await LoadSubtitleBytesAsync(bytes, candidate.Id, ct);
                SubtitleStatus.Text = "Субтитры загружены"; SubtitleOffsetBar.Visibility = Visibility.Visible;
                RenderSubtitleChoices();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { SubtitleStatus.Text = "Не удалось загрузить дорожку. Нажмите её ещё раз для повтора."; }
            finally { if (!ct.IsCancellationRequested) { _subtitleLoading = false; SubtitleChoices.Opacity = 1; RenderSubtitleChoices(); } }
        });
    }
    private void ResetSubtitleOffset_Click(object sender, RoutedEventArgs e) => SubtitleOffset.Value = 0;
    private void AdjustSubtitleOffset_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string value } && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta)) SubtitleOffset.Value = Math.Clamp(SubtitleOffset.Value + delta, -120, 120); }
    private async Task LoadSubtitleBytesAsync(byte[] bytes, int trackId, CancellationToken ct)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Файл субтитров слишком большой.");
        var key = $"{_playingAnime?.Id}-{_playingEpisode?.Season}-{_playingEpisode?.Number}-{trackId}";
        var offsets = _playerDiagnostic ? _diagnosticSubtitleOffsets : await _services.Store.ReadAsync<Dictionary<string, double>>("subtitle-offsets") ?? [];
        ct.ThrowIfCancellationRequested();
        var renderer = new AssRenderer();
        try { renderer.Load(bytes); } catch { renderer.Dispose(); throw; }
        DisposeSubtitleRenderer(); _assRenderer = renderer; _subtitleOffsetKey = key;
        _changingSubtitles = true; SubtitleOffset.Value = offsets.GetValueOrDefault(_subtitleOffsetKey); _changingSubtitles = false;
        SubtitleOffsetBar.Visibility = Visibility.Visible;
        _subtitleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _subtitleTimer.Tick -= RenderSubtitle_Tick; _subtitleTimer.Tick += RenderSubtitle_Tick; _subtitleTimer.Start();
    }
    private void RenderSubtitle_Tick(object? sender, object e)
    {
        if (_assRenderer is null || _mediaPlayer is null || _usingWeb) { AssOverlay.Source = null; return; }
        var session = _mediaPlayer.PlaybackSession;
        var width = NativePlayer.ActualWidth; var height = NativePlayer.ActualHeight;
        if (width < 1 || height < 1) return;
        var aspect = session.NaturalVideoHeight > 0 ? session.NaturalVideoWidth / (double)session.NaturalVideoHeight : 16d / 9;
        if (width / height > aspect) width = height * aspect; else height = width / aspect;
        var w = Math.Min(1920, (int)width); var h = (int)(w / aspect);
        try
        {
            var pixels = _assRenderer.Render(w, h, session.Position.TotalSeconds - SubtitleOffset.Value);
            if (pixels is null) return;
            if (_subtitleBitmap is null || _subtitleBitmap.PixelWidth != w || _subtitleBitmap.PixelHeight != h) _subtitleBitmap = new WriteableBitmap(w, h);
            using var output = _subtitleBitmap.PixelBuffer.AsStream(); output.Write(pixels);
            _subtitleBitmap.Invalidate(); AssOverlay.Source = _subtitleBitmap; AssOverlay.Width = width; AssOverlay.Height = height;
        }
        catch (Exception ex) { DisposeSubtitleRenderer(); SubtitleStatus.Text = "Ошибка рендера субтитров: " + ex.Message; }
    }
    private async void SubtitleOffset_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;
        var value = args.NewValue;
        SubtitleOffsetLabel.Text = value == 0 ? "Сдвиг: 0 сек" : $"Сдвиг: {value.ToString("+0.0;−0.0", CultureInfo.InvariantCulture)} сек";
        if (_changingSubtitles || _subtitleOffsetKey is not { } key) return;
        if (_playerDiagnostic) { _diagnosticSubtitleOffsets[key] = value; return; }
        await RunAsync(async () =>
        {
            await _subtitleOffsetWrites.WaitAsync();
            try { var values = await _services.Store.ReadAsync<Dictionary<string, double>>("subtitle-offsets") ?? [];
                values[key] = value; await _services.Store.WriteAsync("subtitle-offsets", values); }
            finally { _subtitleOffsetWrites.Release(); }
        });
    }
    private async void LoadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var picker = new FileOpenPicker();
            var generation = _subtitleGeneration;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_playerWindow ?? this));
            picker.FileTypeFilter.Add(".ass"); picker.FileTypeFilter.Add(".ssa"); picker.FileTypeFilter.Add(".vtt");
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            if (generation != _subtitleGeneration || _playingAnime is null) return;
            _subtitleCts?.Cancel(); _subtitleCts = new(); var ct = _subtitleCts.Token;
            var properties = await file.GetBasicPropertiesAsync(); if (properties.Size > 8 * 1024 * 1024) throw new InvalidOperationException("Файл превышает 8 МБ.");
            var buffer = await FileIO.ReadBufferAsync(file); var bytes = buffer.ToArray();
            if (file.FileType.Equals(".vtt", StringComparison.OrdinalIgnoreCase)) bytes = Encoding.UTF8.GetBytes(SubtitleParser.ToAss(SubtitleParser.ParseVtt(Encoding.UTF8.GetString(bytes)), _services.Settings));
            else if (!_services.Settings.SubtitleStudioStyle) bytes = Encoding.UTF8.GetBytes(SubtitleParser.ToAss(SubtitleParser.ParseAss(Encoding.UTF8.GetString(bytes)), _services.Settings));
            await LoadSubtitleBytesAsync(bytes, 0, ct);
            _changingSubtitles = true; SubtitlePicker.SelectedIndex = 0; _changingSubtitles = false;
            RenderSubtitleChoices(); SubtitlePopover.Width = 380; SubtitleStatus.Text = file.Name;
        });
    }
    private void DisposeSubtitleRenderer()
    { _subtitleTimer?.Stop(); _assRenderer?.Dispose(); _assRenderer = null; _subtitleBitmap = null; AssOverlay.Source = null; }
    private void ResetSubtitles()
    {
        _subtitleGeneration++; _subtitleCts?.Cancel(); DisposeSubtitleRenderer(); _subtitleOffsetKey = null; _subtitleLoading = false;
        SubtitleStatus.Text = ""; SubtitleChoices.Children.Clear(); SubtitleChoices.Opacity = 1;
        _changingSubtitles = true; SubtitlePicker.Items.Clear(); SubtitlePicker.Items.Add("Выключены"); SubtitlePicker.SelectedIndex = 0; _changingSubtitles = false;
        SubtitleChoicesScroll.Visibility = SubtitleOffsetBar.Visibility = Visibility.Collapsed;
        SubtitlePopover.Width = 280; FindSubtitlesButton.Content = "Найти субтитры"; FindSubtitlesButton.Visibility = Visibility.Visible; FindSubtitlesButton.IsEnabled = true;
    }
}
