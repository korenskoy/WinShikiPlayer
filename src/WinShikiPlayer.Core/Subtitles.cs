using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinShikiPlayer.Core;

public sealed record SubtitleCandidate(int Id, string Language, string Title, Uri AssUri, Uri VttUri, string? AuthorsSummary = null)
{
    public string LanguageCode => SubtitleLanguages.Code(Language);
    public string StudioName => !string.IsNullOrWhiteSpace(AuthorsSummary) ? AuthorsSummary.Trim() : LanguageCode switch
    {
        "ru" => "Русские субтитры", "en" => "English subtitles", "uk" => "Українські субтитри", "ja" => "日本語字幕",
        "de" => "Deutsche Untertitel", "fr" => "Sous-titres français", "es" => "Subtítulos en español", "zh" => "中文字幕", "ko" => "한국어 자막", _ => Language
    };
    public override string ToString() => $"{StudioName} · {LanguageCode}";
}
public static class SubtitleLanguages
{
    public static string Code(string value) { var code = value.Trim().ToLowerInvariant(); return code.StartsWith("sub", StringComparison.Ordinal) ? code[3..] : code; }
    public static string GroupTitle(string code) => Code(code) switch { "ru" => "РУССКИЕ", "en" => "АНГЛИЙСКИЕ", "uk" => "УКРАИНСКИЕ", "ja" => "ЯПОНСКИЕ", "de" => "НЕМЕЦКИЕ", "fr" => "ФРАНЦУЗСКИЕ", "es" => "ИСПАНСКИЕ", "zh" => "КИТАЙСКИЕ", "ko" => "КОРЕЙСКИЕ", var other => other.ToUpperInvariant() };
}
public sealed record SubtitleCue(double Start, double End, string Text);
public sealed record SubtitleEndpoints(string Host, string Api);

public static class SubtitleParser
{
    public static List<SubtitleCue> ParseAss(string source)
    {
        List<SubtitleCue> cues = [];
        string[] format = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
        var inEvents = false;
        foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.StartsWith('[')) { inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inEvents) continue;
            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) { format = trimmed[7..].Split(',', StringSplitOptions.TrimEntries); continue; }
            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed[9..].TrimStart().Split(',', format.Length);
            var startIndex = Array.FindIndex(format, x => x.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var endIndex = Array.FindIndex(format, x => x.Equals("End", StringComparison.OrdinalIgnoreCase));
            var textIndex = Array.FindIndex(format, x => x.Equals("Text", StringComparison.OrdinalIgnoreCase));
            if (parts.Length != format.Length || startIndex < 0 || endIndex < 0 || textIndex < 0) continue;
            var start = Time(parts[startIndex]); var end = Time(parts[endIndex]);
            var text = Regex.Replace(parts[textIndex], @"\{[^}]*\}", "").Replace(@"\N", "\n").Replace(@"\n", "\n").Replace(@"\h", " ");
            if (end > start && start >= 0 && !string.IsNullOrWhiteSpace(text)) cues.Add(new(start, end, text));
        }
        return cues.OrderBy(x => x.Start).ToList();
    }
    public static List<SubtitleCue> ParseVtt(string source)
    {
        var lines = source.Replace("\r\n", "\n").TrimStart('\uFEFF').Split('\n');
        List<SubtitleCue> cues = [];
        for (var i = 0; i < lines.Length; i++)
        {
            var timing = Regex.Match(lines[i], @"^(?<start>(?:\d+:)?\d{2}:\d{2}[.,]\d+)\s+-->\s+(?<end>(?:\d+:)?\d{2}:\d{2}[.,]\d+)");
            if (!timing.Success) continue;
            var start = Time(timing.Groups["start"].Value); var end = Time(timing.Groups["end"].Value);
            var text = new StringBuilder();
            while (++i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) text.AppendLine(lines[i]);
            if (end > start && start >= 0) cues.Add(new(start, end, TextFormatting.Plain(text.ToString())));
        }
        return cues.OrderBy(x => x.Start).ToList();
    }
    public static double Time(string value)
    {
        double time = 0;
        foreach (var chunk in value.Replace(',', '.').Split(':'))
        {
            if (!double.TryParse(chunk, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0) return -1;
            time = time * 60 + number;
        }
        return time;
    }
    public static string ToAss(IEnumerable<SubtitleCue> cues, AppSettings settings)
    {
        var font = settings.SubtitleFont.Replace(",", "").Replace("\n", "").Replace("\r", "");
        var background = settings.SubtitleBackgroundStyle;
        var outline = AssColor(settings.SubtitleOutlineColor);
        var back = AssColor("#000000", settings.SubtitleBackgroundOpacity);
        var header = FormattableString.Invariant($"""
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 1920
        PlayResY: 1080
        WrapStyle: 0
        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,{font},{settings.SubtitleSize * 2},{AssColor(settings.SubtitleTextColor)},&H00FFFFFF,{(background == "box" ? back : outline)},{back},{(settings.SubtitleFontWeight is "semibold" or "bold" or "heavy" ? -1 : 0)},0,0,0,100,100,0,0,{(background == "box" ? 3 : 1)},{settings.SubtitleOutlineWidth},{(settings.SubtitleShadowEnabled && background != "none" ? 1 : 0)},2,40,40,{(int)(1080 * (1 - settings.SubtitleVerticalPosition))},1
        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        """);
        var output = new StringBuilder(header).AppendLine();
        foreach (var cue in cues)
        {
            var text = string.Join(@"\N", cue.Text.Replace("{", "").Replace("}", "").Replace("\r", "").Split('\n').Take(Math.Clamp(settings.SubtitleMaxLines, 1, 10)));
            output.AppendLine($"Dialogue: 0,{AssTime(cue.Start)},{AssTime(cue.End)},Default,,0,0,0,,{text}");
        }
        return output.ToString();
    }
    public static string AssColor(string hex, double opacity = 1)
    {
        if (!Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$")) throw new ArgumentException("Цвет субтитров должен иметь формат #RRGGBB.");
        var alpha = (int)Math.Round(255 * (1 - Math.Clamp(opacity, 0, 1)));
        return $"&H{alpha:X2}{hex[5..7]}{hex[3..5]}{hex[1..3]}";
    }
    private static string AssTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);
}

public sealed class Anime365Client(HttpClient http, LocalStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SubtitleEndpoints? _endpoints;
    public async Task<List<SubtitleCandidate>> SearchAsync(int animeId, int episode, string language, CancellationToken ct)
    {
        var endpoint = await EndpointsAsync(ct);
        var series = await FetchJsonAsync($"{endpoint.Api}/series/?myAnimeListId={animeId}", ct);
        if (series.ValueKind != JsonValueKind.Array || series.GetArrayLength() == 0) return [];
        var seriesId = Number(series[0], "id");
        var detail = await FetchJsonAsync($"{endpoint.Api}/series/{seriesId}?fields=episodes", ct);
        if (!detail.TryGetProperty("episodes", out var episodes)) return [];
        var matching = episodes.EnumerateArray().FirstOrDefault(x => Number(x, "isActive") == 1 &&
            !String(x, "episodeType").Equals("preview", StringComparison.OrdinalIgnoreCase) && Math.Abs(Decimal(x, "episodeInt") - episode) < 0.0001);
        if (matching.ValueKind == JsonValueKind.Undefined) return [];
        var episodeDetail = await FetchJsonAsync($"{endpoint.Api}/episodes/{Number(matching, "id")}", ct);
        if (!episodeDetail.TryGetProperty("translations", out var translations)) return [];
        return translations.EnumerateArray().Where(x => Number(x, "isActive") == 1 && String(x, "typeKind").Equals("sub", StringComparison.OrdinalIgnoreCase) &&
            (language == "auto" || String(x, "type").Equals(language, StringComparison.OrdinalIgnoreCase)))
            .Select(x => new SubtitleCandidate(Number(x, "id"), String(x, "type"),
                String(x, "title"), new($"{endpoint.Host}/translations/ass/{Number(x, "id")}"), new($"{endpoint.Host}/translations/vtt/{Number(x, "id")}"), String(x, "authorsSummary"))).ToList();
    }
    public async Task<byte[]> DownloadAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ApiException(response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 8 * 1024 * 1024) throw new InvalidOperationException("Субтитры превышают 8 МБ.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
    public async Task<SubtitleEndpoints> EndpointsAsync(CancellationToken ct)
    {
        if (_endpoints is not null) return _endpoints;
        var cached = await store.ReadAsync<SubtitleEndpoints>("subtitle-endpoints");
        if (cached is not null) return _endpoints = cached;
        const string key = "MyShikiP1@yer";
        var uri = Xor("JQ0nGBpRRn9QLhAJEy8BfQ8AHwElU24QCl0MFzokCAkxf1guHwpdPgwxGwUKC35bMxYL", key);
        var innerKey = Xor("HgwRXykJ", key);
        using var json = JsonDocument.Parse(await DownloadAsync(new(uri), ct));
        var host = Xor(json.RootElement.GetProperty("host").GetString()!, innerKey).TrimEnd('/');
        var api = Xor(json.RootElement.GetProperty("api").GetString()!, innerKey).TrimEnd('/');
        if (!Uri.TryCreate(host, UriKind.Absolute, out var hostUri) || hostUri.Scheme != "https" ||
            !Uri.TryCreate(api, UriKind.Absolute, out var apiUri) || apiUri.Scheme != "https") throw new InvalidOperationException("Источник субтитров вернул некорректные адреса.");
        _endpoints = new(host, api); await store.WriteAsync("subtitle-endpoints", _endpoints); return _endpoints;
    }
    private async Task<JsonElement> FetchJsonAsync(string uri, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("Anime365Api Library");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ApiException(response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement; return (root.TryGetPropertySafe("data", out var data) ? data : root).Clone();
        }
        finally { try { await Task.Delay(300, ct); } finally { _gate.Release(); } }
    }
    public static string Xor(string value, string key)
    {
        if (key.Length == 0) throw new ArgumentException("XOR key is empty.");
        var bytes = Convert.FromBase64String(value); var keys = Encoding.UTF8.GetBytes(key);
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= keys[i % keys.Length];
        return Encoding.UTF8.GetString(bytes);
    }
    private static int Number(JsonElement value, string key) => (int)Decimal(value, key);
    private static double Decimal(JsonElement value, string key) => value.TryGetProperty(key, out var element) && double.TryParse(element.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
    private static string String(JsonElement value, string key) => value.TryGetProperty(key, out var element) ? element.ToString() : "";
}
internal static class JsonElementExtensions
{
    public static bool TryGetPropertySafe(this JsonElement value, string name, out JsonElement result)
    { if (value.ValueKind == JsonValueKind.Object) return value.TryGetProperty(name, out result); result = default; return false; }
}
