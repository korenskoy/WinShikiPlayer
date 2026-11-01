using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WinShikiPlayer.Core;

public sealed record OAuthToken(string AccessToken, string RefreshToken, long CreatedAt, int ExpiresIn);
public sealed record OAuthAttempt(string State, string Verifier, Uri AuthorizeUri);
public sealed class OAuthException(HttpStatusCode status, string code, string grant) : ApiException(status)
{
    public string Code { get; } = code;
    public override string Message => Code switch
    {
        "invalid_client" => "Shikimori отклонил Client ID или Client Secret (invalid_client).",
        "invalid_grant" => grant == "refresh_token" ? "Shikimori отклонил обновление токена. Войдите снова (invalid_grant)." : "Код входа истёк или уже использован. Запустите новый вход (invalid_grant).",
        "invalid_scope" => "Приложению недоступны запрошенные права (invalid_scope).",
        _ => $"Shikimori отклонил OAuth-запрос: {Code}, HTTP {(int)Status}."
    };
}

public sealed class AuthService
{
    public const string RedirectUri = "http://127.0.0.1:47283/callback/";
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthToken? _token;
    private string? _sessionHost;
    private string? _sessionClientId;
    private Channel<Uri>? _protocolCallbacks;
    private int _generation;
    public bool IsSignedIn => _token is not null && _sessionHost == _settings.ShikimoriHost && _sessionClientId == _settings.ClientId;

    public AuthService(HttpClient http, AppSettings settings, ISecretStore secrets)
    {
        _http = http; _settings = settings; _secrets = secrets;
        var saved = secrets.Read("oauth-session");
        if (saved is null) return;
        try
        {
            var session = JsonSerializer.Deserialize<SavedSession>(saved, JsonDefaults.Options);
            _token = session?.Token; _sessionHost = session?.Host; _sessionClientId = session?.ClientId;
        }
        catch (JsonException) { secrets.Delete("oauth-session"); }
    }

    public OAuthAttempt BeginLogin()
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId)) throw new InvalidOperationException("Сначала укажите Client ID в настройках.");
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = ShikimoriClient.Query(new()
        {
            ["client_id"] = _settings.ClientId, ["redirect_uri"] = _settings.RedirectUri, ["response_type"] = "code",
            ["scope"] = "user_rates topics comments", ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256"
        });
        return new(state, verifier, new($"{_settings.ShikimoriHost}/oauth/authorize?{query}"));
    }

    public async Task ListenAndLoginAsync(OAuthAttempt attempt, Action<Uri> openBrowser, CancellationToken ct)
    {
        if (_settings.RedirectUri == "winshikiplayer://oauth")
        {
            _protocolCallbacks = Channel.CreateBounded<Uri>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
            using var protocolTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            protocolTimeout.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                openBrowser(attempt.AuthorizeUri);
                while (true)
                {
                    var callback = await _protocolCallbacks.Reader.ReadAsync(protocolTimeout.Token);
                    var values = ParseCallback(callback);
                    if (values.GetValueOrDefault("state") != attempt.State) continue;
                    if (values.ContainsKey("error")) throw new InvalidOperationException("Вход отменён на стороне Shikimori.");
                    if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Shikimori не передал код авторизации.");
                    await _gate.WaitAsync(ct);
                    try { await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = attempt.Verifier }, ct); }
                    finally { _gate.Release(); }
                    return;
                }
            }
            finally { _protocolCallbacks = null; }
        }
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();
        openBrowser(attempt.AuthorizeUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var uri = context.Request.Url;
            var query = context.Request.QueryString;
            var valid = uri is not null && uri.AbsolutePath == "/callback/" && context.Request.HttpMethod == "GET" &&
                query["state"] == attempt.State;
            context.Response.StatusCode = valid ? 200 : 400;
            var message = valid ? "You can return to WinShikiPlayer." : "Invalid authorization callback.";
            var bytes = Encoding.UTF8.GetBytes(message);
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(bytes, timeout.Token);
            context.Response.Close();
            if (!valid) continue;
            if (query["error"] is not null) throw new InvalidOperationException("Вход отменён на стороне Shikimori.");
            var code = query["code"];
            if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Shikimori не передал код авторизации.");
            await _gate.WaitAsync(ct);
            try { await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = attempt.Verifier }, ct); }
            finally { _gate.Release(); }
            return;
        }
    }

    public async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        ReloadStoredSession();
        if (!IsSignedIn) return null;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is null || !IsSignedIn) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(_token.CreatedAt).AddSeconds(_token.ExpiresIn - 60) <= DateTimeOffset.UtcNow)
                await RefreshInternalAsync(ct);
            return _token!.AccessToken;
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await RefreshInternalAsync(ct); }
        finally { _gate.Release(); }
    }

    private Task RefreshInternalAsync(CancellationToken ct)
    {
        ReloadStoredSession();
        if (!IsSignedIn) throw new InvalidOperationException("Войдите в Shikimori.");
        return ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = _token!.RefreshToken }, ct);
    }

    private async Task ExchangeAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var generation = _generation;
        var host = _settings.ShikimoriHost;
        var clientId = _settings.ClientId;
        form["client_id"] = _settings.ClientId;
        form["client_secret"] = _secrets.Read("client-secret") ?? "";
        form["redirect_uri"] = _settings.RedirectUri;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.ShikimoriHost}/oauth/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.UserAgent.ParseAdd(_settings.UserAgent);
        using var response = await _http.SendAsync(request, ct);
        // HttpClient is configured without redirects, so credentials never follow a mirror redirect.
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = "oauth_error";
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (error.RootElement.TryGetProperty("error", out var value) && value.GetString() is "invalid_client" or "invalid_grant" or "invalid_scope" or "unsupported_grant_type" or "invalid_request") errorCode = value.GetString()!;
            }
            catch (JsonException) { }
            throw new OAuthException(response.StatusCode, errorCode, form["grant_type"]);
        }
        var token = await response.Content.ReadFromJsonAsync<OAuthToken>(JsonDefaults.Options, ct)
            ?? throw new JsonException("Не удалось прочитать токены.");
        if (string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.RefreshToken)) throw new JsonException("Получен неполный OAuth-ответ.");
        if (generation != _generation || host != _settings.ShikimoriHost || clientId != _settings.ClientId) throw new OperationCanceledException("Конфигурация аккаунта изменилась.");
        _secrets.Write("oauth-session", JsonSerializer.Serialize(new SavedSession(_settings.ShikimoriHost, _settings.ClientId, token), JsonDefaults.Options));
        _token = token; _sessionHost = _settings.ShikimoriHost; _sessionClientId = _settings.ClientId;
    }

    public void SignOut()
    {
        Interlocked.Increment(ref _generation);
        _secrets.Delete("oauth-session"); _token = null; _sessionHost = null; _sessionClientId = null;
    }
    private void ReloadStoredSession()
    {
        var saved = _secrets.Read("oauth-session");
        if (saved is null) { _token = null; _sessionHost = null; _sessionClientId = null; return; }
        try
        {
            var session = JsonSerializer.Deserialize<SavedSession>(saved, JsonDefaults.Options);
            if (session is null) return;
            _token = session.Token; _sessionHost = session.Host; _sessionClientId = session.ClientId;
        }
        catch (JsonException) { }
    }
    public void ReceiveProtocolCallback(Uri uri)
    {
        if (uri.Scheme == "winshikiplayer" && uri.Host == "oauth" && uri.AbsolutePath is "" or "/" && uri.UserInfo.Length == 0)
            _protocolCallbacks?.Writer.TryWrite(uri);
    }
    public static Dictionary<string, string> ParseCallback(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).GroupBy(x => Uri.UnescapeDataString(x[0])).ToDictionary(x => x.Key,
            x => x.Count() == 1 && x.First().Length == 2 ? Uri.UnescapeDataString(x.First()[1].Replace('+', ' ')) : "");
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record SavedSession(string Host, string ClientId, OAuthToken Token);
}
