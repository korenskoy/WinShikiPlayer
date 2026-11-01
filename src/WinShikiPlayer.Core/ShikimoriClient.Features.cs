using System.Text.Json;

namespace WinShikiPlayer.Core;

public sealed partial class ShikimoriClient
{
    public Task<List<AnimeScreenshot>> ScreenshotsAsync(int id, CancellationToken ct = default) => SendAsync<List<AnimeScreenshot>>($"api/animes/{id}/screenshots", ct: ct);
    public Task<List<AnimeVideo>> VideosAsync(int id, CancellationToken ct = default) => SendAsync<List<AnimeVideo>>($"api/animes/{id}/videos", ct: ct);
    public Task<List<Anime>> SimilarAsync(int id, CancellationToken ct = default) => SendAsync<List<Anime>>($"api/animes/{id}/similar", ct: ct);
    public Task<List<RelatedAnime>> RelatedAsync(int id, CancellationToken ct = default) => SendAsync<List<RelatedAnime>>($"api/animes/{id}/related", ct: ct);
    public Task<List<AnimeStudio>> StudiosAsync(CancellationToken ct = default) => SendAsync<List<AnimeStudio>>("api/studios", ct: ct);
    public Task<UserProfile> ProfileAsync(int id, CancellationToken ct = default) => SendAsync<UserProfile>($"api/users/{id}", ct: ct);
    public Task<UserFavourites> FavouritesAsync(int id, CancellationToken ct = default) => SendAsync<UserFavourites>($"api/users/{id}/favourites", ct: ct);
    public Task<List<User>> FriendsAsync(int id, CancellationToken ct = default) => SendAsync<List<User>>($"api/users/{id}/friends?limit=100", ct: ct);
    public Task<List<HistoryEvent>> HistoryAsync(int id, int page = 1, CancellationToken ct = default) =>
        SendAsync<List<HistoryEvent>>($"api/users/{id}/history?target_type=Anime&limit=30&page={page}", ct: ct);
    public Task<Topic> TopicAsync(int id, CancellationToken ct = default) => SendAsync<Topic>($"api/topics/{id}", ct: ct);
    public Task<List<Topic>> ForumTopicsAsync(string forum, int page = 1, CancellationToken ct = default) =>
        SendAsync<List<Topic>>($"api/topics?forum={Uri.EscapeDataString(forum)}&limit=20&page={page}", ct: ct);
    public Task<List<TopicComment>> CommentsAsync(int topicId, int page = 1, CancellationToken ct = default) =>
        SendAsync<List<TopicComment>>($"api/comments?commentable_type=Topic&commentable_id={topicId}&limit=30&page={page}", ct: ct);
    public Task<JsonElement> FavouriteAsync(int id, bool add, CancellationToken ct = default) =>
        SendAsync<JsonElement>($"api/favorites/Anime/{id}", add ? HttpMethod.Post : HttpMethod.Delete, ct: ct);
    public Task<JsonElement> DeleteRateAsync(int rateId, CancellationToken ct = default) => SendAsync<JsonElement>($"api/v2/user_rates/{rateId}", HttpMethod.Delete, ct: ct);
    public Task<TopicComment> CreateCommentAsync(int topicId, string body, CancellationToken ct = default) =>
        SendAsync<TopicComment>("api/comments", HttpMethod.Post, new { comment = new { commentable_id = topicId, commentable_type = "Topic", body } }, ct);

    public async Task<HomeSnapshot> HomeAsync(CancellationToken ct = default)
    {
        var trending = CatalogAsync(new() { ["status"] = "ongoing", ["order"] = "popularity", ["limit"] = "6", ["censored"] = "true" }, ct);
        var episodes = CatalogAsync(new() { ["status"] = "ongoing", ["order"] = "aired_on", ["limit"] = "6", ["censored"] = "true" }, ct);
        var recommended = CatalogAsync(new() { ["order"] = "ranked", ["limit"] = "5", ["censored"] = "true" }, ct);
        await Task.WhenAll(trending, episodes, recommended);
        return new(await EnrichPostersAsync(await trending, ct), await EnrichPostersAsync(await episodes, ct), await EnrichPostersAsync(await recommended, ct));
    }

    public async Task<List<FriendActivity>> FriendActivityAsync(int userId, CancellationToken ct = default)
    {
        var friends = await FriendsAsync(userId, ct);
        List<FriendActivity> items = [];
        // Bounded fan-out respects the shared API pacing gate, including a large friends list.
        foreach (var friend in friends.Take(20))
        {
            ct.ThrowIfCancellationRequested();
            try { items.AddRange((await HistoryAsync(friend.Id, ct: ct)).Take(5).Select(x => new FriendActivity(friend, x))); }
            catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound) { }
        }
        return items.OrderByDescending(x => x.Event.CreatedAt).ToList();
    }
}
