using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private async Task RunReturnSmokeAsync(string[] arguments)
    {
        var output = arguments.FirstOrDefault(x => x.StartsWith("--diagnostic-output="))?["--diagnostic-output=".Length..];
        var autoNext = _services.Settings.AutoNext;
        var autoSkip = _services.Settings.AutoSkipChapters;
        var stage = "detail";
        object report;
        try
        {
            _services.Settings.AutoNext = false;
            _services.Settings.AutoSkipChapters = false;
            var anime = await _services.Shikimori.AnimeAsync(62001);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                stage = "play-" + cycle;
                await StartPlaybackAsync(anime, new(anime, 1, 0, 0, DateTimeOffset.UtcNow));
                var player = _mediaPlayer!;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(55);
                while (player.PlaybackSession.NaturalDuration.TotalSeconds < 60)
                { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Не загрузился поток."); await Task.Delay(100); }
                if (cycle == 1) SetFullscreen(true);
                stage = "ended-" + cycle;
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Ended(MediaPlayer sender, object args) => ended.TrySetResult();
                player.MediaEnded += Ended;
                try
                {
                    SeekPlayer(player.PlaybackSession.NaturalDuration.TotalSeconds - 1);
                    player.Play();
                    await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await Task.Delay(500);
                }
                finally { player.MediaEnded -= Ended; }
                stage = "return-" + cycle;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await ReturnToAnimeAsync();
                Root.UpdateLayout();
                if (_playerWindow is not null || DetailView.Visibility != Visibility.Visible || _detailWatchAction?.ActualHeight is not > 0)
                    throw new InvalidOperationException("После возврата не построена карточка аниме.");
                _ = Resource<Microsoft.UI.Xaml.Media.Brush>("AccentBrush");
            }
            report = new { success = true, actualMediaEnded = true, returnCycles = 2, fromHome = true,
                fromFullscreen = true, resourcesAfterCollection = true, detailRendered = true, accountWritesSuppressed = true };
        }
        catch (Exception ex) { _mediaPlayer?.Pause(); report = new { success = false, stage, errorType = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace }; }
        finally { _services.Settings.AutoNext = autoNext; _services.Settings.AutoSkipChapters = autoSkip; }
        if (output is not null) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
        if (!arguments.Contains("--keep-player-open")) { await StopPlaybackAsync(); Close(); }
    }

    private async Task RunVolumeSmokeAsync(string[] arguments)
    {
        var output = arguments.FirstOrDefault(x => x.StartsWith("--diagnostic-output="))?["--diagnostic-output=".Length..];
        var preferenceDirectory = Path.Combine(Path.GetTempPath(), "WinShikiPlayer-Preferences-" + Guid.NewGuid().ToString("N"));
        _playerPreferenceTestStore = new LocalStore(preferenceDirectory);
        object report;
        try
        {
            var anime = await _services.Shikimori.AnimeAsync(16498);
            await StartPlaybackAsync(anime, new(anime, 1, 0, 0, DateTimeOffset.UtcNow));
            var player = _mediaPlayer!;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (player.PlaybackSession.NaturalDuration.TotalSeconds <= 60)
            {
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Не загрузился тестовый поток.");
                await Task.Delay(100);
            }
            player.Pause();
            if (Math.Abs(VolumeSlider.StepFrequency - 0.01) > 0.0001 || Math.Abs(VolumeSlider.SmallChange - 0.05) > 0.0001)
                throw new InvalidOperationException("Неверный шаг громкости.");
            double[] levels = [0, 0.13, 0.37, 0.68, 1];
            foreach (var level in levels)
            {
                VolumeSlider.Value = level; await Task.Delay(250); Chrome_Tick(null, new());
                if (Math.Abs(player.Volume - level) > 0.005 || Math.Abs(VolumeSlider.Value - level) > 0.005)
                    throw new InvalidOperationException($"Не сохраняется промежуточный уровень {level}.");
            }
            player.Volume = 0.43; Chrome_Tick(null, new());
            if (Math.Abs(VolumeSlider.Value - 0.43) > 0.005) throw new InvalidOperationException("Внешнее изменение громкости не обновило ползунок.");
            VolumeSlider.Value = 0.5; Chrome_Tick(null, new());
            var homePosterFits = HeroImage.Stretch == Microsoft.UI.Xaml.Media.Stretch.Uniform && HeroImage.ActualWidth > 0 && HeroImage.ActualHeight <= Hero.ActualHeight;
            if (!homePosterFits) throw new InvalidOperationException("Постер главного блока не помещается целиком.");
            SpeedPicker.SelectedItem = SpeedPicker.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, "1.75"));
            var saved = await _playerPreferenceTestStore.ReadAsync<AppSettings>("settings");
            if (saved?.PlayerSpeed != 1.75) throw new InvalidOperationException("Выбранная скорость не записана сразу.");
            player.PlaybackSession.PlaybackRate = 1;
            await StopPlaybackAsync(closeWindow: false);
            saved = await _playerPreferenceTestStore.ReadAsync<AppSettings>("settings");
            if (saved?.PlayerSpeed != 1.75) throw new InvalidOperationException("Движок перезаписал выбранную скорость при закрытии.");
            await StartPlaybackAsync(anime, new(anime, 1, 0, 0, DateTimeOffset.UtcNow));
            deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (player.PlaybackSession.NaturalDuration.TotalSeconds <= 60 || Math.Abs(player.PlaybackSession.PlaybackRate - 1.75) > 0.001)
            { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Не восстановилась сохранённая скорость."); await Task.Delay(100); }
            player.Pause(); Chrome_Tick(null, new());
            report = new { success = true, defaultStepFrequency = new Slider().StepFrequency, configuredStepFrequency = VolumeSlider.StepFrequency,
                keyboardStep = VolumeSlider.SmallChange, levels, intermediateLevelsPersist = true, externalVolumeSync = true,
                speedSavedOnSelection = true, speedSurvivesEngineReset = true, restoredSpeed = player.PlaybackSession.PlaybackRate, homePosterFits,
                mutedForTest = player.IsMuted, accountWritesSuppressed = true, timestamp = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) { _mediaPlayer?.Pause(); report = new { success = false, errorType = ex.GetType().Name, message = ex.Message }; }
        finally
        {
            _playerPreferenceTestStore = null;
            var settingsFile = Path.Combine(preferenceDirectory, "settings.json");
            if (File.Exists(settingsFile)) File.Delete(settingsFile);
            if (Directory.Exists(preferenceDirectory) && !Directory.EnumerateFileSystemEntries(preferenceDirectory).Any()) Directory.Delete(preferenceDirectory);
        }
        if (output is not null) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
        if (!arguments.Contains("--keep-player-open")) { await StopPlaybackAsync(); Close(); }
    }

    // Runs the real UI/player while suppressing history, account and settings writes.
    private async Task RunPlayerUiSmokeAsync(string[] arguments)
    {
        var output = arguments.FirstOrDefault(x => x.StartsWith("--diagnostic-output="))?["--diagnostic-output=".Length..];
        var keepOpen = arguments.Contains("--keep-player-open");
        var originalPin = _services.Settings.PlayerAlwaysOnTop;
        var originalAutoNext = _services.Settings.AutoNext;
        var stage = "open";
        object report;
        static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
        static async Task Until(Func<bool> condition)
        {
            var timeout = DateTimeOffset.UtcNow.AddSeconds(55);
            while (!condition()) { if (DateTimeOffset.UtcNow >= timeout) throw new TimeoutException("Плеер не достиг ожидаемого состояния."); await Task.Delay(100); }
        }
        try
        {
            var anime = await _services.Shikimori.AnimeAsync(16498);
            await StartPlaybackAsync(anime, new(anime, 1, 0, 0, DateTimeOffset.UtcNow));
            var player = _mediaPlayer!;
            await Until(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && player.PlaybackSession.NaturalDuration.TotalSeconds > 60);
            Check(_playerWindow is not null && !ReferenceEquals(PlayerView.XamlRoot, Root.XamlRoot), "Плеер не открылся отдельным окном.");
            var session = player.PlaybackSession;
            Check(PlayerView.KeyboardAcceleratorPlacementMode == Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden, "На видео разрешена автоматическая подсказка горячей клавиши.");
            var before = session.Position.TotalSeconds; await Task.Delay(2200);
            Check(session.Position.TotalSeconds > before + 1, "Воспроизведение не продвигается.");
            player.Pause(); await Until(() => session.PlaybackState == MediaPlaybackState.Paused); Chrome_Tick(null, new());
            await Until(() => PlayerChrome.Opacity > 0.99);
            Check(CenterPlay.Visibility == Visibility.Visible && PlayerChrome.Opacity > 0.99, "Пауза не показывает центральную кнопку.");
            player.Play(); await Until(() => session.PlaybackState == MediaPlaybackState.Playing);
            _lastPointer = DateTimeOffset.UtcNow.AddSeconds(-5); Chrome_Tick(null, new());
            await Until(() => PlayerChrome.Opacity < 0.01);
            Check(PlayerChrome.Opacity < 0.01 && !PlayerChrome.IsHitTestVisible, "Управление не скрывается при воспроизведении.");
            RevealPlayerChrome();
            SeekTimeline.RequestSeek(60); await Until(() => session.Position.TotalSeconds >= 59);
            Check(session.Position.TotalSeconds < 65, "Таймлайн перематывает в неверную позицию.");
            if (QualityPicker.Items.Count > 1)
            {
                var other = QualityPicker.Items.Cast<VideoStream>().First(x => !Equals(x, QualityPicker.SelectedItem));
                QualityPicker.SelectedItem = other;
                await Until(() => session.Position.TotalSeconds >= 59 && session.PlaybackState == MediaPlaybackState.Playing && _pendingPosition == 0);
            }
            stage = "translation";
            var otherStudio = TranslationPicker.Items.Cast<KodikTranslation>().FirstOrDefault(x => x.Id != _playingTranslationId && x.Episodes.Any(ep => ep.Season == _playingEpisode!.Season && ep.Number == _playingEpisode.Number));
            if (otherStudio is not null)
            {
                TranslationPicker.SelectedItem = otherStudio;
                await Until(() => _playingTranslationId == otherStudio.Id && !_preparingPlayback && _pendingPosition == 0 && session.Position.TotalSeconds >= 59 && session.PlaybackState == MediaPlaybackState.Playing);
                Check(session.Position.TotalSeconds < 75, "Смена озвучки потеряла позицию.");
            }
            SpeedPicker.SelectedItem = SpeedPicker.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, "1.4"));
            Check(Math.Abs(session.PlaybackRate - 1.4) < 0.001, "Скорость 1.4 не применяется.");
            _services.Settings.PlayerAlwaysOnTop = true; SyncPlayerPin(true);
            Check(_playerWindow!.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true }, "Закрепление окна не работает.");
            player.Pause(); await Until(() => session.PlaybackState == MediaPlaybackState.Paused); Chrome_Tick(null, new());
            Check(_playerWindow.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: false }, "Окно остаётся закреплённым на паузе.");
            SetFullscreen(true); Check(_playerWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen, "Не включается полноэкранный режим.");
            SetFullscreen(false);
            SeekPlayer(session.NaturalDuration.TotalSeconds - 20); Chrome_Tick(null, new());
            Check(NextEpisodeCard.Visibility == Visibility.Visible, "Не появилась карточка следующей серии.");
            SeekPlayer(60); await Until(() => session.Position.TotalSeconds >= 59 && session.Position.TotalSeconds < 70);
            var episodeBefore = _playingEpisode!.Number;
            stage = "next";
            await NextEpisodeAsync(); await Until(() => session.NaturalDuration.TotalSeconds > 60 && session.PlaybackState == MediaPlaybackState.Playing);
            Check(_playingEpisode?.Number == episodeBefore + 1, "Не загрузилась следующая серия.");
            SeekPlayer(90); await Until(() => session.Position.TotalSeconds >= 89);
            player.Pause(); await Until(() => session.PlaybackState == MediaPlaybackState.Paused);
            var second = _playingEpisode!;
            var first = EpisodePicker.Items.Cast<KodikEpisode>().First(x => x.Season == second.Season && x.Number == episodeBefore);
            stage = "resume-round-trip";
            _changingPickers = true; EpisodePicker.SelectedItem = first; _changingPickers = false;
            await PlaySelectedAsync(); await Until(() => _pendingPosition == 0 && session.Position.TotalSeconds >= 59 && session.PlaybackState == MediaPlaybackState.Playing);
            Check(session.Position.TotalSeconds < 75, "Возврат к предыдущей серии потерял её позицию.");
            _changingPickers = true; EpisodePicker.SelectedItem = second; _changingPickers = false;
            await PlaySelectedAsync(); await Until(() => _pendingPosition == 0 && session.Position.TotalSeconds >= 89 && session.PlaybackState == MediaPlaybackState.Playing);
            Check(session.Position.TotalSeconds < 105, "Возврат ко второй серии потерял её позицию.");
            stage = "auto-next";
            _services.Settings.AutoNext = false;
            player.Pause(); await Until(() => session.PlaybackState == MediaPlaybackState.Paused);
            SeekPlayer(session.NaturalDuration.TotalSeconds - 1); await Until(() => session.Position.TotalSeconds > session.NaturalDuration.TotalSeconds - 3);
            await HandleEpisodeEndedAsync(); Check(_playingEpisode == second, "При выключенном автопереходе сменилась серия.");
            _services.Settings.AutoNext = true;
            await HandleEpisodeEndedAsync(); await Until(() => session.NaturalDuration.TotalSeconds > 60 && session.PlaybackState == MediaPlaybackState.Playing);
            Check(_playingEpisode?.Number == second.Number + 1, "Автопереход не загрузил следующую серию.");
            // Navigating the main window must leave the independent player running.
            _section = "catalog"; await LoadSectionAsync();
            Check(_playerWindow is not null && _playingEpisode is not null && session.NaturalDuration.TotalSeconds > 60 && PlayerView.Visibility == Visibility.Visible, "Навигация скрыла или закрыла плеер.");
            Check(Math.Abs(session.PlaybackRate - 1.4) < 0.001, "Следующая серия сбросила скорость воспроизведения.");
            Check(SubtitlePicker.Items.Count == 1 && _assRenderer is null && FindSubtitlesButton.Visibility == Visibility.Visible, "Субтитры загрузились без запроса пользователя.");
            stage = "subtitles";
            await FindSubtitlesAsync();
            var subtitleTracks = SubtitlePicker.Items.OfType<SubtitleCandidate>().Count();
            Check(subtitleTracks > 0, "Источник не вернул дорожки субтитров.");
            if (subtitleTracks > 0)
            {
                if (SubtitlePicker.SelectedItem is not SubtitleCandidate) SubtitlePicker.SelectedItem = SubtitlePicker.Items.OfType<SubtitleCandidate>().First();
                await Until(() => _assRenderer is not null && !_subtitleLoading && SubtitleOffsetBar.Visibility == Visibility.Visible);
                Check(SubtitleChoices.Children.Count > subtitleTracks && SubtitleOffsetBar.Visibility == Visibility.Visible, "Не построены группы субтитров или задержка.");
                var track = (SubtitleCandidate)SubtitlePicker.SelectedItem;
                SubtitleOffset.Value = 2.5;
                SubtitlePicker.SelectedIndex = 0;
                await Until(() => _assRenderer is null && AssOverlay.Source is null);
                SubtitlePicker.SelectedItem = track;
                await Until(() => _assRenderer is not null && !_subtitleLoading);
                Check(SubtitleOffset.Value == 2.5, "Дорожка не восстановила свою задержку.");
                ResetSubtitleOffset_Click(this, new()); Check(SubtitleOffset.Value == 0, "Сброс задержки не работает.");
            }
            SeekPlayer(60); await Until(() => session.Position.TotalSeconds >= 59);
            player.Pause(); _services.Settings.PlayerAlwaysOnTop = originalPin; Chrome_Tick(null, new());
            PlayerView.UpdateLayout();
            var choices = PlayerChoices.TransformToVisual(PlayerView).TransformPoint(new(0, 0));
            Check(choices.X >= 0 && choices.X + PlayerChoices.ActualWidth <= PlayerView.ActualWidth + 1, "Панель управления вышла за ширину окна.");
            report = new { success = true, separateWindow = true, playbackAdvancing = true, timelineSeek = true, qualityKeepsPosition = true,
                speed = session.PlaybackRate, autoHide = true, centralPauseButton = true, pinWhilePlaying = true, fullscreen = true,
                nextEpisodeCard = true, nextEpisodeLoaded = _playingEpisode!.Number, autoNextSetting = true, episodeResumeRoundTrip = true, translationKeepsPosition = otherStudio is not null, mainNavigationPreservesPlayback = true,
                playerWidth = PlayerView.ActualWidth, playerHeight = PlayerView.ActualHeight, controlsWithinWindow = true, subtitlesOnRequest = true, subtitleTracks,
                accountWritesSuppressed = true, timestamp = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) { _mediaPlayer?.Pause(); report = new { success = false, stage, errorType = ex.GetType().Name, message = ex.Message }; }
        finally { _services.Settings.PlayerAlwaysOnTop = originalPin; _services.Settings.AutoNext = originalAutoNext; }
        if (output is not null) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
        if (!keepOpen) { await StopPlaybackAsync(); Close(); }
    }
}
