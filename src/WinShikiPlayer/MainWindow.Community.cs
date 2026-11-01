using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private string _socialTab = "discussions";
    private void AddSocialTabs()
    {
        FeatureToolbar.Children.Clear();
        foreach (var (key, title) in new[] { ("friends", "Друзья"), ("discussions", "Обсуждения"), ("reviews", "Обзоры") })
        {
            var selected = key == _socialTab;
            var button = new Button { Content = title, Width = 200, CornerRadius = new(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0, 0, 0, selected ? 2 : 0), BorderBrush = Resource<Brush>("AccentBrush"), Foreground = Resource<Brush>(selected ? "TextBrush" : "MutedBrush"), Padding = new(12, 10, 12, 10) };
            button.Click += async (_, _) => await RunAsync(async () => { _socialTab = key; await LoadSectionAsync(); });
            FeatureToolbar.Children.Add(button);
        }
    }

    private async Task ShowProfileAsync()
    {
        _loadCts?.Cancel(); _loadCts = new(); var ct = _loadCts.Token;
        var user = await CurrentUserAsync(ct);
        var profileTask = _services.Shikimori.ProfileAsync(user.Id, ct);
        var favouritesTask = _services.Shikimori.FavouritesAsync(user.Id, ct);
        await Task.WhenAll(profileTask, favouritesTask); ct.ThrowIfCancellationRequested();
        var profile = await profileTask; var favourites = await favouritesTask;
        await RecordNavigationAsync(new("profile"));
        BeginFeature("Мой аккаунт");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        if (RemoteUri(profile.Avatar) is { } avatar) header.Children.Add(new Image { Source = new BitmapImage(avatar), Width = 96, Height = 96 });
        var identity = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock { Text = profile.Nickname, FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        identity.Children.Add(new TextBlock { Text = profile.Name ?? "", Opacity = 0.6 });
        identity.Children.Add(new TextBlock { Text = string.Join(" · ", profile.CommonInfo ?? []), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        header.Children.Add(identity); FeatureContent.Children.Add(header);
        var scores = profile.Stats?.Scores?.Anime ?? [];
        var total = profile.Stats?.Statuses?.Anime?.Sum(x => x.Count) ?? 0;
        var rated = scores.Sum(x => x.Count);
        var average = rated == 0 ? "—" : (scores.Sum(x => int.TryParse(x.Name, out var score) ? score * x.Count : 0) / (double)rated).ToString("0.00");
        var kpis = new Controls.FlowPanel { Spacing = 12 };
        foreach (var (value, title) in new[] { (total.ToString(), "ТАЙТЛОВ"), (rated.ToString(), "ОЦЕНОК"), (average, "СРЕДНЯЯ ОЦЕНКА"), ((favourites.Animes?.Count ?? 0).ToString(), "В ИЗБРАННОМ") })
        {
            var contents = new StackPanel { Spacing = 8 }; contents.Children.Add(new TextBlock { Text = value, FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.Bold }); contents.Children.Add(new TextBlock { Text = title, Style = Resource<Style>("SectionLabel") });
            var card = Surface(contents, 18); card.Width = Math.Clamp((Math.Min(1120, Root.ActualWidth - 72) - 36) / 4, 140, 260); kpis.Children.Add(card);
        }
        FeatureContent.Children.Add(kpis);
        AddStatChart(FeatureContent, "Мои списки", profile.Stats?.Statuses?.Anime ?? []);
        AddStatChart(FeatureContent, "Распределение оценок", scores);
        await AddAnimeSectionAsync(FeatureContent, "Избранное", (favourites.Animes ?? []).Select(x => x.ToAnime()), ct);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var lists = new Button { Content = "Открыть мои списки" };
        lists.Click += async (_, _) => await RunAsync(async () => { _section = "library"; await LoadSectionAsync(); });
        var settings = new Button { Content = "Настройки" }; settings.Click += Settings_Click;
        var signOut = new Button { Content = "Выйти из Shikimori" };
        signOut.Click += async (_, _) => await RunAsync(async () => { await StopPlaybackAsync(); _services.Auth.SignOut(); _services.User = null; await _services.Cache.ClearAsync(); UpdateAccount(); _section = "home"; await LoadSectionAsync(); });
        actions.Children.Add(lists); actions.Children.Add(settings); actions.Children.Add(signOut); FeatureContent.Children.Add(actions);
    }

    private async Task ShowTopicAsync(int id, int page = 1)
    {
        _loadCts?.Cancel(); _loadCts = new(); var ct = _loadCts.Token;
        var topicTask = _services.Shikimori.TopicAsync(id, ct);
        var commentsTask = _services.Shikimori.CommentsAsync(id, page, ct);
        await Task.WhenAll(topicTask, commentsTask); ct.ThrowIfCancellationRequested();
        var topic = await topicTask; var comments = await commentsTask;
        await RecordNavigationAsync(new("social", TopicId: id));
        BeginFeature(topic.Title);
        FeatureContent.Children.Add(new TextBlock { Text = topic.Meta, Opacity = 0.6 });
        FeatureContent.Children.Add(FormattedText(topic.Body ?? topic.HtmlBody));
        FeatureContent.Children.Add(Heading($"Комментарии · страница {page}"));
        foreach (var comment in comments)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = $"{comment.User?.Nickname ?? "Shikimori"} · {comment.CreatedAt?.ToLocalTime():g}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(FormattedText(comment.Body ?? comment.HtmlBody));
            FeatureContent.Children.Add(Surface(panel));
        }
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var previous = new Button { Content = "← Предыдущая", IsEnabled = page > 1 };
        var next = new Button { Content = "Следующая →", IsEnabled = comments.Count == 30 };
        previous.Click += async (_, _) => await RunAsync(() => ShowTopicAsync(id, page - 1));
        next.Click += async (_, _) => await RunAsync(() => ShowTopicAsync(id, page + 1));
        pages.Children.Add(previous); pages.Children.Add(next); FeatureContent.Children.Add(pages);
        // macOS currently reads comments; writing is intentionally initiated only by the user's Send button.
        if (_services.Auth.IsSignedIn)
        {
            var compose = new TextBox { Header = "Ваш комментарий", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxLength = 10000 };
            var send = new Button { Content = "Отправить комментарий", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            send.Click += async (_, _) => await RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(compose.Text)) return;
                send.IsEnabled = false;
                try { await _services.Shikimori.CreateCommentAsync(id, compose.Text); await ShowTopicAsync(id); }
                finally { send.IsEnabled = true; }
            });
            FeatureContent.Children.Add(compose); FeatureContent.Children.Add(send);
        }
    }

    private void BeginFeature(string title)
    {
        BrowseView.Visibility = DetailView.Visibility = Visibility.Collapsed;
        FeatureView.Visibility = Visibility.Visible; FeatureContent.Children.Clear();
        var back = new Button { Content = "← Назад" };
        back.Click += async (_, _) => await RunAsync(() => NavigateHistoryAsync(false));
        FeatureContent.Children.Add(back); FeatureContent.Children.Add(Heading(title));
        FeatureView.ChangeView(null, 0, null);
    }
    private void AddStatChart(StackPanel destination, string title, IEnumerable<StatBucket> buckets)
    {
        var data = buckets.ToList(); if (data.Count == 0) return;
        destination.Children.Add(Heading(title));
        var panel = new StackPanel { Spacing = 10 };
        foreach (var bucket in data)
        {
            panel.Children.Add(new TextBlock { Text = $"{StatusLabel(bucket.GroupedId ?? bucket.Name ?? "—")} · {bucket.Count}" });
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, data.Max(x => x.Count)), Value = bucket.Count, Height = 7 });
        }
        destination.Children.Add(Surface(panel));
    }
    private async Task AddAnimeSectionAsync(StackPanel destination, string title, IEnumerable<Anime> animes, CancellationToken ct = default)
    {
        var enriched = await _services.Shikimori.EnrichPostersAsync(animes.ToList(), ct); ct.ThrowIfCancellationRequested();
        var cards = enriched.Select(Card).ToList(); if (cards.Count == 0) return;
        destination.Children.Add(Heading(title));
        var grid = new GridView { ItemsSource = cards, ItemTemplate = (DataTemplate)Application.Current.Resources["AnimeTemplate"],
            ItemContainerStyle = Resource<Style>("PosterItem"), IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
        grid.Loaded += (_, _) => ConfigurePosterGrid(grid); grid.SizeChanged += PosterGrid_SizeChanged;
        grid.ItemClick += Anime_Click; destination.Children.Add(grid);
    }
    private Uri? RemoteUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//")) value = "https:" + value;
        return Uri.TryCreate(new Uri(_services.Settings.ShikimoriHost), value, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
    }
    private sealed class ToggleButtonShim : Button
    {
        public ToggleButtonShim(string text, bool selected)
        { Content = text; if (selected) Style = (Style)Application.Current.Resources["AccentButtonStyle"]; }
    }
}
