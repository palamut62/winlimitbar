using System.Text.Json.Nodes;
using LimitBar;

namespace LimitBar.Tests;

/// Fixtures are trimmed copies of what the real CLIs printed on Windows (2026-10-24).
public class ParseTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 24, 8, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(99.6, "99")]
    [InlineData(99.4, "99")]
    [InlineData(100.0, "100")]
    [InlineData(0.5, "1")]
    [InlineData(0.0, "0")]
    public void PercentTextNeverRoundsUpTo100(double v, string expected) => Assert.Equal(expected, Pct.Text(v));

    [Fact]
    public void PercentRejectsOutOfRange()
    {
        Assert.Equal("—", Pct.Text(null));
        Assert.Null(Pct.Of(-1));
        Assert.Null(Pct.Of(100.01));
        Assert.Null(Pct.Of(double.NaN));
        Assert.Equal(42, Pct.Of(42));
    }

    [Fact]
    public void DateAcceptsSecondsMillisecondsAndIso()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792838155), Parse.Date(JsonValue.Create(1792838155)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792838155), Parse.Date(JsonValue.Create(1792838155000.0)));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 16, 11, 16, 657, TimeSpan.Zero).AddTicks(6350),
            Parse.Date(JsonValue.Create("2026-10-27T16:11:16.657635+00:00")));
        Assert.Null(Parse.Date(JsonValue.Create("soon")));
        Assert.Null(Parse.Date(null));
    }

    const string ClaudeUsageText =
        "You are currently using your subscription to power your Claude Code usage\n\n" +
        "Current session: 39% used · resets Oct 24, 2:09pm (Europe/Istanbul)\n" +
        "Current week (all models): 95% used · resets Oct 24, 10:59pm (Europe/Istanbul)\n" +
        "Current week (Opus): 12.5% used · resets Oct 30 at 9pm (Europe/Istanbul)\n\n" +
        "What's contributing to your limits usage?\n";

    static string Wrap(string result) => new JsonObject { ["result"] = result, ["total_cost_usd"] = 0 }.ToJsonString();

    [Fact]
    public void ClaudeUsageReadsEveryWindowAndResetTime()
    {
        var s = Parse.ClaudeUsage(Wrap(ClaudeUsageText), 0, Now);
        Assert.Equal(VendorState.Ok, s.State);
        Assert.Equal(["5-hour", "Week", "Opus week"], s.Windows.Select(w => w.Label));
        Assert.Equal([39, 95, 12.5], s.Windows.Select(w => w.Used!.Value));
        Assert.Equal(s.Windows[0], s.Bar);
        // Istanbul is UTC+3 all year
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 11, 9, 0, TimeSpan.Zero), s.Windows[0].ResetsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 19, 59, 0, TimeSpan.Zero), s.Windows[1].ResetsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero), s.Windows[2].ResetsAt);
        Assert.True(s.OtherHot);   // the weekly window is at 95 % while the bar shows the 5-hour one
    }

    [Fact]
    public void ClaudeResetRollsIntoNextYear()
    {
        var d = Parse.ClaudeReset("Jan 2 at 9:10am", "Europe/Istanbul", new DateTimeOffset(2026, 12, 30, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 6, 10, 0, TimeSpan.Zero), d);
        Assert.Null(Parse.ClaudeReset("whenever", "Europe/Istanbul", Now));
    }

    [Fact]
    public void ClaudeUsageWithoutLimitsOrSignedOut()
    {
        Assert.Equal(VendorState.Failed, Parse.ClaudeUsage(Wrap("What's contributing to your limits usage?"), 0, Now).State);
        Assert.Equal(VendorState.SignedOut, Parse.ClaudeUsage(Wrap("Not logged in · Please run /login"), 0, Now).State);
        Assert.Equal(VendorState.SignedOut, Parse.ClaudeUsage("Error: not authenticated", 1, Now).State);
        var bad = Parse.ClaudeUsage("garbage", 3, Now);
        Assert.Equal("claude gave no usage (exit 3)", bad.Error);
    }

    [Fact]
    public void ClaudeAccountKeepsOnlySignInAndPlan()
    {
        var a = Parse.ClaudeAccount("""{"loggedIn": true, "email": "x@y.z", "orgName": "Org", "subscriptionType": "pro"}""");
        Assert.Equal((true, "Pro"), a);
        Assert.Equal((false, ""), Parse.ClaudeAccount("""{"loggedIn": false}"""));
        Assert.Null(Parse.ClaudeAccount("nope"));
    }

    [Fact]
    public void ClaudeFileFromStatusline()
    {
        var (file, line) = Parse.Statusline("""
            {"model": {"display_name": "Opus"},
             "rate_limits": {"five_hour": {"used_percentage": 23.4, "resets_at": 1792838155},
                             "seven_day": {"utilization": 0.41, "resets_at": "2026-10-30T18:00:00Z"},
                             "bogus": {"used_percentage": 140}}}
            """, Now);
        Assert.Equal("Opus · 5h 23% · 7d 41%", line);
        var s = Parse.ClaudeFile(file!)!;
        Assert.Equal([23.4, 41.0], s.Windows.Select(w => Math.Round(w.Used!.Value, 6)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792838155), s.Windows[0].ResetsAt);
        Assert.Equal(Now, s.MeasuredAt);
        Assert.DoesNotContain("bogus", file);
    }

    [Fact]
    public void StatuslineWithoutLimitsWritesNothing()
    {
        var (file, line) = Parse.Statusline("""{"model": {"display_name": "Sonnet"}}""", Now);
        Assert.Null(file);
        Assert.Equal("Sonnet", line);
        Assert.Equal("Claude", Parse.Statusline("", Now).Line);
    }

    const string AgyQuota = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
          {"name":"Gemini Models","buckets":[
            {"id":"gemini-weekly","window":"weekly","remaining_fraction":0.19,"reset_time":"2026-10-24T15:19:00Z"},
            {"id":"gemini-5h","window":"5h","remaining_fraction":1,"reset_time":"2026-10-24T13:10:10Z"}]},
          {"name":"Claude and GPT models","buckets":[
            {"id":"3p-weekly","window":"weekly","remaining_fraction":1,"reset_time":"2026-10-31T08:10:10Z"},
            {"id":"3p-5h","window":"5h","remaining_fraction":0.5,"reset_time":"2026-10-24T13:10:10Z"},
            {"id":"broken","window":"5h","remaining_fraction":7}]}]}}}
        """;

    [Fact]
    public void GeminiShowsMostUsedGeminiBucketAndUsedOtherGroups()
    {
        var s = Parse.Gemini(AgyQuota, 0, Now);
        Assert.Equal(VendorState.Ok, s.State);
        Assert.Equal("w", s.Bar!.Short);
        Assert.Equal(81, Math.Round(s.Bar.Used!.Value));
        Assert.Equal(["Gemini Models", "Gemini Models", "Claude and GPT models"], s.Windows.Select(w => w.Label));
        Assert.Equal("5h", s.Windows[2].Short);
    }

    [Fact]
    public void GeminiFailures()
    {
        Assert.Equal("agy gave no quota (exit 2)", Parse.Gemini("{}", 2, Now).Error);
        Assert.Equal(VendorState.SignedOut, Parse.Gemini("please sign in", 1, Now).State);
        Assert.Equal("no Gemini quota in agy's answer",
            Parse.Gemini("""{"command":{"data":{"groups":[{"name":"Other","buckets":[]}]}}}""", 0, Now).Error);
    }

    [Fact]
    public void CodexLogsSkipNonCodexLimitLines()
    {
        var good = """{"timestamp":"2026-10-24T08:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":12.0,"window_minutes":300,"resets_at":1792838155},"secondary":{"used_percent":2.0,"window_minutes":10080,"resets_at":1793424955},"plan_type":"plus"}}}""";
        var empty = """{"timestamp":"2026-10-24T08:05:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"premium","primary":null,"secondary":null,"plan_type":"plus"}}}""";
        var s = Parse.CodexLogTail(string.Join("\n", "partial line", good, empty, ""))!;
        Assert.Equal(["5-hour", "Week"], s.Windows.Select(w => w.Label));
        Assert.Equal("ChatGPT Plus", s.Plan);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero), s.MeasuredAt);
        Assert.Null(Parse.CodexLogTail(empty));
    }

    [Fact]
    public void CodexServerWithResetCredits()
    {
        var r = JsonNode.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1792838155},
             "secondary":{"usedPercent":2,"windowDurationMins":10080,"resetsAt":1793424955},"planType":"plus"},
             "rateLimitResetCredits":{"credits":[{"status":"available","expiresAt":"2026-10-25T16:26:00Z"},
               {"status":"used","expiresAt":"2026-10-24T09:00:00Z"},{"status":"available","expiresAt":"2026-11-02T10:00:00Z"}]}}
            """)!.AsObject();
        var s = Parse.CodexServer(r, Now);
        Assert.Equal([12.0, 2.0], s.Windows.Select(w => w.Used!.Value));
        Assert.Equal("5h", s.Bar!.Short);
        var note = Assert.Single(s.Notes);
        Assert.StartsWith("2 free full resets · next expires ", note);
        Assert.Equal(VendorState.Failed, Parse.CodexServer(new JsonObject(), Now).State);
    }

    [Fact]
    public void GrokWeeklyCredits()
    {
        var r = JsonNode.Parse("""
            {"subscription_tier":"X Premium+","config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY",
             "end":"2026-10-27T16:11:16.657635+00:00"},"creditUsagePercent":2.0,"billingPeriodEnd":"2026-11-01T00:00:00Z"}}
            """)!.AsObject();
        var s = Parse.Grok(r, Now);
        Assert.Equal("X Premium+", s.Plan);
        Assert.Equal(new Window("Weekly credits", "w", 2.0, Parse.Date(JsonValue.Create("2026-10-27T16:11:16.657635+00:00"))), s.Bar);
        // no creditUsagePercent while nothing was spent: shown as "—", never as a made-up 0
        var idle = Parse.Grok(JsonNode.Parse("""{"config":{"billingPeriodEnd":"2026-11-01T00:00:00Z"}}""")!.AsObject(), Now);
        Assert.Null(idle.Bar!.Used);
        Assert.Equal(VendorState.Failed, Parse.Grok(new JsonObject(), Now).State);
    }

    [Fact]
    public void ResetPassedWindowsAreNotHot()
    {
        var past = new Window("Week", "w", 99, DateTimeOffset.Now.AddMinutes(-1));
        var bar = new Window("5-hour", "5h", 10, null);
        Assert.False(new Snapshot { Bar = bar, Windows = [bar, past] }.OtherHot);
        Assert.True(past.ResetPassed);
    }

    [Fact]
    public void SignedOutTextMatchesKnownPhrases()
    {
        Assert.True(Parse.SignedOutText("Error: Not Logged In"));
        Assert.False(Parse.SignedOutText("rate limited"));
    }
}
