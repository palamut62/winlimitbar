using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LimitBar;

/// Keeps one Snapshot per account (a vendor's default login or an extra profile) fresh at the lowest cost:
/// - one CLI at a time, hard 20-40 s deadlines, the whole process tree killed;
/// - a source is re-polled only when its own activity files moved since the last poll (or its reset time
///   passed), plus a slow safety interval; hovering refreshes anything older than 90 s;
/// - Claude reads the opt-in statusline file for free and runs `claude -p /usage` at most every 20 min
///   when that file is missing or stale;
/// - paused while the PC sleeps, refreshed on wake; last good values cached on disk, never shown as 0.
public sealed class Store
{
    public event Action? Changed;
    public Dictionary<string, Snapshot> Snaps { get; } = [];
    public HashSet<string> Busy { get; } = [];
    public List<Account> Accounts { get; private set; } = [];

    sealed class Poll
    {
        public DateTime Last = DateTime.MinValue, Activity = DateTime.MinValue, ClaudeUsage = DateTime.MinValue,
            AccountCheck = DateTime.MinValue, CodexServer = DateTime.MinValue;
        public (bool SignedIn, string Plan)? Claude;
    }

    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    readonly SemaphoreSlim queue = new(1, 1);
    readonly DispatcherTimer timer;
    readonly Dictionary<string, Poll> polls = [];
    HashSet<string> hidden = [];
    bool asleep;

    static readonly string CacheFile = Path.Combine(Paths.Cache, "state.json");
    static readonly string Home = Cli.Home;

    /// Slow safety net per source even when nothing moved (another machine may spend the same quota).
    static readonly Dictionary<Vendor, double> IdleInterval = new()
        { [Vendor.Claude] = 20 * 60, [Vendor.Gemini] = 30 * 60, [Vendor.Codex] = 30 * 60, [Vendor.Grok] = 30 * 60 };
    /// Minimum gap between polls when activity moved.
    static readonly Dictionary<Vendor, double> BusyInterval = new()
        { [Vendor.Claude] = 60, [Vendor.Gemini] = 5 * 60, [Vendor.Codex] = 60, [Vendor.Grok] = 5 * 60 };
    /// A missing CLI is looked for again this often (it may have just been installed).
    const double MissingInterval = 5 * 60;

    /// Accounts the strip shows: everything whose CLI exists and that is not hidden in Settings.
    public List<Account> Installed => Accounts.Where(a => !hidden.Contains(a.Key)
        && (!Snaps.TryGetValue(a.Key, out var s) || s.State != VendorState.Missing)).ToList();

    /// CLIs that were not found (default logins only), listed in the panel.
    public List<Vendor> MissingVendors => Accounts.Where(a => a.Profile.Length == 0 && !hidden.Contains(a.Key)
        && Snaps.TryGetValue(a.Key, out var s) && s.State == VendorState.Missing).Select(a => a.Vendor).ToList();

    public Store()
    {
        Accounts = Settings.Accounts();
        hidden = Settings.Hidden();
        LoadCache();
        SystemEvents.PowerModeChanged += (_, e) => ui.BeginInvoke(() =>
        {
            if (e.Mode == PowerModes.Suspend) asleep = true;
            else if (e.Mode == PowerModes.Resume) { asleep = false; Refresh(force: true); }
        });
        timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh(), ui);
        timer.Start();
        Refresh(force: true);
    }

    /// Settings changed: re-read accounts and hidden flags, drop what no longer exists, read everything again.
    public void Reload()
    {
        Accounts = Settings.Accounts();
        hidden = Settings.Hidden();
        foreach (var k in Snaps.Keys.Where(k => Accounts.All(a => a.Key != k)).ToList()) Snaps.Remove(k);
        SaveCache();
        Changed?.Invoke();
        Refresh(force: true);
    }

    Poll P(Account a) => polls.TryGetValue(a.Key, out var p) ? p : polls[a.Key] = new Poll();

    /// Called by the timer (every 30 s, cheap checks only), on wake, on hover and from the panel.
    public void Refresh(bool force = false, double? olderThan = null)
    {
        if (asleep) return;
        foreach (var a in Accounts)
            if (!hidden.Contains(a.Key) && ShouldPoll(a, force, olderThan)) Schedule(a, force);
    }

    bool ShouldPoll(Account a, bool force, double? olderThan)
    {
        if (Busy.Contains(a.Key)) return false;
        var p = P(a);
        var age = (DateTime.Now - p.Last).TotalSeconds;
        if (force) return true;
        Snaps.TryGetValue(a.Key, out var s);
        if (s is { State: VendorState.Missing or VendorState.Untrusted }) return age > MissingInterval;
        if (a.Vendor == Vendor.Claude) return age > 30;     // the file read is free; the expensive part is gated inside
        if (olderThan is { } o) return age > o;
        if (s != null && s.Windows.Any(w => w.ResetPassed)) return age > 60;
        if (Activity(a) is { } act && act > p.Activity && age > BusyInterval[a.Vendor]) return true;
        return age > IdleInterval[a.Vendor];
    }

    static DateTime? Mtime(string p) =>
        File.Exists(p) ? File.GetLastWriteTimeUtc(p) : Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p) : null;

    /// Newest mtime among the files that move when this account's quota is being spent.
    static DateTime? Activity(Account a)
    {
        List<string> candidates = a.Vendor switch
        {
            Vendor.Claude => [Fetch.ClaudeFilePath(a)],
            Vendor.Gemini => [Path.Combine(Home, ".gemini", "antigravity-cli", "conversations")],
            // <codex home>/sessions/YYYY/MM/DD: today's directory moves on every new session
            Vendor.Codex => [Path.Combine(Fetch.CodexRoot(a), "sessions"), Fetch.CodexDay(a, DateTime.Now)],
            _ => [Path.Combine(Home, ".grok", "sessions")],
        };
        if (a.Vendor == Vendor.Grok && Directory.Exists(candidates[0]))
        {
            try { candidates.AddRange(Directory.EnumerateDirectories(candidates[0])); }   // per-project session folders
            catch (IOException) { }
        }
        return candidates.Select(Mtime).Max();
    }

    void Schedule(Account a, bool force)
    {
        Busy.Add(a.Key);
        Changed?.Invoke();
        var act = Activity(a);
        Snaps.TryGetValue(a.Key, out var prev);   // read on the UI thread; the fetch runs on the queue
        Task.Run(async () =>
        {
            await queue.WaitAsync();
            Snapshot s;
            try { s = FetchOne(a, prev, force); }
            catch (Exception) { s = Snapshot.Status(a.Vendor, VendorState.Failed, "unexpected error"); }
            finally { queue.Release(); }
            await ui.BeginInvoke(() =>
            {
                Busy.Remove(a.Key);
                P(a).Last = DateTime.Now;
                if (act is { } t) P(a).Activity = t;
                if (Accounts.Any(x => x.Key == a.Key)) Apply(a, s);
                else Changed?.Invoke();
            });
        });
    }

    /// Runs on the queue, one account at a time.
    Snapshot FetchOne(Account a, Snapshot? prev, bool force)
    {
        var p = P(a);
        switch (a.Vendor)
        {
            case Vendor.Claude:
            {
                var (bin, why) = Fetch.Binary(Vendor.Claude);
                if (bin == null) return why!;
                if (force || (DateTime.Now - p.AccountCheck).TotalMinutes > 30)
                {
                    p.AccountCheck = DateTime.Now;
                    p.Claude = Fetch.ClaudeAccount(a) ?? p.Claude;
                }
                if (p.Claude is { SignedIn: false }) return Snapshot.Status(Vendor.Claude, VendorState.SignedOut);
                var s = Fetch.ClaudeFile(a);
                var fileAge = s?.MeasuredAt is { } m ? (DateTimeOffset.Now - m).TotalSeconds : double.PositiveInfinity;
                if (fileAge > 20 * 60 && (force || (DateTime.Now - p.ClaudeUsage).TotalMinutes > 20))
                {
                    p.ClaudeUsage = DateTime.Now;
                    s = Fetch.ClaudeUsage(a);
                }
                if (s == null) return prev ?? Snapshot.Status(Vendor.Claude, VendorState.Failed, "waiting for claude");
                return s with { Plan = p.Claude?.Plan ?? s.Plan };
            }
            case Vendor.Gemini:
                return Fetch.Gemini();
            case Vendor.Codex:
            {
                // Session logs are free and exact; the app-server adds the reset credits, covers idle hours and
                // tells a signed-out CLI apart.
                if (Cli.Find("codex").Kind == FoundKind.Ok && !force && Fetch.CodexFromLogs(a) is { } logs
                    && logs.MeasuredAt is { } m && (DateTimeOffset.Now - m).TotalMinutes < 10
                    && (DateTime.Now - p.CodexServer).TotalMinutes < 30)
                    return logs with { Notes = prev?.Notes ?? [] };
                p.CodexServer = DateTime.Now;
                var s = Fetch.CodexFromServer(a);
                if (s.State == VendorState.Failed && Fetch.CodexFromLogs(a) is { } fallback) return fallback;
                return s;
            }
            default:
                return Fetch.Grok();
        }
    }

    void Apply(Account a, Snapshot s)
    {
        Snaps.TryGetValue(a.Key, out var old);
        if (s.State == VendorState.Failed && old is { State: VendorState.Ok, Windows.Count: > 0 })
            s = old with { Error = "Couldn't read — showing last known values" + (s.Error is { } e ? $" ({e})" : "") };
        if (s.Plan.Length == 0 && s.State == VendorState.Ok) s = s with { Plan = old?.Plan ?? "" };
        Snaps[a.Key] = s;
        SaveCache();
        Changed?.Invoke();
    }

    // MARK: cache - percentages, reset times and plan names only

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    void LoadCache()
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, Snapshot>>(File.ReadAllText(CacheFile), Json);
            foreach (var (k, s) in d ?? [])
                if (Accounts.Any(a => a.Key == k) && s.State == VendorState.Ok) Snaps[k] = s;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    void SaveCache() => Paths.Write(CacheFile, JsonSerializer.Serialize(Snaps, Json));
}
