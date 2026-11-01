using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinShikiPlayer.Core;

public sealed record AnimeScreenshot(string? Original, string? Preview);
public sealed record AnimeVideo(int Id, string? Url, string? PlayerUrl, string? ImageUrl, string? Name, string? Kind);
public sealed record RelatedAnime(string? Relation, string? RelationRussian, Anime? Anime);
public sealed record AnimeStudio(int Id, string Name);
public sealed record HistoryEvent(int Id, DateTimeOffset? CreatedAt, string? Description, Anime? Target);
public sealed record FriendActivity(User User, HistoryEvent Event);
public sealed record TopicComment(int Id, string? Body, string? HtmlBody, User? User, DateTimeOffset? CreatedAt);
public sealed record StatBucket(string? Name, int? Size, int? Value, string? GroupedId)
{
    [JsonIgnore] public int Count => Size ?? Value ?? 0;
}
public sealed record StatGroup(List<StatBucket>? Anime);
public sealed record ProfileStats(StatGroup? Statuses, StatGroup? Scores, StatGroup? Types);
public sealed record UserProfile(int Id, string Nickname, string? Avatar, string? Name, string? Website, List<string>? CommonInfo, ProfileStats? Stats);
public sealed record FavouriteAnime(int Id, string? Name, string? Russian, string? Image, string? Url)
{
    public Anime ToAnime() => new() { Id = Id, Name = Name ?? "", Russian = Russian, Image = new(Image, Image), Url = Url };
}
public sealed record UserFavourites(List<FavouriteAnime>? Animes);
public sealed record CommunityStat(string? Name, int Value);
public sealed record AnimeCommunity(List<CommunityStat>? Scores, List<CommunityStat>? Statuses);
public sealed record HomeSnapshot(List<Anime> Trending, List<Anime> NewEpisodes, List<Anime> Recommendations);
public sealed record NavigationEntry(string Section, int? AnimeId = null, int? TopicId = null, string? Search = null);

public static class HistoryMerger
{
    public static List<AnimeCard> Merge(IEnumerable<WatchEntry> local, IEnumerable<HistoryEvent> remote, string host)
    {
        var items = local.Select(x => (x.UpdatedAt, new AnimeCard(x.Anime, host, x.Note)))
            .Concat(remote.Where(x => x.Target is not null).Select(x => (x.CreatedAt ?? DateTimeOffset.MinValue,
                new AnimeCard(x.Target!, host, TextFormatting.Plain(x.Description)))));
        return items.OrderByDescending(x => x.Item1).DistinctBy(x => x.Item2.Anime.Id).Select(x => x.Item2).ToList();
    }
}
