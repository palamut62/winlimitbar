using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace LimitBar;

/// Where each vendor's limits come from. Every source is the vendor's own CLI (or a file limitbar's own
/// statusline helper writes), so sign-in and token refresh stay inside the CLI - limitbar never opens a
/// credential file, cookie or Credential Manager item, and keeps nothing of an account but the plan name.
public static class Fetch
{
    /// The CLI for a vendor, or the snapshot that explains why there is none.
    public static (string? Bin, Snapshot? Why) Binary(Vendor v)
    {
        var f = Cli.Find(v.Cli());
        return f.Kind switch
        {
            FoundKind.Ok when Cli.Launch(f.Path!) == null => (null, Snapshot.Status(v, VendorState.Untrusted,
                $"{v.Cli()}'s interpreter could be replaced by another account — not run")),
            FoundKind.Ok => (f.Path, null),
            FoundKind.Missing => (null, Snapshot.Status(v, VendorState.Missing)),
            _ => (null, Snapshot.Status(v, VendorState.Untrusted, $"{v.Cli()} could be replaced by another account — not run")),
        };
    }

    // MARK: Claude - the statusline file (free) first; `claude -p /usage` only when it is stale.

    public static readonly string ClaudeFilePath = Path.Combine(Paths.Cache, "claude-status.json");

    public static Snapshot? ClaudeFile()
    {
        try { return Parse.ClaudeFile(File.ReadAllText(ClaudeFilePath)); } catch (IOException) { return null; }
    }

    /// Isolation for every `claude` call: user/project settings files ignored (so no hook from them), hooks
    /// off, no MCP server, no transcript saved, private empty cwd, allowlisted environment. `/usage` makes no
    /// model call.
    static readonly string[] ClaudeIsolation = ["--restricted", "--strict-mcp-config", "--no-session-persistence",
        "--settings", """{"disableAllHooks":true,"language":"English"}"""];

    /// `claude -p /usage` - no model call, but a full CLI start. Called at most every 20 min.
    public static Snapshot ClaudeUsage()
    {
        var (bin, why) = Binary(Vendor.Claude);
        if (bin == null) return why!;
        var o = Cli.Run(bin, ["-p", "/usage", "--output-format", "json", "--max-turns", "1", .. ClaudeIsolation], 40);
        return o == null
            ? Snapshot.Status(Vendor.Claude, VendorState.Failed, "claude timed out")
            : Parse.ClaudeUsage(o.Text, o.Code, DateTimeOffset.Now);
    }

    public static (bool SignedIn, string Plan)? ClaudeAccount()
    {
        if (Cli.Find("claude") is not { Kind: FoundKind.Ok, Path: { } bin }) return null;
        return Cli.Run(bin, ["auth", "status", "--json"], 20) is { } o ? Parse.ClaudeAccount(o.Text) : null;
    }

    // MARK: Gemini

    public static Snapshot Gemini()
    {
        var (bin, why) = Binary(Vendor.Gemini);
        if (bin == null) return why!;
        var o = Cli.Run(bin, ["-p", "/quota", "--output-format", "json", "--print-timeout", "30s"], 40);
        return o == null
            ? Snapshot.Status(Vendor.Gemini, VendorState.Failed, "agy timed out")
            : Parse.Gemini(o.Text, o.Code, DateTimeOffset.Now);
    }

    // MARK: Codex - the CLI writes rate_limits into its own session logs for free; app-server when old.

    public static string CodexDay(DateTime d) =>
        Path.Combine(Cli.Home, ".codex", "sessions", d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));

    public static Snapshot? CodexFromLogs()
    {
        // today and yesterday are enough: the 5-hour window is all we need fresh
        var newest = new[] { DateTime.Now, DateTime.Now.AddDays(-1) }.Select(CodexDay).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl"))
            .MaxBy(File.GetLastWriteTimeUtc);
        if (newest == null) return null;
        try
        {
            using var fs = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - 262_144), SeekOrigin.Begin);
            using var r = new StreamReader(fs, Encoding.UTF8);
            return Parse.CodexLogTail(r.ReadToEnd());
        }
        catch (IOException) { return null; }
    }

    public static Snapshot CodexFromServer()
    {
        var (bin, why) = Binary(Vendor.Codex);
        if (bin == null) return why!;
        var init = new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "limitbar", ["version"] = "1" } };
        switch (Cli.Rpc(bin, ["app-server"], init, "initialized", "account/rateLimits/read"))
        {
            case Cli.RpcResult.Ok ok:
                return Parse.CodexServer(ok.Result, DateTimeOffset.Now);
            case Cli.RpcResult.Failed f:
                if (Cli.Run(bin, ["login", "status"], 15) is { } o && Parse.SignedOutText(o.Text))
                    return Snapshot.Status(Vendor.Codex, VendorState.SignedOut);
                return Parse.SignedOutText(f.Why)
                    ? Snapshot.Status(Vendor.Codex, VendorState.SignedOut)
                    : Snapshot.Status(Vendor.Codex, VendorState.Failed, "codex gave no limits");
            default:
                throw new InvalidOperationException();
        }
    }

    // MARK: Grok

    public static Snapshot Grok()
    {
        var (bin, why) = Binary(Vendor.Grok);
        if (bin == null) return why!;
        var init = new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() };
        var env = new Dictionary<string, string> { ["GROK_SUBAGENTS"] = "0", ["GROK_MEMORY"] = "0", ["GROK_WEB_FETCH"] = "0" };
        return Cli.Rpc(bin, ["agent", "stdio"], init, null, "_x.ai/billing", extraEnv: env) switch
        {
            Cli.RpcResult.Ok ok => Parse.Grok(ok.Result, DateTimeOffset.Now),
            Cli.RpcResult.Failed f when Parse.SignedOutText(f.Why) => Snapshot.Status(Vendor.Grok, VendorState.SignedOut),
            _ => Snapshot.Status(Vendor.Grok, VendorState.Failed, "grok gave no billing answer"),
        };
    }
}
