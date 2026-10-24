using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LimitBar;

/// Turns each CLI's answer into a Snapshot. Pure: no process, no file, so every format is unit-tested.
/// Error texts are fixed strings of ours; a CLI's own error message is only matched against, never stored.
public static class Parse
{
    // MARK: JSON helpers - a wrong type is "absent", never an exception

    public static JsonObject? Obj(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonNode.Parse(s) as JsonObject; } catch (Exception) { return null; }
    }

    public static JsonNode? At(JsonNode? n, string key) => n is JsonObject o ? o[key] : null;

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// Any JSON number, whatever CLR type backs it (TryGetValue<double> misses an int-backed value).
    public static double? Num(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) : null;

    public static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : null;

    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public static double? Percent(JsonNode? n) => Pct.Of(Num(n));

    /// Epoch seconds, epoch milliseconds or an ISO-8601 string.
    public static DateTimeOffset? Date(JsonNode? n)
    {
        if (Num(n) is { } x && double.IsFinite(x))
        {
            var ms = x > 1e12 ? x : x * 1000;
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)ms); } catch (ArgumentException) { return null; }
        }
        if (Str(n) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var d)) return d;
        return null;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static bool SignedOutText(string s)
    {
        var t = s.ToLowerInvariant();
        string[] marks = ["not logged in", "not signed in", "please log in", "please sign in", "login required",
            "unauthenticated", "unauthorized", "no credentials", "not authenticated", "authentication required"];
        return marks.Any(t.Contains);
    }

    // MARK: Claude

    /// Written by `LimitBar --statusline` when the user opted in (see README). Holds only the limits.
    public static Snapshot? ClaudeFile(string json)
    {
        var o = Obj(json);
        if (At(o, "limits") is not JsonObject limits) return null;
        Window? Win(string k, string label, string sh) =>
            At(limits, k) is JsonObject w && Percent(w["percent"]) is { } p
                ? new Window(label, sh, p, Date(w["resets_at"])) : null;
        var five = Win("five_hour", "5-hour", "5h");
        var week = Win("seven_day", "Week", "w");
        if (five == null && week == null) return null;
        return new Snapshot
        {
            Vendor = Vendor.Claude, Bar = five ?? week, Windows = new[] { five, week }.OfType<Window>().ToList(),
            MeasuredAt = Date(o!["at"]),
        };
    }

    /// The text of `claude -p /usage --output-format json`.
    public static Snapshot ClaudeUsage(string stdout, int code, DateTimeOffset now)
    {
        var text = Str(At(Obj(stdout), "result"));
        if (text == null)
        {
            var raw = stdout.Length > 2000 ? stdout[..2000] : stdout;
            return SignedOutText(raw)
                ? Snapshot.Status(Vendor.Claude, VendorState.SignedOut)
                : Snapshot.Status(Vendor.Claude, VendorState.Failed, $"claude gave no usage (exit {code})");
        }
        if (SignedOutText(text)) return Snapshot.Status(Vendor.Claude, VendorState.SignedOut);
        Window? Line(string title, string label, string sh)
        {
            var pat = Regex.Escape(title) + @":\s*(\d+(?:\.\d+)?)%\s*used(?:\s*·\s*resets\s+([^(\n]+?)\s*\(([^)]+)\))?";
            var m = Regex.Match(text, pat);
            if (!m.Success || Pct.Of(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) is not { } used)
                return null;
            DateTimeOffset? reset = m.Groups[2].Success && m.Groups[3].Success
                ? ClaudeReset(m.Groups[2].Value, m.Groups[3].Value, now) : null;
            return new Window(label, sh, used, reset);
        }
        var five = Line("Current session", "5-hour", "5h");
        var week = Line("Current week (all models)", "Week", "w");
        // Per-model weekly lines ("Current week (Opus)", ...) appear on some plans; show each one.
        var extra = new List<Window>();
        foreach (Match m in Regex.Matches(text, @"Current week \(([^)]+)\)"))
        {
            var model = m.Groups[1].Value;
            if (model != "all models" && Line($"Current week ({model})", $"{model} week", "w") is { } w) extra.Add(w);
        }
        if (five == null && week == null)
            return Snapshot.Status(Vendor.Claude, VendorState.Failed, "no plan limits in claude /usage (API key account?)");
        return new Snapshot
        {
            Vendor = Vendor.Claude, Bar = five ?? week,
            Windows = new[] { five, week }.OfType<Window>().Concat(extra).ToList(), MeasuredAt = now,
        };
    }

    /// "Oct 21 at 9:10pm" / "Oct 24, 2:09pm" / "Oct 24 at 11pm" in the given IANA zone; the year is the next occurrence.
    public static DateTimeOffset? ClaudeReset(string s, string tz, DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(tz.Trim()); } catch (Exception) { zone = TimeZoneInfo.Local; }
        var clean = s.Trim().Replace(" at ", " ").Replace(",", "").ToUpperInvariant();
        var year = now.ToLocalTime().Year;
        foreach (var fmt in new[] { "MMM d h:mmtt yyyy", "MMM d htt yyyy", "MMM d H:mm yyyy" })
        foreach (var y in new[] { year, year + 1 })
        {
            if (!DateTime.TryParseExact($"{clean} {y}", fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                continue;
            DateTimeOffset d;
            try { d = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)); } catch (ArgumentException) { continue; }
            if (d > now.AddDays(-1)) return d;
        }
        return null;
    }

    /// `claude auth status --json`: whether someone is signed in, and the plan ("Max", "Pro").
    /// Its answer also carries the email and organisation; those are never kept or shown.
    public static (bool SignedIn, string Plan)? ClaudeAccount(string json)
    {
        var o = Obj(json);
        if (o == null) return null;
        return (Bool(o["loggedIn"]) == true, Str(o["subscriptionType"]) is { } p ? Cap(p) : "");
    }

    // MARK: Gemini - `agy -p /quota`: Google's own per-group buckets, zero tokens.

    /// The bar shows the most-used bucket across the Gemini model groups (the one that stops you first);
    /// the panel lists every group. Non-Gemini groups (Claude/GPT inside Antigravity) only once used.
    public static Snapshot Gemini(string stdout, int code, DateTimeOffset now)
    {
        if (At(At(At(Obj(stdout), "command"), "data"), "groups") is not JsonArray groups)
        {
            var raw = stdout.Length > 2000 ? stdout[..2000] : stdout;
            return SignedOutText(raw)
                ? Snapshot.Status(Vendor.Gemini, VendorState.SignedOut)
                : Snapshot.Status(Vendor.Gemini, VendorState.Failed, $"agy gave no quota (exit {code})");
        }
        List<Window> gemini = [], other = [];
        foreach (var g in groups)
        {
            var name = Str(At(g, "name")) ?? "Models";
            if (At(g, "buckets") is not JsonArray buckets) continue;
            foreach (var b in buckets)
            {
                if (Num(At(b, "remaining_fraction")) is not { } rf || !double.IsFinite(rf) || rf < 0 || rf > 1) continue;
                var five = Str(At(b, "window")) == "5h";
                var w = new Window(name, five ? "5h" : "w", (1 - rf) * 100, Date(At(b, "reset_time")));
                if (name.Contains("Gemini", StringComparison.OrdinalIgnoreCase)) gemini.Add(w);
                else if (rf < 1) other.Add(w);
            }
        }
        if (gemini.Count == 0) return Snapshot.Status(Vendor.Gemini, VendorState.Failed, "no Gemini quota in agy's answer");
        gemini = gemini.OrderByDescending(w => w.Used ?? 0).ToList();
        return new Snapshot { Vendor = Vendor.Gemini, Bar = gemini[0], Windows = [.. gemini, .. other], MeasuredAt = now };
    }

    // MARK: Codex

    /// The newest `rate_limits` line in the tail of a session log the CLI wrote for free.
    public static Snapshot? CodexLogTail(string tail)
    {
        foreach (var line in tail.Split('\n').Reverse())
        {
            if (!line.Contains("\"rate_limits\"")) continue;
            var o = Obj(line);
            if (At(At(o, "payload"), "rate_limits") is not JsonObject rl) continue;
            var s = CodexSnapshot(rl, "used_percent", "resets_at", Date(o!["timestamp"]));
            if (s.Windows.Count > 0) return s;   // non-codex limit ids log primary/secondary as null
        }
        return null;
    }

    /// `codex app-server` -> `account/rateLimits/read`.
    public static Snapshot CodexServer(JsonObject r, DateTimeOffset now)
    {
        if (r["rateLimits"] is not JsonObject rl) return Snapshot.Status(Vendor.Codex, VendorState.Failed, "codex gave no limits");
        var s = CodexSnapshot(rl, "usedPercent", "resetsAt", now);
        var credits = (At(At(r, "rateLimitResetCredits"), "credits") as JsonArray ?? new JsonArray())
            .Where(c => Str(At(c, "status")) == "available")
            .Select(c => Date(At(c, "expiresAt"))).OfType<DateTimeOffset>().Order().ToList();
        if (credits.Count == 0) return s;
        var n = credits.Count;
        return s with { Notes = [$"{n} free full reset{(n == 1 ? "" : "s")} · next expires {RelativeDay(credits[0], now)}"] };
    }

    static Snapshot CodexSnapshot(JsonObject rl, string used, string reset, DateTimeOffset? measured)
    {
        Window? Win(string k)
        {
            if (rl[k] is not JsonObject w || Percent(w[used]) is not { } u) return null;
            var mins = Int(w.ContainsKey("window_minutes") ? w["window_minutes"] : w["windowDurationMins"]) ?? 0;
            return new Window(mins <= 300 ? "5-hour" : "Week", mins <= 300 ? "5h" : "w", u, Date(w[reset]));
        }
        var five = Win("primary");
        var week = Win("secondary");
        var plan = Str(rl.ContainsKey("plan_type") ? rl["plan_type"] : rl["planType"]) is { } p ? "ChatGPT " + Cap(p) : "";
        return new Snapshot
        {
            Vendor = Vendor.Codex, Plan = plan, Bar = five ?? week,
            Windows = new[] { five, week }.OfType<Window>().ToList(), MeasuredAt = measured,
        };
    }

    // MARK: Grok - Grok Build's ACP extension `_x.ai/billing` (what its /usage screen reads).

    public static Snapshot Grok(JsonObject r, DateTimeOffset now)
    {
        if (r["config"] is not JsonObject c) return Snapshot.Status(Vendor.Grok, VendorState.Failed, "grok gave no billing");
        var period = c["currentPeriod"] as JsonObject;
        var weekly = Str(period?["type"])?.Contains("WEEKLY") ?? true;
        // creditUsagePercent is a 0-100 percentage (1.0 = 1 %).
        var end = period != null && period.ContainsKey("end") ? period["end"] : c["billingPeriodEnd"];
        var w = new Window(weekly ? "Weekly credits" : "Credits", weekly ? "w" : "", Percent(c["creditUsagePercent"]), Date(end));
        return new Snapshot
        {
            Vendor = Vendor.Grok, Plan = Str(r["subscription_tier"]) ?? "", Bar = w, Windows = [w], MeasuredAt = now,
        };
    }

    public static string RelativeDay(DateTimeOffset d, DateTimeOffset now)
    {
        var l = d.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var inv = CultureInfo.InvariantCulture;
        if (l.Date == today) return "today " + l.ToString("HH:mm", inv);
        if (l.Date == today.AddDays(1)) return "tomorrow " + l.ToString("HH:mm", inv);
        return l.ToString("ddd d MMM", inv);
    }

    // MARK: `LimitBar --statusline`

    /// Claude Code pipes a JSON blob with the current rate limits on every refresh. Returns the file to save
    /// (only the limits; null when this refresh carried none) and the one line to print.
    public static (string? File, string Line) Statusline(string input, DateTimeOffset now)
    {
        var blob = Obj(input) ?? new JsonObject();
        var rl = (At(blob, "rate_limits") ?? At(blob, "rateLimits")) as JsonObject ?? new JsonObject();
        var limits = new JsonObject();
        foreach (var (kind, value) in rl)
        {
            if (value is not JsonObject w) continue;
            var pct = new[] { "used_percentage", "used_percent", "percentUsed", "percent", "utilization" }
                .Select(k => Num(w[k])).FirstOrDefault(x => x != null);
            if (pct is { } x && x <= 1 && w.ContainsKey("utilization")) pct = x * 100;
            if (Pct.Of(pct) is not { } p) continue;
            var reset = w["resets_at"] ?? w["resetsAt"] ?? w["reset_at"];
            limits[kind] = new JsonObject { ["percent"] = p, ["resets_at"] = reset?.DeepClone() };
        }
        string? file = limits.Count == 0 ? null
            : new JsonObject { ["at"] = now.ToUnixTimeMilliseconds() / 1000.0, ["limits"] = limits }.ToJsonString();
        var parts = new[] { ("five_hour", "5h"), ("seven_day", "7d") }
            .Select(t => Num(At(At(limits, t.Item1), "percent")) is { } p ? $"{t.Item2} {Pct.Text(p)}%" : null)
            .OfType<string>();
        var model = Str(At(At(blob, "model"), "display_name")) ?? "Claude";
        return (file, string.Join(" · ", parts.Prepend(model)));
    }
}
