using System.Text.Json.Serialization;

namespace WinShikiPlayer.Core;

public sealed record UserRateV2(int Id, int UserId, int TargetId, string Status, int Score, int Episodes, DateTimeOffset UpdatedAt);

public sealed partial class ShikimoriClient
{
    public async Task<List<Anime>> HydrateAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        List<Anime> result = [];
        foreach (var chunk in ids.Distinct().Chunk(40))
        {
            var query = "query($ids:String!){animes(ids:$ids,limit:50){id name russian kind rating score status episodes episodesAired duration licenseNameRu airedOn{date} releasedOn{date} poster{originalUrl mainUrl} genres{id name russian} studios{id name}}}";
            var response = await SendAsync<GraphEnvelope>("api/graphql", HttpMethod.Post,
                new { query, variables = new { ids = string.Join(',', chunk) } }, ct);
            if (response.Data is null) throw new InvalidOperationException("Не удалось получить метаданные Shikimori GraphQL.");
            result.AddRange(response.Data.Animes.Select(x => x.ToAnime()));
        }
        return result;
    }
    public async Task<List<Anime>> EnrichPostersAsync(List<Anime> animes, CancellationToken ct = default)
    {
        var missing = animes.Where(x => string.IsNullOrEmpty(x.Image?.Original) || x.Image.Original.Contains("missing_") || x.Image.Original.Contains("/system/animes/")).Select(x => x.Id).ToList();
        if (missing.Count == 0) return animes;
        try
        {
            var hydrated = (await HydrateAsync(missing, ct)).ToDictionary(x => x.Id);
            return animes.Select(x => hydrated.TryGetValue(x.Id, out var match) ? x with { Image = match.Image } : x).ToList();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or ApiException or InvalidOperationException) { return animes; }
    }
    public async Task<List<LibraryEntry>> CompleteLibraryAsync(int userId, CancellationToken ct = default)
    {
        List<UserRateV2> rates = []; HashSet<int> seen = [];
        for (var page = 1; page <= 300; page++)
        {
            var chunk = await SendAsync<List<UserRateV2>>($"api/v2/user_rates?user_id={userId}&target_type=Anime&limit=100&page={page}", ct: ct);
            var fresh = chunk.Where(x => seen.Add(x.Id)).ToList(); rates.AddRange(fresh);
            if (chunk.Count != 100 || fresh.Count == 0) break;
        }
        if (rates.Count == 0) return [];
        var animes = (await HydrateAsync(rates.Select(x => x.TargetId), ct)).ToDictionary(x => x.Id);
        return rates.Select(x => new LibraryEntry(x.Id, x.Score, x.Status, x.Episodes,
            (animes.GetValueOrDefault(x.TargetId) ?? new Anime { Id = x.TargetId, Name = $"Anime #{x.TargetId}" })
                with { UserRate = new(x.Id, x.Score, x.Status, x.Episodes) }, x.UpdatedAt)).OrderByDescending(x => x.UpdatedAt).ToList();
    }
    private sealed record GraphEnvelope(GraphData? Data);
    private sealed record GraphData(List<GraphAnime> Animes);
    private sealed record GraphDate(string? Date);
    private sealed record GraphPoster([property: JsonPropertyName("originalUrl")] string? OriginalUrl,
        [property: JsonPropertyName("mainUrl")] string? MainUrl);
    private sealed record GraphAnime(int Id, string Name, string? Russian, string? Kind, string? Rating, string? Score, string? Status,
        int Episodes, [property: JsonPropertyName("episodesAired")] int EpisodesAired, int Duration,
        [property: JsonPropertyName("licenseNameRu")] string? LicenseNameRu,
        [property: JsonPropertyName("airedOn")] GraphDate? AiredOn, [property: JsonPropertyName("releasedOn")] GraphDate? ReleasedOn,
        GraphPoster? Poster, List<NamedItem>? Genres, List<AnimeStudio>? Studios)
    {
        public Anime ToAnime() => new()
        { Id = Id, Name = Name, Russian = Russian, Kind = Kind, Rating = Rating, Score = Score, Status = Status, Episodes = Episodes,
            EpisodesAired = EpisodesAired, Duration = Duration, LicenseNameRu = LicenseNameRu, AiredOn = AiredOn?.Date, ReleasedOn = ReleasedOn?.Date,
            Image = new(Poster?.OriginalUrl ?? Poster?.MainUrl, Poster?.MainUrl), Genres = Genres ?? [], Studios = Studios ?? [] };
    }
}
