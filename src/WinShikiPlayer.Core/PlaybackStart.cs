namespace WinShikiPlayer.Core;

public sealed record PlaybackStart(int Episode, int Season = 1, double PositionSeconds = 0, int TranslationId = 0)
{
    public bool CanResume => PositionSeconds > 1;
    public KodikEpisode? FindEpisode(IEnumerable<KodikEpisode> episodes) =>
        episodes.FirstOrDefault(x => x.Number == Episode && x.Season == Season)
        ?? episodes.FirstOrDefault(x => x.Number == Episode);

    public WatchEntry ToWatchEntry(Anime anime, KodikTranslation? translation = null)
    {
        var available = translation is null ? null : FindEpisode(translation.Episodes);
        if (translation is not null && available is null)
            throw new InvalidOperationException($"Эпизод {Episode} недоступен в озвучке «{translation.Title}». Выберите другую озвучку.");
        var season = available?.Season ?? Season;
        return new(anime, Episode, season == Season ? PositionSeconds : 0, 0, DateTimeOffset.UtcNow,
            translation?.Id ?? TranslationId, season);
    }

    public static PlaybackStart Select(Anime anime, WatchEntry? saved)
    {
        if (saved?.Anime.Id != anime.Id) saved = null;
        var watched = Math.Max(0, anime.UserRate?.Episodes ?? 0);
        var next = watched < int.MaxValue ? watched + 1 : watched;
        // Shikimori's progress determines the next episode. Local progress only
        // resumes that episode; an old replay must not override the user's list.
        if (anime.UserRate is null && saved is { Episode: > 0 })
            next = IsComplete(saved) && saved.Episode < int.MaxValue ? saved.Episode + 1 : saved.Episode;
        var episode = anime.AvailableEpisodes > 0 ? Math.Clamp(next, 1, anime.AvailableEpisodes) : Math.Max(1, next);
        var canResume = saved is not null && saved.Episode == episode && saved.Episode > watched &&
            double.IsFinite(saved.PositionSeconds) && saved.PositionSeconds > 1 && !IsComplete(saved);
        return new(episode, canResume ? saved!.Season : 1, canResume ? saved!.PositionSeconds : 0, saved?.TranslationId ?? 0);
    }

    private static bool IsComplete(WatchEntry saved) => double.IsFinite(saved.DurationSeconds) && saved.DurationSeconds > 0 &&
        saved.PositionSeconds / saved.DurationSeconds >= 0.9;
}
