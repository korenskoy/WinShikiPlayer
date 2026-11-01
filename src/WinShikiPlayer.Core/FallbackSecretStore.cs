namespace WinShikiPlayer.Core;

// Only application credentials may be supplied by the fallback. User sessions
// remain in the user's credential store, and overrides always take precedence.
public sealed class FallbackSecretStore(ISecretStore user, Func<string, string?> applicationDefault) : ISecretStore
{
    public string? Read(string key) => user.Read(key) ?? (key is "client-secret" or "kodik-token" ? applicationDefault(key) : null);
    public void Write(string key, string value) => user.Write(key, value);
    public void Delete(string key) => user.Delete(key);
}
