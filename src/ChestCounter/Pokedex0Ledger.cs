using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter;

/// <summary>What the counter already sent (or chose not to send) to a Pokédex0 hunt for one match.</summary>
public sealed class LedgerEntry
{
    /// <summary>Chests of the match already accounted for on the hunt.</summary>
    public int Chests { get; set; }

    /// <summary>
    /// Counted before the hunt was linked: not sent (only new matches count), unless the player asks for the one-time
    /// sync. A re-analysis of such a match only updates the figure.
    /// </summary>
    public bool Baseline { get; set; }

    /// <summary>Operations already created for this match: the next one is number <see cref="Changes"/> (0, 1, 2…).</summary>
    public int Changes { get; set; }
}

/// <summary>An increment waiting to be sent (the outbox survives restarts and offline periods).</summary>
public sealed class PendingOperation
{
    public const string MatchKind = "match";
    public const string InitialSyncKind = "initial-sync";

    public string OperationId { get; set; } = "";
    public int Increment { get; set; }
    public string HuntId { get; set; } = "";
    public string Kind { get; set; } = MatchKind;
    public string? SessionId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// The ledger of one hunt. Kept when the player links another hunt: linking this one again later goes on from what it
/// already received (a match is never sent twice to the same hunt).
/// </summary>
public sealed class HuntLedger
{
    public string? Name { get; set; }
    public bool Multiplayer { get; set; }

    /// <summary>The counter's profile (Fortnite account) whose matches count for the hunt: the one shown when it was linked.</summary>
    public string? ProfileKey { get; set; }
    public DateTime? LinkedUtc { get; set; }

    /// <summary>Key = match SessionId.</summary>
    public Dictionary<string, LedgerEntry> Matches { get; set; } = new();

    /// <summary>Chests of the one-time sync, once it was queued (null = not asked).</summary>
    public int? InitialSyncChests { get; set; }

    /// <summary>The one-time sync was acknowledged by Pokédex0.</summary>
    public bool InitialSyncDone { get; set; }

    public DateTime? LastSentUtc { get; set; }
    public int LastSentChests { get; set; }
    public int SentChests { get; set; }
}

/// <summary>
/// pokedex0.json in the data folder: the linked hunt, the per-hunt ledgers and the outbox. No token here (they are
/// encrypted in their own file, <see cref="Pokedex0TokenStore"/>).
/// </summary>
public sealed class Pokedex0Ledger
{
    public int Version { get; set; } = 1;

    // The Pokédex0 account (shown in the window only).
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>The hunt the chests go to (null = none chosen).</summary>
    public string? HuntId { get; set; }

    /// <summary>Key = hunt id.</summary>
    public Dictionary<string, HuntLedger> Hunts { get; set; } = new();

    public List<PendingOperation> Outbox { get; set; } = new();

    /// <summary>
    /// Why sending stopped until the player acts: "inactive" (hunt completed / abandoned), "gone" (hunt deleted),
    /// "forbidden" (access refused), "invalid" (request refused), "auth" (sign in again). Null = sending.
    /// </summary>
    public string? Paused { get; set; }

    /// <summary>The server's message for the pause (untrusted text, shown escaped).</summary>
    public string? PausedDetail { get; set; }

    [JsonIgnore] public bool Linked => !string.IsNullOrEmpty(HuntId) && Hunts.ContainsKey(HuntId);

    /// <summary>The ledger of the linked hunt (null = none).</summary>
    [JsonIgnore] public HuntLedger? Hunt => HuntId != null && Hunts.TryGetValue(HuntId, out var h) ? h : null;
}

/// <summary>
/// The rules of the ledger (no network, no file): what a new or re-analyzed match adds to the hunt, the baseline taken
/// when a hunt is linked, the one-time sync and the batches of the outbox.
/// </summary>
public static class Pokedex0Book
{
    public const int MaxOperationsPerRequest = 100;
    public const int MaxIncrement = 1_000_000;

    /// <summary>
    /// Links a hunt. Operations still queued for another hunt are dropped (their chests are returned, and taken back
    /// out of that hunt's ledger). Every match of the profile the hunt has not seen yet becomes its baseline: not sent.
    /// </summary>
    public static int Link(Pokedex0Ledger s, string huntId, string? huntName, bool multiplayer, TrackerData data, string? profileKey, DateTime nowUtc)
    {
        var dropped = DropOutbox(s, o => o.HuntId != huntId);
        if (!s.Hunts.TryGetValue(huntId, out var h))
        {
            s.Hunts[huntId] = h = new HuntLedger { LinkedUtc = nowUtc };
        }
        h.Name = huntName;
        h.Multiplayer = multiplayer;
        if (profileKey != null)
        {
            h.ProfileKey = profileKey;
        }
        s.HuntId = huntId;
        s.Paused = null;
        s.PausedDetail = null;
        if (h.ProfileKey != null)
        {
            foreach (var m in Profiles.Counted(data).Where(m => Profiles.Key(m) == h.ProfileKey && m.SessionId.Length > 0))
            {
                if (!h.Matches.ContainsKey(m.SessionId))
                {
                    h.Matches[m.SessionId] = new LedgerEntry { Chests = Math.Max(0, m.ChestsOpened), Baseline = true };
                }
            }
        }
        // A hunt linked before: a match it already has and that was re-analyzed meanwhile sends its difference.
        Reconcile(s, data, nowUtc);
        return dropped;
    }

    /// <summary>Removes the queued operations matching <paramref name="which"/> and takes them back out of their ledgers.</summary>
    public static int DropOutbox(Pokedex0Ledger s, Func<PendingOperation, bool> which)
    {
        var dropped = s.Outbox.Where(which).ToList();
        foreach (var op in dropped)
        {
            if (!s.Hunts.TryGetValue(op.HuntId, out var h))
            {
                continue;
            }
            if (op.Kind == PendingOperation.InitialSyncKind)
            {
                // Not sent: the matches it covered are baseline again (they had no operation of their own).
                h.InitialSyncChests = null;
                foreach (var e in h.Matches.Values.Where(e => !e.Baseline && e.Changes == 0))
                {
                    e.Baseline = true;
                }
            }
            else if (op.SessionId != null && h.Matches.TryGetValue(op.SessionId, out var entry))
            {
                // Its operation number stays used: the next one gets a new id.
                entry.Chests -= op.Increment;
                if (entry.Chests == 0 && op.Increment > 0)
                {
                    // Nothing of this match ever reached the hunt: it is baseline now, like a match counted before
                    // the hunt was linked (only the one-time sync can send it).
                    entry.Baseline = true;
                    entry.Chests = op.Increment;
                }
            }
        }
        s.Outbox.RemoveAll(o => dropped.Contains(o));
        return dropped.Sum(o => o.Increment);
    }

    /// <summary>
    /// A match was added or re-analyzed: queues the difference between its chests and what the hunt already has from
    /// it. Only the player's own chests of the linked profile, in the modes that count. Null when nothing changes.
    /// </summary>
    public static PendingOperation? Record(Pokedex0Ledger s, MatchRecord m, DateTime nowUtc)
    {
        var h = s.Hunt;
        if (h == null || string.IsNullOrEmpty(m.SessionId) || !GameModes.Counts(m))
        {
            return null;
        }
        var key = Profiles.Key(m);
        if (h.ProfileKey == null)
        {
            // Linked before the very first match: the first account that plays becomes the hunt's profile.
            if (key.Length == 0)
            {
                return null;
            }
            h.ProfileKey = key;
        }
        if (key != h.ProfileKey)
        {
            return null;
        }

        var chests = Math.Max(0, m.ChestsOpened);
        if (h.Matches.TryGetValue(m.SessionId, out var entry) && entry.Baseline)
        {
            entry.Chests = chests; // counted before the hunt was linked: never sent on its own
            return null;
        }
        var delta = chests - (entry?.Chests ?? 0);
        if (delta < 0)
        {
            // A correction down never takes back more than the counter gave this hunt (sent or still queued):
            // Pokédex0 refuses a count below zero, and the rest of its count is not the counter's.
            var given = h.SentChests + s.Outbox.Where(o => o.HuntId == s.HuntId).Sum(o => o.Increment);
            delta = Math.Max(delta, -Math.Max(0, given));
        }
        if (delta == 0 || Math.Abs(delta) > MaxIncrement)
        {
            return null; // Pokédex0 refuses an increment of 0
        }
        entry ??= h.Matches[m.SessionId] = new LedgerEntry();
        var op = new PendingOperation
        {
            OperationId = UuidV5.MatchOperation(s.HuntId!, m.SessionId, entry.Changes),
            Increment = delta,
            HuntId = s.HuntId!,
            Kind = PendingOperation.MatchKind,
            SessionId = m.SessionId,
            CreatedUtc = nowUtc,
        };
        entry.Chests += delta; // = chests, unless the correction was limited above
        entry.Changes++;
        s.Outbox.Add(op);
        return op;
    }

    /// <summary>Every match of the data, as if just recorded (catches up a match saved right before a crash).</summary>
    public static List<PendingOperation> Reconcile(Pokedex0Ledger s, TrackerData data, DateTime nowUtc)
    {
        var ops = new List<PendingOperation>();
        if (!s.Linked)
        {
            return ops;
        }
        foreach (var m in data.Matches.ToList())
        {
            if (Record(s, m, nowUtc) is { } op)
            {
                ops.Add(op);
            }
        }
        return ops;
    }

    /// <summary>Chests (and matches) the one-time sync would add: the baseline not sent yet. Manual chests are not in it.</summary>
    public static (int Chests, int Matches) InitialSyncPreview(Pokedex0Ledger s)
    {
        var h = s.Hunt;
        if (h == null || h.InitialSyncChests != null || h.InitialSyncDone)
        {
            return (0, 0);
        }
        var baseline = h.Matches.Values.Where(e => e.Baseline).ToList();
        return (baseline.Sum(e => e.Chests), baseline.Count(e => e.Chests > 0));
    }

    /// <summary>Queues the one-time sync as one operation. Null (and nothing queued) when there is nothing to send.</summary>
    public static PendingOperation? QueueInitialSync(Pokedex0Ledger s, DateTime nowUtc)
    {
        var (chests, _) = InitialSyncPreview(s);
        if (chests <= 0 || chests > MaxIncrement)
        {
            return null;
        }
        var h = s.Hunt!;
        foreach (var e in h.Matches.Values.Where(e => e.Baseline))
        {
            e.Baseline = false; // from now on, a re-analysis of these matches sends its difference
        }
        h.InitialSyncChests = chests;
        var op = new PendingOperation
        {
            OperationId = UuidV5.InitialSyncOperation(s.HuntId!),
            Increment = chests,
            HuntId = s.HuntId!,
            Kind = PendingOperation.InitialSyncKind,
            CreatedUtc = nowUtc,
        };
        s.Outbox.Add(op);
        return op;
    }

    /// <summary>The next request: up to 100 operations of the linked hunt, oldest first.</summary>
    public static List<PendingOperation> NextBatch(Pokedex0Ledger s, int max = MaxOperationsPerRequest) =>
        s.Linked ? s.Outbox.Where(o => o.HuntId == s.HuntId).Take(Math.Clamp(max, 1, MaxOperationsPerRequest)).ToList() : new();

    /// <summary>Operations Pokédex0 acknowledged (counted now, or already before): they leave the outbox.</summary>
    public static int Acknowledge(Pokedex0Ledger s, IEnumerable<PendingOperation> sent, DateTime nowUtc)
    {
        var ids = sent.Select(o => o.OperationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = s.Outbox.Where(o => ids.Contains(o.OperationId)).ToList();
        s.Outbox.RemoveAll(o => ids.Contains(o.OperationId));
        foreach (var group in done.GroupBy(o => o.HuntId))
        {
            if (!s.Hunts.TryGetValue(group.Key, out var h))
            {
                continue;
            }
            if (group.Any(o => o.Kind == PendingOperation.InitialSyncKind))
            {
                h.InitialSyncDone = true;
            }
            var chests = group.Sum(o => o.Increment);
            h.LastSentUtc = nowUtc;
            h.LastSentChests = chests;
            h.SentChests += chests;
        }
        return done.Sum(o => o.Increment);
    }

    /// <summary>Chests waiting in the outbox for the linked hunt.</summary>
    public static int PendingChests(Pokedex0Ledger s) => s.Outbox.Where(o => o.HuntId == s.HuntId).Sum(o => o.Increment);
}

/// <summary>Reads and writes pokedex0.json atomically (like data.json).</summary>
public sealed class Pokedex0LedgerFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Pokedex0LedgerFile(string folder) => Path = System.IO.Path.Combine(folder, "pokedex0.json");

    public string Path { get; }

    public Pokedex0Ledger Load()
    {
        if (!File.Exists(Path))
        {
            return new Pokedex0Ledger();
        }
        try
        {
            var ledger = JsonSerializer.Deserialize<Pokedex0Ledger>(File.ReadAllText(Path), Json) ?? new Pokedex0Ledger();
            ledger.Hunts ??= new();
            ledger.Outbox ??= new();
            foreach (var h in ledger.Hunts.Values)
            {
                h.Matches ??= new();
            }
            return ledger;
        }
        catch (Exception ex)
        {
            // Unreadable: kept aside, and the hunt must be linked again (a new baseline: nothing can count twice).
            try { File.Copy(Path, Path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
            ErrorLog.Write("Unreadable pokedex0.json", ex.GetType().Name + ": " + ex.Message);
            return new Pokedex0Ledger();
        }
    }

    public void Save(Pokedex0Ledger ledger)
    {
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, Json));
        File.Move(tmp, Path, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(Path); } catch { }
    }
}
