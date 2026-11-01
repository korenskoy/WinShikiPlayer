using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WinShikiPlayer.Core;

public class ApiException(HttpStatusCode status) : Exception(status switch
{
    HttpStatusCode.Unauthorized => "Сессия истекла. Войдите в Shikimori снова.",
    HttpStatusCode.Forbidden => "Сервис отклонил запрос. Проверьте доступ приложения и домен.",
    HttpStatusCode.TooManyRequests => "Слишком много запросов. Повторите немного позже.",
    HttpStatusCode.NotFound => "Запись не найдена.",
    _ => $"Сервис вернул ошибку HTTP {(int)status}."
})
{
    public HttpStatusCode Status { get; } = status;
}

public sealed partial class ShikimoriClient(HttpClient http, AppSettings settings, AuthService auth)
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private DateTimeOffset _lastRequest;
    public Task<List<Anime>> CatalogAsync(Dictionary<string, string?> query, CancellationToken ct = default) =>
        SendAsync<List<Anime>>("api/animes?" + Query(query), ct: ct);
    public Task<Anime> AnimeAsync(int id, CancellationToken ct = default) => SendAsync<Anime>($"api/animes/{id}", ct: ct);
    public Task<List<NamedItem>> GenresAsync(CancellationToken ct = default) => SendAsync<List<NamedItem>>("api/genres", ct: ct);
    public Task<List<CalendarEntry>> CalendarAsync(CancellationToken ct = default) => SendAsync<List<CalendarEntry>>("api/calendar", ct: ct);
    public Task<List<LibraryEntry>> LibraryAsync(int userId, string? status, int page, CancellationToken ct = default) =>
        SendAsync<List<LibraryEntry>>($"api/users/{userId}/anime_rates?" + Query(new()
        { ["limit"] = "50", ["page"] = page.ToString(), ["status"] = status }), ct: ct);
    public Task<User> WhoAmIAsync(CancellationToken ct = default) => SendAsync<User>("api/users/whoami", ct: ct);
    public Task<List<Topic>> TopicsAsync(int page, CancellationToken ct = default) =>
        SendAsync<List<Topic>>($"api/topics?forum=animanga&limit=20&page={page}", ct: ct);
    public Task<UserRate> SaveRateAsync(int userId, Anime anime, string status, int score, int episodes, CancellationToken ct = default)
    {
        var payload = new { user_rate = new { user_id = userId.ToString(), target_id = anime.Id.ToString(), target_type = "Anime", status,
            score = score.ToString(), episodes = episodes.ToString() } };
        return SendAsync<UserRate>(anime.UserRate is { } rate ? $"api/v2/user_rates/{rate.Id}" : "api/v2/user_rates",
            anime.UserRate is null ? HttpMethod.Post : HttpMethod.Patch, payload, ct);
    }

    private async Task<T> SendAsync<T>(string path, HttpMethod? method = null, object? body = null, CancellationToken ct = default)
    {
        method ??= HttpMethod.Get;
        for (var attempt = 0; ; attempt++)
        {
            var access = await auth.AccessTokenAsync(ct);
            using var request = new HttpRequestMessage(method, $"{settings.ShikimoriHost}/{path}");
            request.Headers.UserAgent.ParseAdd(settings.UserAgent);
            request.Headers.Accept.Add(new("application/json"));
            if (access is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (body is not null) request.Content = JsonContent.Create(body, options: JsonDefaults.Options);
            // Shikimori limits bursts; all requests from this client share a pacing gate.
            await _requests.WaitAsync(ct);
            HttpResponseMessage response;
            try
            {
                var remaining = TimeSpan.FromMilliseconds(350) - (DateTimeOffset.UtcNow - _lastRequest);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
                _lastRequest = DateTimeOffset.UtcNow;
                response = await http.SendAsync(request, ct);
            }
            finally { _requests.Release(); }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && access is not null && attempt == 0)
                {
                    await auth.RefreshAsync(ct);
                    continue;
                }
                if ((method == HttpMethod.Get || path == "api/graphql") && attempt < 2 && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests))
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? TimeSpan.FromSeconds(attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 30)), ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new ApiException(response.StatusCode);
                if (response.StatusCode == HttpStatusCode.NoContent) return default!;
                return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, ct)
                    ?? throw new JsonException("Сервис вернул пустой ответ.");
            }
        }
    }

    public static string Query(Dictionary<string, string?> values) => string.Join('&', values
        .Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
}
