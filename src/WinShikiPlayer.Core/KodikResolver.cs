using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinShikiPlayer.Core;

public sealed record SkipRange(double Start, double End);
public sealed record VideoStream(string Quality, Uri Uri);
public sealed record ResolvedVideo(List<VideoStream> Streams, SkipRange? Opening, SkipRange? Ending);

// Port of the original KodikVideoLinksResolver. The provider can change its player protocol.
public sealed class KodikResolver(HttpClient http)
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<Uri, (DateTimeOffset Until, ResolvedVideo Video)> _cache = [];
    public async Task<ResolvedVideo> ResolveAsync(Uri link, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cacheLock) if (_cache.TryGetValue(link, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Video;
        var video = await ResolveCoreAsync(link, ct);
        ct.ThrowIfCancellationRequested();
        lock (_cacheLock)
        {
            _cache[link] = (DateTimeOffset.UtcNow.AddMinutes(5), video);
            if (_cache.Count > 100) _cache.Remove(_cache.MinBy(x => x.Value.Until).Key);
        }
        return video;
    }
    public void Invalidate(Uri link) { lock (_cacheLock) _cache.Remove(link); }
    private async Task<ResolvedVideo> ResolveCoreAsync(Uri link, CancellationToken ct)
    {
        var match = Regex.Match(link.AbsolutePath, @"^/(?<type>[a-z]+)/(?<id>\d+)/(?<hash>[a-z0-9]+)/", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("Неподдерживаемая ссылка Kodik. Используйте веб-плеер.");
        var page = await FetchAsync(link, new("https://shikimori.io/"), ct);
        var skip = ParseSkipRanges(page);
        var jsPath = Regex.Match(page, "src=\"(?<path>/assets/js/app\\.player_single\\.[a-z0-9]+\\.js)\"", RegexOptions.IgnoreCase);
        var endpoint = "/ftor";
        if (jsPath.Success)
        {
            var js = await FetchAsync(new(link, jsPath.Groups["path"].Value), link, ct);
            var encoded = Regex.Match(js, "type:\"POST\",url:atob\\(\"(?<data>[^\"]+)\"\\)");
            if (encoded.Success)
            {
                try
                {
                    var path = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Groups["data"].Value));
                    if (path.StartsWith('/') && !path.StartsWith("//") && !path.Contains('\\')) endpoint = path;
                }
                catch (FormatException) { }
            }
        }
        var url = new Uri(link.GetLeftPart(UriPartial.Authority) + endpoint + "?" + ShikimoriClient.Query(new()
        { ["type"] = match.Groups["type"].Value, ["id"] = match.Groups["id"].Value, ["hash"] = match.Groups["hash"].Value }));
        var body = await FetchAsync(url, link, ct, HttpMethod.Post);
        using var json = JsonDocument.Parse(body);
        List<VideoStream> streams = [];
        if (json.RootElement.TryGetProperty("links", out var links))
            foreach (var quality in links.EnumerateObject())
                foreach (var src in Sources(quality.Value))
                    if (DecodeSource(src) is { } uri) streams.Add(new(quality.Name, uri));
        if (streams.Count == 0) throw new InvalidOperationException("Kodik не вернул видеопоток. Попробуйте веб-плеер.");
        return new(streams.OrderByDescending(x => int.TryParse(x.Quality, out var n) ? n : 0).DistinctBy(x => x.Quality).ToList(), skip.Opening, skip.Ending);
    }

    private async Task<string> FetchAsync(Uri uri, Uri referer, CancellationToken ct, HttpMethod? method = null)
    {
        try { return await FetchOnceAsync(uri, referer, ct, method); }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException || ex is ApiException { Status: >= System.Net.HttpStatusCode.InternalServerError }))
        {
            // Both the player page and /ftor only read stream metadata.
            await Task.Delay(200, ct);
            return await FetchOnceAsync(uri, referer, ct, method);
        }
    }
    private async Task<string> FetchOnceAsync(Uri uri, Uri referer, CancellationToken ct, HttpMethod? method)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
        request.Headers.Referrer = referer;
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Firefox/149.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ApiException(response.StatusCode);
        const int max = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("Ответ плеера слишком большой.");
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > max) throw new InvalidOperationException("Ответ плеера слишком большой.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static IEnumerable<string> Sources(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var src in Sources(item)) yield return src;
        else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("src", out var src))
            foreach (var item in Sources(src)) yield return item;
    }

    public static Uri? DecodeSource(string value)
    {
        var direct = Direct(value);
        if (direct is not null) return direct;
        var rotated = new string(value.Select(c => c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 18) % 26)
            : c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 18) % 26) : c).ToArray());
        foreach (var text in new[] { value, rotated })
        {
            var normalized = text.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            try { if (Direct(Encoding.UTF8.GetString(Convert.FromBase64String(normalized))) is { } uri) return uri; }
            catch (FormatException) { }
        }
        return null;
    }

    private static Uri? Direct(string value)
    {
        value = value.Trim();
        if (value.StartsWith("//")) value = "https:" + value;
        else if (!value.Contains("://") && value.Split('/')[0].Contains('.')) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri : null;
    }

    public static (SkipRange? Opening, SkipRange? Ending) ParseSkipRanges(string page)
    {
        var match = Regex.Match(page, "parseSkipButtons?\\(\"(?<data>[^\"]+)\"\\s*,");
        var ranges = match.Groups["data"].Value.Split(',').Take(2).Select(x => x.Split('-')).Where(x => x.Length == 2)
            .Select(x => new SkipRange(Time(x[0]), Time(x[1]))).Where(x => x.Start >= 0 && x.End > x.Start).ToList();
        return ranges.Count switch { >= 2 => (ranges[0], ranges[1]), 1 when ranges[0].Start < 300 => (ranges[0], null),
            1 => (null, ranges[0]), _ => (null, null) };
    }
    private static double Time(string value)
    {
        double total = 0;
        foreach (var part in value.Trim().Split(':'))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0) return -1;
            total = total * 60 + n;
        }
        return total;
    }
}
