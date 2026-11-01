namespace WinShikiPlayer.Core;

public sealed record PlayerChapter(string Label, double Start, double End);

public static class PlayerTimeline
{
    public static string Time(double seconds) => !double.IsFinite(seconds) || seconds < 0 ? "00:00" :
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    public static double Clamp(double position, double duration) => !double.IsFinite(position) || !double.IsFinite(duration) || duration <= 0 ? 0 : Math.Clamp(position, 0, duration);

    public static List<PlayerChapter> Chapters(double duration, SkipRange? opening, SkipRange? ending)
    {
        List<PlayerChapter> result = [];
        if (!double.IsFinite(duration) || duration <= 0) return result;
        foreach (var (label, range) in new[] { ("ОП", opening), ("ЭД", ending) })
            if (range is not null && double.IsFinite(range.Start) && double.IsFinite(range.End) && range.End > range.Start && range.End > 0 && range.Start < duration)
                result.Add(new(label, Math.Max(0, range.Start), Math.Min(duration, range.End)));
        return result;
    }

    public static SkipRange? Active(double position, SkipRange? opening, SkipRange? ending) =>
        new[] { opening, ending }.FirstOrDefault(x => x is not null && double.IsFinite(x.Start) && double.IsFinite(x.End) && x.Start >= 0 && x.End > x.Start && position >= x.Start && position < x.End);

    public static double? SkipTarget(double position, double duration, SkipRange? opening, SkipRange? ending) =>
        duration > 0 && Active(position, opening, ending) is { } active ? Math.Min(active.End, Math.Max(0, duration - 1)) : null;

    public static bool ShowNext(double position, double duration, bool hasNext) => hasNext && double.IsFinite(duration) && duration > 0 && duration - position is > 0 and <= 30;
}
