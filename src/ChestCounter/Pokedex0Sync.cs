using System.Net;

namespace ChestCounter;

public enum Pokedex0CallbackResult
{
    Connected,
    Denied,
    Invalid,
    Failed,
}

/// <summary>What one pass over the outbox ended with.</summary>
public enum Pokedex0FlushResult
{
    /// <summary>Nothing (more) to send, or sending is not possible now (not connected, paused, no hunt).</summary>
    Idle,
    /// <summary>Everything that could be sent was acknowledged.</summary>
    Sent,
    /// <summary>No answer or a temporary server error: tried again later (backoff).</summary>
    RetryLater,
    /// <summary>Stopped until the player acts (another hunt, sign in again).</summary>
    Paused,
}

/// <summary>
/// The optional Pokédex0 connection: the player connects their account in their browser (OAuth 2.1 + PKCE), picks a
/// hunt, and every match counted from then on adds its chests to it. The ledger and the outbox are on disk
/// (<see cref="Pokedex0LedgerFile"/>), the tokens encrypted apart (<see cref="Pokedex0TokenStore"/>).
/// The network is only used when connected, never on the replay scan or the window's thread: a background thread sends
/// the outbox (when something is queued, at startup, and again later with a backoff after a failure).
/// See docs/pokedex0-integration.md for the server side.
/// </summary>
public sealed class Pokedex0Sync : IDisposable
{
    public const string RevokedMessage = "Connected app access was revoked";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Pokedex0LedgerFile _file;
    private readonly Pokedex0TokenStore _tokenStore;
    private readonly Pokedex0Client _client;
    private readonly OAuthStateStore _states = new();
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly AutoResetEvent _signal = new(false);
    private Pokedex0Ledger _ledger;
    private Pokedex0Tokens? _tokens;
    private Thread? _worker;
    private volatile bool _stopped;
    private int _huntsRequested;

    // In memory only.
    private List<Pokedex0Hunt>? _hunts;
    private bool _huntsLoading;
    private bool _huntsFailed;
    private DateTime _nextAttempt = DateTime.MinValue;
    private int _failures;
    private bool _unreachable;
    private DateTime _nextHuntsRefresh = DateTime.MinValue;

    /// <summary>The linked hunt is read again this often (the team total changes when friends play).</summary>
    public static readonly TimeSpan HuntsRefreshInterval = TimeSpan.FromMinutes(5);
    private readonly HashSet<string> _endpointFlipped = new();
    private int _refreshCount;

    public Pokedex0Sync(string dataFolder, string baseUrl, HttpMessageHandler? handler = null, Func<DateTime>? clock = null)
    {
        _file = new Pokedex0LedgerFile(dataFolder);
        _tokenStore = new Pokedex0TokenStore(dataFolder);
        _client = new Pokedex0Client(baseUrl, handler);
        _now = clock ?? (() => DateTime.UtcNow);
        _ledger = _file.Load();
        _tokens = _tokenStore.Load();
    }

    /// <summary>A line for the activity log (written by the main loop). Never given a token, a code or a verifier.</summary>
    public Action<string, ConsoleColor>? Log { get; set; }

    /// <summary>Something the window shows changed.</summary>
    public Action? Changed { get; set; }

    public Pokedex0Client Client => _client;

    public bool Connected { get { lock (_gate) return _tokens != null; } }

    /// <summary>Number of refresh requests sent (tests: refreshes are single-flight).</summary>
    public int RefreshCount => Volatile.Read(ref _refreshCount);

    /// <summary>Reads the ledger under the lock (tests, and the window's state).</summary>
    public T Read<T>(Func<Pokedex0Ledger, T> read)
    {
        lock (_gate)
        {
            return read(_ledger);
        }
    }

    // ---- Connecting ----------------------------------------------------------------------------------------------

    /// <summary>The page to open in the player's browser; Pokédex0 sends them back to the counter's local server.</summary>
    public string BeginConnect(int port)
    {
        var pending = _states.Create($"http://127.0.0.1:{port}/oauth/callback", _now());
        Changed?.Invoke();
        return _client.AuthorizeUrl(pending);
    }

    public bool WaitingForBrowser => !Connected && _states.HasPending(_now());

    public void CancelConnect()
    {
        _states.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// GET /oauth/callback (on a thread of the local server): checks the state (once, within 10 minutes) and the
    /// issuer, then exchanges the code. Nothing of the request is ever written anywhere.
    /// </summary>
    public Pokedex0CallbackResult HandleCallback(IReadOnlyDictionary<string, string> query)
    {
        var pending = _states.TryConsume(query.GetValueOrDefault("state"), _now());
        if (pending == null)
        {
            return Pokedex0CallbackResult.Invalid; // not started by this counter, already used, or too old
        }
        if (!_client.IssuerMatchesAsync(query.GetValueOrDefault("iss")).GetAwaiter().GetResult())
        {
            Say(L.T("Pokédex0 : réponse d'un autre serveur, connexion refusée.", "Pokédex0: answer from another server, connection refused.", "Pokédex0: respuesta de otro servidor, conexión rechazada."), ConsoleColor.Red);
            return Pokedex0CallbackResult.Failed;
        }
        if (query.TryGetValue("error", out var error))
        {
            if (error == "access_denied")
            {
                Say(L.T("Pokédex0 : connexion annulée.", "Pokédex0: connection cancelled.", "Pokédex0: conexión cancelada."), ConsoleColor.DarkYellow);
                return Pokedex0CallbackResult.Denied;
            }
            Say(L.T($"Pokédex0 : la connexion a échoué ({Pokedex0Client.Clean(error, 40)}).", $"Pokédex0: the connection failed ({Pokedex0Client.Clean(error, 40)}).", $"Pokédex0: la conexión falló ({Pokedex0Client.Clean(error, 40)})."), ConsoleColor.Red);
            return Pokedex0CallbackResult.Failed;
        }
        if (!query.TryGetValue("code", out var code) || code.Length is 0 or > 4096)
        {
            return Pokedex0CallbackResult.Failed;
        }

        var result = _client.ExchangeCodeAsync(code, pending.Verifier, pending.RedirectUri).GetAwaiter().GetResult();
        if (result.Tokens is not { } tokens)
        {
            Say(L.T($"Pokédex0 : la connexion a échoué ({FailureText(result.Status, result.Error)}). Réessaie.", $"Pokédex0: the connection failed ({FailureText(result.Status, result.Error)}). Try again.", $"Pokédex0: la conexión falló ({FailureText(result.Status, result.Error)}). Inténtalo de nuevo."), ConsoleColor.Red);
            return Pokedex0CallbackResult.Failed;
        }
        StoreTokens(tokens, expected: null, force: true);

        var session = _client.GetAsync("/api/oauth/session", tokens.AccessToken).GetAwaiter().GetResult();
        var account = session.Ok ? Pokedex0Client.ParseAccount(session.Body) : new Pokedex0Account(null, null, null);
        string? who;
        lock (_gate)
        {
            if (account.Id != null && _ledger.UserId != null && account.Id != _ledger.UserId)
            {
                // Another Pokédex0 account: nothing of the previous one is sent to it.
                var dropped = Pokedex0Book.DropOutbox(_ledger, _ => true);
                _ledger = new Pokedex0Ledger();
                if (dropped != 0)
                {
                    Say(L.T($"Pokédex0 : autre compte, {L.Count(dropped, "coffre en attente abandonné", "coffres en attente abandonnés", "", "", "", "")}.",
                        $"Pokédex0: another account, {L.Count(dropped, "", "", "waiting chest dropped", "waiting chests dropped", "", "")}.",
                        $"Pokédex0: otra cuenta, {L.Count(dropped, "", "", "", "", "cofre en espera descartado", "cofres en espera descartados")}."), ConsoleColor.DarkYellow);
                }
            }
            if (account.Id != null)
            {
                _ledger.UserId = account.Id;
                _ledger.Username = account.Username;
                _ledger.DisplayName = account.DisplayName;
            }
            if (_ledger.Paused == "auth")
            {
                _ledger.Paused = null;
                _ledger.PausedDetail = null;
            }
            _failures = 0;
            _nextAttempt = DateTime.MinValue;
            _unreachable = false;
            SaveLedger();
            who = AccountName();
        }
        Say(who != null
            ? L.T($"Pokédex0 : connecté en tant que {who}.", $"Pokédex0: connected as {who}.", $"Pokédex0: conectado como {who}.")
            : L.T("Pokédex0 : connecté.", "Pokédex0: connected.", "Pokédex0: conectado."), ConsoleColor.Green);
        RequestHunts();
        return Pokedex0CallbackResult.Connected;
    }

    /// <summary>Forgets the connection: tokens (revoked on Pokédex0 too, best effort), hunt, ledger and outbox. Returns the chests that were waiting.</summary>
    public int Disconnect()
    {
        Pokedex0Tokens? tokens;
        int dropped;
        lock (_gate)
        {
            tokens = _tokens;
            _tokens = null;
            _tokenStore.Delete();
            dropped = _ledger.Outbox.Sum(o => o.Increment);
            _ledger = new Pokedex0Ledger();
            _file.Delete();
            _hunts = null;
            _failures = 0;
            _unreachable = false;
        }
        _states.Clear();
        if (tokens != null)
        {
            _ = Task.Run(() => RevokeAfterRefreshAsync(tokens.RefreshToken));
        }
        Changed?.Invoke();
        return dropped;
    }

    private async Task RevokeAfterRefreshAsync(string refreshToken)
    {
        // A refresh in flight finishes first (it revokes the token it gets itself, see Refresh).
        await _refreshLock.WaitAsync();
        _refreshLock.Release();
        await _client.RevokeAsync(refreshToken);
    }

    // ---- Tokens: refreshes are single-flight (a rotated refresh token presented again revokes the whole grant) ----

    /// <summary>
    /// A usable access token. <paramref name="rejected"/>: the one a 401 answered (refreshed unless another caller
    /// already did). Transient = no answer from Pokédex0 (try later with the same refresh token).
    /// </summary>
    private (string? Access, bool Transient) AccessToken(string? rejected)
    {
        Pokedex0Tokens? t;
        lock (_gate) t = _tokens;
        if (t == null)
        {
            return (null, false);
        }
        if (rejected == null && t.AccessExpiresUtc - _now() > RefreshMargin)
        {
            return (t.AccessToken, false);
        }
        _refreshLock.Wait();
        try
        {
            lock (_gate) t = _tokens;
            if (t == null)
            {
                return (null, false);
            }
            var stillGood = rejected != null ? t.AccessToken != rejected : t.AccessExpiresUtc - _now() > RefreshMargin;
            if (stillGood)
            {
                return (t.AccessToken, false); // refreshed by another caller while this one waited
            }
            Interlocked.Increment(ref _refreshCount);
            var result = _client.RefreshAsync(t.RefreshToken).GetAwaiter().GetResult();
            if (result.Tokens is { } fresh)
            {
                if (!StoreTokens(fresh, expected: t, force: false))
                {
                    _ = _client.RevokeAsync(fresh.RefreshToken); // disconnected meanwhile
                    return (null, false);
                }
                return (fresh.AccessToken, false);
            }
            if (result.Rejected)
            {
                // invalid_grant: the grant is gone (revoked on the website, or expired). Never retried with this token.
                SetSignedOut();
                return (null, false);
            }
            return (null, true);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Keeps new tokens: on disk first (encrypted), then in memory. False when the connection changed meanwhile.</summary>
    private bool StoreTokens(Pokedex0Tokens tokens, Pokedex0Tokens? expected, bool force)
    {
        lock (_gate)
        {
            if (!force && !ReferenceEquals(_tokens, expected))
            {
                return false;
            }
            try
            {
                _tokenStore.Save(tokens);
            }
            catch (Exception ex)
            {
                // Kept in memory anyway: after a rotation the new refresh token is the only valid one.
                ErrorLog.Write("Pokédex0: tokens not saved", ex.GetType().Name + ": " + ex.Message);
            }
            _tokens = tokens;
            return true;
        }
    }

    /// <summary>The tokens no longer work: forgotten, the hunt and the outbox are kept for when the player signs in again.</summary>
    private void SetSignedOut()
    {
        int waiting;
        lock (_gate)
        {
            if (_tokens == null)
            {
                return;
            }
            _tokens = null;
            _tokenStore.Delete();
            _ledger.Paused = "auth";
            _ledger.PausedDetail = null;
            waiting = Pokedex0Book.PendingChests(_ledger);
            SaveLedger();
        }
        Say(L.T($"Pokédex0 : déconnecté (accès retiré ou expiré). Reconnecte-toi dans Réglages → Pokédex0 ; {L.Count(waiting, "coffre attend", "coffres attendent", "", "", "", "")}.",
            $"Pokédex0: disconnected (access revoked or expired). Sign in again in Settings → Pokédex0; {L.Count(waiting, "", "", "chest is", "chests are", "", "")} waiting.",
            $"Pokédex0: desconectado (acceso retirado o caducado). Vuelve a conectarte en Ajustes → Pokédex0; {L.Count(waiting, "", "", "", "", "cofre espera", "cofres esperan")}."), ConsoleColor.DarkYellow);
    }

    /// <summary>
    /// An API call with the access token: refreshed before when it expires within a minute, and once more after a
    /// 401 (then the call is made again, same body). Status -1 = signed out.
    /// </summary>
    private Pokedex0Response Authorized(Func<string, Task<Pokedex0Response>> call)
    {
        var (access, transient) = AccessToken(null);
        if (access == null)
        {
            return new Pokedex0Response(transient ? 0 : -1, null, null, null);
        }
        var response = call(access).GetAwaiter().GetResult();
        if (response.Status == 401)
        {
            (access, transient) = AccessToken(access);
            if (access == null)
            {
                return new Pokedex0Response(transient ? 0 : -1, null, null, null);
            }
            response = call(access).GetAwaiter().GetResult();
            if (response.Status == 401)
            {
                SetSignedOut();
                return new Pokedex0Response(-1, null, null, null);
            }
        }
        if (response.Status == 403 && response.Error == RevokedMessage)
        {
            SetSignedOut(); // revoked in Connected Apps: the outbox waits for the player to connect again
            return new Pokedex0Response(-1, null, response.Error, null);
        }
        return response;
    }

    // ---- Hunts ---------------------------------------------------------------------------------------------------

    /// <summary>Reads the list of hunts again, in the background.</summary>
    public void RequestHunts()
    {
        Interlocked.Exchange(ref _huntsRequested, 1);
        lock (_gate) _huntsLoading = _tokens != null;
        _signal.Set();
        Changed?.Invoke();
    }

    /// <summary>GET /api/shiny-hunts now (worker thread, or tests). True when read.</summary>
    public bool LoadHuntsNow()
    {
        lock (_gate)
        {
            if (_tokens == null)
            {
                _huntsLoading = false;
                return false;
            }
            _huntsLoading = true;
        }
        var response = Authorized(a => _client.GetAsync("/api/shiny-hunts", a));
        lock (_gate)
        {
            _huntsLoading = false;
            _huntsFailed = !response.Ok;
            // Next periodic read in 5 minutes, after a success or a failure alike (never a tight loop).
            _nextHuntsRefresh = _now() + HuntsRefreshInterval;
            if (response.Ok)
            {
                _hunts = Pokedex0Client.ParseHunts(response.Body);
                // The linked hunt renamed or changed on the website, and its counts (friends play too).
                if (_ledger.Hunt is { } h && _hunts.FirstOrDefault(x => x.Id == _ledger.HuntId) is { } fresh)
                {
                    TakeHunt(h, fresh);
                    SaveLedger();
                }
            }
        }
        Changed?.Invoke();
        return response.Ok;
    }

    /// <summary>What the list says about a hunt: name, kind, odds, and its counts (unless older than those kept).</summary>
    private static void TakeHunt(HuntLedger h, Pokedex0Hunt listed)
    {
        h.Name = listed.Name;
        h.Multiplayer = listed.Multiplayer;
        h.OddsDenominator = listed.OddsDenominator;
        h.TakeCounts(listed.OwnCount, listed.DisplayCount, listed.Revision);
    }

    /// <summary>
    /// The overlay's Pokédex0 chance, or null: not connected, no hunt, no valid custom odds (or a verified game method),
    /// or no count read from Pokédex0 yet.
    /// </summary>
    public Pokedex0OddsSnapshot? OddsSnapshot()
    {
        lock (_gate)
        {
            return _tokens == null ? null : Pokedex0Book.OddsSnapshot(_ledger);
        }
    }

    public IReadOnlyList<Pokedex0Hunt> Hunts { get { lock (_gate) return _hunts?.ToList() ?? new List<Pokedex0Hunt>(); } }

    /// <summary>
    /// Links a hunt of the list (main loop: reads the matches). The matches already counted are its baseline, not sent.
    /// Returns an error to show, or null; <paramref name="dropped"/> = chests that were waiting for the previous hunt.
    /// </summary>
    public string? SelectHunt(string? huntId, TrackerData data, Config config, out int dropped)
    {
        dropped = 0;
        var id = Pokedex0Client.NormalizeId(huntId);
        lock (_gate)
        {
            if (_tokens == null)
            {
                return L.T("Connecte-toi d'abord à Pokédex0.", "Connect to Pokédex0 first.", "Conéctate primero a Pokédex0.");
            }
            var hunt = id == null ? null : _hunts?.FirstOrDefault(h => h.Id == id);
            if (hunt == null)
            {
                return L.T("Cette chasse n'est plus dans la liste : actualise-la.", "This hunt is no longer in the list: refresh it.", "Esta búsqueda ya no está en la lista: actualízala.");
            }
            dropped = Pokedex0Book.Link(_ledger, hunt.Id, hunt.Name, hunt.Multiplayer, data, Profiles.Current(data, config)?.Key, _now());
            TakeHunt(_ledger.Hunt!, hunt);
            _failures = 0;
            _nextAttempt = DateTime.MinValue;
            _unreachable = false;
            SaveLedger();
        }
        _signal.Set();
        Changed?.Invoke();
        return null;
    }

    // ---- Matches -------------------------------------------------------------------------------------------------

    /// <summary>A match was added or re-analyzed (main loop). Queues its new chests for the hunt, if any.</summary>
    public void OnMatch(MatchRecord m)
    {
        PendingOperation? op;
        lock (_gate)
        {
            op = Pokedex0Book.Record(_ledger, m, _now());
            if (op != null)
            {
                SaveLedger();
            }
        }
        if (op != null)
        {
            _signal.Set();
            Changed?.Invoke();
        }
    }

    /// <summary>Every match against the ledger (startup): a match saved right before a crash is caught up.</summary>
    public void Reconcile(TrackerData data)
    {
        bool queued;
        lock (_gate)
        {
            queued = Pokedex0Book.Reconcile(_ledger, data, _now()).Count > 0;
            if (queued)
            {
                SaveLedger();
            }
        }
        if (queued)
        {
            _signal.Set();
        }
    }

    public (int Chests, int Matches) InitialSyncPreview() { lock (_gate) return Pokedex0Book.InitialSyncPreview(_ledger); }

    /// <summary>
    /// Queues the one-time sync, if it still adds <paramref name="expectedChests"/> (the figure the player confirmed).
    /// Returns an error to show, or null.
    /// </summary>
    public string? StartInitialSync(int expectedChests)
    {
        lock (_gate)
        {
            var (chests, _) = Pokedex0Book.InitialSyncPreview(_ledger);
            if (chests <= 0)
            {
                return L.T("Rien à synchroniser.", "Nothing to sync.", "Nada que sincronizar.");
            }
            if (chests != expectedChests)
            {
                return L.T("Le nombre a changé entre-temps : vérifie-le et confirme de nouveau.", "The number changed meanwhile: check it and confirm again.", "El número cambió mientras tanto: compruébalo y confirma de nuevo.");
            }
            if (Pokedex0Book.QueueInitialSync(_ledger, _now()) == null)
            {
                return L.T("Trop de coffres pour une seule synchronisation.", "Too many chests for one sync.", "Demasiados cofres para una sola sincronización.");
            }
            SaveLedger();
        }
        _signal.Set();
        Changed?.Invoke();
        return null;
    }

    /// <summary>"Retry": sending starts again now (after the hunt was reactivated on the website, or a long outage).</summary>
    public void RetryNow()
    {
        lock (_gate)
        {
            if (_ledger.Paused != "auth")
            {
                _ledger.Paused = null;
                _ledger.PausedDetail = null;
                SaveLedger();
            }
            _failures = 0;
            _nextAttempt = DateTime.MinValue;
        }
        RequestHunts();
    }

    // ---- Sending -------------------------------------------------------------------------------------------------

    public void Start()
    {
        if (_worker != null)
        {
            return;
        }
        if (Connected)
        {
            Interlocked.Exchange(ref _huntsRequested, 1); // the hunts at startup (then the outbox)
        }
        _worker = new Thread(Loop) { IsBackground = true, Name = "Pokedex0", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>The worker looks at its schedule again now (tests: after moving the clock).</summary>
    public void Wake() => _signal.Set();

    public void Stop()
    {
        _stopped = true;
        _signal.Set();
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (!_stopped)
        {
            _signal.WaitOne(NextWait());
            if (_stopped)
            {
                break;
            }
            try
            {
                if (Interlocked.Exchange(ref _huntsRequested, 0) == 1 || DueForHuntsRefresh())
                {
                    LoadHuntsNow();
                }
                if (DueForSending())
                {
                    FlushNow();
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Pokédex0 sync", ex.GetType().Name + ": " + ex.Message);
                lock (_gate) Backoff(null);
            }
        }
    }

    /// <summary>How long the worker sleeps: until the next try when something waits, else until it is woken up.</summary>
    private TimeSpan NextWait()
    {
        lock (_gate)
        {
            if (_tokens == null)
            {
                return Timeout.InfiniteTimeSpan; // not connected: no network at all
            }
            if (Volatile.Read(ref _huntsRequested) == 1)
            {
                return TimeSpan.Zero;
            }
            var wait = Timeout.InfiniteTimeSpan;
            if (_ledger.Paused == null && Pokedex0Book.NextBatch(_ledger, 1).Count > 0)
            {
                wait = _nextAttempt - _now();
                wait = wait <= TimeSpan.Zero ? TimeSpan.Zero : wait > MaxBackoff ? MaxBackoff : wait;
            }
            if (_ledger.Linked)
            {
                // The linked hunt is read again every 5 minutes (its team total), only while connected.
                var refresh = _nextHuntsRefresh - _now();
                refresh = refresh <= TimeSpan.Zero ? TimeSpan.Zero : refresh;
                wait = wait == Timeout.InfiniteTimeSpan || refresh < wait ? refresh : wait;
            }
            return wait;
        }
    }

    private bool DueForHuntsRefresh()
    {
        lock (_gate) return _tokens != null && _ledger.Linked && _now() >= _nextHuntsRefresh;
    }

    /// <summary>When the linked hunt is read again (tests).</summary>
    public DateTime NextHuntsRefreshUtc { get { lock (_gate) return _nextHuntsRefresh; } }

    private bool DueForSending()
    {
        lock (_gate) return _tokens != null && _ledger.Paused == null && _now() >= _nextAttempt && Pokedex0Book.NextBatch(_ledger, 1).Count > 0;
    }

    /// <summary>Sends the outbox now, batch after batch (worker thread, or tests). Ignores the backoff delay.</summary>
    public Pokedex0FlushResult FlushNow()
    {
        var any = false;
        while (true)
        {
            List<PendingOperation> batch;
            string huntId;
            bool multiplayer;
            lock (_gate)
            {
                if (_tokens == null || _ledger.Paused != null || _ledger.Hunt is not { } h)
                {
                    return any ? Pokedex0FlushResult.Sent : Pokedex0FlushResult.Idle;
                }
                batch = Pokedex0Book.NextBatch(_ledger);
                huntId = _ledger.HuntId!;
                multiplayer = h.Multiplayer;
            }
            if (batch.Count == 0)
            {
                return any ? Pokedex0FlushResult.Sent : Pokedex0FlushResult.Idle;
            }
            var result = SendBatch(batch, huntId, multiplayer, isolate: true);
            Changed?.Invoke();
            if (result != Pokedex0FlushResult.Sent)
            {
                return result;
            }
            any = true;
        }
    }

    private static string IncrementPath(string huntId, bool multiplayer) =>
        multiplayer ? $"/api/shiny-hunts/{huntId}/participants/increment" : $"/api/shiny-hunts/{huntId}/increment";

    /// <summary>One request (all-or-nothing on the server), and what its answer means for the outbox.</summary>
    private Pokedex0FlushResult SendBatch(List<PendingOperation> batch, string huntId, bool multiplayer, bool isolate)
    {
        var body = new { operations = batch.Select(o => new { operationId = o.OperationId, increment = o.Increment }).ToList() };
        var r = Authorized(a => _client.PostJsonAsync(IncrementPath(huntId, multiplayer), a, body));

        if (r.Ok)
        {
            Acknowledged(batch, huntId, r);
            return Pokedex0FlushResult.Sent;
        }
        if (r.Status == -1)
        {
            return Pokedex0FlushResult.Paused; // signed out: the outbox waits
        }
        if (r.Transient)
        {
            // No answer, 5xx, 503 "Retrying is safe": the same body later (same ids: never counted twice).
            lock (_gate) Backoff(r.RetryAfter);
            return Pokedex0FlushResult.RetryLater;
        }

        var error = r.Error ?? "";
        if (r.Status == 400 && (error.Contains("participant counter endpoint", StringComparison.OrdinalIgnoreCase) || error.Contains("not multiplayer", StringComparison.OrdinalIgnoreCase)))
        {
            // Solo / multiplayer the other way round: the other endpoint, once.
            bool retry;
            lock (_gate)
            {
                retry = _endpointFlipped.Add(huntId) && _ledger.Hunt is { } h && _ledger.HuntId == huntId;
                if (retry)
                {
                    _ledger.Hunt!.Multiplayer = !multiplayer;
                    SaveLedger();
                }
            }
            if (retry)
            {
                Interlocked.Exchange(ref _huntsRequested, 1);
                return SendBatch(batch, huntId, !multiplayer, isolate);
            }
        }
        var conflict = r.Status == 409 && error.Contains("operation ID", StringComparison.OrdinalIgnoreCase);
        if (r.Status == 400 || conflict || (r.Status is >= 400 and < 500 and not (403 or 404 or 409)))
        {
            // Permanent for these operations. Several in the request: one at a time, so that only the bad one is dropped.
            if (isolate && batch.Count > 1)
            {
                foreach (var op in batch)
                {
                    var one = SendBatch(new List<PendingOperation> { op }, huntId, multiplayer, isolate: false);
                    if (one is Pokedex0FlushResult.RetryLater or Pokedex0FlushResult.Paused)
                    {
                        return one;
                    }
                }
                return Pokedex0FlushResult.Sent;
            }
            Drop(batch, conflict, r.Status, error);
            return Pokedex0FlushResult.Sent;
        }

        // The hunt itself: deleted (404), not accessible (403), completed or abandoned (409). The outbox is kept.
        var reason = r.Status switch { 404 => "gone", 403 => "forbidden", _ => "inactive" };
        int waiting;
        string? name;
        lock (_gate)
        {
            _ledger.Paused = reason;
            _ledger.PausedDetail = Pokedex0Client.Clean(error, 200);
            waiting = Pokedex0Book.PendingChests(_ledger);
            name = _ledger.Hunt?.Name;
            SaveLedger();
        }
        Say(PausedText(reason, name, waiting), ConsoleColor.DarkYellow);
        Interlocked.Exchange(ref _huntsRequested, 1);
        _signal.Set();
        return Pokedex0FlushResult.Paused;
    }

    private void Acknowledged(List<PendingOperation> batch, string huntId, Pokedex0Response r)
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (r.Body is { ValueKind: System.Text.Json.JsonValueKind.Object } b && b.TryGetProperty("appliedOperationIds", out var ids) && ids.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() is { } s)
                {
                    applied.Add(s);
                }
            }
        }
        var added = batch.Where(o => applied.Contains(o.OperationId)).Sum(o => o.Increment);
        string? name;
        lock (_gate)
        {
            Pokedex0Book.Acknowledge(_ledger, batch, _now());
            _failures = 0;
            _nextAttempt = DateTime.MinValue;
            _unreachable = false;
            // Same step as the outbox removal above: the counts now include these chests, and they are no longer
            // pending, so the overlay's chance never counts them twice nor drops for a moment.
            if (Pokedex0Client.ParseAcknowledgement(r.Body) is { } ack && _ledger.Hunts.TryGetValue(huntId, out var acked))
            {
                var multiplayer = acked.Multiplayer;
                acked.TakeCounts(ack.ViewerCount ?? (multiplayer ? null : ack.DisplayCount), ack.DisplayCount, ack.Revision);
            }
            name = _ledger.Hunts.GetValueOrDefault(huntId)?.Name;
            SaveLedger();
        }
        if (added != 0)
        {
            var n = L.Count(Math.Abs(added), "coffre", "chest", "cofre");
            var sign = added > 0 ? "+" : "−";
            Say(L.T($"Pokédex0 : {sign}{n} pour « {name} ».", $"Pokédex0: {sign}{n} for \"{name}\".", $"Pokédex0: {sign}{n} para «{name}»."), ConsoleColor.Green);
        }
    }

    /// <summary>A permanent refusal (400, or an id already used with other data): the operation is dropped and logged.</summary>
    private void Drop(List<PendingOperation> batch, bool conflict, int status, string error)
    {
        var ids = batch.Select(o => o.OperationId).ToHashSet();
        lock (_gate)
        {
            // Never sent again, and the ledger keeps it as accounted for: the same refused correction is not made
            // again at the next start. A conflict on the one-time sync means Pokédex0 already has one: done.
            foreach (var op in batch.Where(o => o.Kind == PendingOperation.InitialSyncKind))
            {
                if (_ledger.Hunts.TryGetValue(op.HuntId, out var h))
                {
                    if (conflict) h.InitialSyncDone = true; else h.InitialSyncChests = null;
                }
            }
            _ledger.Outbox.RemoveAll(o => ids.Contains(o.OperationId));
            SaveLedger();
        }
        var chests = batch.Sum(o => o.Increment);
        var detail = Pokedex0Client.Clean(error, 160) is { Length: > 0 } d ? d : FailureText(status, null);
        Say(L.T($"Pokédex0 : refusé, {L.Count(chests, "coffre", "chest", "cofre")} non envoyé(s) ({detail}).",
            $"Pokédex0: refused, {L.Count(chests, "coffre", "chest", "cofre")} not sent ({detail}).",
            $"Pokédex0: rechazado, {L.Count(chests, "coffre", "chest", "cofre")} sin enviar ({detail})."), ConsoleColor.Red);
    }

    /// <summary>Next try after a failure: Retry-After when the server gives one, else 15 s, 30 s, 1, 2, then 5 minutes.</summary>
    private void Backoff(TimeSpan? retryAfter)
    {
        _failures++;
        _unreachable = true;
        var delay = _failures switch { 1 => TimeSpan.FromSeconds(15), 2 => TimeSpan.FromSeconds(30), 3 => TimeSpan.FromMinutes(1), 4 => TimeSpan.FromMinutes(2), _ => MaxBackoff };
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            var asked = ra > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : ra;
            delay = _failures <= 3 ? asked : (asked > delay ? asked : delay); // a short Retry-After, a few times only
        }
        _nextAttempt = _now() + delay;
    }

    /// <summary>When the next try is (tests).</summary>
    public DateTime NextAttemptUtc { get { lock (_gate) return _nextAttempt; } }

    // ---- Window --------------------------------------------------------------------------------------------------

    /// <summary>The Pokédex0 part of the window's state. No token, code, verifier or state in it.</summary>
    public object GuiState(TrackerData data)
    {
        lock (_gate)
        {
            var connected = _tokens != null;
            var h = _ledger.Hunt;
            var pending = Pokedex0Book.PendingChests(_ledger);
            var (initChests, initMatches) = Pokedex0Book.InitialSyncPreview(_ledger);
            var listed = h == null ? null : _hunts?.FirstOrDefault(x => x.Id == _ledger.HuntId);
            var (kind, text) = Status(connected, h, pending);
            var profileName = h?.ProfileKey == null ? null : Profiles.List(data).FirstOrDefault(p => p.Key == h.ProfileKey)?.Name;
            return new
            {
                connected,
                reconnect = !connected && _ledger.Paused == "auth",
                waiting = !connected && _states.HasPending(_now()),
                user = AccountName(),
                site = _client.BaseUrl,
                hunts = (_hunts ?? new()).Select(x => new { id = x.Id, label = HuntLabel(x) }).ToList(),
                huntsLoaded = _hunts != null,
                huntsLoading = _huntsLoading,
                huntsFailed = _huntsFailed && _hunts == null,
                hunt = h == null ? null : new
                {
                    id = _ledger.HuntId,
                    name = h.Name,
                    listed = listed != null,
                    details = HuntDetails(listed, h),
                    profile = profileName,
                },
                pending,
                pendingText = L.Count(pending, "coffre", "chest", "cofre"),
                initial = initChests > 0 ? new
                {
                    chests = initChests,
                    chestsText = L.N(initChests),
                    matchesText = L.Count(initMatches, "partie", "parties", "match", "matches", "partida", "partidas"),
                } : null,
                initialQueued = h?.InitialSyncChests != null && !h.InitialSyncDone,
                initialDone = h?.InitialSyncDone == true,
                paused = _ledger.Paused,
                status = new { kind, text },
                home = connected || _ledger.Paused == "auth" ? (h != null ? $"Pokédex0 · {h.Name} · {text}" : $"Pokédex0 · {text}") : null,
            };
        }
    }

    private (string Kind, string Text) Status(bool connected, HuntLedger? h, int pending)
    {
        if (!connected)
        {
            return _ledger.Paused == "auth"
                ? ("warn", L.T($"Déconnecté : reconnecte-toi ({L.Count(pending, "coffre en attente", "coffres en attente", "", "", "", "")}).",
                    $"Signed out: sign in again ({L.Count(pending, "", "", "chest waiting", "chests waiting", "", "")}).",
                    $"Desconectado: vuelve a conectarte ({L.Count(pending, "", "", "", "", "cofre en espera", "cofres en espera")})."))
                : ("", "");
        }
        if (h == null)
        {
            return ("warn", L.T("Choisis une chasse.", "Choose a hunt.", "Elige una búsqueda."));
        }
        if (_ledger.Paused is { } reason)
        {
            return ("err", PausedText(reason, h.Name, pending));
        }
        if (_unreachable && pending != 0)
        {
            var at = L.Clock(L.Shown(_nextAttempt));
            return ("warn", L.T($"Pokédex0 injoignable : {L.Count(pending, "coffre en attente", "coffres en attente", "", "", "", "")}, nouvel essai à {at}.",
                $"Pokédex0 cannot be reached: {L.Count(pending, "", "", "chest waiting", "chests waiting", "", "")}, next try at {at}.",
                $"No se puede acceder a Pokédex0: {L.Count(pending, "", "", "", "", "cofre en espera", "cofres en espera")}, nuevo intento a las {at}."));
        }
        if (pending != 0)
        {
            return ("busy", L.T($"Envoi de {L.Count(pending, "coffre", "chest", "cofre")}…", $"Sending {L.Count(pending, "coffre", "chest", "cofre")}…", $"Enviando {L.Count(pending, "coffre", "chest", "cofre")}…"));
        }
        if (h.LastSentUtc is { } last)
        {
            var sign = h.LastSentChests >= 0 ? "+" : "−";
            var when = L.DateTimeShort(L.Shown(last));
            return ("ok", L.T($"Dernier envoi : {sign}{L.Count(Math.Abs(h.LastSentChests), "coffre", "chest", "cofre")} ({when})",
                $"Last sent: {sign}{L.Count(Math.Abs(h.LastSentChests), "coffre", "chest", "cofre")} ({when})",
                $"Último envío: {sign}{L.Count(Math.Abs(h.LastSentChests), "coffre", "chest", "cofre")} ({when})"));
        }
        return ("ok", L.T("Prêt : tes prochaines parties y seront ajoutées.", "Ready: your next matches will be added to it.", "Listo: tus próximas partidas se añadirán."));
    }

    private static string PausedText(string reason, string? hunt, int pending)
    {
        var waiting = L.Count(pending, "coffre en attente", "coffres en attente", "chest waiting", "chests waiting", "cofre en espera", "cofres en espera");
        return reason switch
        {
            "gone" => L.T($"La chasse « {hunt} » a été supprimée sur Pokédex0 : choisis-en une autre ({waiting}).",
                $"The hunt \"{hunt}\" was deleted on Pokédex0: choose another one ({waiting}).",
                $"La búsqueda «{hunt}» se borró en Pokédex0: elige otra ({waiting})."),
            "forbidden" => L.T($"Pokédex0 refuse l'accès à la chasse « {hunt} » : choisis-en une autre ({waiting}).",
                $"Pokédex0 refuses access to the hunt \"{hunt}\": choose another one ({waiting}).",
                $"Pokédex0 rechaza el acceso a la búsqueda «{hunt}»: elige otra ({waiting})."),
            "auth" => L.T($"Déconnecté : reconnecte-toi ({waiting}).", $"Signed out: sign in again ({waiting}).", $"Desconectado: vuelve a conectarte ({waiting})."),
            _ => L.T($"La chasse « {hunt} » n'est plus active (terminée ou abandonnée) : envoi en pause. Choisis-en une autre, ou réactive-la sur Pokédex0 puis « Réessayer » ({waiting}).",
                $"The hunt \"{hunt}\" is no longer active (completed or abandoned): sending is paused. Choose another one, or reactivate it on Pokédex0 and press \"Retry\" ({waiting}).",
                $"La búsqueda «{hunt}» ya no está activa (completada o abandonada): envío en pausa. Elige otra, o reactívala en Pokédex0 y pulsa «Reintentar» ({waiting})."),
        };
    }

    private static string HuntLabel(Pokedex0Hunt h)
    {
        var parts = new List<string> { h.Name };
        parts.Add(h.Multiplayer
            ? L.T($"multijoueur · toi {L.N(h.MyCount ?? 0)} / {L.N(h.DisplayCount)}", $"multiplayer · you {L.N(h.MyCount ?? 0)} / {L.N(h.DisplayCount)}", $"multijugador · tú {L.N(h.MyCount ?? 0)} / {L.N(h.DisplayCount)}")
            : L.T($"solo · {L.N(h.DisplayCount)}", $"solo · {L.N(h.DisplayCount)}", $"individual · {L.N(h.DisplayCount)}"));
        if (h.OddsDenominator is { } odds)
        {
            parts.Add($"1/{L.N(odds)}");
        }
        return string.Join(" · ", parts);
    }

    private static string HuntDetails(Pokedex0Hunt? listed, HuntLedger h)
    {
        var total = h.ServerTotal ?? listed?.DisplayCount;
        var mine = h.ServerOwn ?? listed?.MyCount;
        var parts = new List<string>();
        parts.Add(h.Multiplayer ? L.T("multijoueur", "multiplayer", "multijugador") : L.T("solo", "solo", "individual"));
        if (h.Multiplayer && mine != null)
        {
            parts.Add(L.T($"toi {L.N(mine.Value)}", $"you {L.N(mine.Value)}", $"tú {L.N(mine.Value)}"));
        }
        if (total != null)
        {
            parts.Add(L.T($"total {L.N(total.Value)}", $"total {L.N(total.Value)}", $"total {L.N(total.Value)}"));
        }
        if (listed?.TargetCount is { } target)
        {
            parts.Add(L.T($"objectif {L.N(target)}", $"target {L.N(target)}", $"objetivo {L.N(target)}"));
        }
        if (listed?.OddsDenominator is { } odds)
        {
            parts.Add(L.T($"chances 1/{L.N(odds)}", $"odds 1/{L.N(odds)}", $"probabilidad 1/{L.N(odds)}"));
        }
        if (listed?.Method is { Length: > 0 } method)
        {
            parts.Add(method);
        }
        if (h.SentChests != 0)
        {
            parts.Add(L.T($"{L.N(h.SentChests)} envoyés par le compteur", $"{L.N(h.SentChests)} sent by the counter", $"{L.N(h.SentChests)} enviados por el contador"));
        }
        return string.Join(" · ", parts);
    }

    private string? AccountName()
    {
        var display = _ledger.DisplayName;
        var user = _ledger.Username;
        if (display is { Length: > 0 } && user is { Length: > 0 } && display != user)
        {
            return $"{display} (@{user})";
        }
        return display is { Length: > 0 } ? display : user is { Length: > 0 } ? "@" + user : null;
    }

    private static string FailureText(int status, string? error) => status == 0
        ? L.T("pas de réponse", "no answer", "sin respuesta")
        : error is { Length: > 0 } ? $"{status} {error}" : status.ToString();

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>Under the lock. A failure to save is logged (the outbox stays in memory and is saved at the next change).</summary>
    private void SaveLedger()
    {
        try
        {
            _file.Save(_ledger);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Pokédex0: pokedex0.json not saved", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void Say(string text, ConsoleColor color) => Log?.Invoke($"{L.Stamp} {text}", color);

    /// <summary>The page shown in the browser after Pokédex0 sent the player back. Static: nothing of the request in it.</summary>
    public static string CallbackPage(Pokedex0CallbackResult result)
    {
        var (title, text) = result switch
        {
            Pokedex0CallbackResult.Connected => (L.T("Connecté à Pokédex0", "Connected to Pokédex0", "Conectado a Pokédex0"),
                L.T("C'est fait : tu peux fermer cet onglet et revenir au compteur de coffres.", "Done: you can close this tab and go back to the chest counter.", "Listo: puedes cerrar esta pestaña y volver al contador de cofres.")),
            Pokedex0CallbackResult.Denied => (L.T("Connexion annulée", "Connection cancelled", "Conexión cancelada"),
                L.T("Le compteur n'a pas été connecté. Tu peux fermer cet onglet.", "The counter was not connected. You can close this tab.", "El contador no se conectó. Puedes cerrar esta pestaña.")),
            Pokedex0CallbackResult.Invalid => (L.T("Lien expiré", "Link expired", "Enlace caducado"),
                L.T("Cette connexion n'a pas été demandée par le compteur, ou elle a expiré. Recommence depuis le compteur : Réglages → Pokédex0 → Connecter.", "This connection was not started by the counter, or it expired. Start again from the counter: Settings → Pokédex0 → Connect.", "Esta conexión no la inició el contador, o caducó. Empieza de nuevo desde el contador: Ajustes → Pokédex0 → Conectar.")),
            _ => (L.T("La connexion a échoué", "The connection failed", "La conexión falló"),
                L.T("Réessaie depuis le compteur : Réglages → Pokédex0 → Connecter. Le détail est dans l'activité du compteur.", "Try again from the counter: Settings → Pokédex0 → Connect. The details are in the counter's activity.", "Inténtalo de nuevo desde el contador: Ajustes → Pokédex0 → Conectar. El detalle está en la actividad del contador.")),
        };
        var ok = result == Pokedex0CallbackResult.Connected;
        var border = ok ? "#c9a24f" : "#a3361f";
        var heading = ok ? "#7a4a1e" : "#a3361f";
        return $$"""
            <!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="referrer" content="no-referrer">
            <title>Chest Counter · Pokédex0</title>
            <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1b24;color:#efe2c4;font:16px/1.5 "Segoe UI",system-ui,sans-serif}
            .c{max-width:460px;margin:16px;padding:24px 28px;border-radius:10px;background:#f2e6c9;color:#2a1c0f;border:2px solid {{border}}}
            h1{margin:0 0 8px;font-size:22px;color:{{heading}}}p{margin:0}</style></head>
            <body><div class="c"><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(text)}}</p></div></body></html>
            """;
    }
}
