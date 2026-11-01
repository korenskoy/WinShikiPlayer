using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private static UIElement SettingsToggleRow(ToggleSwitch toggle)
    {
        var title = toggle.Header?.ToString() ?? ""; toggle.Header = null; toggle.OnContent = toggle.OffContent = ""; toggle.MinWidth = 40; toggle.Width = 40;
        var row = new Grid { ColumnSpacing = 16, Padding = new(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }); Grid.SetColumn(toggle, 1); row.Children.Add(toggle); return row;
    }
    private async void Settings_Click(object sender, RoutedEventArgs e) { if (_ready) await RunAsync(ShowSettingsAsync); }

    private async Task ShowSettingsAsync()
    {
        if (_dialogOpen) return;
        _loginCts?.Cancel();
        _dialogOpen = true;
        try
        {
            var settings = _services.Settings;
            var clientId = new TextBox { Header = "Shikimori Client ID", Text = settings.ClientId };
            var secret = new PasswordBox { Header = "Shikimori Client Secret", Password = _services.Secrets.Read("client-secret") ?? "" };
            var token = new PasswordBox { Header = "API-токен Kodik", Password = _services.Secrets.Read("kodik-token") ?? "" };
            var host = new TextBox { Header = "Домен Shikimori", Text = settings.ShikimoriHost };
            var agent = new TextBox { Header = "Имя OAuth-приложения (User-Agent)", Text = settings.UserAgent };
            var redirect = new ComboBox { Header = "Redirect URI", HorizontalAlignment = HorizontalAlignment.Stretch };
            redirect.Items.Add("winshikiplayer://oauth"); redirect.Items.Add(AuthService.RedirectUri);
            redirect.SelectedItem = settings.RedirectUri;
            var kodik = new TextBox { Header = "Домен API Kodik", Text = settings.KodikHost };
            var theme = new ComboBox { Header = "Оформление", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (label, value) in AppPalette.All.Select(x => (x.Name, x.Id)).Concat(new[] { ("Авто · Otaku", "auto.otaku"), ("Авто · Plum", "auto.plum") }))
                theme.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            var themeId = settings.Theme switch { "Dark" => "midnight", "Light" => "paper", "System" => "auto.otaku", _ => settings.Theme };
            theme.SelectedItem = theme.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, themeId)) ?? theme.Items[0];
            var autoNext = new ToggleSwitch { Header = "Следующая серия автоматически", IsOn = settings.AutoNext };
            var autoSkip = new ToggleSwitch { Header = "Автопропуск опенинга и эндинга", IsOn = settings.AutoSkipChapters };
            var preferredQuality = new ComboBox { Header = "Качество по умолчанию", ItemsSource = new[] { "auto", "1080", "720", "480", "360" }, SelectedItem = settings.PreferredQuality };
            var subtitleLanguage = new ComboBox { Header = "Язык субтитров", ItemsSource = new[] { "auto", "subru", "suben", "off" }, SelectedItem = settings.SubtitleLanguage };
            var studioStyle = new ToggleSwitch { Header = "Студийное оформление ASS (libass)", IsOn = settings.SubtitleStudioStyle };
            var subtitleFont = new TextBox { Header = "Шрифт собственных субтитров", Text = settings.SubtitleFont };
            var subtitleSize = new NumberBox { Header = "Размер субтитров", Minimum = 12, Maximum = 72, Value = settings.SubtitleSize };
            var subtitlePosition = new NumberBox { Header = "Положение по вертикали (0.1–0.98)", Minimum = 0.1, Maximum = 0.98, SmallChange = 0.02, Value = settings.SubtitleVerticalPosition };
            var panel = new StackPanel { Spacing = 24, Width = 470 };
            panel.Children.Add(new TextBlock { Text = "Оформление", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            var themeCards = new Controls.FlowPanel { Spacing = 8 };
            var autoThemes = new Controls.FlowPanel { Spacing = 8 };
            foreach (var palette in AppPalette.All)
            {
                var sample = new StackPanel { Spacing = 8 };
                var color = Windows.UI.Color.FromArgb(255, (byte)(palette.Background >> 16), (byte)(palette.Background >> 8), (byte)palette.Background);
                var accentColor = Windows.UI.Color.FromArgb(255, (byte)(palette.Accent >> 16), (byte)(palette.Accent >> 8), (byte)palette.Accent);
                sample.Children.Add(new Border { Height = 26, CornerRadius = new(5), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color), Child = new Border { Width = 24, Height = 6, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(8, 0, 0, 0), CornerRadius = new(3), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(accentColor) } });
                sample.Children.Add(new TextBlock { Text = palette.Id == "midnight" ? "Midnight" : palette.Id == "plum" ? "Plum" : palette.Id == "paper" ? "Paper" : "Slate", FontSize = 12 });
                var card = new Button { Content = sample, Width = 110, Padding = new(10), Tag = palette.Id, BorderBrush = Resource<Microsoft.UI.Xaml.Media.Brush>(palette.Id == themeId ? "AccentBrush" : "LineBrush") };
                card.Click += (_, _) => { theme.SelectedItem = theme.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, palette.Id)); foreach (var sibling in themeCards.Children.OfType<Button>()) sibling.BorderBrush = Resource<Microsoft.UI.Xaml.Media.Brush>(Equals(sibling.Tag, palette.Id) ? "AccentBrush" : "LineBrush"); foreach (var item in autoThemes.Children.OfType<Button>()) item.Background = Resource<Microsoft.UI.Xaml.Media.Brush>("SurfaceBrush"); };
                themeCards.Children.Add(card);
            }
            panel.Children.Add(themeCards);
            foreach (var (title, id) in new[] { ("Авто · Otaku", "auto.otaku"), ("Авто · Plum", "auto.plum") })
            {
                var button = new Button { Content = title, Tag = id, Style = Resource<Style>("ChipButton"), Background = Resource<Microsoft.UI.Xaml.Media.Brush>(id == themeId ? "AccentBrush" : "SurfaceBrush") };
                button.Click += (_, _) => { theme.SelectedItem = theme.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, id)); foreach (var card in themeCards.Children.OfType<Button>()) card.BorderBrush = Resource<Microsoft.UI.Xaml.Media.Brush>("LineBrush"); foreach (var item in autoThemes.Children.OfType<Button>()) item.Background = Resource<Microsoft.UI.Xaml.Media.Brush>(Equals(item.Tag, id) ? "AccentBrush" : "SurfaceBrush"); };
                autoThemes.Children.Add(button);
            }
            panel.Children.Add(autoThemes);
            var playback = new StackPanel { Spacing = 14 }; playback.Children.Add(SettingsToggleRow(autoNext)); playback.Children.Add(SettingsToggleRow(autoSkip)); playback.Children.Add(preferredQuality);
            panel.Children.Add(SidebarSection("Плеер", playback, true));
            var subtitleOptions = new StackPanel { Spacing = 14 };
            foreach (var control in new UIElement[] { subtitleLanguage, studioStyle, subtitleFont, subtitleSize, subtitlePosition }) subtitleOptions.Children.Add(control is ToggleSwitch toggle ? SettingsToggleRow(toggle) : control);
            var subtitleStyle = new Services.SubtitleStyleEditor(settings); subtitleOptions.Children.Add(subtitleStyle.Panel);
            panel.Children.Add(SidebarSection("Субтитры", subtitleOptions, false));
            var connection = new StackPanel { Spacing = 14 };
            connection.Children.Add(new TextBlock { Text = "Параметры Shikimori и Kodik. Секреты сохраняются в диспетчере учётных данных Windows.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Resource<Microsoft.UI.Xaml.Media.Brush>("MutedBrush") });
            foreach (var control in new UIElement[] { host, kodik, token, clientId, secret, redirect, agent }) connection.Children.Add(control);
            var defaultApps = new Button { Content = "Настроить открытие OAuth-ссылок" };
            defaultApps.Click += (_, _) => { Services.ProtocolActivation.Register(); Services.ProtocolActivation.OpenDefaultApps(); }; connection.Children.Add(defaultApps);
            connection.Children.Add(new HyperlinkButton { Content = "Приложение OAuth Shikimori ↗", NavigateUri = new("https://shikimori.io/oauth/applications") });
            panel.Children.Add(SidebarSection("Подключение", connection, false));
            var clearCache = new Button { Content = "Очистить кэш данных" };
            clearCache.Click += async (_, _) => await RunAsync(async () => { await _services.Cache.ClearAsync(); clearCache.Content = "Кэш очищен ✓"; });
            panel.Children.Add(clearCache);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Resource<Microsoft.UI.Xaml.Media.Brush>("AccentBrush") };
            panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Настройки", PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Primary, PrimaryButtonStyle = Resource<Style>("PrimaryButton"), CloseButtonStyle = Resource<Style>("QuietButton"),
                Content = new ScrollViewer { Content = panel, MaxHeight = 580, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            };
            dialog.Resources["DefaultButtonStyle"] = Resource<Style>("PrimaryButton");
            dialog.Background = Resource<Microsoft.UI.Xaml.Media.Brush>("SurfaceBrush");
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var shikiHost = AppSettings.ValidateHost(host.Text);
                    var kodikHost = AppSettings.ValidateHost(kodik.Text);
                    var id = clientId.Text.Trim();
                    var changedAccount = shikiHost != settings.ShikimoriHost || id != settings.ClientId;
                    var updated = settings with { ShikimoriHost = shikiHost, KodikHost = kodikHost, ClientId = id,
                        Theme = SelectedTag(theme) ?? "Dark", AutoNext = autoNext.IsOn, RedirectUri = (string)redirect.SelectedItem, UserAgent = agent.Text.Trim() };
                    updated.AutoSkipChapters = autoSkip.IsOn; updated.PreferredQuality = (string)preferredQuality.SelectedItem;
                    updated.SubtitleLanguage = (string)subtitleLanguage.SelectedItem; updated.SubtitleStudioStyle = studioStyle.IsOn;
                    updated.SubtitleFont = subtitleFont.Text.Trim(); updated.SubtitleSize = subtitleSize.Value;
                    updated.SubtitleVerticalPosition = subtitlePosition.Value;
                    subtitleStyle.ReadInto(updated);
                    if (!double.IsFinite(updated.SubtitleSize) || !double.IsFinite(updated.SubtitleVerticalPosition) || updated.SubtitleFont.Length == 0) throw new ArgumentException("Проверьте параметры субтитров.");
                    if (string.IsNullOrWhiteSpace(updated.UserAgent) || updated.UserAgent.Contains('\r') || updated.UserAgent.Contains('\n')) throw new ArgumentException("Укажите имя OAuth-приложения.");
                    await _services.Store.WriteAsync("settings", updated);
                    _services.Secrets.Write("client-secret", secret.Password.Trim());
                    _services.Secrets.Write("kodik-token", token.Password.Trim());
                    if (changedAccount) { _services.Auth.SignOut(); _services.User = null; await _services.Cache.ClearAsync(); }
                    settings.ShikimoriHost = updated.ShikimoriHost; settings.KodikHost = updated.KodikHost;
                    settings.ClientId = updated.ClientId; settings.Theme = updated.Theme; settings.AutoNext = updated.AutoNext;
                    settings.RedirectUri = updated.RedirectUri; settings.UserAgent = updated.UserAgent;
                    settings.AutoSkipChapters = updated.AutoSkipChapters; settings.PreferredQuality = updated.PreferredQuality;
                    settings.SubtitleLanguage = updated.SubtitleLanguage; settings.SubtitleStudioStyle = updated.SubtitleStudioStyle;
                    settings.SubtitleFont = updated.SubtitleFont; settings.SubtitleSize = updated.SubtitleSize; settings.SubtitleVerticalPosition = updated.SubtitleVerticalPosition;
                    subtitleStyle.ReadInto(settings);
                    ApplyTheme(); UpdateAccount();
                }
                catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
                finally { deferral.Complete(); }
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await StopPlaybackAsync();
                await LoadSectionAsync();
            }
        }
        finally { _dialogOpen = false; }
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _dialogOpen) return;
        await RunAsync(async () =>
        {
            if (_loginCts is not null) { _loginCts.Cancel(); return; }
            if (_services.Auth.IsSignedIn)
            {
                await ShowProfileAsync(); return;
            }
            if (string.IsNullOrWhiteSpace(_services.Settings.ClientId) || string.IsNullOrWhiteSpace(_services.Secrets.Read("client-secret")))
                await ShowSettingsAsync();
            if (string.IsNullOrWhiteSpace(_services.Settings.ClientId) || string.IsNullOrWhiteSpace(_services.Secrets.Read("client-secret"))) return;
            if (_services.Settings.RedirectUri == "winshikiplayer://oauth")
            {
                Services.ProtocolActivation.Register();
                if (!Services.ProtocolActivation.IsDefaultHandler())
                {
                    Notice.Title = "Назначьте приложение для ссылок WINSHIKIPLAYER";
                    Notice.Message = "В параметрах Windows выберите WinShikiPlayer для типа ссылки WINSHIKIPLAYER, затем повторите вход.";
                    Notice.Severity = InfoBarSeverity.Warning; Notice.IsOpen = true;
                    var chooseApp = new Button { Content = "Открыть параметры Windows" };
                    chooseApp.Click += (_, _) => Services.ProtocolActivation.OpenDefaultApps(); Notice.ActionButton = chooseApp;
                    Services.ProtocolActivation.OpenDefaultApps(); return;
                }
            }
            _loginCts = new();
            Notice.Title = "Вход в Shikimori";
            Notice.Message = "Завершите вход в браузере. Нажмите кнопку аккаунта ещё раз, чтобы отменить.";
            Notice.Severity = InfoBarSeverity.Informational; Notice.IsOpen = true;
            Notice.ActionButton = null;
            try
            {
                var attempt = _services.Auth.BeginLogin();
                if (_services.Settings.RedirectUri == "winshikiplayer://oauth") Services.ProtocolActivation.Register();
                await _services.Auth.ListenAndLoginAsync(attempt, OpenBrowser, _loginCts.Token);
                await CurrentUserAsync();
                Notice.IsOpen = false;
                await LoadSectionAsync();
            }
            finally { _loginCts.Dispose(); _loginCts = null; Notice.ActionButton = null; if (Notice.Severity == InfoBarSeverity.Informational) Notice.IsOpen = false; }
        });
    }
}



