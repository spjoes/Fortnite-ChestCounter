using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>The mock Pokédex0 (tests/mock-pokedex0/server.mjs, Node), started once for the end-to-end tests.</summary>
public sealed class MockPokedex0 : IDisposable
{
    public const string Solo = "0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10";
    public const string Multi = "7d2a9e44-1b3c-4f5d-8e6a-9c0b1d2e3f40";
    public const string Done = "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e50";
    public const string Other = "2e3f4a5b-6c7d-4e8f-9a0b-1c2d3e4f5a60";

    private readonly Process _node;
    private readonly HttpClient _http = new();

    public MockPokedex0()
    {
        L.Set("en");
        var script = Path.Combine(AppContext.BaseDirectory, "mock-pokedex0", "server.mjs");
        _node = Process.Start(new ProcessStartInfo("node", $"\"{script}\" 0")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("node could not be started (Node.js is needed for these tests)");
        var line = _node.StandardOutput.ReadLine() ?? throw new InvalidOperationException("mock server did not start: " + _node.StandardError.ReadToEnd());
        Base = "http://127.0.0.1:" + line.Split(' ')[1];
    }

    public string Base { get; }

    public JsonElement Admin(string name, object? body = null)
    {
        using var content = new StringContent(body == null ? "" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var r = _http.PostAsync($"{Base}/__admin/{name}", content).GetAwaiter().GetResult();
        Assert.True(r.IsSuccessStatusCode, $"admin {name}: {(int)r.StatusCode}");
        return JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
    }

    public JsonElement State() => Admin("state");
    public int Count(string hunt) => State().GetProperty("hunts").GetProperty(hunt).GetProperty("count").GetInt32();
    public int Stat(string name) => State().GetProperty("stats").GetProperty(name).GetInt32();

    public void Dispose()
    {
        try { _node.Kill(entireProcessTree: true); } catch { }
        _node.Dispose();
        _http.Dispose();
    }
}

public sealed class MockServerTests : IClassFixture<MockPokedex0>, IDisposable
{
    private readonly MockPokedex0 _mock;
    private readonly string _dir = Fixtures.TempDir();
    private readonly List<GuiServer> _servers = new();
    private readonly List<Pokedex0Sync> _syncs = new();
    private readonly ConcurrentQueue<string> _log = new();

    public MockServerTests(MockPokedex0 mock)
    {
        _mock = mock;
        _mock.Admin("reset");
    }

    public void Dispose()
    {
        foreach (var s in _syncs) s.Stop();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Pokedex0Sync NewSync()
    {
        var sync = new Pokedex0Sync(_dir, _mock.Base) { Log = (t, _) => _log.Enqueue(t) };
        _syncs.Add(sync);
        return sync;
    }

    /// <summary>The counter's real local server, with only the OAuth callback wired (as Program does).</summary>
    private GuiServer Gui(Pokedex0Sync sync)
    {
        var gui = new GuiServer(() => new { }, (_, _) => null, _dir, Path.Combine(_dir, "report.html"), Path.Combine(_dir, "overlay.html"))
        {
            OAuthCallback = q => Pokedex0Sync.CallbackPage(sync.HandleCallback(q)),
        };
        gui.Start();
        _servers.Add(gui);
        return gui;
    }

    private static HttpClient Browser() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    /// <summary>The player's browser: the authorize page of Pokédex0 redirects to the counter's /oauth/callback.</summary>
    private (string Page, Uri Callback) BrowserRoundTrip(string authorizeUrl)
    {
        using var browser = Browser();
        var authorize = browser.GetAsync(authorizeUrl).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
        var callback = authorize.Headers.Location!;
        var page = browser.GetAsync(callback).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        return (WebUtility.HtmlDecode(page.Content.ReadAsStringAsync().GetAwaiter().GetResult()), callback); // text as shown
    }

    private Pokedex0Sync Connected()
    {
        var sync = NewSync();
        var gui = Gui(sync);
        var (page, _) = BrowserRoundTrip(sync.BeginConnect(gui.Port));
        Assert.True(page.Contains("Connected to Pokédex0"), $"port {gui.Port}: {page} | {string.Join(" | ", _log)}");
        Assert.True(sync.Connected);
        return sync;
    }

    private Pokedex0Sync Linked(string hunt, TrackerData data)
    {
        var sync = Connected();
        Assert.True(sync.LoadHuntsNow());
        Assert.Null(sync.SelectHunt(hunt, data, Fixtures.Config(), out _));
        return sync;
    }

    private Pokedex0Tokens StoredTokens() => new Pokedex0TokenStore(_dir).Load()!;

    // ---- Connecting ----------------------------------------------------------------------------------------------

    [Fact]
    public void ConnectThroughTheBrowserRedirect()
    {
        var sync = NewSync();
        var gui = Gui(sync);
        var authorizeUrl = sync.BeginConnect(gui.Port);
        Assert.True(sync.WaitingForBrowser);
        var (page, callback) = BrowserRoundTrip(authorizeUrl);

        Assert.Contains("Connected to Pokédex0", page);
        var q = GuiServer.ParseQuery(callback.Query.TrimStart('?'));
        Assert.DoesNotContain(q["code"], page); // nothing of the request is echoed
        Assert.DoesNotContain(q["state"], page);
        Assert.True(sync.Connected);
        Assert.Equal("ash", sync.Read(l => l.Username));
        var authorize = _mock.State().GetProperty("lastAuthorize");
        Assert.Equal($"http://127.0.0.1:{gui.Port}/oauth/callback", authorize.GetProperty("redirect_uri").GetString());
        Assert.Equal(_mock.Base + "/api", authorize.GetProperty("resource").GetString());

        // On disk: the tokens encrypted, nothing secret in pokedex0.json.
        var tokens = StoredTokens();
        Assert.StartsWith("rt_", tokens.RefreshToken);
        var raw = File.ReadAllText(Path.Combine(_dir, "pokedex0-auth.bin"), Encoding.Latin1);
        Assert.DoesNotContain(tokens.RefreshToken, raw);
        Assert.DoesNotContain(tokens.AccessToken, raw);
        var ledger = File.ReadAllText(Path.Combine(_dir, "pokedex0.json"));
        Assert.DoesNotContain(tokens.RefreshToken, ledger);
        Assert.DoesNotContain(q["code"], ledger);
        // The window's state carries no token, code, state or verifier either.
        var gs = JsonSerializer.Serialize(sync.GuiState(Fixtures.Data()));
        Assert.DoesNotContain(tokens.AccessToken, gs);
        Assert.DoesNotContain(tokens.RefreshToken, gs);
        Assert.DoesNotContain(q["state"], gs);
        Assert.DoesNotContain(string.Join("", _log), tokens.RefreshToken);

        // The same callback again (replayed): the state was single use.
        using var browser = Browser();
        var again = WebUtility.HtmlDecode(browser.GetAsync(callback).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Assert.Contains("Link expired", again);
        Assert.Equal(1, _mock.Stat("codeExchanges"));
    }

    [Fact]
    public void DeniedInTheBrowser()
    {
        var sync = NewSync();
        var gui = Gui(sync);
        _mock.Admin("denyNext");
        var (page, _) = BrowserRoundTrip(sync.BeginConnect(gui.Port));
        Assert.Contains("Connection cancelled", page);
        Assert.DoesNotContain("<b>denied", page); // error_description is never shown
        Assert.False(sync.Connected);
        Assert.Equal(0, _mock.Stat("codeExchanges"));
    }

    [Fact]
    public void CallbackFromAnotherIssuerOrWithoutStateIsRefused()
    {
        var sync = NewSync();
        var gui = Gui(sync);
        var state = GuiServer.ParseQuery(new Uri(sync.BeginConnect(gui.Port)).Query.TrimStart('?'))["state"];
        using var browser = Browser();
        string Get(string query) => WebUtility.HtmlDecode(browser.GetAsync($"http://127.0.0.1:{gui.Port}/oauth/callback?{query}").GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult());

        Assert.Contains("Link expired", Get("code=abc"));
        Assert.Contains("Link expired", Get("code=abc&state=forged"));
        Assert.Contains("The connection failed", Get($"code=abc&state={Uri.EscapeDataString(state)}&iss=https%3A%2F%2Fevil.example"));
        Assert.Contains("Link expired", Get($"code=abc&state={Uri.EscapeDataString(state)}")); // used up by the try above
        Assert.False(sync.Connected);
        Assert.Equal(0, _mock.Stat("codeExchanges"));

        // Another Host (DNS rebinding): refused before anything else.
        using var raw = new System.Net.Sockets.TcpClient("127.0.0.1", gui.Port);
        var stream = raw.GetStream();
        stream.Write(Encoding.ASCII.GetBytes($"GET /oauth/callback?state=x HTTP/1.1\r\nHost: evil.example:{gui.Port}\r\n\r\n"));
        var answer = new StreamReader(stream).ReadToEnd();
        Assert.StartsWith("HTTP/1.1 403", answer);
    }

    [Fact]
    public void HuntsListActiveOnly()
    {
        var sync = Connected();
        Assert.True(sync.LoadHuntsNow());
        var hunts = sync.Hunts;
        Assert.Equal(new[] { MockPokedex0.Solo, MockPokedex0.Multi, MockPokedex0.Other }.OrderBy(x => x), hunts.Select(h => h.Id).OrderBy(x => x));
        var solo = hunts.Single(h => h.Id == MockPokedex0.Solo);
        Assert.Equal(4096, solo.OddsDenominator);
        Assert.Equal(1287, solo.DisplayCount);
        Assert.Equal("Fortnite chests", solo.Method);
        Assert.True(hunts.Single(h => h.Id == MockPokedex0.Multi).Multiplayer);
        Assert.Contains("onerror", hunts.Single(h => h.Id == MockPokedex0.Other).Name); // kept as text, escaped by the page
        Assert.NotNull(sync.SelectHunt(MockPokedex0.Done, Fixtures.Data(), Fixtures.Config(), out _)); // completed: not offered
        Assert.NotNull(sync.SelectHunt("'); alert(1); ('", Fixtures.Data(), Fixtures.Config(), out _));
    }

    // ---- Sending -------------------------------------------------------------------------------------------------

    [Fact]
    public void NewMatchesAreAddedOnlyOnce()
    {
        var data = Fixtures.Data(Fixtures.Match("old1", 10), Fixtures.Match("old2", 20));
        var sync = Linked(MockPokedex0.Solo, data);
        Assert.Equal(Pokedex0FlushResult.Idle, sync.FlushNow()); // the baseline is not sent
        Assert.Equal(1287, _mock.Count(MockPokedex0.Solo));

        sync.OnMatch(Fixtures.Match("new", 7));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(1294, _mock.Count(MockPokedex0.Solo));
        Assert.Contains(_log, l => l.Contains("+7 chests"));

        sync.OnMatch(Fixtures.Match("new", 9)); // re-analysis
        sync.FlushNow();
        Assert.Equal(1296, _mock.Count(MockPokedex0.Solo));
        sync.OnMatch(Fixtures.Match("new", 9)); // unchanged
        Assert.Equal(Pokedex0FlushResult.Idle, sync.FlushNow());

        // A retry of the same operation (lost answer): acknowledged, not counted again.
        var client = new Pokedex0Client(_mock.Base);
        var body = new { operations = new[] { new { operationId = UuidV5.MatchOperation(MockPokedex0.Solo, "new", 0), increment = 7 } } };
        var retry = client.PostJsonAsync($"/api/shiny-hunts/{MockPokedex0.Solo}/increment", StoredTokens().AccessToken, body).GetAwaiter().GetResult();
        Assert.True(retry.Ok);
        Assert.Equal(0, retry.Body!.Value.GetProperty("appliedOperationIds").GetArrayLength());
        Assert.Equal(1296, _mock.Count(MockPokedex0.Solo));
        Assert.Contains("total 1,296", JsonSerializer.Serialize(sync.GuiState(data))); // from the acknowledgement
    }

    [Fact]
    public void CrashBeforeTheAnswerWasSavedDoesNotCountTwice()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        sync.OnMatch(Fixtures.Match("m", 5));
        var ledgerPath = Path.Combine(_dir, "pokedex0.json");
        var beforeSend = File.ReadAllText(ledgerPath);
        sync.FlushNow();
        sync.Stop();
        File.WriteAllText(ledgerPath, beforeSend); // as if the counter died before saving the acknowledgement

        var restarted = NewSync();
        Assert.True(restarted.Connected); // tokens kept (DPAPI file)
        Assert.Equal(5, restarted.Read(l => Pokedex0Book.PendingChests(l)));
        Assert.Equal(Pokedex0FlushResult.Sent, restarted.FlushNow());
        Assert.Equal(1292, _mock.Count(MockPokedex0.Solo)); // +5 once
        Assert.Equal(0, restarted.Read(l => l.Outbox.Count));
    }

    [Fact]
    public void BatchesOfOneHundred()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        var before = _mock.Stat("incrementRequests");
        for (var i = 0; i < 150; i++)
        {
            sync.OnMatch(Fixtures.Match("b" + i, 2));
        }
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(2, _mock.Stat("incrementRequests") - before);
        Assert.Equal(1287 + 300, _mock.Count(MockPokedex0.Solo));
    }

    [Fact]
    public void MultiplayerUsesTheParticipantCounter()
    {
        var sync = Linked(MockPokedex0.Multi, Fixtures.Data());
        sync.OnMatch(Fixtures.Match("m", 6));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(46, _mock.Count(MockPokedex0.Multi));
        var gs = JsonSerializer.Serialize(sync.GuiState(Fixtures.Data()));
        Assert.Contains("you 46", gs);
        Assert.Contains("total 546", gs);
    }

    [Fact]
    public void WrongEndpointIsCorrectedOnce()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        sync.Read(l => l.Hunt!.Multiplayer = true); // e.g. changed on the website since
        sync.OnMatch(Fixtures.Match("m", 3));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(1290, _mock.Count(MockPokedex0.Solo));
        Assert.False(sync.Read(l => l.Hunt!.Multiplayer));
    }

    [Fact]
    public void OneTimeSync()
    {
        var data = Fixtures.Data(Fixtures.Match("a", 10), Fixtures.Match("b", 20));
        var sync = Linked(MockPokedex0.Solo, data);
        Assert.Equal((30, 2), sync.InitialSyncPreview());
        Assert.NotNull(sync.StartInitialSync(29)); // not the figure the player saw
        Assert.Null(sync.StartInitialSync(30));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(1317, _mock.Count(MockPokedex0.Solo));
        Assert.True(sync.Read(l => l.Hunt!.InitialSyncDone));
        Assert.Equal((0, 0), sync.InitialSyncPreview());
        Assert.NotNull(sync.StartInitialSync(30));
        Assert.True(_mock.State().GetProperty("ops").TryGetProperty(UuidV5.InitialSyncOperation(MockPokedex0.Solo), out _));
    }

    // ---- Tokens --------------------------------------------------------------------------------------------------

    [Fact]
    public void ExpiredAccessTokenIsRefreshedAndTheRotatedTokenSaved()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        var first = StoredTokens().RefreshToken;
        _mock.Admin("expireAccessTokens");
        sync.OnMatch(Fixtures.Match("m", 4));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow()); // 401 -> refresh -> same body again
        Assert.Equal(1291, _mock.Count(MockPokedex0.Solo));
        Assert.Equal(1, sync.RefreshCount);
        Assert.Equal(1, _mock.Stat("refreshes"));
        var second = StoredTokens().RefreshToken;
        Assert.NotEqual(first, second); // the rotated token is on disk before it is used

        // A restart uses the saved (new) refresh token: no reuse of the rotated one.
        sync.Stop();
        _mock.Admin("expireAccessTokens");
        var restarted = NewSync();
        Assert.True(restarted.LoadHuntsNow());
        Assert.Equal(0, _mock.Stat("refreshReuse"));
        Assert.True(restarted.Connected);
    }

    [Fact]
    public void ConcurrentUnauthorizedCallsRefreshOnce()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        _mock.Admin("expireAccessTokens");
        _mock.Admin("refreshDelay", new { ms = 600 }); // both callers get their 401 while the refresh is in flight
        var results = new bool[2];
        var threads = Enumerable.Range(0, 2).Select(i => new Thread(() => results[i] = sync.LoadHuntsNow())).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.All(results, Assert.True);
        Assert.Equal(1, sync.RefreshCount);
        Assert.Equal(1, _mock.Stat("refreshes"));
        Assert.Equal(0, _mock.Stat("refreshReuse"));
        Assert.True(sync.Connected);
    }

    [Fact]
    public void ReusingARotatedRefreshTokenRevokesTheGrant()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        var old = StoredTokens().RefreshToken;
        _mock.Admin("expireAccessTokens");
        Assert.True(sync.LoadHuntsNow()); // rotation: old -> new
        var client = new Pokedex0Client(_mock.Base);
        var reuse = client.RefreshAsync(old).GetAwaiter().GetResult(); // what the counter must never do
        Assert.True(reuse.Rejected);
        Assert.Equal("invalid_grant", reuse.Error);
        Assert.Equal(1, _mock.Stat("refreshReuse"));

        // The whole grant is gone: the counter's own (new) token fails too, and it shows "disconnected",
        // keeping what is queued for after the player signs in again.
        sync.OnMatch(Fixtures.Match("m", 4));
        _mock.Admin("expireAccessTokens");
        Assert.Equal(Pokedex0FlushResult.Paused, sync.FlushNow());
        Assert.False(sync.Connected);
        Assert.Equal("auth", sync.Read(l => l.Paused));
        Assert.Equal(4, sync.Read(l => Pokedex0Book.PendingChests(l)));
        Assert.False(File.Exists(Path.Combine(_dir, "pokedex0-auth.bin")));
        Assert.Equal(1, _mock.Stat("refreshReuse")); // only the test's reuse: the counter never presented a rotated token
        // Refreshes: the counter's rotation, the test's reuse, the counter's one try with its (revoked) token. Never more.
        Assert.Equal(3, _mock.Stat("refreshes"));
        Assert.Equal(2, sync.RefreshCount);
        Assert.Equal(Pokedex0FlushResult.Idle, sync.FlushNow()); // no network while signed out
        Assert.Equal(3, _mock.Stat("refreshes"));

        // Signing in again sends what waited.
        var gui = Gui(sync);
        BrowserRoundTrip(sync.BeginConnect(gui.Port));
        Assert.Null(sync.Read(l => l.Paused));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(1291, _mock.Count(MockPokedex0.Solo));
    }

    [Fact]
    public void RevokedInConnectedAppsKeepsTheOutbox()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        _mock.Admin("revokeGrants");
        sync.OnMatch(Fixtures.Match("m", 2));
        Assert.Equal(Pokedex0FlushResult.Paused, sync.FlushNow());
        Assert.False(sync.Connected);
        Assert.Equal(2, sync.Read(l => Pokedex0Book.PendingChests(l)));
        Assert.Contains(_log, l => l.Contains("disconnected"));
    }

    // ---- Errors --------------------------------------------------------------------------------------------------

    [Fact]
    public void InactiveHuntPausesAndKeepsTheQueue()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        _mock.Admin("setHuntStatus", new { id = MockPokedex0.Solo, status = "completed" });
        sync.OnMatch(Fixtures.Match("m", 5));
        Assert.Equal(Pokedex0FlushResult.Paused, sync.FlushNow());
        Assert.Equal("inactive", sync.Read(l => l.Paused));
        Assert.Equal(5, sync.Read(l => Pokedex0Book.PendingChests(l)));
        Assert.Equal(Pokedex0FlushResult.Idle, sync.FlushNow()); // paused: nothing sent until the player acts
        Assert.Contains("no longer active", JsonSerializer.Serialize(sync.GuiState(Fixtures.Data())));

        _mock.Admin("setHuntStatus", new { id = MockPokedex0.Solo, status = "active" }); // reactivated on the website
        sync.RetryNow();
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(1292, _mock.Count(MockPokedex0.Solo));
    }

    [Fact]
    public void SwitchingHuntsAfterAPauseDropsTheQueue()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        _mock.Admin("setHuntStatus", new { id = MockPokedex0.Solo, status = "abandoned" });
        sync.OnMatch(Fixtures.Match("m", 5));
        sync.FlushNow();
        sync.LoadHuntsNow();
        Assert.Null(sync.SelectHunt(MockPokedex0.Other, Fixtures.Data(Fixtures.Match("m", 5)), Fixtures.Config(), out var dropped));
        Assert.Equal(5, dropped);
        Assert.Null(sync.Read(l => l.Paused));
        Assert.Equal(Pokedex0FlushResult.Idle, sync.FlushNow());
        Assert.Equal(5, _mock.Count(MockPokedex0.Other)); // "m" is the new hunt's baseline: not sent
    }

    [Fact]
    public void DeletedHuntPauses()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        _mock.Admin("deleteHunt", new { id = MockPokedex0.Solo });
        sync.OnMatch(Fixtures.Match("m", 5));
        Assert.Equal(Pokedex0FlushResult.Paused, sync.FlushNow());
        Assert.Equal("gone", sync.Read(l => l.Paused));
    }

    [Fact]
    public void ServiceUnavailableIsRetriedWithTheSameBody()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        _mock.Admin("failNext", new { count = 1, status = 503, retryAfter = 1 });
        sync.OnMatch(Fixtures.Match("m", 5));
        var before = DateTime.UtcNow;
        Assert.Equal(Pokedex0FlushResult.RetryLater, sync.FlushNow());
        var wait = sync.NextAttemptUtc - before;
        Assert.InRange(wait.TotalSeconds, 0.5, 3); // Retry-After: 1
        Assert.Contains("cannot be reached", JsonSerializer.Serialize(sync.GuiState(Fixtures.Data())));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(1292, _mock.Count(MockPokedex0.Solo));

        _mock.Admin("failNext", new { count = 2, status = 500 });
        sync.OnMatch(Fixtures.Match("m2", 1));
        sync.FlushNow();
        sync.FlushNow();
        var backoff = sync.NextAttemptUtc - DateTime.UtcNow;
        Assert.InRange(backoff.TotalSeconds, 20, 31); // 15 s, then 30 s
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(1293, _mock.Count(MockPokedex0.Solo));
    }

    [Fact]
    public void RefusedCorrectionIsDroppedAndLogged()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        sync.OnMatch(Fixtures.Match("m", 5));
        sync.FlushNow();
        _mock.Admin("setCount", new { id = MockPokedex0.Solo, count = 2 }); // lowered on the website
        sync.OnMatch(Fixtures.Match("m", 0)); // -5 would take 2 below zero: 400, permanent
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(2, _mock.Count(MockPokedex0.Solo));
        Assert.Equal(0, sync.Read(l => l.Outbox.Count)); // dropped, not retried
        Assert.Contains(_log, l => l.Contains("refused") && l.Contains("Count must be between"));
        sync.OnMatch(Fixtures.Match("ok", 3));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(5, _mock.Count(MockPokedex0.Solo));

        // In a batch (all-or-nothing on the server): split, so that only the refused operation is dropped.
        _mock.Admin("setCount", new { id = MockPokedex0.Solo, count = 1 });
        sync.OnMatch(Fixtures.Match("ok", 0));   // -3: below zero
        sync.OnMatch(Fixtures.Match("ok2", 1));  // +1: fine alone, but the batch's total (1 - 3 + 1) is below zero
        Assert.Equal(2, sync.Read(l => l.Outbox.Count));
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(2, _mock.Count(MockPokedex0.Solo));
        Assert.Equal(0, sync.Read(l => l.Outbox.Count));
        sync.Reconcile(Fixtures.Data(Fixtures.Match("m", 0), Fixtures.Match("ok", 0), Fixtures.Match("ok2", 1))); // next start
        Assert.Equal(0, sync.Read(l => l.Outbox.Count)); // not made again at the next start
    }

    [Fact]
    public void IdUsedWithOtherDataIsNotRetried()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        sync.OnMatch(Fixtures.Match("m", 5));
        sync.FlushNow();
        var id = UuidV5.MatchOperation(MockPokedex0.Solo, "m", 0);
        sync.Read(l => { l.Outbox.Add(new PendingOperation { OperationId = id, Increment = 6, HuntId = MockPokedex0.Solo, SessionId = "m" }); return 0; });
        Assert.Equal(Pokedex0FlushResult.Sent, sync.FlushNow());
        Assert.Equal(0, sync.Read(l => l.Outbox.Count));
        Assert.Equal(1292, _mock.Count(MockPokedex0.Solo));
        Assert.Contains(_log, l => l.Contains("already used with different data"));
    }

    [Fact]
    public void DisconnectForgetsEverythingAndRevokes()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        _mock.Admin("setHuntStatus", new { id = MockPokedex0.Solo, status = "completed" });
        sync.OnMatch(Fixtures.Match("m", 5));
        sync.FlushNow();
        Assert.Equal(5, sync.Disconnect());
        Assert.False(sync.Connected);
        Assert.False(File.Exists(Path.Combine(_dir, "pokedex0-auth.bin")));
        Assert.False(File.Exists(Path.Combine(_dir, "pokedex0.json")));
        Assert.False(sync.Read(l => l.Linked));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_mock.Stat("revocations") == 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Equal(1, _mock.Stat("revocations"));
        var requests = _mock.Stat("incrementRequests");
        sync.OnMatch(Fixtures.Match("n", 5));
        Assert.Equal(Pokedex0FlushResult.Idle, sync.FlushNow());
        Assert.Equal(requests, _mock.Stat("incrementRequests"));
    }

    [Fact]
    public void WorkerSendsInTheBackground()
    {
        var sync = Linked(MockPokedex0.Solo, Fixtures.Data());
        sync.Start();
        sync.OnMatch(Fixtures.Match("bg", 8));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_mock.Count(MockPokedex0.Solo) != 1295 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Equal(1295, _mock.Count(MockPokedex0.Solo));
    }
}
