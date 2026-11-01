using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinShikiPlayer.Core;
using System.Diagnostics;

namespace WinShikiPlayer.Services;

internal static class ApplicationDiagnostics
{
    internal static void RecordUiError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppServices.DataDirectory);
            // Record locations, not exception messages, request URLs or credentials.
            var errors = new List<object>();
            for (Exception? current = exception; current is not null; current = current.InnerException)
                errors.Add(new { type = current.GetType().FullName, current.HResult, stack = current.StackTrace });
            File.WriteAllText(Path.Combine(AppServices.DataDirectory, "ui-error.json"),
                JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, errors }));
        }
        catch { /* Diagnostics must never replace the original UI error. */ }
    }

    public static async Task RunAsync(string[] arguments)
    {
        var output = arguments.FirstOrDefault(x => x.StartsWith("--diagnostic-output="))?["--diagnostic-output=".Length..];
        object result;
        try
        {
            if (arguments.Contains("--protocol-smoke") || arguments.Contains("--direct-protocol-smoke"))
            {
                ProtocolActivation.Register();
                var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
                var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var listen = ProtocolActivation.ListenAsync(uri =>
                {
                    if (uri.Host == "diagnostic" && uri.Query == "?nonce=" + nonce) received.TrySetResult(true);
                }, timeout.Token);
                var uri = "winshikiplayer://diagnostic?nonce=" + nonce;
                if (arguments.Contains("--direct-protocol-smoke"))
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }; start.ArgumentList.Add(uri); Process.Start(start);
                }
                else
                {
                    var support = await Windows.System.Launcher.QueryUriSupportAsync(new Uri(uri), Windows.System.LaunchQuerySupportType.Uri);
                    if (support != Windows.System.LaunchQuerySupportStatus.Available) throw new InvalidOperationException("Windows URI support: " + support);
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true, ErrorDialog = false });
                }
                await received.Task.WaitAsync(timeout.Token); timeout.Cancel(); await listen;
                result = new { success = true, shellLaunchSucceeded = true, callbackDelivered = true, timestamp = DateTimeOffset.UtcNow };
            }
            else if (arguments.Contains("--register-protocol"))
            {
                ProtocolActivation.Register();
                var command = new StringBuilder(2048); uint size = (uint)command.Capacity;
                var status = AssocQueryString(0x800, 1, "WinShikiPlayer.Protocol", "open", command, ref size);
                if (status != 0 || !command.ToString().Contains("WinShikiPlayer.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Windows не подтвердила обработчик схемы: 0x{status:X8}");
                result = new { success = true, protocolRegistered = true, selectedAsDefault = ProtocolActivation.IsDefaultHandler(), timestamp = DateTimeOffset.UtcNow };
            }
            else if (arguments.Contains("--auth-config-smoke"))
            {
                var services = new AppServices(); await services.InitializeAsync();
                var configPath = Path.Combine(AppServices.DataDirectory, "appsettings.local.json");
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(configPath))!;
                using var request = new HttpRequestMessage(HttpMethod.Post, services.Settings.ShikimoriHost + "/oauth/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string,string>
                    {
                        ["grant_type"]="authorization_code", ["client_id"]=services.Settings.ClientId,
                        ["client_secret"]=services.Secrets.Read("client-secret") ?? "", ["redirect_uri"]=services.Settings.RedirectUri,
                        ["code"]="winshikiplayer-diagnostic-invalid-code", ["code_verifier"]="abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
                    })
                };
                request.Headers.UserAgent.ParseAdd(services.Settings.UserAgent);
                using var response = await services.Http.SendAsync(request);
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var code = error.RootElement.TryGetProperty("error",out var e) ? e.GetString() : "unknown";
                result = new { success = code == "invalid_grant", clientIdMatches = services.Settings.ClientId == config["SHIKIMORI_CLIENT_ID"],
                    secretMatches = services.Secrets.Read("client-secret") == config["SHIKIMORI_CLIENT_SECRET"],
                    redirectUri = services.Settings.RedirectUri, userAgent = services.Settings.UserAgent, httpStatus = (int)response.StatusCode,
                    oauthError = code, timestamp = DateTimeOffset.UtcNow };
            }
            else if (arguments.Contains("--subtitle-smoke"))
            {
                var cues = SubtitleParser.ParseVtt("WEBVTT\n\n00:01.000 --> 00:04.000\nПроверка ASS renderer\n");
                using var renderer = new AssRenderer(); renderer.Load(Encoding.UTF8.GetBytes(SubtitleParser.ToAss(cues, new AppSettings())));
                var frame = renderer.Render(1280, 720, 2);
                if (frame is null || !frame.Where((_, i) => i % 4 == 3).Any(x => x > 0)) throw new InvalidOperationException("libass не отрисовал тестовый текст.");
                var services = new AppServices(); await services.InitializeAsync();
                var tracks = await services.Subtitles.SearchAsync(16498, 1, "auto", default);
                if (tracks.Count > 0)
                {
                    var bytes = await services.Subtitles.DownloadAsync(tracks[0].AssUri, default); renderer.Load(bytes);
                    renderer.Render(1280, 720, 30);
                }
                result = new { success = true, libassRendered = true, anime365Reachable = true, subtitleTracks = tracks.Count, timestamp = DateTimeOffset.UtcNow };
            }
            else if (arguments.Contains("--session-smoke"))
            {
                var services = new AppServices(); await services.InitializeAsync();
                if (!services.Auth.IsSignedIn) throw new InvalidOperationException("Нет сохранённой сессии Shikimori.");
                var user = await services.Shikimori.WhoAmIAsync();
                var library = await services.Shikimori.LibraryAsync(user.Id, null, 1);
                await services.Auth.RefreshAsync(default);
                var refreshed = await services.Shikimori.WhoAmIAsync();
                if (refreshed.Id != user.Id) throw new InvalidOperationException("После обновления токена изменился аккаунт.");
                result = new { success = true, signedIn = true, whoamiSucceeded = true, librarySucceeded = true,
                    refreshSucceeded = true, libraryItemsOnFirstPage = library.Count, timestamp = DateTimeOffset.UtcNow };
            }
            else
            {
                var services = new AppServices(); await services.InitializeAsync();
                var translations = await services.Kodik.SearchAsync(16498);
                var resolved = await services.Resolver.ResolveAsync(translations[0].Episodes[0].Link, default);
                using var player = new MediaPlayer { AutoPlay = true, IsMuted = true };
                var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                player.MediaOpened += (_, _) => opened.TrySetResult(true);
                player.MediaFailed += (_, args) => opened.TrySetException(new InvalidOperationException("MediaPlayer: " + args.Error));
                player.Source = MediaSource.CreateFromUri(resolved.Streams[0].Uri);
                player.Play();
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(45));
                var initial = player.PlaybackSession.Position.TotalSeconds;
                await Task.Delay(4000);
                var position = player.PlaybackSession.Position.TotalSeconds;
                var duration = player.PlaybackSession.NaturalDuration.TotalSeconds;
                if (position < initial + 1 || duration < 60) throw new InvalidOperationException("Время воспроизведения не продвигается.");
                player.PlaybackSession.Position = TimeSpan.FromSeconds(60);
                await Task.Delay(1500);
                var seeked = player.PlaybackSession.Position.TotalSeconds;
                if (seeked < 59) throw new InvalidOperationException("Перемотка не сработала.");
                player.Pause();
                result = new { success = true, mediaOpened = true, playbackAdvancing = true, seekSucceeded = true,
                    durationSeconds = duration, positionSeconds = position, seekPositionSeconds = seeked,
                    qualities = resolved.Streams.Count, studios = translations.Count, timestamp = DateTimeOffset.UtcNow };
            }
        }
        catch (Exception ex) { Environment.ExitCode = 1; result = new { success = false, errorType = ex.GetType().Name, message = ex.Message, timestamp = DateTimeOffset.UtcNow }; }
        if (output is not null) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryString(uint flags, uint str, string association, string extra, StringBuilder output, ref uint size);
}
