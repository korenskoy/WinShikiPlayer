using System.Net;
using System.Text.Json;
using Windows.Security.Credentials;
using WinShikiPlayer.Core;

namespace WinShikiPlayer.Services;

public sealed class CredentialStore : ISecretStore
{
    private const string Resource = "WinShikiPlayer";
    private readonly PasswordVault _vault = new();
    public string? Read(string key)
    {
        try { var credential = _vault.Retrieve(Resource, key); credential.RetrievePassword(); return credential.Password; }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070490) || ex.HResult == unchecked((int)0x80070057)) { return null; }
    }
    public void Write(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { Delete(key); return; }
        _vault.Add(new PasswordCredential(Resource, key, value));
    }
    public void Delete(string key)
    {
        if (Read(key) is not null) _vault.Remove(_vault.Retrieve(Resource, key));
    }
}

public sealed class AppServices
{
    public static string DataDirectory => ProtocolActivation.HasPackageIdentity() ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShikiPlayer");
    public LocalStore Store { get; }
    private readonly ISecretStore _userSecrets;
    private readonly bool _skipLocalConfiguration;
    public ISecretStore Secrets { get; private set; }
    // Redirects are handled as errors: never forward bearer tokens, client secrets or Kodik tokens to another host.
    public HttpClient Http { get; } = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    public AppSettings Settings { get; private set; } = new();
    public AuthService Auth { get; private set; } = null!;
    public ShikimoriClient Shikimori { get; private set; } = null!;
    public KodikClient Kodik { get; private set; } = null!;
    public KodikResolver Resolver { get; private set; } = null!;
    public Anime365Client Subtitles { get; private set; } = null!;
    public WatchHistory History { get; }
    public EpisodeProgressStore Progress { get; }
    public RepositoryCache Cache { get; }
    public User? User { get; set; }
    public AppServices(LocalStore? store = null, ISecretStore? secrets = null, bool skipLocalConfiguration = false)
    {
        Store = store ?? new(DataDirectory); _userSecrets = secrets ?? new CredentialStore(); Secrets = _userSecrets;
        _skipLocalConfiguration = skipLocalConfiguration;
        History = new(Store); Progress = new(Store); Cache = new(Store);
    }
    public string CacheKey(string key) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Settings.ShikimoriHost + ":" + (User?.Id.ToString() ?? "guest") + ":" + key)));
    public async Task InitializeAsync()
    {
        var savedSettings = await Store.ReadAsync<AppSettings>("settings");
        Settings = savedSettings ?? new();
        using var resource = typeof(AppServices).Assembly.GetManifestResourceStream("WinShikiPlayer.BundledConfiguration.json");
        var bundled = resource is null ? new Dictionary<string, string>() : await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(resource) ?? [];
        var bundledClient = bundled.GetValueOrDefault("SHIKIMORI_CLIENT_ID") ?? "";
        if (string.IsNullOrWhiteSpace(Settings.ClientId)) Settings.ClientId = bundledClient;
        if (savedSettings is null)
        {
            Settings.ShikimoriHost = bundled.GetValueOrDefault("SHIKIMORI_BASE_URL") ?? Settings.ShikimoriHost;
            Settings.RedirectUri = bundled.GetValueOrDefault("SHIKIMORI_REDIRECT_URI") ?? Settings.RedirectUri;
            Settings.UserAgent = bundled.GetValueOrDefault("SHIKIMORI_USER_AGENT") ?? Settings.UserAgent;
        }
        Secrets = new FallbackSecretStore(_userSecrets, key => key switch
        {
            "client-secret" when Settings.ClientId == bundledClient => bundled.GetValueOrDefault("SHIKIMORI_CLIENT_SECRET"),
            "kodik-token" => bundled.GetValueOrDefault("KODIK_API_TOKEN"),
            _ => null
        });
        var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
        if (!File.Exists(localPath)) localPath = Path.Combine(DataDirectory, "appsettings.local.json");
        if (!_skipLocalConfiguration && File.Exists(localPath))
        {
            var local = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(localPath)) ?? [];
            if (local.GetValueOrDefault("SHIKIMORI_CLIENT_ID") is { Length: > 0 } id) Settings.ClientId = id;
            if (local.GetValueOrDefault("SHIKIMORI_BASE_URL") is { Length: > 0 } host) Settings.ShikimoriHost = host;
            if (local.GetValueOrDefault("SHIKIMORI_REDIRECT_URI") is { Length: > 0 } redirect) Settings.RedirectUri = redirect;
            if (local.GetValueOrDefault("SHIKIMORI_USER_AGENT") is { Length: > 0 } agent) Settings.UserAgent = agent;
            if (local.GetValueOrDefault("SHIKIMORI_CLIENT_SECRET") is { Length: > 0 } secret) Secrets.Write("client-secret", secret);
            if (local.GetValueOrDefault("KODIK_API_TOKEN") is { Length: > 0 } token) Secrets.Write("kodik-token", token);
        }
        Settings.ShikimoriHost = AppSettings.ValidateHost(Settings.ShikimoriHost);
        Settings.KodikHost = AppSettings.ValidateHost(Settings.KodikHost);
        if (Settings.RedirectUri is not ("winshikiplayer://oauth" or AuthService.RedirectUri)) throw new InvalidOperationException("Неподдерживаемый Redirect URI в конфигурации.");
        Auth = new(Http, Settings, Secrets);
        Shikimori = new(Http, Settings, Auth);
        Kodik = new(Http, Settings, Secrets);
        Resolver = new(Http);
        Subtitles = new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All })
            { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 * 1024 * 1024 }, Store);
    }
}
