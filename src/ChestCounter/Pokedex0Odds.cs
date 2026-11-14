using System.Globalization;

namespace ChestCounter;

/// <summary>
/// The chance shown on the overlay for a Pokédex0 hunt with custom odds (1 in N per chest). Mirrors the website's
/// lib/shiny-odds.ts so that the counter and pokedex0.com always show the same figure:
/// getProbabilityAcrossRolls (chance of at least one success in floor(n) tries) and formatShinyProbability.
/// The team chance of a multiplayer custom-odds hunt is the same formula on the hunt total: every participant tries at
/// the same 1/N and there is no Shiny Charm with custom odds, so the website's group probability reduces to it.
/// </summary>
public static class Pokedex0Odds
{
    public const int MaxDenominator = 1_000_000;

    /// <summary>JavaScript's Number.EPSILON (2^-52), not .NET's double.Epsilon (the smallest denormal).</summary>
    private static readonly double JsEpsilon = Math.Pow(2, -52);

    /// <summary>isValidCustomOddsDenominator: a whole number from 1 to 1,000,000.</summary>
    public static bool ValidDenominator(long? n) => n is >= 1 and <= MaxDenominator;

    /// <summary>getProbabilityAcrossRolls(1 / denominator, tries).</summary>
    public static double Probability(int denominator, double tries)
    {
        if (denominator <= 0 || double.IsNaN(tries) || tries <= 0)
        {
            return 0;
        }
        var p = 1.0 / denominator;
        if (p >= 1 || double.IsPositiveInfinity(tries))
        {
            return 1;
        }
        var probability = -ExpM1(Math.Floor(tries) * Log1P(-p));
        return Math.Min(probability, 1 - JsEpsilon);
    }

    /// <summary>
    /// formatShinyProbability: "0%", "100%", "&lt;0.0001%", "&gt;99.99%", else the percent with at most 4 decimals
    /// below 0.1 % and 2 above (no trailing zeros, as Intl's maximumFractionDigits), with the culture's decimal separator.
    /// </summary>
    public static string FormatPercent(double probability, CultureInfo culture)
    {
        var sep = culture.NumberFormat.NumberDecimalSeparator;
        if (!double.IsFinite(probability) || probability <= 0)
        {
            return "0%";
        }
        if (probability >= 1)
        {
            return "100%";
        }
        var percent = probability * 100;
        if (percent < 0.0001)
        {
            return "<0" + sep + "0001%";
        }
        if (percent > 99.99)
        {
            return ">99" + sep + "99%";
        }
        // "F" with a precision rounds the exact binary value half away from zero, like Intl's default "halfExpand".
        var text = percent.ToString(percent < 0.1 ? "F4" : "F2", CultureInfo.InvariantCulture);
        if (text.Contains('.'))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }
        return text.Replace(".", sep) + "%";
    }

    /// <summary>
    /// The overlay line: "1 in 137,500 · 0.87%" (solo) or "1 in 137,500 · You 0.87% · Team 1.61%" (multiplayer).
    /// </summary>
    public static string Line(Pokedex0OddsSnapshot s)
    {
        var odds = $"{L.T("1 sur", "1 in", "1 entre")} {L.N(s.Denominator)}";
        var own = FormatPercent(Probability(s.Denominator, s.OwnCount), L.Culture);
        if (!s.Multiplayer)
        {
            return $"{odds} · {own}";
        }
        var team = FormatPercent(Probability(s.Denominator, s.TeamCount), L.Culture);
        return $"{odds} · {L.T("Toi", "You", "Tú")} {own} · {L.T("Équipe", "Team", "Equipo")} {team}";
    }

    /// <summary>
    /// log(1 + x), accurate for small x (.NET's double.LogP1 is Log(x + 1), which loses the digits of 1/137,500).
    /// Goldberg's correction: exact up to a few ulps.
    /// </summary>
    public static double Log1P(double x)
    {
        if (double.IsNaN(x) || x == 0)
        {
            return x;
        }
        var u = 1.0 + x;
        if (u == 1.0)
        {
            return x;
        }
        if (double.IsInfinity(u) || u <= 0)
        {
            return Math.Log(u);
        }
        return Math.Log(u) * x / (u - 1.0);
    }

    /// <summary>exp(x) - 1, accurate for small x (Kahan's correction; a short series very close to 0).</summary>
    public static double ExpM1(double x)
    {
        if (double.IsNaN(x) || x == 0)
        {
            return x;
        }
        if (Math.Abs(x) < 1e-5)
        {
            return x + x * x / 2 + x * x * x / 6;
        }
        var u = Math.Exp(x);
        if (u == 1.0)
        {
            return x;
        }
        var um1 = u - 1.0;
        if (um1 == -1.0)
        {
            return -1.0;
        }
        if (double.IsInfinity(u))
        {
            return u;
        }
        return um1 * x / Math.Log(u);
    }
}

/// <summary>
/// What the overlay's Pokédex0 line is computed from: the linked hunt's custom odds and the counts (Pokédex0's own
/// counts plus the chests still waiting to be sent).
/// </summary>
public sealed record Pokedex0OddsSnapshot(int Denominator, bool Multiplayer, long OwnCount, long TeamCount);
