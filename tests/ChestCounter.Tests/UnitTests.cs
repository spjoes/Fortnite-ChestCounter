using System.Text.Json;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ChestCounter.Tests;

internal static class Fixtures
{
    public const string Hunt = "0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10";
    public const string OtherHunt = "2e3f4a5b-6c7d-4e8f-9a0b-1c2d3e4f5a60";
    public static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    public static MatchRecord Match(string session, int chests, string account = "acct-1", string playlist = "Playlist_DefaultSolo") => new()
    {
        SessionId = session,
        EpicId = account,
        PlayerName = account,
        Playlist = playlist,
        ChestsOpened = chests,
        MatchStartUtc = Now,
        AnalyzedAtUtc = Now,
    };

    public static TrackerData Data(params MatchRecord[] matches) => new() { Matches = matches.ToList() };

    public static Config Config(string profile = "acct-1") => new() { Profile = profile };

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public class UuidV5Tests
{
    [Theory]
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "python.org", "886313e1-3b8a-5372-9b90-0c9aee199e5d")]      // Python docs (NAMESPACE_DNS)
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "www.example.com", "2ed6657d-e927-568b-95e1-2665a8aea6a2")] // RFC 9562 A.4
    public void KnownVectors(string ns, string name, string expected) =>
        Assert.Equal(expected, UuidV5.Create(Guid.Parse(ns), name));

    [Fact]
    public void OperationIdsAreDeterministicAndDistinct()
    {
        // Same values as Python: uuid5(UUID("8f0c2b5e-..."), "<hunt>|abc|0") — the ids never change between versions.
        Assert.Equal("25d1eeca-dd37-505e-83ad-c8fd41a758a0", UuidV5.MatchOperation(Fixtures.Hunt, "abc", 0));
        Assert.Equal("25d1eeca-dd37-505e-83ad-c8fd41a758a0", UuidV5.MatchOperation(Fixtures.Hunt.ToUpperInvariant(), "abc", 0));
        Assert.Equal("fcbf2667-1af0-5528-aa73-a71a347e1511", UuidV5.InitialSyncOperation(Fixtures.Hunt));
        Assert.Equal("90ea64fd-655d-59a4-95cb-010b4efbdffa", UuidV5.Create(UuidV5.Pokedex0Namespace, "é|ü")); // UTF-8 names
        Assert.NotEqual(UuidV5.MatchOperation(Fixtures.Hunt, "abc", 0), UuidV5.MatchOperation(Fixtures.Hunt, "abc", 1));
        Assert.NotEqual(UuidV5.MatchOperation(Fixtures.Hunt, "abc", 0), UuidV5.MatchOperation(Fixtures.OtherHunt, "abc", 0));
    }

    [Fact]
    public void AcceptedByPokedex0Pattern()
    {
        var pattern = new System.Text.RegularExpressions.Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
        for (var i = 0; i < 200; i++)
        {
            var id = UuidV5.MatchOperation(Fixtures.Hunt, "session-" + i, i % 7);
            Assert.Matches(pattern, id);
            Assert.Equal('5', id[14]);
        }
    }
}

public class PkceAndStateTests
{
    [Fact]
    public void Rfc7636AppendixB() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void VerifierShape()
    {
        var v = Pkce.NewVerifier();
        Assert.Equal(43, v.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", v);
        Assert.NotEqual(v, Pkce.NewVerifier());
    }

    [Fact]
    public void StateIsSingleUse()
    {
        var store = new OAuthStateStore();
        var p = store.Create("http://127.0.0.1:1/oauth/callback", Fixtures.Now);
        Assert.True(p.State.Length >= 22);
        Assert.Same(p, store.TryConsume(p.State, Fixtures.Now.AddSeconds(5)));
        Assert.Null(store.TryConsume(p.State, Fixtures.Now.AddSeconds(6))); // used
    }

    [Fact]
    public void StateExpiresAfterTenMinutes()
    {
        var store = new OAuthStateStore();
        var fresh = store.Create("r", Fixtures.Now);
        var old = store.Create("r", Fixtures.Now);
        Assert.NotNull(store.TryConsume(fresh.State, Fixtures.Now.AddMinutes(10)));
        Assert.Null(store.TryConsume(old.State, Fixtures.Now.AddMinutes(10).AddSeconds(1)));
        Assert.False(store.HasPending(Fixtures.Now.AddMinutes(11)));
    }

    [Fact]
    public void UnknownOrMissingStateRefused()
    {
        var store = new OAuthStateStore();
        var p = store.Create("r", Fixtures.Now);
        Assert.Null(store.TryConsume(null, Fixtures.Now));
        Assert.Null(store.TryConsume("", Fixtures.Now));
        Assert.Null(store.TryConsume(p.State + "x", Fixtures.Now));
        Assert.Null(store.TryConsume(p.State[..^1], Fixtures.Now));
        Assert.NotNull(store.TryConsume(p.State, Fixtures.Now)); // the wrong tries did not use it up
    }

    [Fact]
    public void AuthorizeUrlHasTheContractParameters()
    {
        var client = new Pokedex0Client(Pokedex0Endpoint.DefaultBase);
        var p = new OAuthStateStore().Create("http://127.0.0.1:47613/oauth/callback", Fixtures.Now);
        var url = new Uri(client.AuthorizeUrl(p));
        Assert.Equal("https://www.pokedex0.com/api/auth/oauth2/authorize", url.GetLeftPart(UriPartial.Path));
        var q = GuiServer.ParseQuery(url.Query.TrimStart('?'));
        Assert.Equal("fortnite-chestcounter", q["client_id"]);
        Assert.Equal("http://127.0.0.1:47613/oauth/callback", q["redirect_uri"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("openid profile offline_access hunts:read hunts:increment", q["scope"]);
        Assert.Equal(p.State, q["state"]);
        Assert.Equal(Pkce.Challenge(p.Verifier), q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("https://www.pokedex0.com/api", q["resource"]);
        Assert.DoesNotContain(p.Verifier, url.ToString());
    }
}

public class EndpointTests
{
    [Theory]
    [InlineData(null, "https://www.pokedex0.com")]
    [InlineData("", "https://www.pokedex0.com")]
    [InlineData("http://example.com", "https://www.pokedex0.com")]  // http only on this PC
    [InlineData("ftp://127.0.0.1", "https://www.pokedex0.com")]
    [InlineData("https://user:pw@example.com", "https://www.pokedex0.com")]
    [InlineData("http://127.0.0.1:5000/", "http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("https://staging.example.com/", "https://staging.example.com")]
    public void BaseUrl(string? given, string expected) => Assert.Equal(expected, Pokedex0Endpoint.Resolve(given));

    [Fact]
    public void ServerUrlsOnlyOnTheSameOrigin()
    {
        Assert.True(Pokedex0Endpoint.SameOrigin("https://www.pokedex0.com", "https://www.pokedex0.com/api/auth/oauth2/token"));
        Assert.False(Pokedex0Endpoint.SameOrigin("https://www.pokedex0.com", "https://evil.example/token"));
        Assert.False(Pokedex0Endpoint.SameOrigin("https://www.pokedex0.com", "http://www.pokedex0.com/token"));
    }

    [Fact]
    public void CallbackQueryParsing()
    {
        var q = GuiServer.ParseQuery("code=a%2Bb&state=x+y&iss=https%3A%2F%2Fwww.pokedex0.com");
        Assert.Equal("a+b", q["code"]);
        Assert.Equal("x y", q["state"]);
        Assert.Equal("https://www.pokedex0.com", q["iss"]);
        Assert.Empty(GuiServer.ParseQuery("state=a&state=b")); // a repeated parameter: refused as a whole
    }
}

public class LedgerTests
{
    private static Pokedex0Ledger Linked(TrackerData data, string hunt = Fixtures.Hunt)
    {
        var s = new Pokedex0Ledger();
        Pokedex0Book.Link(s, hunt, "Hunt", false, data, "acct-1", Fixtures.Now);
        return s;
    }

    [Fact]
    public void LinkingTakesABaselineAndSendsNothing()
    {
        var data = Fixtures.Data(Fixtures.Match("a", 5), Fixtures.Match("b", 7), Fixtures.Match("x", 9, account: "acct-2"));
        var s = Linked(data);
        Assert.Empty(s.Outbox);
        Assert.Equal(2, s.Hunt!.Matches.Count);
        Assert.All(s.Hunt.Matches.Values, e => Assert.True(e.Baseline));
        Assert.Empty(Pokedex0Book.Reconcile(s, data, Fixtures.Now)); // nothing new
    }

    [Fact]
    public void NewMatchQueuesItsChests()
    {
        var data = Fixtures.Data(Fixtures.Match("a", 5));
        var s = Linked(data);
        var op = Pokedex0Book.Record(s, Fixtures.Match("new", 6), Fixtures.Now);
        Assert.NotNull(op);
        Assert.Equal(6, op!.Increment);
        Assert.Equal(UuidV5.MatchOperation(Fixtures.Hunt, "new", 0), op.OperationId);
        Assert.Equal(Fixtures.Hunt, op.HuntId);
        Assert.Single(s.Outbox);
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("new", 6), Fixtures.Now)); // unchanged: nothing
    }

    [Fact]
    public void ReanalysisSendsTheDifferenceUpAndDown()
    {
        var s = Linked(Fixtures.Data());
        Pokedex0Book.Record(s, Fixtures.Match("m", 6), Fixtures.Now);
        Pokedex0Book.Acknowledge(s, s.Outbox.ToList(), Fixtures.Now);
        var up = Pokedex0Book.Record(s, Fixtures.Match("m", 8), Fixtures.Now)!;
        Assert.Equal(2, up.Increment);
        Assert.Equal(UuidV5.MatchOperation(Fixtures.Hunt, "m", 1), up.OperationId);
        var down = Pokedex0Book.Record(s, Fixtures.Match("m", 3), Fixtures.Now)!;
        Assert.Equal(-5, down.Increment);
        Assert.Equal(UuidV5.MatchOperation(Fixtures.Hunt, "m", 2), down.OperationId);
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("m", 3), Fixtures.Now));
        Assert.Equal(3, s.Hunt!.Matches["m"].Chests);
    }

    [Fact]
    public void OtherProfilesModesAndZeroChestsAreIgnored()
    {
        var s = Linked(Fixtures.Data(Fixtures.Match("a", 1)));
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("p", 4, account: "acct-2"), Fixtures.Now));
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("q", 4, playlist: "Playlist_Figment_Solo"), Fixtures.Now)); // OG: not counted
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("z", 0), Fixtures.Now)); // 0 is refused by Pokédex0
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("", 4), Fixtures.Now));
        Assert.Empty(s.Outbox);
    }

    [Fact]
    public void ManualChestsAreNeverSent()
    {
        var data = Fixtures.Data(Fixtures.Match("a", 5));
        var config = Fixtures.Config();
        var s = Linked(data);
        Profiles.AddManual(data, config, 250);
        Profiles.SetManual(data, config, 400);
        Assert.Empty(Pokedex0Book.Reconcile(s, data, Fixtures.Now));
        Assert.Equal((5, 1), Pokedex0Book.InitialSyncPreview(s)); // the one-time sync leaves them out too
    }

    [Fact]
    public void BaselineReanalysisIsSilentButCountsInTheOneTimeSync()
    {
        var s = Linked(Fixtures.Data(Fixtures.Match("a", 5), Fixtures.Match("b", 0)));
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("a", 8), Fixtures.Now));
        Assert.Equal((8, 1), Pokedex0Book.InitialSyncPreview(s));
    }

    [Fact]
    public void OneTimeSyncIsOneOperationOnce()
    {
        var s = Linked(Fixtures.Data(Fixtures.Match("a", 5), Fixtures.Match("b", 7)));
        Pokedex0Book.Record(s, Fixtures.Match("new", 3), Fixtures.Now); // not in the sync: sent on its own
        Assert.Equal((12, 2), Pokedex0Book.InitialSyncPreview(s));
        var op = Pokedex0Book.QueueInitialSync(s, Fixtures.Now)!;
        Assert.Equal(12, op.Increment);
        Assert.Equal(UuidV5.InitialSyncOperation(Fixtures.Hunt), op.OperationId);
        Assert.Equal((0, 0), Pokedex0Book.InitialSyncPreview(s));
        Assert.Null(Pokedex0Book.QueueInitialSync(s, Fixtures.Now));
        Pokedex0Book.Acknowledge(s, s.Outbox.ToList(), Fixtures.Now);
        Assert.True(s.Hunt!.InitialSyncDone);
        // After the sync, a re-analysis of a match it covered sends its difference.
        Assert.Equal(1, Pokedex0Book.Record(s, Fixtures.Match("a", 6), Fixtures.Now)!.Increment);
    }

    [Fact]
    public void NothingToSyncWithoutMatches()
    {
        var s = Linked(Fixtures.Data());
        Assert.Equal((0, 0), Pokedex0Book.InitialSyncPreview(s));
        Assert.Null(Pokedex0Book.QueueInitialSync(s, Fixtures.Now));
    }

    [Fact]
    public void NegativeCorrectionNeverExceedsWhatTheHuntReceived()
    {
        var s = Linked(Fixtures.Data());
        Pokedex0Book.Record(s, Fixtures.Match("m", 5), Fixtures.Now);
        Pokedex0Book.Acknowledge(s, s.Outbox.ToList(), Fixtures.Now);
        s.Hunt!.SentChests = 2; // e.g. a correction was refused before
        Assert.Equal(-2, Pokedex0Book.Record(s, Fixtures.Match("m", 0), Fixtures.Now)!.Increment);
    }

    [Fact]
    public void SwitchingHuntsDropsTheQueueAndNeverSendsAMatchTwice()
    {
        var data = Fixtures.Data(Fixtures.Match("a", 5));
        var s = Linked(data);
        Pokedex0Book.Record(s, Fixtures.Match("sent", 4), Fixtures.Now);
        Pokedex0Book.Acknowledge(s, s.Outbox.ToList(), Fixtures.Now);
        Pokedex0Book.Record(s, Fixtures.Match("queued", 6), Fixtures.Now);
        data.Matches.Add(Fixtures.Match("sent", 4));
        data.Matches.Add(Fixtures.Match("queued", 6));

        var dropped = Pokedex0Book.Link(s, Fixtures.OtherHunt, "Other", false, data, "acct-1", Fixtures.Now);
        Assert.Equal(6, dropped);
        Assert.Empty(s.Outbox);
        Assert.Equal(Fixtures.OtherHunt, s.HuntId);
        Assert.Equal((15, 3), Pokedex0Book.InitialSyncPreview(s)); // a fresh baseline for the new hunt

        // Back to the first hunt: it remembers "sent"; "queued" was dropped (never sent), so it is now baseline.
        Assert.Equal(0, Pokedex0Book.Link(s, Fixtures.Hunt, "Hunt", false, data, "acct-1", Fixtures.Now));
        Assert.Empty(s.Outbox);
        Assert.Equal((11, 2), Pokedex0Book.InitialSyncPreview(s)); // "a" + "queued": "sent" is never sent twice
        Assert.True(s.Hunt!.Matches["queued"].Baseline);
        Assert.False(s.Hunt.Matches["sent"].Baseline);
    }

    [Fact]
    public void DroppedOneTimeSyncCanBeAskedAgain()
    {
        var data = Fixtures.Data(Fixtures.Match("a", 5));
        var s = Linked(data);
        Pokedex0Book.QueueInitialSync(s, Fixtures.Now);
        Pokedex0Book.Link(s, Fixtures.OtherHunt, "Other", false, data, "acct-1", Fixtures.Now);
        Pokedex0Book.Link(s, Fixtures.Hunt, "Hunt", false, data, "acct-1", Fixtures.Now);
        Assert.Equal((5, 1), Pokedex0Book.InitialSyncPreview(s));
    }

    [Fact]
    public void LinkedBeforeTheFirstMatchAdoptsItsAccount()
    {
        var s = new Pokedex0Ledger();
        Pokedex0Book.Link(s, Fixtures.Hunt, "Hunt", false, Fixtures.Data(), null, Fixtures.Now);
        Assert.Equal(3, Pokedex0Book.Record(s, Fixtures.Match("first", 3, account: "acct-9"), Fixtures.Now)!.Increment);
        Assert.Equal("acct-9", s.Hunt!.ProfileKey);
        Assert.Null(Pokedex0Book.Record(s, Fixtures.Match("other", 3, account: "acct-1"), Fixtures.Now));
    }

    [Fact]
    public void BatchesOfAtMostOneHundredForTheLinkedHuntOnly()
    {
        var s = Linked(Fixtures.Data());
        for (var i = 0; i < 250; i++)
        {
            Pokedex0Book.Record(s, Fixtures.Match("m" + i, 1 + i % 5), Fixtures.Now);
        }
        s.Outbox.Add(new PendingOperation { OperationId = Guid.NewGuid().ToString(), Increment = 1, HuntId = Fixtures.OtherHunt });
        var sizes = new List<int>();
        while (Pokedex0Book.NextBatch(s) is { Count: > 0 } batch)
        {
            Assert.All(batch, o => Assert.Equal(Fixtures.Hunt, o.HuntId));
            Assert.Equal(batch.Count, batch.Select(o => o.OperationId).Distinct().Count());
            sizes.Add(batch.Count);
            Pokedex0Book.Acknowledge(s, batch, Fixtures.Now);
        }
        Assert.Equal(new[] { 100, 100, 50 }, sizes);
        Assert.Single(s.Outbox); // the other hunt's operation is never sent with these
    }

    [Fact]
    public void OutboxSurvivesARestart()
    {
        var dir = Fixtures.TempDir();
        try
        {
            var file = new Pokedex0LedgerFile(dir);
            var s = Linked(Fixtures.Data(Fixtures.Match("a", 5)));
            s.UserId = "u";
            Pokedex0Book.Record(s, Fixtures.Match("m", 4), Fixtures.Now);
            Pokedex0Book.QueueInitialSync(s, Fixtures.Now);
            file.Save(s);

            var back = new Pokedex0LedgerFile(dir).Load();
            Assert.Equal(Fixtures.Hunt, back.HuntId);
            Assert.Equal(s.Outbox.Select(o => (o.OperationId, o.Increment, o.Kind, o.SessionId)), back.Outbox.Select(o => (o.OperationId, o.Increment, o.Kind, o.SessionId)));
            Assert.Equal(1, back.Hunt!.Matches["m"].Changes);
            Assert.Equal(5, back.Hunt.InitialSyncChests);
            Assert.Null(Pokedex0Book.Record(back, Fixtures.Match("m", 4), Fixtures.Now)); // the ledger came back too
            Assert.Equal(UuidV5.MatchOperation(Fixtures.Hunt, "m", 1), Pokedex0Book.Record(back, Fixtures.Match("m", 5), Fixtures.Now)!.OperationId);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DamagedFileStartsUnlinked()
    {
        var dir = Fixtures.TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "pokedex0.json"), "{ not json");
            Assert.False(new Pokedex0LedgerFile(dir).Load().Linked);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class ParsingTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void HuntsObjectOrArrayActiveOnlyDefensive()
    {
        var hunts = """
            {"hunts":[
              {"id":"0B8F3C1E-6A43-4D6E-9A51-2C7D0E5F9A10","name":"Solo","is_multiplayer":false,"is_owner":true,"status":"active","viewer_effective_status":"active","display_count":1287,"viewer_participant_count":1287,"custom_odds_denominator":4096,"hunt_method":"Fortnite chests","game":null,"pokemon":{"name":"Gimmighoul"},"target_count":null,"extra":{"x":[1]}},
              {"id":"7d2a9e44-1b3c-4f5d-8e6a-9c0b1d2e3f40","name":"Multi","is_multiplayer":true,"is_owner":false,"status":"active","viewer_effective_status":"completed","display_count":5},
              {"id":"7d2a9e44-1b3c-4f5d-8e6a-9c0b1d2e3f41","name":"Multi2","is_multiplayer":true,"is_owner":false,"status":"active","display_count":"12","viewer_participant_count":3.0,"custom_odds_denominator":0},
              {"id":"1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e50","name":"Done","status":"completed"},
              {"id":"not-a-uuid","name":"Bad","status":"active"},
              {"id":"2e3f4a5b-6c7d-4e8f-9a0b-1c2d3e4f5a60","name":"Not mine","is_multiplayer":false,"is_owner":false,"status":"active"},
              {"id":"3e3f4a5b-6c7d-4e8f-9a0b-1c2d3e4f5a60","name":null,"status":"active","is_owner":true,"name2":"\u0000"},
              42, null, "x"
            ]}
            """;
        var list = Pokedex0Client.ParseHunts(Json(hunts));
        Assert.Equal(new[] { "0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10", "7d2a9e44-1b3c-4f5d-8e6a-9c0b1d2e3f41", "3e3f4a5b-6c7d-4e8f-9a0b-1c2d3e4f5a60" }, list.Select(h => h.Id));
        var solo = list[0];
        Assert.Equal(4096, solo.OddsDenominator);
        Assert.Equal(1287, solo.DisplayCount);
        Assert.Null(solo.Game);
        Assert.Equal("Gimmighoul", solo.Pokemon);
        Assert.False(solo.Multiplayer);
        Assert.Equal(12, list[1].DisplayCount);
        Assert.Equal(3, list[1].MyCount);
        Assert.Null(list[1].OddsDenominator); // 0 is not valid odds
        Assert.Equal("—", list[2].Name);

        Assert.Single(Pokedex0Client.ParseHunts(Json("""[{"id":"0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10","status":"active","is_owner":true}]""")));
        Assert.Empty(Pokedex0Client.ParseHunts(Json("""{"error":"x"}""")));
        Assert.Empty(Pokedex0Client.ParseHunts(null));
    }

    [Fact]
    public void NamesFromTheNetworkLoseControlCharacters()
    {
        Assert.Equal("a b c", Pokedex0Client.Clean("a\nb\u202Ec", 50));
        Assert.Equal(5, Pokedex0Client.Clean(new string('x', 99), 5)!.Length);
    }

    [Fact]
    public void SessionWithNullNames()
    {
        var a = Pokedex0Client.ParseAccount(Json("""{"user":{"id":"u1","username":null,"display_name":null},"client_id":"fortnite-chestcounter"}"""));
        Assert.Equal("u1", a.Id);
        Assert.Null(a.Username);
        Assert.Null(Pokedex0Client.ParseAccount(Json("[]")).Id);
    }

    [Fact]
    public void Acknowledgement()
    {
        var ack = Pokedex0Client.ParseAcknowledgement(Json("""{"acknowledgement":{"displayCount":5321,"viewerParticipantCount":1294,"revision":88},"hunt":null}"""))!;
        Assert.Equal(5321, ack.DisplayCount);
        Assert.Equal(1294, ack.ViewerCount);
        Assert.Equal(88, ack.Revision);
        Assert.Null(Pokedex0Client.ParseAcknowledgement(Json("""{"hunt":null}""")));
    }

    [Fact]
    public void TokensAreEncryptedAtRest()
    {
        var dir = Fixtures.TempDir();
        try
        {
            var store = new Pokedex0TokenStore(dir);
            store.Save(new Pokedex0Tokens { AccessToken = "at_SECRET_ACCESS", RefreshToken = "rt_SECRET_REFRESH", AccessExpiresUtc = Fixtures.Now });
            var bytes = File.ReadAllBytes(store.Path);
            var text = System.Text.Encoding.UTF8.GetString(bytes) + System.Text.Encoding.Unicode.GetString(bytes);
            Assert.DoesNotContain("SECRET", text);
            Assert.Equal("rt_SECRET_REFRESH", store.Load()!.RefreshToken);
            Assert.DoesNotContain("SECRET", store.Load()!.ToString());
            File.WriteAllBytes(store.Path, new byte[] { 1, 2, 3 });
            Assert.Null(store.Load()); // damaged: forgotten
            Assert.False(File.Exists(store.Path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
