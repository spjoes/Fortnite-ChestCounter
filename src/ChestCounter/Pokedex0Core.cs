using System.Security.Cryptography;
using System.Text;

namespace ChestCounter;

/// <summary>
/// Name-based UUIDs (RFC 4122 / RFC 9562 version 5: SHA-1 of a namespace and a name). Used as the operation ids sent to
/// Pokédex0: the same match always gives the same id, so a retry, a crash or a re-analysis can never count twice.
/// </summary>
public static class UuidV5
{
    /// <summary>Namespace of the Chest Counter's Pokédex0 operations (a fixed random UUID: never change it).</summary>
    public static readonly Guid Pokedex0Namespace = Guid.Parse("8f0c2b5e-4d7a-4c1e-9b36-2f5d8e7a1c90");

    /// <summary>The version 5 UUID of <paramref name="name"/> (UTF-8) in <paramref name="ns"/>, in the usual text form.</summary>
    public static string Create(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[nsBytes.Length + nameBytes.Length];
        nsBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, nsBytes.Length);
#pragma warning disable CA5350 // SHA-1 is what RFC 4122 version 5 is defined with (an identifier, not a signature)
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true).ToString("D");
    }

    /// <summary>Operation id of the n-th change (0, 1, 2…) of a match's chests for a hunt.</summary>
    public static string MatchOperation(string huntId, string sessionId, int n) =>
        Create(Pokedex0Namespace, $"{huntId.ToLowerInvariant()}|{sessionId}|{n}");

    /// <summary>Operation id of the one-time sync of the chests counted before the hunt was linked.</summary>
    public static string InitialSyncOperation(string huntId) =>
        Create(Pokedex0Namespace, $"{huntId.ToLowerInvariant()}|initial-sync");
}

/// <summary>Proof Key for Code Exchange (RFC 7636), S256 method.</summary>
public static class Pkce
{
    /// <summary>A new code verifier: 32 random bytes, base64url (43 characters).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>An authorization started in the browser, waiting for Pokédex0 to send the player back.</summary>
public sealed record PendingAuthorization(string State, string Verifier, string RedirectUri, DateTime CreatedUtc);

/// <summary>
/// The "state" values of the authorizations started from the counter: a callback is accepted only with one of them,
/// once, within 10 minutes. Thread-safe (the callback arrives on a thread of the local server).
/// </summary>
public sealed class OAuthStateStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxPending = 4;
    private readonly object _gate = new();
    private readonly List<PendingAuthorization> _pending = new();

    public PendingAuthorization Create(string redirectUri, DateTime nowUtc)
    {
        var pending = new PendingAuthorization(Pkce.Base64Url(RandomNumberGenerator.GetBytes(32)), Pkce.NewVerifier(), redirectUri, nowUtc);
        lock (_gate)
        {
            _pending.RemoveAll(p => nowUtc - p.CreatedUtc > Lifetime);
            // A few tries at once at most (the player clicked Connect again): the oldest is forgotten.
            while (_pending.Count >= MaxPending)
            {
                _pending.RemoveAt(0);
            }
            _pending.Add(pending);
        }
        return pending;
    }

    /// <summary>True while an authorization started in the browser can still come back.</summary>
    public bool HasPending(DateTime nowUtc)
    {
        lock (_gate)
        {
            return _pending.Any(p => nowUtc - p.CreatedUtc <= Lifetime);
        }
    }

    /// <summary>
    /// The authorization of this state, removed so it cannot be used again; null when unknown, used or expired.
    /// Every pending state is compared in constant time.
    /// </summary>
    public PendingAuthorization? TryConsume(string? state, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 256)
        {
            return null;
        }
        var given = Encoding.UTF8.GetBytes(state);
        lock (_gate)
        {
            PendingAuthorization? found = null;
            foreach (var p in _pending)
            {
                if (CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(p.State)))
                {
                    found = p;
                }
            }
            if (found == null)
            {
                return null;
            }
            _pending.Remove(found);
            return nowUtc - found.CreatedUtc <= Lifetime ? found : null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}

/// <summary>Where Pokédex0 is: https://www.pokedex0.com (the apex redirects, and the token audience is on www), or a test server given in CHESTCOUNTER_POKEDEX0_URL.</summary>
public static class Pokedex0Endpoint
{
    public const string DefaultBase = "https://www.pokedex0.com";
    public const string EnvironmentVariable = "CHESTCOUNTER_POKEDEX0_URL";

    /// <summary>The base URL (no trailing slash): the override when it is acceptable, else www.pokedex0.com.</summary>
    public static string Resolve(string? overrideUrl) => Normalize(overrideUrl) ?? DefaultBase;

    public static string FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>An https URL, or http only on this PC (127.0.0.1, ::1, localhost); null otherwise.</summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (!IsAllowed(uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>A URL given by the server (token endpoint) is used only on the same origin as the base URL.</summary>
    public static bool SameOrigin(string baseUrl, string other) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var a) && Uri.TryCreate(other, UriKind.Absolute, out var b)
        && IsAllowed(b) && a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
