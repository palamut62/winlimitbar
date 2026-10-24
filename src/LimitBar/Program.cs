using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace LimitBar;

public static class Program
{
    public static readonly string Version =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    const string About = """
        Product Owner: Umut Çelik
          X:      https://x.com/palamut62
          GitHub: https://github.com/palamut62
        Windows port of https://github.com/dancik/limitbar (MIT)
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--statusline")) return Statusline();
        if (args.Contains("--probe")) return Probe();
        if (args.Contains("--version"))
        {
            Out().WriteLine($"LimitBar {Version} for Windows\n{About}");
            return 0;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Out().WriteLine($"""
                LimitBar {Version} - how much of your AI coding limits you've used, on the Windows taskbar.

                  LimitBar.exe               start the taskbar strip (one instance per user)
                  LimitBar.exe --probe       ask every CLI once and print what LimitBar would show, as JSON
                  LimitBar.exe --statusline  Claude Code status line helper (reads its JSON on stdin)
                  LimitBar.exe --version

                {About}
                """);
            return 0;
        }

        using var single = new Mutex(true, @"Local\limitbar-win", out var first);
        if (!first) return 0;
        Theme.Reload();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var store = new Store();
        new StripWindow(store).Show();
        return app.Run();
    }

    /// Output for the CLI modes. A GUI-subsystem exe has no console of its own: borrow the parent's when
    /// stdout is not redirected (Claude Code's status line and `| more` redirect it).
    static TextWriter Out()
    {
        if (!Console.IsOutputRedirected && AttachConsole(-1)) Console.OutputEncoding = Encoding.UTF8;
        return new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    }

    [DllImport("kernel32")]
    static extern bool AttachConsole(int pid);

    /// `LimitBar --probe` asks every CLI once and prints what limitbar would show, as JSON: state, plan,
    /// windows. Nothing else of the account.
    static int Probe()
    {
        var all = new SortedDictionary<string, Snapshot>();
        foreach (var v in VendorInfo.All)
        {
            all[v.Key()] = v switch
            {
                Vendor.Claude => ProbeClaude(),
                Vendor.Gemini => Fetch.Gemini(),
                Vendor.Codex => Fetch.CodexFromServer(),
                _ => Fetch.Grok(),
            };
        }
        var json = JsonSerializer.Serialize(all, new JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Out().WriteLine(json);
        return 0;
    }

    static Snapshot ProbeClaude()
    {
        var (bin, why) = Fetch.Binary(Vendor.Claude);
        if (bin == null) return why!;
        var acct = Fetch.ClaudeAccount();
        if (acct is { SignedIn: false }) return Snapshot.Status(Vendor.Claude, VendorState.SignedOut);
        var s = Fetch.ClaudeFile() ?? Fetch.ClaudeUsage();
        return s with { Plan = acct?.Plan ?? s.Plan };
    }

    /// `LimitBar --statusline` - optional Claude Code status line (see README). Keeps only the limits, in
    /// %LOCALAPPDATA%\limitbar\claude-status.json, and prints one short line, e.g. "Opus · 5h 23% · 7d 41%".
    static int Statusline()
    {
        string input;
        using (var r = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)) input = r.ReadToEnd();
        var (file, line) = Parse.Statusline(input, DateTimeOffset.Now);
        if (file != null) Paths.Write(Fetch.ClaudeFilePath, file);   // only a refresh that carries limits replaces it
        Out().WriteLine(line);
        return 0;
    }
}
