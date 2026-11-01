using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinShikiPlayer.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Original anime and whoami fixtures", Fixtures),
    ("Catalog query escaping, anonymous request", Catalog),
    ("Rate creation and update payloads", Rates),
    ("GET retries transient errors", Retry),
    ("Mutations never retry server errors", NoMutationRetry),
    ("OAuth state, PKCE, redirect and callback", OAuthCallback),
    ("Expired token refresh is serialized", Refresh),
    ("Failed refresh preserves stored session", RefreshFailure),
    ("Session never crosses configured host", HostIsolation),
    ("Host validation rejects insecure inputs", HostValidation),
    ("Kodik studios, seasons and blocked episodes", Kodik),
    ("Kodik direct, base64, ROT18 and URL-safe sources", SourceDecoding),
    ("Opening and ending ranges", SkipRanges),
    ("Concurrent history persistence and deduplication", Persistence),
    ("VTT timing, multiline text and custom ASS output", Subtitles),
    ("Merged history orders and deduplicates local and remote entries", MergedHistory),
    ("Repository cache deduplicates concurrent loads", Cache),
    ("Anime365 wrapped camelCase responses and language filtering", Anime365),
    ("Shikimori mixed numeric and string score names", NumericScoreNames),
    ("Navigation history branches after going back", Navigation),
    ("Complete library paginates and hydrates every title", CompleteLibrary),
    ("Catalog and library multi-filters", Filters),
    ("Playback uses Shikimori progress instead of a stale local replay", PlaybackSelection),
    ("Trailer catalog excludes previews and upgrades thumbnail URLs", Trailers),
    ("Player chapters, skip boundaries and next-episode countdown", PlayerChapters),
    ("Next-episode prefetch deduplicates, isolates studios and cancels", PlayerPrefetch),
    ("Stream recovery retries twice then exhausts qualities without loops", PlayerRecovery),
    ("Episode positions survive round trips, seasons, completion and restart", EpisodePositions),
    ("Kodik resolver retries transient reads and caches each exact episode", ResolverCache)
};
if (args.Contains("--live")) tests = [.. tests, ("Live Shikimori catalog and Kodik resolution", LiveServices)];
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task ResolverCache()
{
    var calls = 0; var first = true;
    using var http = new HttpClient(new MockHandler(request =>
    {
        calls++;
        if (first) { first = false; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
        return Task.FromResult(request.Method == HttpMethod.Post ? Ok("""{"links":{"720":[{"src":"https://cdn.example/video.mp4"}]}}""") : Ok("<html></html>"));
    }));
    var resolver = new KodikResolver(http); var link = new Uri("https://player.example/seria/1/abc/720p");
    Assert((await resolver.ResolveAsync(link, default)).Streams.Count == 1 && calls == 3, "Transient resolver read was not retried once");
    await resolver.ResolveAsync(link, default); Assert(calls == 3, "Repeated studio pick made unnecessary metadata requests");
    await resolver.ResolveAsync(new("https://player.example/seria/2/abc/720p"), default); Assert(calls == 5, "Cached stream crossed episodes");
    resolver.Invalidate(link); await resolver.ResolveAsync(link, default); Assert(calls == 7, "Manual retry did not invalidate the cached URL");
    using var denied = new HttpClient(new MockHandler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }));
    var before = calls;
    try { await new KodikResolver(denied).ResolveAsync(link, default); throw new Exception("Forbidden stream was accepted"); }
    catch (ApiException ex) when (ex.Status == HttpStatusCode.Forbidden) { }
    Assert(calls == before + 1, "Permanent authorization error was retried");
}
static async Task EpisodePositions()
{
    var path = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString());
    var store = new LocalStore(path); var progress = new EpisodeProgressStore(store);
    var now = DateTimeOffset.UtcNow;
    await Task.WhenAll(progress.SaveAsync(new(10, 1, 1, 60, 1400, now)), progress.SaveAsync(new(10, 1, 2, 90, 1400, now)), progress.SaveAsync(new(10, 2, 1, 120, 1400, now)));
    Assert((await progress.FindAsync(10, 1, 1))?.Position == 60 && (await progress.FindAsync(10, 1, 2))?.Position == 90, "Changing episodes lost an earlier position");
    Assert((await progress.FindAsync(10, 2, 1))?.Position == 120, "Seasons share a resume position");
    Assert(await progress.FindAsync(11, 1, 1) is null, "Resume point crossed titles");
    var reopened = new EpisodeProgressStore(new(path));
    Assert((await reopened.FindAsync(10, 1, 2))?.Position == 90, "Resume point did not survive restart");
    await progress.SaveAsync(new(10, 1, 1, 1260, 1400, now.AddSeconds(2)));
    Assert(await progress.FindAsync(10, 1, 1) is null, "Completed episode resumed at its end");
    await progress.SaveAsync(new(10, 1, 1, 70, 1400, now.AddSeconds(1)));
    Assert(await progress.FindAsync(10, 1, 1) is null, "Older write restored completed progress");
    await progress.SaveAsync(new(10, 1, 2, 0, 1400, now.AddSeconds(3)));
    Assert((await progress.FindAsync(10, 1, 2))?.Position == 90, "Zero position overwrote a valid resume");
    await progress.SaveAsync(new(10, 1, 2, double.NaN, 1400, now));
    Assert((await progress.FindAsync(10, 1, 2))?.Position == 90, "Invalid position entered persistence");
}
static Task PlayerRecovery()
{
    var high = new VideoStream("720p", new("https://example.test/high.mp4")); var low = new VideoStream("480p", new("https://example.test/low.mp4"));
    var recovery = new StreamRecovery(); VideoStream[] streams = [high, low];
    Assert(recovery.Next(high, streams) is { Delay.TotalMilliseconds: 200, Fallback: false }, "First retry has no backoff");
    Assert(recovery.Next(high, streams) is { Delay.TotalMilliseconds: 500, Fallback: false }, "Second retry has no backoff");
    Assert(recovery.Next(high, streams) is { Fallback: true } fallback && fallback.Stream == low, "Failed quality does not fall back");
    recovery.Next(low, streams); recovery.Next(low, streams);
    Assert(recovery.Next(low, streams) is null, "Exhausted qualities retry forever");
    recovery.Reset(); Assert(recovery.Next(high, streams) is { Fallback: false }, "Manual retry does not reset recovery");
    return Task.CompletedTask;
}
static Task PlayerChapters()
{
    var opening = new SkipRange(30, 120); var ending = new SkipRange(1300, 1500);
    var chapters = PlayerTimeline.Chapters(1400, opening, ending);
    Assert(chapters.Count == 2 && chapters[1].End == 1400, "Chapter exceeds duration");
    Assert(PlayerTimeline.Chapters(0, opening, ending).Count == 0, "Unknown duration shows chapters");
    Assert(PlayerTimeline.Chapters(1400, new(double.NaN, 120), new(1500, 1600)).Count == 0, "Invalid ranges drawn on timeline");
    Assert(PlayerTimeline.Active(30, opening, ending) == opening && PlayerTimeline.Active(120, opening, ending) is null, "Opening boundary incorrect");
    Assert(PlayerTimeline.Active(60, opening, new(40, 80)) == opening, "Opening does not win over overlapping ending");
    Assert(PlayerTimeline.SkipTarget(1390, 1400, opening, ending) == 1399, "Skip can seek past the playable end");
    Assert(PlayerTimeline.SkipTarget(20, 1400, opening, ending) is null, "Skip is active outside chapters");
    Assert(!PlayerTimeline.ShowNext(1369, 1400, true) && PlayerTimeline.ShowNext(1370, 1400, true), "Countdown threshold is not thirty seconds");
    Assert(!PlayerTimeline.ShowNext(1400, 1400, true) && !PlayerTimeline.ShowNext(1390, 1400, false), "Countdown displayed after ending or without next episode");
    Assert(PlayerTimeline.Clamp(-10, 100) == 0 && PlayerTimeline.Clamp(200, 100) == 100 && PlayerTimeline.Clamp(double.NaN, 100) == 0, "Seek clamping failed");
    Assert(PlayerTimeline.Time(3661.9) == "1:01:01" && PlayerTimeline.Time(double.NaN) == "00:00", "Timecode formatting failed");
    return Task.CompletedTask;
}
static async Task PlayerPrefetch()
{
    var link = new Uri("https://example.test/studio-a/2"); var other = new Uri("https://example.test/studio-b/2");
    var video = new ResolvedVideo([new("720p", new("https://example.test/video.mp4"))], null, null);
    var calls = 0; var gate = new TaskCompletionSource<ResolvedVideo>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var prefetch = new EpisodePrefetch(async (_, ct) => { calls++; return await gate.Task.WaitAsync(ct); });
    prefetch.Prepare(link); prefetch.Prepare(link);
    Assert(calls == 1, "Concurrent duplicate prefetches started");
    Assert(await prefetch.TakeAsync(other, default) is null, "Prefetch leaked to another studio");
    gate.SetResult(video);
    Assert(ReferenceEquals(await prefetch.TakeAsync(link, default), video), "Resolved streams were not reused");
    Assert(await prefetch.TakeAsync(link, default) is null, "Prefetch was consumed twice");
    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var pending = new EpisodePrefetch(async (_, ct) => { using var registration = ct.Register(() => cancelled.TrySetResult(true)); await Task.Delay(Timeout.Infinite, ct); return video; });
    pending.Prepare(link); pending.Reset();
    Assert(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1)), "Closing player did not cancel prefetch");
    Assert(await pending.TakeAsync(link, default) is null, "Cancelled stream remained available");
    using var failed = new EpisodePrefetch((_, _) => throw new HttpRequestException("offline"));
    failed.Prepare(link); Assert(await failed.TakeAsync(link, default) is null, "Speculative error interrupted playback");
}
static Task Fixtures()
{
    var anime = JsonSerializer.Deserialize<Anime>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/anime_detail.json")), JsonDefaults.Options)!;
    Assert(anime.Id == 50 && anime.Title == "аниме_50", "Anime fixture decoding");
    var user = JsonSerializer.Deserialize<User>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/whoami.json")), JsonDefaults.Options)!;
    Assert(user.Id == 1001 && user.Nickname == "FixtureUser", "User fixture decoding");
    Assert(new Anime { Status = "ongoing", Episodes = 12, EpisodesAired = 3 }.AvailableEpisodes == 3, "Unaired episodes must not be offered");
    Assert(new Anime { Status = "anons", Episodes = 12 }.AvailableEpisodes == 0, "Announcements must not offer playback");
    Assert(new AnimeCard(anime with { Image = new("/uploads/poster/test.jpg", null) }, "https://shikimori.io").PosterUri?.AbsoluteUri == "https://shikimori.io/uploads/poster/test.jpg", "Relative poster must use configured host");
    Assert(new AnimeCard(anime with { Image = new("/assets/globals/missing_original.jpg", null) }, "https://shikimori.io").PosterUri is null, "Missing poster must use the app placeholder instead of downloading a 404 illustration");
    return Task.CompletedTask;
}
static async Task Catalog()
{
    var handler = new MockHandler(async request =>
    {
        Assert(request.Headers.Authorization is null, "Anonymous request carries credentials");
        Assert(request.Headers.UserAgent.ToString() == "WinShikiPlayer", "User-Agent");
        Assert(request.RequestUri!.Query.Contains("search=" + Uri.EscapeDataString("Фрирен & dragon")), "Search escaped incorrectly");
        await Task.Yield(); return Ok("[]");
    });
    using var http = new HttpClient(handler);
    var settings = new AppSettings(); var auth = new AuthService(http, settings, new Secrets());
    var client = new ShikimoriClient(http, settings, auth);
    Assert((await client.CatalogAsync(new() { ["search"] = "Фрирен & dragon", ["kind"] = "tv" })).Count == 0, "Empty catalog");
}
static async Task Rates()
{
    var calls = 0;
    var handler = new MockHandler(async request =>
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var payload = json.RootElement.GetProperty("user_rate");
        Assert(payload.GetProperty("target_id").GetString() == "50" && payload.GetProperty("episodes").GetString() == "3", "Payload fields");
        Assert(request.Method == (calls++ == 0 ? HttpMethod.Post : HttpMethod.Patch), "Wrong mutation method");
        return Ok("{\"id\":77,\"score\":8,\"status\":\"watching\",\"episodes\":3}");
    });
    using var http = new HttpClient(handler); var settings = new AppSettings();
    var client = new ShikimoriClient(http, settings, new(http, settings, new Secrets()));
    var anime = new Anime { Id = 50 };
    var rate = await client.SaveRateAsync(1, anime, "watching", 8, 3);
    await client.SaveRateAsync(1, anime with { UserRate = rate }, "watching", 8, 3);
    Assert(calls == 2, "Two mutations expected");
}
static async Task Retry()
{
    var calls = 0;
    using var http = new HttpClient(new MockHandler(_ => Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok("[]"))));
    var settings = new AppSettings();
    await new ShikimoriClient(http, settings, new(http, settings, new Secrets())).CatalogAsync([]);
    Assert(calls == 2, "GET must retry once");
}
static async Task NoMutationRetry()
{
    var calls = 0;
    using var http = new HttpClient(new MockHandler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
    var settings = new AppSettings();
    try { await new ShikimoriClient(http, settings, new(http, settings, new Secrets())).SaveRateAsync(1, new() { Id = 50 }, "watching", 1, 1); throw new Exception("Expected failure"); }
    catch (ApiException) { Assert(calls == 1, "Mutation was retried"); }
}
static async Task OAuthCallback()
{
    var secrets = new Secrets(); secrets.Write("client-secret", "test-secret");
    var settings = new AppSettings { ClientId = "test-id" };
    var handler = new MockHandler(async request =>
    {
        var body = await request.Content!.ReadAsStringAsync();
        Assert(body.Contains("code=test-code") && body.Contains("code_verifier=") && body.Contains("redirect_uri=winshikiplayer%3A%2F%2Foauth"), "Missing OAuth fields");
        return Ok(TokenJson("access"));
    });
    using var http = new HttpClient(handler);
    var auth = new AuthService(http, settings, secrets);
    var attempt = auth.BeginLogin(); var another = auth.BeginLogin();
    Assert(attempt.State != another.State && attempt.Verifier.Length >= 43, "Weak or reused nonce");
    var query = AuthService.ParseCallback(attempt.AuthorizeUri);
    var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    Assert(query["code_challenge"] == challenge && query["code_challenge_method"] == "S256", "PKCE challenge mismatch");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var login = auth.ListenAndLoginAsync(attempt, _ => { }, cts.Token);
    auth.ReceiveProtocolCallback(new("winshikiplayer://evil?state=" + attempt.State + "&code=bad"));
    Assert(!login.IsCompleted, "Wrong callback host accepted");
    auth.ReceiveProtocolCallback(new("winshikiplayer://oauth?state=wrong-state&code=bad"));
    auth.ReceiveProtocolCallback(new("winshikiplayer://oauth?state=" + attempt.State + "&code=test-code"));
    await login;
    Assert(auth.IsSignedIn && await auth.AccessTokenAsync(default) == "access", "Session not created");
}
static async Task Refresh()
{
    var secrets = SessionSecrets(expired: true); var calls = 0;
    using var http = new HttpClient(new MockHandler(async _ => { calls++; await Task.Delay(10); return Ok(TokenJson("new-access")); }));
    var auth = new AuthService(http, new(), secrets);
    var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => auth.AccessTokenAsync(default)));
    Assert(calls == 1 && tokens.All(x => x == "new-access"), "Refresh was not serialized");
}
static async Task RefreshFailure()
{
    var secrets = SessionSecrets(expired: true); var saved = secrets.Read("oauth-session");
    using var http = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
    var auth = new AuthService(http, new(), secrets);
    try { await auth.AccessTokenAsync(default); throw new Exception("Expected refresh failure"); }
    catch (ApiException) { Assert(auth.IsSignedIn && secrets.Read("oauth-session") == saved, "Failed refresh destroyed credentials"); }
    auth.SignOut(); Assert(!auth.IsSignedIn && secrets.Read("oauth-session") is null, "Explicit signout did not clear session");
}
static async Task HostIsolation()
{
    using var http = new HttpClient(new MockHandler(_ => throw new Exception("No network expected")));
    var settings = new AppSettings(); var auth = new AuthService(http, settings, SessionSecrets());
    Assert(auth.IsSignedIn, "Fixture session missing");
    settings.ShikimoriHost = "https://other.example";
    Assert(!auth.IsSignedIn && await auth.AccessTokenAsync(default) is null, "Credentials crossed host boundary");
}
static Task HostValidation()
{
    Assert(AppSettings.ValidateHost(" shikimori.io/ ") == "https://shikimori.io", "Host normalization");
    foreach (var host in new[] { "http://test.example", "https://u:p@test.example", "https://test.example/path", "https://test.example?token=x", "https://test.example/#x" })
    {
        try { AppSettings.ValidateHost(host); throw new Exception("Accepted invalid host: " + host); }
        catch (ArgumentException) { }
    }
    return Task.CompletedTask;
}
static Task Kodik()
{
    using var json = JsonDocument.Parse("""
    {"results":[
     {"translation":{"id":1,"title":"Blocked"},"blocked_seasons":"all","link":"//player.example/video/1/a/720p"},
     {"translation":{"id":2,"title":"Studio"},"blocked_seasons":{"1":["2"],"3":"all"},"seasons":{
       "1":{"episodes":{"1":"//player.example/1","2":{"link":"//player.example/2"}}},
       "2":{"episodes":{"1":{"link":"https://player.example/season2"},"2":"javascript:alert(1)"}},
       "3":{"episodes":{"1":"//player.example/blocked"}}}},
     {"translation":{"id":3,"title":"Movie"},"link":"//player.example/video/3/b/720p"}
    ]}
    """);
    var result = KodikClient.Parse(json.RootElement);
    Assert(result.Count == 2 && result[0].Episodes.Count == 2, "Blocked or invalid links survived");
    Assert(result[0].Episodes[1].Season == 2 && result[0].Episodes[1].Number == 1, "Season numbering collapsed");
    Assert(result[1].Episodes[0].Number == 1, "Movie fallback missing");
    return Task.CompletedTask;
}
static Task SourceDecoding()
{
    const string url = "https://cdn.example/path/master.m3u8";
    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
    var obfuscated = new string(encoded.Select(c => c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 8) % 26) : c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 8) % 26) : c).ToArray());
    foreach (var source in new[] { url, "//cdn.example/path/master.m3u8", encoded, encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_'), obfuscated })
        Assert(KodikResolver.DecodeSource(source)?.AbsoluteUri == url, "Source decode failed");
    Assert(KodikResolver.DecodeSource("javascript:alert(1)") is null, "Unsafe source accepted");
    return Task.CompletedTask;
}
static Task SkipRanges()
{
    var ranges = KodikResolver.ParseSkipRanges("parseSkipButtons(\"00:20-01:50,21:00-22:30\",\"x\")");
    Assert(ranges.Opening == new SkipRange(20, 110) && ranges.Ending == new SkipRange(1260, 1350), "Two skip ranges");
    var ending = KodikResolver.ParseSkipRanges("parseSkipButton(\"21:00-22:30\",\"x\")");
    Assert(ending.Opening is null && ending.Ending is not null, "Late range misclassified as opening");
    Assert(KodikResolver.ParseSkipRanges("parseSkipButtons(\"2:00-1:00\",\"x\")").Opening is null, "Invalid range accepted");
    return Task.CompletedTask;
}
static async Task Persistence()
{
    var path = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString());
        var history = new WatchHistory(new(path));
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => history.SaveAsync(new(new() { Id = i, Name = "Anime" }, 1, 20, 100, DateTimeOffset.UtcNow))));
        await history.SaveAsync(new(new() { Id = 1, Name = "Anime" }, 2, 50, 100, DateTimeOffset.UtcNow, 7, 2));
        var reloaded = await new WatchHistory(new(path)).LoadAsync();
        Assert(reloaded.Count == 20 && reloaded[0].Anime.Id == 1 && reloaded[0].Episode == 2 && reloaded[0].Season == 2, "History lost updates or duplicates");
}
static async Task LiveServices()
{
    var local = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync("appsettings.local.json"))!;
    var secrets = new Secrets(); secrets.Write("kodik-token", local["KODIK_API_TOKEN"]);
    var settings = new AppSettings { ShikimoriHost = local["SHIKIMORI_BASE_URL"] };
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
    var api = new ShikimoriClient(http, settings, new(http, settings, secrets));
    var catalog = await api.CatalogAsync(new() { ["limit"] = "1", ["order"] = "popularity" });
    Assert(catalog.Count == 1, "Live catalog is empty");
    var anime = await api.AnimeAsync(catalog[0].Id);
    Assert(anime.Id == catalog[0].Id, "Live detail mismatch");
    var hydrated = await api.HydrateAsync([anime.Id]);
    Assert(hydrated.Count == 1 && hydrated[0].Id == anime.Id && hydrated[0].Image?.Original?.StartsWith("https://") == true, "Live GraphQL hydration failed");
    Assert((await api.GenresAsync()).Count > 0, "Live genres empty");
    Assert((await api.CalendarAsync()).Count > 0, "Live calendar empty");
    Assert((await api.TopicsAsync(1)).Count > 0, "Live topics empty");
    Assert((await api.ScreenshotsAsync(anime.Id)).Count > 0, "Live screenshots empty");
    Assert((await api.VideosAsync(anime.Id)).Count > 0, "Live videos empty");
    Assert((await api.RelatedAsync(anime.Id)).Any(x => x.Anime is not null), "Live related empty");
    Assert((await api.SimilarAsync(anime.Id)).Count > 0, "Live similar empty");
    var translations = await new KodikClient(http, settings, secrets).SearchAsync(anime.Id);
    Assert(translations.Count > 0, "Live Kodik returned no studios");
    var resolved = await new KodikResolver(http).ResolveAsync(translations[0].Episodes[0].Link, default);
    Assert(resolved.Streams.Count > 0, "No video streams");
    Console.WriteLine($"  Live: catalog and detail OK; {translations.Count} studios; {resolved.Streams.Count} video qualities");
}
static Task Subtitles()
{
    var cues = SubtitleParser.ParseVtt("\uFEFFWEBVTT\r\n\r\ncue-1\r\n00:00:01.500 --> 00:00:03.000 align:center\r\n<b>Привет</b>\r\nВторая строка\r\n\r\n00:05.000 --> 00:04.000\r\nInvalid\r\n");
    Assert(cues.Count == 1 && cues[0].Start == 1.5 && cues[0].End == 3 && cues[0].Text.Contains("Вторая строка"), "VTT parser lost timing or multiline text");
    var ass = SubtitleParser.ToAss(cues, new AppSettings());
    Assert(ass.Contains("Dialogue: 0,0:00:01.50,0:00:03.00") && ass.Contains(@"Привет\NВторая строка"), "ASS conversion lost cue timing or line break");
    Assert(SubtitleParser.Time("invalid") == -1, "Invalid timestamp accepted");
    var assCues = SubtitleParser.ParseAss("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.50,0:00:03.00,Default,,0,0,0,,{\\b1}Hello, world\\NSecond\n");
    Assert(assCues.Count == 1 && assCues[0].Text == "Hello, world\nSecond", "Custom ASS parsing lost commas, lines or timing");
    var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new("ru-RU");
        var styled = SubtitleParser.ToAss(cues, new AppSettings { SubtitleSize = 28.5, SubtitleTextColor = "#123456", SubtitleOutlineWidth = 1.5 });
        Assert(styled.Contains(",57,&H00563412") && styled.Contains(",1.5,"), "ASS output used locale-specific decimal separators or incorrect color order");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    return Task.CompletedTask;
}
static Task NumericScoreNames()
{
    const string payload = "{\"id\":1,\"name\":\"Test\",\"rates_scores_stats\":[{\"name\":10,\"value\":123},{\"name\":\"9\",\"value\":45}]}";
    var anime = JsonSerializer.Deserialize<Anime>(payload, JsonDefaults.Options)!;
    Assert(anime.ScoreStats.Count == 2 && anime.ScoreStats[0].Name == "10" && anime.ScoreStats[1].Name == "9", "Numeric score labels broke detail decoding");
    var stat = JsonSerializer.Deserialize<StatBucket>("{\"name\":8,\"size\":17}", JsonDefaults.Options)!;
    Assert(stat.Name == "8" && stat.Count == 17, "Numeric profile score label failed");
    return Task.CompletedTask;
}
static Task Navigation()
{
    var history = new NavigationHistory(); history.Push(new("home")); history.Push(new("catalog")); history.Push(new("catalog", AnimeId: 1));
    Assert(history.Back()?.Section == "catalog" && history.CanGoForward, "Back did not retain forward route");
    history.Push(new("social", TopicId: 5)); Assert(!history.CanGoForward && history.Current?.TopicId == 5, "New route did not replace forward branch");
    var restored = new NavigationHistory(); restored.Restore(history.Snapshot()); Assert(restored.Current == history.Current && restored.Back()?.Section == "catalog", "Navigation restore lost position");
    return Task.CompletedTask;
}
static async Task CompleteLibrary()
{
    var pages = 0; var graphCalls = 0;
    using var http = new HttpClient(new MockHandler(async request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/v2/user_rates")
        {
            pages++;
            var start = pages == 1 ? 1 : 101; var count = pages == 1 ? 100 : 2;
            return Ok(JsonSerializer.Serialize(Enumerable.Range(start, count).Select(i => new UserRateV2(i, 7, i, "watching", 8, 2, DateTimeOffset.UtcNow)), JsonDefaults.Options));
        }
        Assert(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/graphql", "Unexpected hydration endpoint");
        graphCalls++;
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var ids = json.RootElement.GetProperty("variables").GetProperty("ids").GetString()!.Split(',').Select(int.Parse).ToArray();
        Assert(ids.Length <= 40, "GraphQL chunk too large");
        // A deleted title may be absent from GraphQL; its rate must remain visible.
        return Ok(JsonSerializer.Serialize(new { data = new { animes = ids.Where(id => id != 102).Select(id => new { id = id.ToString(), name = "Title " + id, score = 8.2, episodes = 12, episodesAired = 12, duration = 24,
            airedOn = new { date = "2025-10-01" }, poster = new { originalUrl = "https://example.com/poster.jpg" } }) } }));
    }));
    var settings = new AppSettings();
    var result = await new ShikimoriClient(http, settings, new(http, settings, new Secrets())).CompleteLibraryAsync(7);
    Assert(pages == 2 && graphCalls == 3 && result.Count == 102 && result.Select(x => x.Anime.Id).Distinct().Count() == 102, "Library lost titles or pages");
    Assert(result.Single(x => x.Anime.Id == 1).Anime.AiredOn == "2025-10-01" && result.Single(x => x.Anime.Id == 102).Anime.UserRate?.Score == 8, "Hydrated fields or missing-title rate lost");
}
static Task Filters()
{
    var anime = new Anime { Id = 1, Name = "Test", Kind = "tv", Status = "released", Rating = "pg_13", Episodes = 12, Duration = 24,
        Score = "8.2", AiredOn = "2025-10-01", Genres = [new(7, "Adventure", null)], UserRate = new(1, 9, "watching", 3) };
    var filters = new CatalogFilters { Kinds = ["movie", "tv_13"], Statuses = ["released"], Genres = [7, 9], Ratings = ["pg_13"],
        Durations = ["D"], Season = "2020_2026", MyList = ["watching"], MinimumScore = 8 };
    Assert(filters.Matches(anime) && !(filters with { Season = "spring_2025" }).Matches(anime), "Multi-filter or year-range mismatch");
    Assert((filters with { Season = "fall_2025" }).Matches(anime) && !(filters with { MinimumScore = 9 }).Matches(anime), "Season or score mismatch");
    var query = filters.Query(2);
    Assert(query["genre_v2"] == "7,9" && query["kind"] == "movie,tv_13" && query["censored"] is null, "Catalog filter serialization mismatch");
    Assert((filters with { ExcludeCensored = true }).Query(1)["censored"] == "false", "Censored filter inverted");
    return Task.CompletedTask;
}
static Task MergedHistory()
{
    var now = DateTimeOffset.UtcNow; var anime = new Anime { Id = 1, Name = "Test" };
    var local = new[] { new WatchEntry(anime, 2, 30, 1200, now) };
    var remote = new[] { new HistoryEvent(1, now.AddMinutes(-1), "old", anime), new HistoryEvent(2, now.AddMinutes(1), "new", new Anime { Id = 2, Name = "Other" }) };
    var merged = HistoryMerger.Merge(local, remote, "https://shikimori.io");
    Assert(merged.Count == 2 && merged[0].Anime.Id == 2 && merged[1].Meta.Contains("Серия 2"), "History merge used stale duplicate");
    return Task.CompletedTask;
}
static async Task Cache()
{
    var cache = new RepositoryCache(new LocalStore(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString()))); var calls = 0;
    async Task<List<int>> Load() { Interlocked.Increment(ref calls); await Task.Delay(20); return [1, 2, 3]; }
    var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.GetAsync("test", Load, TimeSpan.FromMinutes(10))));
    Assert(calls == 1 && results.All(x => x.SequenceEqual(new[] { 1, 2, 3 })), "Concurrent loads were duplicated");
    await cache.GetAsync("test", Load, TimeSpan.FromMinutes(10), true); Assert(calls == 2, "Forced refresh did not reload");
}
static async Task Anime365()
{
    var store = new LocalStore(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString()));
    await store.WriteAsync("subtitle-endpoints", new SubtitleEndpoints("https://media.example", "https://api.example"));
    using var http = new HttpClient(new MockHandler(request => Task.FromResult(Ok(request.RequestUri!.AbsolutePath switch
    {
        "/series/" => "{\"data\":[{\"id\":10}]}",
        "/series/10" => "{\"data\":{\"episodes\":[{\"id\":20,\"episodeInt\":\"1\",\"isActive\":1,\"episodeType\":\"tv\"},{\"id\":21,\"episodeInt\":1,\"isActive\":1,\"episodeType\":\"preview\"}]}}",
        "/episodes/20" => "{\"data\":{\"translations\":[{\"id\":30,\"isActive\":1,\"typeKind\":\"sub\",\"type\":\"subru\",\"title\":\"Русские\",\"authorsSummary\":\"Test Studio\"},{\"id\":31,\"isActive\":1,\"typeKind\":\"sub\",\"type\":\"suben\"},{\"id\":32,\"isActive\":0,\"typeKind\":\"sub\",\"type\":\"subru\"}]}}",
        _ => throw new Exception("Unexpected subtitle endpoint")
    }))));
    var tracks = await new Anime365Client(http, store).SearchAsync(1, 1, "subru", default);
    Assert(tracks.Count == 1 && tracks[0].Id == 30 && tracks[0].AssUri.AbsolutePath == "/translations/ass/30", "Wrong episode, language, or inactive subtitle selected");
    Assert(tracks[0].StudioName == "Test Studio" && tracks[0].Title == "Русские" && tracks[0].LanguageCode == "ru", "Studio label lost behind a long episode title");
    Assert(SubtitleLanguages.GroupTitle("suben") == "АНГЛИЙСКИЕ", "API language token leaked into the group header");
}
static string TokenJson(string access) => JsonSerializer.Serialize(new OAuthToken(access, "refresh", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 3600), JsonDefaults.Options);
static Task Trailers()
{
    AnimeVideo Video(int id, string? kind, string? url = "https://www.youtube.com/watch?v=test") => new(id, url, null, "http://i.ytimg.com/vi/test/hqdefault.jpg", "PV", kind);
    var cards = TrailerCatalog.Select([Video(1, "episode_preview"), Video(2, "clip"), Video(3, "character"), Video(4, "PV"), Video(5, "op"), Video(6, "ed"), Video(7, "cm"), Video(8, "trailer"), Video(9, "promo"), Video(10, null), Video(11, "pv", "javascript:alert(1)")]);
    Assert(cards.Count == 7 && cards[0].Video.Id == 4, "Episode previews or unrelated videos leaked into trailers");
    Assert(cards[0].PreviewUri?.Scheme == "https" && cards[0].PreviewUri?.Port == 443, "HTTP preview was not upgraded correctly");
    Assert(cards[4].KindLabel == "TRAILER" && cards[6].KindLabel == "VIDEO", "Trailer kind labels differ from the original");
    Assert(TrailerCatalog.Select(Enumerable.Range(1, 20).Select(i => Video(i, "pv"))).Count == 10, "More than ten trailers are displayed");
    var youtube = TrailerCatalog.Select([new AnimeVideo(12, "https://youtube.com/watch?v=test", null, "http://img.youtube.com/vi/test/hqdefault.jpg", "PV", "pv")])[0];
    Assert(youtube.PreviewUri?.AbsoluteUri == "https://i.ytimg.com/vi/test/hqdefault.jpg", "YouTube thumbnail did not use the HTTPS image CDN");
    return Task.CompletedTask;
}
static Task PlaybackSelection()
{
    var anime = new Anime { Id = 62001, Name = "Test", Status = "released", Episodes = 24, UserRate = new(1, 0, "watching", 15) };
    var stale = new WatchEntry(anime, 1, 28, 1400, DateTimeOffset.UtcNow, 7);
    var next = PlaybackStart.Select(anime, stale);
    Assert(next.Episode == 16 && !next.CanResume && next.PositionSeconds == 0, "Old episode 1 overrode 15 watched episodes");
    var current = stale with { Episode = 16, PositionSeconds = 123 };
    var resumed = PlaybackStart.Select(anime, current);
    Assert(resumed.Episode == 16 && resumed.CanResume && resumed.PositionSeconds == 123, "Position in the next episode was not resumed");
    Assert(PlaybackStart.Select(anime, null).Episode == 16, "Missing local history reset playback to episode 1");
    Assert(!PlaybackStart.Select(anime, current with { PositionSeconds = 1350 }).CanResume, "Completed episode resumed near its end");
    Assert(PlaybackStart.Select(anime with { UserRate = new(1, 0, "completed", 24) }, stale).Episode == 24, "Playback exceeded the last episode");
    Assert(PlaybackStart.Select(anime with { UserRate = null }, stale).CanResume, "Anonymous local resume stopped working");
    Assert(PlaybackStart.Select(anime with { UserRate = null }, stale with { PositionSeconds = 1350 }).Episode == 2, "Anonymous completed episode did not advance");
    Assert(PlaybackStart.Select(anime, current with { Anime = anime with { Id = 2 } }).PositionSeconds == 0, "Resume leaked across titles");
    Assert(PlaybackStart.Select(anime with { UserRate = new(1, 0, "rewatching", 0) }, stale).Episode == 1, "Rewatch counter was ignored");
    var episodes = Enumerable.Range(1, 24).Select(n => new KodikEpisode(1, n, new Uri($"https://example.com/{n}"))).ToList();
    Assert(next.ToWatchEntry(anime, new(7, "Studio", episodes)).Episode == 16, "Displayed episode and actual playback differ");
    Assert(next.FindEpisode(episodes.Take(15)) is null, "Unavailable episode silently fell back to episode 1");
    var explicitReplay = new PlaybackStart(1).ToWatchEntry(anime, new(7, "Studio", episodes));
    Assert(explicitReplay.Episode == 1 && explicitReplay.PositionSeconds == 0, "Explicitly selected replay was prevented");
    return Task.CompletedTask;
}
static Secrets SessionSecrets(bool expired = false)
{
    var secrets = new Secrets();
    secrets.Write("oauth-session", JsonSerializer.Serialize(new { host = "https://shikimori.io", client_id = "", token = new OAuthToken("old-access", "refresh", expired ? 1 : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 3600) }, JsonDefaults.Options));
    return secrets;
}
static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
sealed class Secrets : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    public string? Read(string key) => _values.GetValueOrDefault(key);
    public void Write(string key, string value) => _values[key] = value;
    public void Delete(string key) => _values.Remove(key);
}
