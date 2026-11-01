using System.Globalization;

namespace WinShikiPlayer.Core;

public sealed record CatalogFilters
{
    public List<string> Kinds { get; init; } = [];
    public List<string> Statuses { get; init; } = [];
    public List<int> Genres { get; init; } = [];
    public string Season { get; init; } = "";
    public List<string> Ratings { get; init; } = [];
    public List<string> Durations { get; init; } = [];
    public List<string> Origins { get; init; } = [];
    public List<int> Studios { get; init; } = [];
    public List<string> MyList { get; init; } = [];
    public int MinimumScore { get; init; }
    public bool ExcludeCensored { get; init; }
    public bool LicensedOnly { get; init; }
    public bool Ascending { get; init; }
    public string Order { get; init; } = "popularity";
    public string? Search { get; init; }

    public Dictionary<string, string?> Query(int page) => new()
    {
        ["page"] = page.ToString(), ["limit"] = "30", ["search"] = Search, ["order"] = Order,
        ["kind"] = string.Join(',', Kinds), ["status"] = string.Join(',', Statuses), ["genre_v2"] = string.Join(',', Genres),
        ["season"] = Season, ["rating"] = string.Join(',', Ratings), ["duration"] = string.Join(',', Durations), ["origin"] = string.Join(',', Origins),
        ["score"] = MinimumScore > 0 ? MinimumScore.ToString() : null, ["studio"] = string.Join(',', Studios),
        ["mylist"] = string.Join(',', MyList), ["censored"] = ExcludeCensored ? "false" : null, ["licensed"] = LicensedOnly ? "true" : null
    };
    public bool Matches(Anime anime)
    {
        bool Kind(string value) => value switch
        {
            "tv_13" => anime.Kind == "tv" && anime.Episodes is > 0 and <= 13,
            "tv_24" => anime.Kind == "tv" && anime.Episodes is > 13 and <= 24,
            "tv_48" => anime.Kind == "tv" && anime.Episodes > 24,
            _ => anime.Kind == value
        };
        if (Kinds.Count > 0 && !Kinds.Any(Kind)) return false;
        if (Statuses.Count > 0 && !Statuses.Contains(anime.Status ?? "")) return false;
        if (Ratings.Count > 0 && !Ratings.Contains(anime.Rating ?? "")) return false;
        if (ExcludeCensored && anime.Rating == "rx") return false;
        if (LicensedOnly && string.IsNullOrWhiteSpace(anime.LicenseNameRu)) return false;
        if (Origins.Count > 0 && !Origins.Contains(anime.Origin ?? "")) return false;
        if (Genres.Count > 0 && !Genres.Any(id => anime.Genres.Any(x => x.Id == id))) return false;
        if (Studios.Count > 0 && !anime.Studios.Any(x => Studios.Contains(x.Id))) return false;
        if (MyList.Count > 0 && !MyList.Contains(anime.UserRate?.Status ?? "")) return false;
        if (MinimumScore > 0 && (!double.TryParse(anime.Score, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) || score < MinimumScore)) return false;
        if (!string.IsNullOrWhiteSpace(Search) && !anime.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) && !anime.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)) return false;
        if (Durations.Count > 0 && (anime.Duration <= 0 || !Durations.Contains(anime.Duration < 10 ? "S" : anime.Duration <= 30 ? "D" : "F"))) return false;
        if (!string.IsNullOrWhiteSpace(Season))
        {
            if (!DateOnly.TryParse(anime.AiredOn ?? anime.ReleasedOn, out var date)) return false;
            var releaseSeason = date.Month <= 3 ? "winter" : date.Month <= 6 ? "spring" : date.Month <= 9 ? "summer" : "fall";
            if (!Season.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(value => value == date.Year.ToString() || value == $"{releaseSeason}_{date.Year}" ||
                value.Split('_') is [var from, var to] && int.TryParse(from, out var first) && int.TryParse(to, out var last) && date.Year >= first && date.Year <= last)) return false;
        }
        return true;
    }
    public List<LibraryEntry> FilterLibrary(IEnumerable<LibraryEntry> entries, string? status)
    {
        var filtered = entries.Where(x => (string.IsNullOrEmpty(status) || x.Status == status) && Matches(x.Anime));
        var sorted = Order switch
        {
            "name" => filtered.OrderBy(x => x.Anime.Title).ToList(), "rate_score" => filtered.OrderByDescending(x => x.Score).ToList(),
            "ranked" => filtered.OrderByDescending(x => double.TryParse(x.Anime.Score, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ? score : 0).ToList(),
            "aired_on" => filtered.OrderByDescending(x => x.Anime.AiredOn).ToList(), "episodes" => filtered.OrderByDescending(x => x.Anime.Episodes).ToList(),
            "kind" => filtered.OrderBy(x => x.Anime.Kind).ToList(), "status" => filtered.OrderBy(x => x.Anime.Status).ToList(),
            _ => filtered.OrderByDescending(x => x.UpdatedAt).ToList()
        };
        if (Ascending) sorted.Reverse();
        return sorted;
    }
}
