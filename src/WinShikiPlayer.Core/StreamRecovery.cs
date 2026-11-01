namespace WinShikiPlayer.Core;

public sealed record StreamRetry(VideoStream Stream, TimeSpan Delay, bool Fallback);

// Matches the original's two delayed retries, then the next quality.
public sealed class StreamRecovery
{
    private readonly Dictionary<Uri, int> _attempts = [];
    public void Reset() => _attempts.Clear();
    public StreamRetry? Next(VideoStream current, IReadOnlyList<VideoStream> streams)
    {
        var count = _attempts.GetValueOrDefault(current.Uri);
        if (count < 2) { _attempts[current.Uri] = count + 1; return new(current, TimeSpan.FromMilliseconds(count == 0 ? 200 : 500), false); }
        var after = false;
        foreach (var stream in streams)
        {
            if (stream.Uri == current.Uri) { after = true; continue; }
            if (after && _attempts.GetValueOrDefault(stream.Uri) < 2) return new(stream, TimeSpan.Zero, true);
        }
        return null;
    }
}
