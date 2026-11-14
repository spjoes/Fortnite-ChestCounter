using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>The overlay's Pokédex0 chance, checked against the website's own JavaScript (tests/mock-pokedex0/odds-reference.mjs).</summary>
public class OddsTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>getProbabilityAcrossRolls + formatShinyProbability of the website, run in Node, for each [N, tries].</summary>
    private static List<(double P, string Text)> Website(IReadOnlyList<(int N, long Tries)> pairs)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "mock-pokedex0", "odds-reference.mjs");
        using var node = Process.Start(new ProcessStartInfo("node", $"\"{script}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        node.StandardInput.Write(JsonSerializer.Serialize(pairs.Select(p => new[] { p.N, p.Tries })));
        node.StandardInput.Close();
        var json = node.StandardOutput.ReadToEnd();
        node.WaitForExit();
        return JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("p").GetDouble(), e.GetProperty("text").GetString()!)).ToList();
    }

    private static void SameAsWebsite(IReadOnlyList<(int N, long Tries)> pairs)
    {
        var web = Website(pairs);
        for (var i = 0; i < pairs.Count; i++)
        {
            var (n, tries) = pairs[i];
            var p = Pokedex0Odds.Probability(n, tries);
            var (wp, wtext) = web[i];
            // Same value to the last digits or so (V8's fdlibm log1p/expm1 vs the corrections used here).
            Assert.True(Math.Abs(p - wp) <= 1e-13 * Math.Max(wp, 1e-300) || p == wp, $"N={n} n={tries}: {p:R} vs {wp:R}");
            // Same text: the website's (en-US), and the same digits with the app language's decimal separator.
            Assert.Equal(wtext, Pokedex0Odds.FormatPercent(p, En));
            Assert.Equal(wtext.Replace(".", ","), Pokedex0Odds.FormatPercent(p, Fr));
            Assert.Equal(wtext.Replace(".", ","), Pokedex0Odds.FormatPercent(p, Es));
        }
    }

    [Fact]
    public void RequiredValuesMatchTheWebsite()
    {
        var pairs = new List<(int, long)>
        {
            (137_500, 0), (137_500, 1), (137_500, 1_000), (137_500, 137_500), (137_500, 1_000_000),
            (1, 0), (1, 1), (2, 1), (1_000_000, 1), (2, 60), (137_500, 1_200), (137_500, 2_234), (8192, 1), (4096, 4096),
        };
        SameAsWebsite(pairs);
        Assert.Equal("0%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(137_500, 0), En));
        Assert.Equal("0.0007%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(137_500, 1), En));
        Assert.Equal("0.72%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(137_500, 1_000), En));
        Assert.Equal("63.21%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(137_500, 137_500), En));
        Assert.Equal("99.93%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(137_500, 1_000_000), En));
        Assert.Equal("0%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(1, 0), En));
        Assert.Equal("100%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(1, 1), En));
        Assert.Equal("50%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(2, 1), En));
        Assert.Equal("<0.0001%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(1_000_000, 1), En)); // 1e-6 * 100 < 0.0001 in doubles
        Assert.Equal(">99.99%", Pokedex0Odds.FormatPercent(Pokedex0Odds.Probability(2, 60), En));
        Assert.Equal(1 - Math.Pow(2, -52), Pokedex0Odds.Probability(2, 60)); // capped at 1 - Number.EPSILON
    }

    [Fact]
    public void ThousandsOfRandomValuesMatchTheWebsite()
    {
        var rng = new Random(20261114);
        var pairs = new List<(int, long)>();
        for (var i = 0; i < 3000; i++)
        {
            var n = i % 3 == 0 ? rng.Next(1, 1_000_001) : i % 3 == 1 ? rng.Next(1, 10_000) : 137_500;
            var scale = Math.Pow(10, rng.NextDouble() * 7);
            pairs.Add((n, (long)(scale * rng.NextDouble())));
        }
        SameAsWebsite(pairs);
    }

    [Fact]
    public void FractionDigits()
    {
        Assert.Equal("0.05%", Pokedex0Odds.FormatPercent(0.0005, En));        // below 0.1 %: up to 4 decimals, no trailing zeros
        Assert.Equal("0.0123%", Pokedex0Odds.FormatPercent(0.000123449, En));
        Assert.Equal("0.1%", Pokedex0Odds.FormatPercent(0.001, En));          // from 0.1 %: up to 2 decimals
        Assert.Equal("12.35%", Pokedex0Odds.FormatPercent(0.123456, En));
        Assert.Equal("12,35%", Pokedex0Odds.FormatPercent(0.123456, Fr));
        Assert.Equal("12,35%", Pokedex0Odds.FormatPercent(0.123456, Es));
        Assert.Equal("<0,0001%", Pokedex0Odds.FormatPercent(1e-7, Fr));
        Assert.Equal(">99,99%", Pokedex0Odds.FormatPercent(0.99999, Fr));
        Assert.Equal("0%", Pokedex0Odds.FormatPercent(double.NaN, En));
    }

    [Fact]
    public void AccurateLogAndExp()
    {
        // The naive forms lose the digits of 1/137,500; the corrected ones keep them.
        var x = -1.0 / 137_500;
        // Reference values: V8's Math.log1p / Math.expm1.
        Assert.Equal(-0.000007272753719136489, Pokedex0Odds.Log1P(x), 1e-21);
        Assert.Equal(-0.0000010000005000003334, Pokedex0Odds.Log1P(-1e-6), 1e-21);
        Assert.Equal(1.00000000005e-10, Pokedex0Odds.ExpM1(1e-10), 1e-25);
        Assert.NotEqual(-0.000007272753719136489, Math.Log(1 - 1.0 / 137_500)); // what plain Log(1 - p) gives
        Assert.Equal(Math.Exp(3) - 1, Pokedex0Odds.ExpM1(3), 1e-12);
        Assert.Equal(Math.Log(3), Pokedex0Odds.Log1P(2), 1e-15);
    }

    [Fact]
    public void LineInEachLanguage()
    {
        try
        {
            L.Set("en");
            Assert.Equal("1 in 137,500 · 0.87%", Pokedex0Odds.Line(new Pokedex0OddsSnapshot(137_500, false, 1_200, 1_200)));
            Assert.Equal("1 in 137,500 · You 0.87% · Team 1.61%", Pokedex0Odds.Line(new Pokedex0OddsSnapshot(137_500, true, 1_200, 2_234)));
            L.Set("fr");
            Assert.Equal($"1 sur {L.N(137_500)} · Toi 0,87% · Équipe 1,61%", Pokedex0Odds.Line(new Pokedex0OddsSnapshot(137_500, true, 1_200, 2_234)));
            Assert.Equal($"1 sur {L.N(137_500)} · 0,87%", Pokedex0Odds.Line(new Pokedex0OddsSnapshot(137_500, false, 1_200, 1_200)));
            L.Set("es");
            Assert.Equal($"1 entre {L.N(137_500)} · Tú 0,87% · Equipo 1,61%", Pokedex0Odds.Line(new Pokedex0OddsSnapshot(137_500, true, 1_200, 2_234)));
        }
        finally
        {
            L.Set("en");
        }
    }
}

/// <summary>When the line is shown, and the counts it uses.</summary>
public class OddsRuleTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static Pokedex0Hunt? Hunt(string fields) =>
        Pokedex0Client.ParseHunt(Json($$"""{"id":"0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10","name":"H","status":"active","is_owner":true,"display_count":10{{fields}}}"""));

    [Fact]
    public void OddsOnlyWithAValidDenominatorAndNoVerifiedMethod()
    {
        Assert.Equal(137_500, Hunt(""","custom_odds_denominator":137500,"shiny_method_effect_id":null""")!.OddsDenominator);
        Assert.Equal(137_500, Hunt(""","custom_odds_denominator":137500""")!.OddsDenominator); // field missing = no method
        Assert.Equal(1, Hunt(""","custom_odds_denominator":1""")!.OddsDenominator);
        Assert.Equal(1_000_000, Hunt(""","custom_odds_denominator":1000000""")!.OddsDenominator);
        var verified = Hunt(""","custom_odds_denominator":137500,"shiny_method_effect_id":12""")!;
        Assert.Null(verified.OddsDenominator);
        Assert.True(verified.VerifiedMethod);
        foreach (var bad in new[] { "0", "-5", "1000001", "4096.5", "\"4096\"", "null", "true", "1e300" })
        {
            Assert.Null(Hunt($$""","custom_odds_denominator":{{bad}}""")!.OddsDenominator);
        }
        Assert.Null(Hunt("")!.OddsDenominator);
    }

    private static Pokedex0Ledger Linked(bool multiplayer, int? odds, long? own, long? total, long? revision = 1)
    {
        var s = new Pokedex0Ledger();
        Pokedex0Book.Link(s, Fixtures.Hunt, "H", multiplayer, Fixtures.Data(), "acct-1", Fixtures.Now);
        s.Hunt!.OddsDenominator = odds;
        s.Hunt.TakeCounts(own, total, revision);
        return s;
    }

    [Fact]
    public void HiddenWithoutHuntOddsOrCounts()
    {
        Assert.Null(Pokedex0Book.OddsSnapshot(new Pokedex0Ledger()));                   // no hunt
        Assert.Null(Pokedex0Book.OddsSnapshot(Linked(false, null, 10, 10)));            // no custom odds (or a verified method)
        Assert.Null(Pokedex0Book.OddsSnapshot(Linked(false, 0, 10, 10)));               // invalid denominator
        Assert.Null(Pokedex0Book.OddsSnapshot(Linked(false, 2_000_000, 10, 10)));
        Assert.Null(Pokedex0Book.OddsSnapshot(Linked(false, 4096, null, null)));        // nothing read from Pokédex0 yet
        Assert.NotNull(Pokedex0Book.OddsSnapshot(Linked(false, 4096, 10, 10)));
    }

    [Fact]
    public void HiddenWhenNotConnected()
    {
        var dir = Fixtures.TempDir();
        try
        {
            var ledger = Linked(true, 137_500, 40, 540);
            new Pokedex0LedgerFile(dir).Save(ledger);
            var sync = new Pokedex0Sync(dir, "http://127.0.0.1:9"); // no tokens in this folder
            Assert.False(sync.Connected);
            Assert.NotNull(sync.Read(Pokedex0Book.OddsSnapshot)); // the ledger has it…
            Assert.Null(sync.OddsSnapshot());                       // …but nothing is shown while not connected
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PendingChestsAreAddedToPokedex0sCounts()
    {
        var s = Linked(true, 137_500, 40, 540);
        Assert.Equal(new Pokedex0OddsSnapshot(137_500, true, 40, 540), Pokedex0Book.OddsSnapshot(s));
        Pokedex0Book.Record(s, Fixtures.Match("m1", 6), Fixtures.Now);
        Pokedex0Book.Record(s, Fixtures.Match("m2", 3), Fixtures.Now);
        Assert.Equal(new Pokedex0OddsSnapshot(137_500, true, 49, 549), Pokedex0Book.OddsSnapshot(s));

        // Solo: the hunt's count is the player's.
        var solo = Linked(false, 4096, 1287, 1287);
        Pokedex0Book.Record(solo, Fixtures.Match("m", 5), Fixtures.Now);
        Assert.Equal(new Pokedex0OddsSnapshot(4096, false, 1292, 1292), Pokedex0Book.OddsSnapshot(solo));
    }

    [Fact]
    public void AcknowledgementSwapsPendingForTheServerCountAtOnce()
    {
        var s = Linked(true, 137_500, 40, 540, revision: 7);
        Pokedex0Book.Record(s, Fixtures.Match("m1", 6), Fixtures.Now);
        Pokedex0Book.Record(s, Fixtures.Match("m2", 3), Fixtures.Now);
        var before = Pokedex0Book.OddsSnapshot(s);

        // The first operation is acknowledged: Pokédex0's counts now include its 6 chests and it leaves the outbox
        // (one step under the sync's lock, as in Pokedex0Sync.Acknowledged).
        var first = s.Outbox.Take(1).ToList();
        Pokedex0Book.Acknowledge(s, first, Fixtures.Now);
        Assert.True(s.Hunt!.TakeCounts(46, 546, 8));
        Assert.Equal(before, Pokedex0Book.OddsSnapshot(s)); // 46 + 3 pending = 40 + 9 pending: no double count, no dip

        // An older snapshot (a hunt list read before that increment) is ignored.
        Assert.False(s.Hunt.TakeCounts(40, 540, 7));
        Assert.Equal(before, Pokedex0Book.OddsSnapshot(s));

        // A friend played: a newer list with a bigger total.
        Assert.True(s.Hunt.TakeCounts(46, 600, 9));
        Assert.Equal(new Pokedex0OddsSnapshot(137_500, true, 49, 603), Pokedex0Book.OddsSnapshot(s));
    }

    [Fact]
    public void OverlayModelAddsTheLineOnlyWhenGiven()
    {
        L.Set("en");
        var without = OverlayModel.Build(Fixtures.Data(Fixtures.Match("a", 3)), null, false);
        Assert.Null(without.Odds);
        Assert.DoesNotContain(without.Rows, r => r.Odds);
        var with = OverlayModel.Build(Fixtures.Data(Fixtures.Match("a", 3)), null, false, odds: "1 in 137,500 · 0.87%");
        Assert.Equal("1 in 137,500 · 0.87%", with.Odds);
        Assert.Equal("1 in 137,500 · 0.87%", with.Rows[^1].Value);
        Assert.True(with.Rows[^1].Odds);
        Assert.Equal(without.Rows.Count + 1, with.Rows.Count);
        Assert.Contains("1 in 137,500 · 0.87%", System.Net.WebUtility.HtmlDecode(ReportWriter.Overlay(with)));
        Assert.DoesNotContain("class=\"r o\"", ReportWriter.Overlay(without));
        Assert.DoesNotContain("row o", ReportWriter.Overlay(without));
    }
}
