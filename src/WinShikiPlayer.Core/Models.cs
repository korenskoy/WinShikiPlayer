using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinShikiPlayer.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new FlexibleStringConverter() },
        WriteIndented = true
    };
}

public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType == JsonTokenType.Number) { using var number = JsonDocument.ParseValue(ref reader); return number.RootElement.GetRawText(); }
        if (reader.TokenType == JsonTokenType.Null) return null;
        throw new JsonException("Ожидалась строка или число.");
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed record AnimeImage(string? Original, string? Preview);
public sealed record NamedItem(int Id, string Name, string? Russian);
public sealed record UserRate(int Id, int Score, string Status, int Episodes);
public sealed record User(int Id, string Nickname, string? Avatar, string? Url);
public sealed record Anime
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? Russian { get; init; }
    public AnimeImage? Image { get; init; }
    public string? Url { get; init; }
    public string? Kind { get; init; }
    public string? Score { get; init; }
    public string? Status { get; init; }
    public int Episodes { get; init; }
    public int EpisodesAired { get; init; }
    public string? AiredOn { get; init; }
    public string? Description { get; init; }
    public int Duration { get; init; }
    public string? Rating { get; init; }
    public string? Origin { get; init; }
    public string? LicenseNameRu { get; init; }
    public string? ReleasedOn { get; init; }
    public DateTimeOffset? NextEpisodeAt { get; init; }
    public int? TopicId { get; init; }
    public bool Favoured { get; init; }
    public List<AnimeStudio> Studios { get; init; } = [];
    [JsonPropertyName("rates_scores_stats")] public List<CommunityStat> ScoreStats { get; init; } = [];
    [JsonPropertyName("rates_statuses_stats")] public List<CommunityStat> StatusStats { get; init; } = [];
    public List<NamedItem> Genres { get; init; } = [];
    public UserRate? UserRate { get; init; }
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Russian) ? Name : Russian;
    [JsonIgnore] public string Meta => $"{AiredOn?.Split('-')[0] ?? "—"} · {Kind?.ToUpperInvariant() ?? "ANIME"} · {(Episodes > 0 ? Episodes : EpisodesAired)} эп.";
    [JsonIgnore] public int AvailableEpisodes => Status == "anons" ? 0 : Status == "ongoing" ? EpisodesAired : Math.Max(Episodes, EpisodesAired);
    [JsonIgnore] public string PlainDescription => TextFormatting.Plain(Description);
}

public sealed record AnimeCard(Anime Anime, string Host, string? Note = null, string? Badge = null)
{
    public string Title => Anime.Title;
    public string Subtitle => Anime.Name;
    public string Meta => Note ?? Anime.Meta;
    public string Score => Badge ?? (string.IsNullOrEmpty(Anime.Score) || Anime.Score == "0.0" ? "—" : Anime.Score);
    public Uri? PosterUri => string.IsNullOrWhiteSpace(Anime.Image?.Original) || Anime.Image.Original.Contains("missing_", StringComparison.OrdinalIgnoreCase) ? null : Uri.TryCreate(Anime.Image.Original, UriKind.Absolute, out var uri)
        ? uri.Scheme is "https" or "http" ? uri : null
        : Uri.TryCreate(new Uri(Host), Anime.Image?.Original ?? "", out var relative) ? relative : null;
}

public sealed record CalendarEntry(Anime Anime, int NextEpisode, DateTimeOffset? NextEpisodeAt);
public sealed record LibraryEntry(int Id, int Score, string Status, int Episodes, Anime Anime, DateTimeOffset? UpdatedAt = null);
public sealed record Topic(int Id, string? TopicTitle, string? Body, string? HtmlBody, int CommentsCount, User? User)
{
    public string Title => string.IsNullOrWhiteSpace(TopicTitle) ? "Обсуждение" : TopicTitle;
    public string Preview => TextFormatting.Plain(Body ?? HtmlBody);
    public string Meta => $"{User?.Nickname ?? "Shikimori"} · {CommentsCount} комментариев";
}

public sealed record WatchEntry(Anime Anime, int Episode, double PositionSeconds, double DurationSeconds, DateTimeOffset UpdatedAt, int TranslationId = 0, int Season = 1)
{
    public string Note => $"Серия {Episode} · {TimeSpan.FromSeconds(PositionSeconds):mm\\:ss}";
}

public static class TextFormatting
{
    public static string Plain(string? text) => WebUtility.HtmlDecode(Regex.Replace(
        Regex.Replace(text ?? "", @"<br\s*/?>|</p>", "\n", RegexOptions.IgnoreCase), @"<[^>]+>|\[/?[^\]]+\]", "")).Trim();
}

public sealed record AppSettings
{
    public string ShikimoriHost { get; set; } = "https://shikimori.io";
    public string KodikHost { get; set; } = "https://kodik-api.com";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "winshikiplayer://oauth";
    public string UserAgent { get; set; } = "WinShikiPlayer";
    public string Theme { get; set; } = "Dark";
    public bool AutoNext { get; set; } = true;
    public bool AutoSkipChapters { get; set; }
    public string PreferredQuality { get; set; } = "auto";
    public double PlayerVolume { get; set; } = 0.8;
    public double PlayerSpeed { get; set; } = 1;
    public bool PlayerAlwaysOnTop { get; set; }
    public string SubtitleLanguage { get; set; } = "auto";
    public bool SubtitleStudioStyle { get; set; } = true;
    public string SubtitleFont { get; set; } = "Segoe UI";
    public double SubtitleSize { get; set; } = 28;
    public double SubtitleVerticalPosition { get; set; } = 0.92;
    public string SubtitleFontWeight { get; set; } = "semibold";
    public string SubtitleTextColor { get; set; } = "#FFFFFF";
    public string SubtitleOutlineColor { get; set; } = "#000000";
    public double SubtitleOutlineWidth { get; set; } = 2;
    public bool SubtitleShadowEnabled { get; set; } = true;
    public string SubtitleBackgroundStyle { get; set; } = "shadow";
    public double SubtitleBackgroundOpacity { get; set; } = 0.35;
    public int SubtitleMaxLines { get; set; } = 3;
    public string CatalogView { get; set; } = "grid";
    public bool NetworkLogsEnabled { get; set; }
    public static string ValidateHost(string value)
    {
        var text = value.Trim().TrimEnd('/');
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Введите HTTPS-домен без пути, например https://shikimori.io.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
