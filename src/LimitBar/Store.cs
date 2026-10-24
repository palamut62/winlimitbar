using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LimitBar;

/// Keeps one Snapshot per vendor fresh at the lowest cost:
/// - one CLI at a time, hard 20-40 s deadlines, the whole process tree killed;
/// - a source is re-polled only when its own activity files moved since the last poll (or its reset time
///   passed), plus a slow safety interval; hovering refreshes anything older than 90 s;
/// - Claude reads the opt-in statusline file for free and runs `claude -p /usage` at most every 20 min
///   when that file is missing or stale;
/// - paused while the PC sleeps, refreshed on wake; last good values cached on disk, never shown as 0.
public sealed class Store
{
    public event Action? Changed;
    public Dictionary<Vendor, Snapshot> Snaps { get; } = [];
    public HashSet<Vendor> Busy { get; } = [];

    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    readonly SemaphoreSlim queue = new(1, 1);
    readonly DispatcherTimer timer;
    bool asleep;
    readonly Dictionary<Vendor, DateTime> lastPoll = [], lastActivity = [];
    DateTime lastClaudeUsage = DateTime.MinValue, lastAccountCheck = DateTime.MinValue, lastCodexServer = DateTime.MinValue;
    (bool SignedIn, string Plan)? claudeAccount;

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

    /// Vendors the strip shows: everything whose CLI exists. Missing ones are listed in the panel only.
    public List<Vendor> Installed =>
        VendorInfo.All.Where(v => !Snaps.TryGetValue(v, out var s) || s.State != VendorState.Missing).ToList();

    public Store()
    {
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

    /// Called by the timer (every 30 s, cheap checks only), on wake, on hover and from the panel.
    public void Refresh(bool force = false, double? olderThan = null)
    {
        if (asleep) return;
        foreach (var v in VendorInfo.All)
            if (ShouldPoll(v, force, olderThan)) Schedule(v, force);
    }

    bool ShouldPoll(Vendor v, bool force, double? olderThan)
    {
        if (Busy.Contains(v)) return false;
        var age = (DateTime.Now - lastPoll.GetValueOrDefault(v, DateTime.MinValue)).TotalSeconds;
        if (force) return true;
        Snaps.TryGetValue(v, out var s);
        if (s is { State: VendorState.Missing or VendorState.Untrusted }) return age > MissingInterval;
        if (v == Vendor.Claude) return age > 30;     // the file read is free; the expensive part is gated inside
        if (olderThan is { } o) return age > o;
        if (s != null && s.Windows.Any(w => w.ResetPassed)) return age > 60;
        if (Activity(v) is { } a && a > lastActivity.GetValueOrDefault(v, DateTime.MinValue) && age > BusyInterval[v])
            return true;
        return age > IdleInterval[v];
    }

    static DateTime? Mtime(string p) =>
        File.Exists(p) ? File.GetLastWriteTimeUtc(p) : Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p) : null;

    /// Newest mtime among the files that move when this vendor's quota is being spent.
    static DateTime? Activity(Vendor v)
    {
        List<string> candidates = v switch
        {
            Vendor.Claude => [Fetch.ClaudeFilePath],
            Vendor.Gemini => [Path.Combine(Home, ".gemini", "antigravity-cli", "conversations")],
            // ~/.codex/sessions/YYYY/MM/DD: today's directory moves on every new session
            Vendor.Codex => [Path.Combine(Home, ".codex", "sessions"), Fetch.CodexDay(DateTime.Now)],
            _ => [Path.Combine(Home, ".grok", "sessions")],
        };
        if (v == Vendor.Grok && Directory.Exists(candidates[0]))
        {
            try { candidates.AddRange(Directory.EnumerateDirectories(candidates[0])); }   // per-project session folders
            catch (IOException) { }
        }
        return candidates.Select(Mtime).Max();
    }

    void Schedule(Vendor v, bool force)
    {
        Busy.Add(v);
        Changed?.Invoke();
        var act = Activity(v);
        Snaps.TryGetValue(v, out var prev);   // read on the UI thread; the fetch runs on the queue
        Task.Run(async () =>
        {
            await queue.WaitAsync();
            Snapshot s;
            try { s = FetchOne(v, prev, force); }
            catch (Exception) { s = Snapshot.Status(v, VendorState.Failed, "unexpected error"); }
            finally { queue.Release(); }
            await ui.BeginInvoke(() =>
            {
                Busy.Remove(v);
                lastPoll[v] = DateTime.Now;
                if (act is { } a) lastActivity[v] = a;
                Apply(v, s);
            });
        });
    }

    /// Runs on the queue, one vendor at a time.
    Snapshot FetchOne(Vendor v, Snapshot? prev, bool force)
    {
        switch (v)
        {
            case Vendor.Claude:
            {
                var (bin, why) = Fetch.Binary(Vendor.Claude);
                if (bin == null) return why!;
                if (force || (DateTime.Now - lastAccountCheck).TotalMinutes > 30)
                {
                    lastAccountCheck = DateTime.Now;
                    claudeAccount = Fetch.ClaudeAccount() ?? claudeAccount;
                }
                if (claudeAccount is { SignedIn: false }) return Snapshot.Status(Vendor.Claude, VendorState.SignedOut);
                var s = Fetch.ClaudeFile();
                var fileAge = s?.MeasuredAt is { } m ? (DateTimeOffset.Now - m).TotalSeconds : double.PositiveInfinity;
                if (fileAge > 20 * 60 && (force || (DateTime.Now - lastClaudeUsage).TotalMinutes > 20))
                {
                    lastClaudeUsage = DateTime.Now;
                    s = Fetch.ClaudeUsage();
                }
                if (s == null) return prev ?? Snapshot.Status(Vendor.Claude, VendorState.Failed, "waiting for claude");
                return s with { Plan = claudeAccount?.Plan ?? s.Plan };
            }
            case Vendor.Gemini:
                return Fetch.Gemini();
            case Vendor.Codex:
            {
                // Session logs are free and exact; the app-server adds the reset credits, covers idle hours and
                // tells a signed-out CLI apart.
                if (Cli.Find("codex").Kind == FoundKind.Ok && !force && Fetch.CodexFromLogs() is { } logs
                    && logs.MeasuredAt is { } m && (DateTimeOffset.Now - m).TotalMinutes < 10
                    && (DateTime.Now - lastCodexServer).TotalMinutes < 30)
                    return logs with { Notes = prev?.Notes ?? [] };
                lastCodexServer = DateTime.Now;
                var s = Fetch.CodexFromServer();
                if (s.State == VendorState.Failed && Fetch.CodexFromLogs() is { } fallback) return fallback;
                return s;
            }
            default:
                return Fetch.Grok();
        }
    }

    void Apply(Vendor v, Snapshot s)
    {
        Snaps.TryGetValue(v, out var old);
        if (s.State == VendorState.Failed && old is { State: VendorState.Ok, Windows.Count: > 0 })
            s = old with { Error = "Couldn't read — showing last known values" + (s.Error is { } e ? $" ({e})" : "") };
        if (s.Plan.Length == 0 && s.State == VendorState.Ok) s = s with { Plan = old?.Plan ?? "" };
        Snaps[v] = s;
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
                if (Enum.TryParse<Vendor>(k, true, out var v) && s.State == VendorState.Ok) Snaps[v] = s with { Vendor = v };
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    void SaveCache() =>
        Paths.Write(CacheFile, JsonSerializer.Serialize(Snaps.ToDictionary(p => p.Key.Key(), p => p.Value), Json));
}
