namespace WinShikiPlayer.Core;

// One next-episode resolve, scoped to its exact studio/episode URL.
public sealed class EpisodePrefetch(Func<Uri, CancellationToken, Task<ResolvedVideo>> resolve) : IDisposable
{
    private Uri? _link;
    private Task<ResolvedVideo?>? _task;
    private CancellationTokenSource? _cancellation;
    public void Prepare(Uri link)
    {
        if (_link == link) return;
        Reset(); _link = link; _cancellation = new();
        _task = LoadAsync(link, _cancellation.Token);
    }
    private async Task<ResolvedVideo?> LoadAsync(Uri link, CancellationToken ct)
    {
        try { var result = await resolve(link, ct); return ct.IsCancellationRequested ? null : result; }
        catch { return null; } // Speculative failures must not interrupt playback.
    }
    public async Task<ResolvedVideo?> TakeAsync(Uri link, CancellationToken ct)
    {
        if (_link != link || _task is null) return null;
        var result = await _task.WaitAsync(ct);
        if (_link == link) { _task = null; _link = null; _cancellation?.Dispose(); _cancellation = null; }
        return result;
    }
    public void Reset() { _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; _link = null; _task = null; }
    public void Dispose() => Reset();
}
