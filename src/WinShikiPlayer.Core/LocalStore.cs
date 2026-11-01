using System.Text.Json;

namespace WinShikiPlayer.Core;

public sealed class LocalStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public LocalStore(string directory) { _directory = directory; Directory.CreateDirectory(directory); }

    public async Task<T?> ReadAsync<T>(string name)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_directory, name + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), JsonDefaults.Options) : default;
        }
        catch (JsonException) { return default; }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync<T>(string name, T value)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_directory, name + ".json");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveCacheAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "cache-*.json", SearchOption.TopDirectoryOnly)) File.Delete(path);
        }
        finally { _gate.Release(); }
    }
}

public interface ISecretStore
{
    string? Read(string key);
    void Write(string key, string value);
    void Delete(string key);
}

public sealed class WatchHistory(LocalStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<List<WatchEntry>> LoadAsync() => await store.ReadAsync<List<WatchEntry>>("history") ?? [];
    public async Task SaveAsync(WatchEntry entry)
    {
        await _gate.WaitAsync();
        try
        {
            var items = await LoadAsync();
            items.RemoveAll(x => x.Anime.Id == entry.Anime.Id);
            items.Insert(0, entry);
            await store.WriteAsync("history", items.Take(300).ToList());
        }
        finally { _gate.Release(); }
    }
}
