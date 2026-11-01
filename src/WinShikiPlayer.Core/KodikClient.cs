using System.Text.Json;

namespace WinShikiPlayer.Core;

public sealed record KodikEpisode(int Season, int Number, Uri Link)
{
    public string Label => Season > 1 ? $"Сезон {Season} · Серия {Number}" : $"Серия {Number}";
}
public sealed record KodikTranslation(int Id, string Title, List<KodikEpisode> Episodes)
{
    public override string ToString() => Title;
}

public sealed class KodikClient(HttpClient http, AppSettings settings, ISecretStore secrets)
{
    public async Task<List<KodikTranslation>> SearchAsync(int animeId, CancellationToken ct = default)
    {
        var token = secrets.Read("kodik-token");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Укажите API-токен Kodik в настройках, чтобы смотреть видео.");
        // Keep the token in the request body, outside URLs and diagnostic messages.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.KodikHost}/search")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = token, ["shikimori_id"] = animeId.ToString(), ["with_seasons"] = "true",
                ["with_episodes"] = "true", ["with_episodes_data"] = "true", ["limit"] = "100"
            })
        };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ApiException(response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Parse(json.RootElement);
    }

    public static List<KodikTranslation> Parse(JsonElement root)
    {
        if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Kodik отклонил запрос. Проверьте токен и доступ к API.");
        if (!root.TryGetProperty("results", out var results)) return [];
        List<KodikTranslation> translations = [];
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("translation", out var translation) || !translation.TryGetProperty("id", out var id)) continue;
            item.TryGetProperty("blocked_seasons", out var blocked);
            if (IsAll(blocked)) continue;
            List<KodikEpisode> episodes = [];
            var hasSeasons = item.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Object;
            if (hasSeasons)
            {
                foreach (var season in seasons.EnumerateObject())
                {
                    var seasonBlock = blocked.ValueKind == JsonValueKind.Object && blocked.TryGetProperty(season.Name, out var b) ? b : default;
                    if (IsAll(seasonBlock) || !season.Value.TryGetProperty("episodes", out var map)) continue;
                    foreach (var ep in map.EnumerateObject())
                    {
                        if (seasonBlock.ValueKind == JsonValueKind.Array && seasonBlock.EnumerateArray().Any(x => x.ToString() == ep.Name)) continue;
                        Add(episodes, int.TryParse(season.Name, out var number) ? number : 1, ep.Name, ep.Value);
                    }
                }
            }
            else if (item.TryGetProperty("episodes", out var flat) && flat.ValueKind == JsonValueKind.Object)
                foreach (var ep in flat.EnumerateObject()) Add(episodes, 1, ep.Name, ep.Value);
            else if (item.TryGetProperty("link", out var link)) Add(episodes, 1, "1", link);
            if (episodes.Count > 0)
                translations.Add(new(id.GetInt32(), translation.TryGetProperty("title", out var title) ? title.GetString() ?? "Перевод" : "Перевод",
                    episodes.DistinctBy(x => (x.Season, x.Number)).OrderBy(x => x.Season).ThenBy(x => x.Number).ToList()));
        }
        return translations;
    }

    private static bool IsAll(JsonElement element) => element.ValueKind == JsonValueKind.String && element.GetString() == "all";
    private static void Add(List<KodikEpisode> episodes, int season, string number, JsonElement value)
    {
        var link = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("link", out var l) ? l.GetString() : null;
        if (!int.TryParse(number, out var episode) || episode < 1 || string.IsNullOrWhiteSpace(link)) return;
        if (link.StartsWith("//")) link = "https:" + link;
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
            episodes.Add(new(season, episode, uri));
    }
}
