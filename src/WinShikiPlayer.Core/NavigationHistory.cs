namespace WinShikiPlayer.Core;

public sealed record NavigationSnapshot(List<NavigationEntry> Entries, int Index);

public sealed class NavigationHistory
{
    private readonly List<NavigationEntry> _entries = [];
    private int _index = -1;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;
    public NavigationEntry? Current => _index >= 0 ? _entries[_index] : null;
    public void Restore(NavigationSnapshot? snapshot)
    {
        _entries.Clear(); _index = -1;
        if (snapshot is null || snapshot.Index < 0 || snapshot.Index >= snapshot.Entries.Count) return;
        _entries.AddRange(snapshot.Entries.TakeLast(120));
        _index = Math.Clamp(snapshot.Index - Math.Max(0, snapshot.Entries.Count - 120), 0, _entries.Count - 1);
    }
    public void Push(NavigationEntry entry)
    {
        if (entry == Current) return;
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(entry); if (_entries.Count > 120) _entries.RemoveAt(0); _index = _entries.Count - 1;
    }
    public NavigationEntry? Back() { if (!CanGoBack) return null; return _entries[--_index]; }
    public NavigationEntry? Forward() { if (!CanGoForward) return null; return _entries[++_index]; }
    public NavigationSnapshot Snapshot() => new([.. _entries], _index);
}
