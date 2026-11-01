namespace WinShikiPlayer.Core;

public sealed record TrailerCard(AnimeVideo Video, Uri NavigateUri, Uri? PreviewUri)
{
    public string KindLabel => Video.Kind?.ToLowerInvariant() switch
    {
        "trailer" or "promo" => "TRAILER",
        { } kind => kind.ToUpperInvariant(),
        _ => "VIDEO"
    };
}

public static class TrailerCatalog
{
    private static readonly HashSet<string> Kinds = ["pv", "op", "ed", "cm", "trailer", "promo"];

    public static List<TrailerCard> Select(IEnumerable<AnimeVideo> videos)
    {
        List<TrailerCard> result = [];
        foreach (var video in videos)
        {
            if (video.Kind is not null && !Kinds.Contains(video.Kind.ToLowerInvariant())) continue;
            var target = WebUri(video.Url ?? video.PlayerUrl);
            if (target is null) continue;
            var preview = WebUri(video.ImageUrl);
            if (preview?.Scheme == "http") preview = new UriBuilder(preview) { Scheme = "https", Port = preview.IsDefaultPort ? -1 : preview.Port }.Uri;
            // Same YouTube thumbnail CDN/path; img.youtube.com can fail DNS
            // independently of the image CDN on Windows networks.
            if (preview?.Host.Equals("img.youtube.com", StringComparison.OrdinalIgnoreCase) == true)
                preview = new UriBuilder(preview) { Host = "i.ytimg.com" }.Uri;
            result.Add(new(video, target, preview));
            if (result.Count == 10) break;
        }
        return result;
    }

    private static Uri? WebUri(string? value)
    {
        if (value?.StartsWith("//", StringComparison.Ordinal) == true) value = "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri : null;
    }
}
