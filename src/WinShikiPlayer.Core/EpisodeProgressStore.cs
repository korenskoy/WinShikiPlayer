namespace WinShikiPlayer.Core;

public sealed record EpisodeProgress(int AnimeId, int Season, int Episode, double Position, double Duration, DateTimeOffset UpdatedAt)
{
    public bool Completed => Duration > 0 && Position / Duration >= 0.9;
    public static EpisodeProgress From(WatchEntry entry) => new(entry.Anime.Id, entry.Season, entry.Episode, entry.PositionSeconds, entry.DurationSeconds, entry.UpdatedAt);
    public string Key => $"{AnimeId}:{Season}:{Episode}";
}

// Resume points are independent of the one-row-per-title history feed.
public sealed class EpisodeProgressStore(LocalStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task SaveAsync(EpisodeProgress entry)
    {
        if (entry.AnimeId <= 0 || entry.Season <= 0 || entry.Episode <= 0 || !double.IsFinite(entry.Position) || !double.IsFinite(entry.Duration) || entry.Position <= 1 || entry.Duration <= 0) return;
        await _gate.WaitAsync();
        try
        {
            var entries = await store.ReadAsync<Dictionary<string, EpisodeProgress>>("playback-progress") ?? [];
            if (entries.TryGetValue(entry.Key, out var previous) && previous.UpdatedAt > entry.UpdatedAt) return;
            entries[entry.Key] = entry;
            await store.WriteAsync("playback-progress", entries.Values.OrderByDescending(x => x.UpdatedAt).Take(1000).ToDictionary(x => x.Key));
        }
        finally { _gate.Release(); }
    }
    public async Task<EpisodeProgress?> FindAsync(int animeId, int season, int episode)
    {
        var entries = await store.ReadAsync<Dictionary<string, EpisodeProgress>>("playback-progress");
        var entry = entries?.GetValueOrDefault($"{animeId}:{season}:{episode}");
        return entry is not null && !entry.Completed && double.IsFinite(entry.Position) && entry.Position > 1 ? entry : null;
    }
}
