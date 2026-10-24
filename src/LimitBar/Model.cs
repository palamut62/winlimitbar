using System.Text.Json.Serialization;

namespace LimitBar;

public enum Vendor { Claude, Gemini, Codex, Grok }

public static class VendorInfo
{
    public static readonly Vendor[] All = [Vendor.Claude, Vendor.Gemini, Vendor.Codex, Vendor.Grok];

    public static string Key(this Vendor v) => v.ToString().ToLowerInvariant();

    public static string Name(this Vendor v) => v.ToString();

    /// The CLI limitbar asks, and what the panel suggests when it is missing or signed out.
    public static string Cli(this Vendor v) => v switch
    {
        Vendor.Claude => "claude", Vendor.Gemini => "agy", Vendor.Codex => "codex", _ => "grok",
    };

    public static string Product(this Vendor v) => v switch
    {
        Vendor.Claude => "Claude Code", Vendor.Gemini => "Google Antigravity CLI", Vendor.Codex => "Codex CLI",
        _ => "Grok Build",
    };

    public static string LoginHint(this Vendor v) => v switch
    {
        Vendor.Claude => "run `claude`, then /login", Vendor.Gemini => "run `agy` once and sign in",
        Vendor.Codex => "run `codex login`", _ => "run `grok login`",
    };
}

/// Why a vendor shows no numbers. `Ok` = the CLI answered.
public enum VendorState
{
    Ok,
    Missing,     // CLI not found: hidden from the taskbar strip, listed in the panel
    Untrusted,   // found, but another account could replace it: never run
    SignedOut,   // the CLI says nobody is signed in
    Failed,      // it ran and did not give limits (timeout, error, changed format)
}

/// One limit window as the vendor reports it. `Used` is percent USED, 0-100.
public sealed record Window(string Label, string Short, double? Used, DateTimeOffset? ResetsAt)
{
    /// The reset time has passed since we read it: the old percent is obsolete, not "0 %".
    [JsonIgnore] public bool ResetPassed => ResetsAt is { } r && r < DateTimeOffset.Now;
}

public sealed record Snapshot
{
    public Vendor Vendor { get; init; }
    public VendorState State { get; init; } = VendorState.Ok;
    public string Plan { get; init; } = "";          // from the CLI's own answer: "Max", "ChatGPT Plus", ...
    public Window? Bar { get; init; }                 // the window the strip shows for this vendor
    public List<Window> Windows { get; init; } = [];  // everything shown in the panel, bar window first
    public List<string> Notes { get; init; } = [];    // e.g. "2 free full resets · next expires Fri 3 Jan"
    public DateTimeOffset? MeasuredAt { get; init; }  // when the vendor's numbers were taken
    public string? Error { get; init; }               // last failure, shown dimmed; previous numbers are kept

    /// Any window of this vendor at >= 90 % other than the one in the bar -> "!" next to the digit.
    [JsonIgnore]
    public bool OtherHot => Windows.Any(w => w != Bar && !w.ResetPassed && (w.Used ?? 0) >= 90);

    public static Snapshot Status(Vendor v, VendorState state, string? error = null) =>
        new() { Vendor = v, State = state, Error = error };
}

public static class Pct
{
    /// A percentage from a CLI is only used when it is a finite number in 0...100.
    public static double? Of(double? v) => v is { } x && double.IsFinite(x) && x >= 0 && x <= 100 ? x : null;

    /// Strip digit: rounded, but 99.6 % must never read as 100 %.
    public static string Text(double? v)
    {
        if (v is not { } x) return "—";
        var r = Math.Round(x, MidpointRounding.AwayFromZero);
        return ((int)(r >= 100 && x < 100 ? 99 : r)).ToString();
    }
}
