using System.Collections.Concurrent;

namespace WinShikiPlayer.Core;

public sealed class RepositoryCache(LocalStore store)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private int _generation;
    public async Task ClearAsync()
    {
        await _writes.WaitAsync();
        try { Interlocked.Increment(ref _generation); await store.RemoveCacheAsync(); }
        finally { _writes.Release(); }
    }
    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load, TimeSpan ttl, bool refresh = false)
    {
        var gate = _gates.GetOrAdd(key, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            var generation = Volatile.Read(ref _generation);
            var entry = await store.ReadAsync<Entry<T>>("cache-" + key);
            if (!refresh && entry is not null && entry.CreatedAt + ttl > DateTimeOffset.UtcNow) return entry.Value;
            try
            {
                var value = await load();
                await _writes.WaitAsync();
                try
                {
                    if (generation == Volatile.Read(ref _generation))
                        await store.WriteAsync("cache-" + key, new Entry<T>(DateTimeOffset.UtcNow, value));
                }
                finally { _writes.Release(); }
                return value;
            }
            catch (System.Net.Http.HttpRequestException) when (entry is not null) { return entry.Value; }
        }
        finally { gate.Release(); }
    }
    private sealed record Entry<T>(DateTimeOffset CreatedAt, T Value);
}
