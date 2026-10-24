using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LimitBar;

/// %LOCALAPPDATA%\limitbar - everything limitbar writes lives here (inherits the profile's owner-only ACL).
public static class Paths
{
    public static readonly string Cache = Create(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "limitbar"));

    /// A private, empty working directory: no CLI ever runs inside a project and picks up its config.
    public static readonly string WorkDir = Create(Path.Combine(Cache, "cwd"));

    static string Create(string d) { Directory.CreateDirectory(d); return d; }

    /// Atomic replace, so a reader never sees half a file.
    public static void Write(string path, string text)
    {
        var tmp = $"{path}.{Environment.ProcessId}";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { try { File.Delete(tmp); } catch (IOException) { } }
        catch (UnauthorizedAccessException) { try { File.Delete(tmp); } catch (IOException) { } }
    }

    /// `%LOCALAPPDATA%\limitbar\settings.json`, e.g. {"codexPath": "C:\\tools\\codex.exe"} - the Windows
    /// stand-in for `defaults write io.github.dancik.limitbar codexPath ...`.
    public static string? Setting(string key)
    {
        try { return Parse.Str(Parse.At(Parse.Obj(File.ReadAllText(Path.Combine(Cache, "settings.json"))), key)); }
        catch (IOException) { return null; }
    }
}

/// Child processes for the vendor CLIs. Each runs in its own Job Object, so a timeout kills the whole tree -
/// node, helpers and all - not just the first pid (the Windows stand-in for a POSIX process group).
public sealed class Child : IDisposable
{
    /// A CLI that prints more than this is not answering a quota question; stop reading.
    const int MaxOutput = 4 << 20;

    readonly Process p;
    readonly IntPtr job;
    readonly Stream stdout;
    readonly Timer deadline;
    readonly List<byte> buffer = [];
    readonly object killLock = new();
    bool inputClosed, done;

    public bool TimedOut { get; private set; }

    Child(Process p, IntPtr job, TimeSpan timeout)
    {
        this.p = p;
        this.job = job;
        stdout = p.StandardOutput.BaseStream;
        // Blocking pipe reads cannot be cancelled on Windows; killing the tree closes the pipe and ends them.
        deadline = new Timer(_ => { TimedOut = true; Kill(); }, null, timeout, Timeout.InfiniteTimeSpan);
    }

    public static Child? Start(IReadOnlyList<string> argv, IDictionary<string, string> env, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.WorkDir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env) psi.Environment[k] = v;
        Process? p;
        try { p = Process.Start(psi); } catch (Exception) { return null; }
        if (p == null) return null;
        // shortcut: assigned right after start, so a grandchild forked in the first instant could escape the job;
        // start suspended via CreateProcess if that ever matters.
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job != IntPtr.Zero) AssignProcessToJobObject(job, p.Handle);
        p.ErrorDataReceived += (_, _) => { };   // stderr is drained and dropped, never stored
        p.BeginErrorReadLine();
        return new Child(p, job, timeout);
    }

    public void Write(string s)
    {
        if (inputClosed) return;
        try
        {
            p.StandardInput.Write(s);
            p.StandardInput.Flush();
        }
        catch (IOException) { }
    }

    public void CloseInput()
    {
        if (inputClosed) return;
        inputClosed = true;
        try { p.StandardInput.Close(); } catch (IOException) { }
    }

    bool Fill()
    {
        if (buffer.Count >= MaxOutput) return false;
        var chunk = new byte[65536];
        int n;
        try { n = stdout.Read(chunk, 0, chunk.Length); } catch (Exception) { return false; }
        if (n <= 0) return false;
        buffer.AddRange(chunk.AsSpan(0, n));
        return true;
    }

    /// Next newline-terminated line, or null at EOF / deadline.
    public string? ReadLine()
    {
        while (true)
        {
            var nl = buffer.IndexOf((byte)'\n');
            if (nl >= 0)
            {
                var line = Encoding.UTF8.GetString(buffer.GetRange(0, nl).ToArray());
                buffer.RemoveRange(0, nl + 1);
                return line.TrimEnd('\r');
            }
            if (!Fill())
            {
                if (buffer.Count == 0) return null;
                var rest = Encoding.UTF8.GetString(buffer.ToArray());
                buffer.Clear();
                return rest;
            }
        }
    }

    /// Everything until EOF (or deadline).
    public string ReadAll()
    {
        while (Fill()) { }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// Wait for a normal exit after output ended; kill if it lingers. Returns the exit code, -1 if killed.
    public int Finish(int ms = 2000)
    {
        var code = p.WaitForExit(ms) ? p.ExitCode : -1;
        Kill();                                   // helpers the CLI left behind in its job
        return code;
    }

    void Kill()
    {
        lock (killLock)
        {
            if (done) return;
            done = true;
        }
        if (job != IntPtr.Zero) TerminateJobObject(job, 1);
        try { p.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    public void Dispose()
    {
        deadline.Dispose();
        Kill();
        if (job != IntPtr.Zero) CloseHandle(job);
        p.Dispose();
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}

public enum FoundKind { Ok, Missing, Untrusted }

public readonly record struct Found(FoundKind Kind, string? Path = null);

public static class Cli
{
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string Sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// Where the vendor CLIs install themselves on Windows. Fixed: PATH is never taken from the environment,
    /// so a directory added by some other tool is never searched.
    public static readonly string[] SearchPath =
    [
        $@"{Home}\.local\bin", $@"{Home}\.grok\bin", $@"{Local}\agy\bin", $@"{Roaming}\npm", $@"{Home}\.bun\bin",
        $@"{Local}\Volta\bin",
        $@"{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}\nodejs",
    ];

    static readonly string[] SystemPath = [Sys, Win, $@"{Sys}\Wbem", $@"{Sys}\WindowsPowerShell\v1.0"];

    /// The executable for `name`: an explicit path from settings.json (`<name>Path`) first, then the fixed
    /// search path. Only files that pass `Trusted` are ever run.
    public static Found Find(string name)
    {
        if (Paths.Setting($"{name}Path") is { } p && Path.IsPathFullyQualified(p))
        {
            if (!File.Exists(p)) return new(FoundKind.Missing);
            return Trusted(p) ? new(FoundKind.Ok, p) : new(FoundKind.Untrusted, p);
        }
        foreach (var dir in SearchPath)
        foreach (var ext in new[] { ".exe", ".cmd" })
        {
            var f = Path.Combine(dir, name + ext);
            if (File.Exists(f)) return Trusted(f) ? new(FoundKind.Ok, f) : new(FoundKind.Untrusted, f);
        }
        return new(FoundKind.Missing);
    }

    // MARK: trust - could another local account replace what we are about to run?

    static readonly SecurityIdentifier? Me = WindowsIdentity.GetCurrent().User;

    static readonly HashSet<string> TrustedSids =
    [
        "S-1-5-18",                                                         // SYSTEM
        "S-1-5-32-544",                                                     // Administrators (can elevate anyway)
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",   // TrustedInstaller
        "S-1-3-0", "S-1-3-4",                                               // CREATOR OWNER, OWNER RIGHTS
    ];

    const FileSystemRights Generic = (FileSystemRights)0x50000000;          // GENERIC_ALL | GENERIC_WRITE

    /// Rights that let someone swap this very object (or, on a folder, drop a DLL next to an exe).
    const FileSystemRights Replace = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | Generic;

    const FileSystemRights Write = Replace | FileSystemRights.WriteData | FileSystemRights.AppendData;

    static bool Ours(IdentityReference? id) =>
        id is SecurityIdentifier s && (s == Me || TrustedSids.Contains(s.Value));

    /// The file, its resolved target and their own folders must be owned by this user, an admin or the
    /// system and writable by nobody else; every folder above must be impossible for anyone else to replace.
    /// A file that fails is never run.
    public static bool Trusted(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var target = new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
            if (!File.Exists(target)) return false;
            return SafeFile(full) && SafeChain(Path.GetDirectoryName(full)!)
                && (target == full || (SafeFile(target) && SafeChain(Path.GetDirectoryName(target)!)));
        }
        catch (Exception) { return false; }
    }

    static bool SafeFile(string p) => Safe(new FileInfo(p).GetAccessControl(), Write);

    /// `dir` writable by nobody else; every ancestor up to the drive root not replaceable by anybody else.
    public static bool SafeChain(string dir)
    {
        try
        {
            for (var d = dir; d != null; d = Path.GetDirectoryName(d))
                if (!Safe(new DirectoryInfo(d).GetAccessControl(), d == dir ? Write : Replace)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    static bool Safe(FileSystemSecurity sec, FileSystemRights forbidden)
    {
        if (!Ours(sec.GetOwner(typeof(SecurityIdentifier)))) return false;
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue;
            if (!Ours(r.IdentityReference) && (r.FileSystemRights & forbidden) != 0) return false;
        }
        return true;
    }

    // MARK: launching

    /// How to start `binary`: itself if it is an .exe; for an npm shim (`.cmd`) the program it would start -
    /// the native exe, or node plus the script - resolved here, checked with `Trusted` and run directly, so
    /// cmd.exe never parses our arguments. null = the target is missing or untrusted: do not run.
    public static string[]? Launch(string binary)
    {
        if (binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return [binary];
        if (!binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return null;
        string text;
        try { text = File.ReadAllText(binary); } catch (IOException) { return null; }
        // The program is the last quoted %dp0% path; an earlier one is the shim's `IF EXIST "%dp0%\node.exe"`.
        var m = Regex.Matches(text, @"""%dp0%\\([^""]+?\.(exe|js|cjs|mjs))""", RegexOptions.IgnoreCase).LastOrDefault();
        if (m == null) return null;
        var dir = Path.GetDirectoryName(binary)!;
        var target = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value));
        if (!Trusted(target)) return null;
        if (m.Groups[2].Value.Equals("exe", StringComparison.OrdinalIgnoreCase)) return [target];
        var local = Path.Combine(dir, "node.exe");
        var node = File.Exists(local) ? new Found(FoundKind.Ok, local) : Find("node");
        if (node.Kind != FoundKind.Ok || !Trusted(node.Path!)) return null;
        return [node.Path!, target];
    }

    static readonly string[] KeepEnv =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "USERPROFILE", "USERNAME", "USERDOMAIN",
        "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "ALLUSERSPROFILE",
        "PUBLIC", "TEMP", "TMP", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "COMPUTERNAME",
    ];

    /// The environment a CLI gets is an allowlist, not ours minus a denylist: API keys, GitHub tokens, SSH
    /// agent sockets and anything else we were started with stay out. The CLI's own folders go first on PATH
    /// so a Node-based CLI finds the node it was installed with; only folders nobody else can write to.
    public static Dictionary<string, string> Env(IEnumerable<string> argv)
    {
        var e = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in KeepEnv)
            if (Environment.GetEnvironmentVariable(k) is { } v) e[k] = v;
        var path = new List<string>();
        foreach (var d in argv.Select(Path.GetDirectoryName).OfType<string>().Concat(SearchPath).Concat(SystemPath))
            if (!path.Contains(d, StringComparer.OrdinalIgnoreCase) && Directory.Exists(d) && SafeChain(d)) path.Add(d);
        e["PATH"] = string.Join(';', path);
        e["HOME"] = Home;
        e["LANG"] = "en_US.UTF-8";
        e["NO_COLOR"] = "1";
        return e;
    }

    public sealed record Output(string Text, int Code);

    /// Run `binary` with `args` to completion under a hard deadline. null if it could not start or timed out.
    public static Output? Run(string binary, IEnumerable<string> args, int timeoutSeconds = 20,
        IDictionary<string, string>? extraEnv = null)
    {
        if (Launch(binary) is not { } start) return null;
        var env = Env(start);
        foreach (var (k, v) in extraEnv ?? new Dictionary<string, string>()) env[k] = v;
        using var c = Child.Start([.. start, .. args], env, TimeSpan.FromSeconds(timeoutSeconds));
        if (c == null) return null;
        c.CloseInput();
        var text = c.ReadAll();
        if (c.TimedOut) return null;
        return new Output(text, c.Finish());
    }

    public abstract record RpcResult
    {
        public sealed record Ok(JsonObject Result) : RpcResult;
        public sealed record Failed(string Why) : RpcResult;
    }

    /// One JSON-RPC exchange over stdio: initialize, optional notification, one call; then kill.
    public static RpcResult Rpc(string binary, IEnumerable<string> args, JsonObject initialize, string? notify,
        string method, int timeoutSeconds = 20, IDictionary<string, string>? extraEnv = null)
    {
        if (Launch(binary) is not { } start) return new RpcResult.Failed("could not start");
        var env = Env(start);
        foreach (var (k, v) in extraEnv ?? new Dictionary<string, string>()) env[k] = v;
        using var c = Child.Start([.. start, .. args], env, TimeSpan.FromSeconds(timeoutSeconds));
        if (c == null) return new RpcResult.Failed("could not start");
        void Send(JsonObject o) => c.Write(o.ToJsonString() + "\n");
        JsonObject? Reply(int id)
        {
            while (c.ReadLine() is { } line)
                if (Parse.Obj(line) is { } o && Parse.Int(o["id"]) == id) return o;
            return null;
        }
        string Failure(JsonObject? o) => o == null
            ? (c.TimedOut ? "timed out" : "no answer")
            : Parse.Str(Parse.At(o["error"], "message")) ?? "unexpected answer";

        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = initialize });
        var i = Reply(1);
        if (i?["result"] == null) return new RpcResult.Failed(Failure(i));
        if (notify != null) Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = notify, ["params"] = new JsonObject() });
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = method, ["params"] = new JsonObject() });
        var r = Reply(2);
        return r?["result"] is JsonObject result ? new RpcResult.Ok(result) : new RpcResult.Failed(Failure(r));
    }
}
