using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChestCounter;

/// <summary>The OAuth tokens of the Pokédex0 connection. Never written anywhere but <see cref="Pokedex0TokenStore"/>.</summary>
public sealed class Pokedex0Tokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime AccessExpiresUtc { get; set; }
    public string? Scope { get; set; }

    /// <summary>No token in a log or an exception message by accident.</summary>
    public override string ToString() => "Pokedex0Tokens(***)";
}

/// <summary>
/// pokedex0-auth.bin in the data folder: the tokens, encrypted with Windows DPAPI for the current Windows user (another
/// user of the PC, or a copy of the file on another PC, cannot read them).
/// </summary>
public sealed class Pokedex0TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChestCounter.Pokedex0.Tokens.v1");

    public Pokedex0TokenStore(string folder) => Path = System.IO.Path.Combine(folder, "pokedex0-auth.bin");

    public string Path { get; }

    public Pokedex0Tokens? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path))
        {
            return null;
        }
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
            var tokens = JsonSerializer.Deserialize<Pokedex0Tokens>(json);
            CryptographicOperations.ZeroMemory(json);
            return tokens is { RefreshToken.Length: > 0 } ? tokens : null;
        }
        catch
        {
            // Another Windows user, another PC, a damaged file: the player connects again.
            Delete();
            return null;
        }
    }

    public void Save(Pokedex0Tokens tokens)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var bytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(json);
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, Path, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(Path); } catch { }
        try { File.Delete(Path + ".tmp"); } catch { }
    }
}

/// <summary>Answer of a Pokédex0 API call. Status 0 = no answer (offline, timeout).</summary>
public sealed record Pokedex0Response(int Status, JsonElement? Body, string? Error, TimeSpan? RetryAfter)
{
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>Worth trying again later on its own: no answer, server error, too many requests.</summary>
    public bool Transient => Status == 0 || Status >= 500 || Status == 429 || Status == 408;
}

/// <summary>Result of the token endpoint: the tokens, or why not.</summary>
public sealed record Pokedex0TokenResult(Pokedex0Tokens? Tokens, int Status, string? Error)
{
    /// <summary>The refresh token (or the code) was refused: connecting again is the only way.</summary>
    public bool Rejected => Tokens == null && Status is 400 or 401 or 403;
}

/// <summary>A hunt of the player, as listed by Pokédex0 (only what the counter shows; every field is optional).</summary>
/// <param name="OddsDenominator">Custom odds 1 in N, only when valid (1..1,000,000) and no verified game method is set
/// (the website then ignores custom odds, see getHuntShinyOddsInput).</param>
/// <param name="VerifiedMethod">shiny_method_effect_id is set: a catalogued game method decides the odds.</param>
/// <param name="Revision">Counter revision (monotonic): orders count snapshots.</param>
public sealed record Pokedex0Hunt(
    string Id, string Name, bool Multiplayer, bool IsOwner, string Status, int DisplayCount, int? MyCount,
    int? TargetCount, string? Method, string? Pokemon, string? Game, int? OddsDenominator,
    bool VerifiedMethod = false, long? Revision = null)
{
    /// <summary>The player's own count: their participant count (multiplayer), the hunt's count (solo).</summary>
    public int OwnCount => Multiplayer ? MyCount ?? 0 : DisplayCount;
}

/// <summary>The Pokédex0 account the counter is connected to.</summary>
public sealed record Pokedex0Account(string? Id, string? Username, string? DisplayName);

/// <summary>From /.well-known/oauth-authorization-server (each URL on the base URL's origin only).</summary>
public sealed record Pokedex0Metadata(string TokenEndpoint, string RevocationEndpoint, string Issuer);

/// <summary>The counts an increment answer carries (acknowledgement): the hunt's total and the player's own count.</summary>
public sealed record Pokedex0Acknowledgement(int? DisplayCount, int? ViewerCount, long? Revision);

/// <summary>
/// The calls to Pokédex0 (OAuth 2.1 authorization code + PKCE, then its API with the access token). One shared
/// HttpClient, https only (http only for a test server on this PC), no redirect followed, short timeouts.
/// </summary>
public sealed class Pokedex0Client
{
    public const string ClientId = "fortnite-chestcounter";
    public const string Scopes = "openid profile offline_access hunts:read hunts:increment";

    private static readonly Lazy<HttpClient> SharedHttp = new(() => NewHttp(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    }));

    private readonly HttpClient _http;
    private Pokedex0Metadata? _metadata;

    public Pokedex0Client(string baseUrl, HttpMessageHandler? handler = null)
    {
        BaseUrl = Pokedex0Endpoint.Normalize(baseUrl) ?? Pokedex0Endpoint.DefaultBase;
        _http = handler == null ? SharedHttp.Value : NewHttp(handler);
    }

    private static HttpClient NewHttp(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounter/{AppInfo.VersionText} (+{AppInfo.RepoUrl})");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public string BaseUrl { get; }

    /// <summary>The resource indicator (RFC 8707) the tokens are for: the Pokédex0 API.</summary>
    public string Resource => BaseUrl + "/api";

    /// <summary>The page opened in the player's browser (their Pokédex0 session is used there).</summary>
    public string AuthorizeUrl(PendingAuthorization p)
    {
        var q = new (string, string)[]
        {
            ("client_id", ClientId),
            ("redirect_uri", p.RedirectUri),
            ("response_type", "code"),
            ("scope", Scopes),
            ("state", p.State),
            ("code_challenge", Pkce.Challenge(p.Verifier)),
            ("code_challenge_method", "S256"),
            ("resource", Resource),
        };
        return BaseUrl + "/api/auth/oauth2/authorize?" + string.Join("&", q.Select(x => x.Item1 + "=" + Uri.EscapeDataString(x.Item2)));
    }

    /// <summary>
    /// The server's OAuth metadata (RFC 8414): token and revocation endpoints and issuer. A URL is used only on the
    /// base URL's origin, else the usual address. Remembered once the server answered.
    /// </summary>
    public async Task<Pokedex0Metadata> DiscoverAsync(CancellationToken ct = default)
    {
        if (_metadata != null)
        {
            return _metadata;
        }
        var fallback = new Pokedex0Metadata(BaseUrl + "/api/auth/oauth2/token", BaseUrl + "/api/auth/oauth2/revoke", BaseUrl);
        try
        {
            using var response = await _http.GetAsync(BaseUrl + "/.well-known/oauth-authorization-server", ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    string Url(string name, string def) => Str(root, name) is { } u && Pokedex0Endpoint.SameOrigin(BaseUrl, u) ? u : def;
                    return _metadata = new Pokedex0Metadata(
                        Url("token_endpoint", fallback.TokenEndpoint),
                        Url("revocation_endpoint", fallback.RevocationEndpoint),
                        Url("issuer", fallback.Issuer).TrimEnd('/'));
                }
            }
            // No usable metadata: the usual addresses (remembered only when the server says there is none).
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound ? _metadata = fallback : fallback;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return fallback; // offline: not remembered, the metadata is read again next time
        }
    }

    /// <summary>The issuer the authorization response's "iss" must match (RFC 9207).</summary>
    public async Task<bool> IssuerMatchesAsync(string? iss, CancellationToken ct = default)
    {
        if (iss == null)
        {
            return true; // optional parameter
        }
        var expected = (await DiscoverAsync(ct)).Issuer;
        return string.Equals(iss.TrimEnd('/'), expected, StringComparison.Ordinal);
    }

    /// <summary>Revokes a refresh token (Disconnect). Best effort: the result is ignored.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        try
        {
            var endpoint = (await DiscoverAsync(ct)).RevocationEndpoint;
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = refreshToken,
                ["token_type_hint"] = "refresh_token",
                ["client_id"] = ClientId,
            });
            using var response = await _http.PostAsync(endpoint, content, ct);
        }
        catch
        {
            // offline: the player can also revoke the app on the website
        }
    }

    public Task<Pokedex0TokenResult> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct = default) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
            ["resource"] = Resource,
        }, null, ct);

    /// <summary>A new access token. Refresh tokens rotate: the answer's refresh token replaces the old one.</summary>
    public Task<Pokedex0TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["resource"] = Resource,
        }, refreshToken, ct);

    private async Task<Pokedex0TokenResult> TokenRequestAsync(Dictionary<string, string> form, string? previousRefresh, CancellationToken ct)
    {
        try
        {
            var endpoint = (await DiscoverAsync(ct)).TokenEndpoint;
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(endpoint, content, ct);
            var status = (int)response.StatusCode;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            JsonElement? body = null;
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                body = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            if (!response.IsSuccessStatusCode || body is not { ValueKind: JsonValueKind.Object } b)
            {
                // The OAuth error code only ("invalid_grant"…): its description could quote what was sent.
                var error = body is { ValueKind: JsonValueKind.Object } eb && Str(eb, "error") is { } e ? Clean(e, 60) : null;
                return new Pokedex0TokenResult(null, response.IsSuccessStatusCode ? 502 : status, error);
            }
            var access = Str(b, "access_token");
            var refresh = Str(b, "refresh_token") ?? previousRefresh;
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            {
                return new Pokedex0TokenResult(null, 502, "no_token");
            }
            var expiresIn = Int(b, "expires_in") is { } s && s > 0 ? s : 1800;
            var expires = DateTime.UtcNow.AddSeconds(Math.Min(expiresIn, 7 * 24 * 3600));
            // expires_at (Unix seconds) when given and sensible: it is what the server goes by.
            if (b.TryGetProperty("expires_at", out var ea) && ea.ValueKind == JsonValueKind.Number && ea.TryGetInt64(out var unix)
                && DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, 0, 253402300799)).UtcDateTime is var at && at > DateTime.UtcNow && at < expires.AddMinutes(5))
            {
                expires = at;
            }
            return new Pokedex0TokenResult(new Pokedex0Tokens
            {
                AccessToken = access,
                RefreshToken = refresh,
                AccessExpiresUtc = expires,
                Scope = Str(b, "scope"),
            }, status, null);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new Pokedex0TokenResult(null, 0, "offline");
        }
    }

    public Task<Pokedex0Response> GetAsync(string path, string accessToken, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, accessToken, null, ct);

    public Task<Pokedex0Response> PostJsonAsync(string path, string accessToken, object body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, accessToken, body, ct);

    private async Task<Pokedex0Response> SendAsync(HttpMethod method, string path, string accessToken, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (body != null)
            {
                request.Content = JsonContent(body);
            }
            using var response = await _http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            JsonElement? json = null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                json = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
            string? error = null;
            if (!response.IsSuccessStatusCode && json is { ValueKind: JsonValueKind.Object } e)
            {
                error = Str(e, "error") is { } msg ? Clean(msg, 200) : null;
            }
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            return new Pokedex0Response(status, json, error, retryAfter);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new Pokedex0Response(0, null, null, null);
        }
    }

    private static HttpContent JsonContent(object body)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    // ---- Answers read defensively: unknown or missing fields never break anything ------------------------------

    /// <summary>The hunts of GET /api/shiny-hunts ({"hunts": [...]} or a bare array): only those still active for the player.</summary>
    public static List<Pokedex0Hunt> ParseHunts(JsonElement? body)
    {
        var list = new List<Pokedex0Hunt>();
        if (body is not { } root)
        {
            return list;
        }
        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hunts", out var h) && h.ValueKind == JsonValueKind.Array ? h
            : default;
        if (items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var item in items.EnumerateArray())
        {
            if (ParseHunt(item) is { } hunt && hunt.Status == "active" && (hunt.Multiplayer || hunt.IsOwner))
            {
                list.Add(hunt);
            }
            if (list.Count >= 500)
            {
                break;
            }
        }
        return list;
    }

    public static Pokedex0Hunt? ParseHunt(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || NormalizeId(Str(item, "id")) is not { } id)
        {
            return null;
        }
        var multiplayer = Bool(item, "is_multiplayer") ?? false;
        // Like the website: custom odds only count when no verified method is set (missing or null), and only as a
        // whole number from 1 to 1,000,000 (isValidCustomOddsDenominator).
        var verified = item.TryGetProperty("shiny_method_effect_id", out var method) && method.ValueKind != JsonValueKind.Null;
        var odds = WholeNumber(item, "custom_odds_denominator") ?? WholeNumber(item, "customOddsDenominator");
        long? revision = item.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Number && rev.TryGetInt64(out var rv) ? rv : null;
        return new Pokedex0Hunt(
            id,
            Clean(Str(item, "name"), 120) is { Length: > 0 } name ? name : "—",
            multiplayer,
            Bool(item, "is_owner") ?? !multiplayer,
            Str(item, "viewer_effective_status") ?? Str(item, "status") ?? "",
            Int(item, "display_count") ?? Int(item, "current_count") ?? 0,
            Int(item, "viewer_participant_count"),
            Int(item, "target_count") is > 0 and var t ? t : null,
            Clean(Str(item, "hunt_method"), 80),
            Clean(Name(item, "pokemon"), 60),
            Clean(Name(item, "game"), 60),
            !verified && Pokedex0Odds.ValidDenominator(odds) ? (int)odds!.Value : null,
            verified,
            revision);
    }

    /// <summary>A JSON number that is a whole number (Number.isSafeInteger), or null: 4096.5 or "4096" are not odds.</summary>
    private static long? WholeNumber(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
        && double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) <= 9007199254740991 ? (long)d : null;

    /// <summary>GET /api/oauth/session: the account behind the token.</summary>
    public static Pokedex0Account ParseAccount(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            return new Pokedex0Account(null, null, null);
        }
        return new Pokedex0Account(Clean(Str(user, "id"), 64), Clean(Str(user, "username"), 60), Clean(Str(user, "display_name"), 60));
    }

    /// <summary>The "acknowledgement" of an increment answer (any field may be missing).</summary>
    public static Pokedex0Acknowledgement? ParseAcknowledgement(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("acknowledgement", out var a) || a.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        long? revision = a.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var rv) ? rv : null;
        return new Pokedex0Acknowledgement(Int(a, "displayCount"), Int(a, "viewerParticipantCount"), revision);
    }

    /// <summary>A hunt id as the counter keeps it (a UUID, lower case), or null.</summary>
    public static string? NormalizeId(string? id) =>
        id != null && id.Length == 36 && Guid.TryParseExact(id, "D", out var g) ? g.ToString("D") : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out var i)) return i;
            if (v.TryGetDouble(out var d) && double.IsFinite(d) && Math.Abs(d) < int.MaxValue) return (int)d;
        }
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            return s;
        }
        return null;
    }

    /// <summary>"pokemon": {"name": …} (or a plain string).</summary>
    private static string? Name(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }
        if (v.ValueKind == JsonValueKind.Object)
        {
            return Str(v, "name") ?? Str(v, "display_name");
        }
        return null;
    }

    /// <summary>Text from the network, made safe to keep: no control characters, limited length (still escaped when shown).</summary>
    public static string? Clean(string? s, int max)
    {
        if (s == null)
        {
            return null;
        }
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var c in s)
        {
            if (sb.Length >= max) break;
            sb.Append(char.IsControl(c) || c is '\u2028' or '\u2029' or '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069' ? ' ' : c);
        }
        return sb.ToString().Trim();
    }
}
