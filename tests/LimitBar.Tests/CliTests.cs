using System.Security.AccessControl;
using System.Security.Principal;
using LimitBar;

namespace LimitBar.Tests;

public sealed class CliTests : IDisposable
{
    // not %TEMP%: on this machine sandbox accounts may write there, so nothing in it passes Cli.Trusted
    readonly string dir = Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "limitbar-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string Touch(string rel, string text = "x")
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void NpmShimResolvesToTheNativeExe()
    {
        var exe = Touch(@"node_modules\@anthropic-ai\claude-code\bin\claude.exe");
        var shim = Touch("claude.cmd", "@ECHO off\r\nCALL :find_dp0\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n");
        Assert.Equal([exe], Cli.Launch(shim));
    }

    [Fact]
    public void NpmShimForAScriptUsesTheBundledNodeNotTheIfExistLine()
    {
        var node = Touch("node.exe");
        var js = Touch(@"node_modules\@openai\codex\bin\codex.js");
        var shim = Touch("codex.cmd",
            "IF EXIST \"%dp0%\\node.exe\" (\r\n  SET \"_prog=%dp0%\\node.exe\"\r\n)\r\n" +
            "endLocal & \"%_prog%\"  \"%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n");
        Assert.Equal([node, js], Cli.Launch(shim));
    }

    [Fact]
    public void ShimWithMissingTargetIsNotRun()
    {
        var shim = Touch("gone.cmd", "\"%dp0%\\node_modules\\gone\\bin\\gone.exe\" %*");
        Assert.Null(Cli.Launch(shim));
        Assert.Null(Cli.Launch(Touch("tool.ps1")));
    }

    [Fact]
    public void FileOthersCanWriteIsNotTrusted()
    {
        var safe = Touch(@"bin\tool.exe");
        Assert.True(Cli.Trusted(safe));

        var shared = Touch(@"shared\tool.exe");
        var info = new FileInfo(shared);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        Assert.False(Cli.Trusted(shared));
    }

    [Fact]
    public void FolderOthersCanWriteToMakesItsExeUntrusted()
    {
        var exe = Touch(@"open\tool.exe");
        var d = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        var acl = d.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        d.SetAccessControl(acl);
        Assert.False(Cli.Trusted(exe));   // a DLL dropped next to it would load into the CLI
    }

    [Fact]
    public void EnvironmentIsAnAllowlist()
    {
        Environment.SetEnvironmentVariable("LIMITBAR_TEST_SECRET", "sk-123");
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        var e = Cli.Env([Touch(@"bin\tool.exe")]);
        Assert.False(e.ContainsKey("LIMITBAR_TEST_SECRET"));
        Assert.False(e.ContainsKey("CLAUDECODE"));
        Assert.Equal("1", e["NO_COLOR"]);
        Assert.StartsWith(Path.Combine(dir, "bin"), e["PATH"]);
    }

    [Fact]
    public void RunKillsATreeThatOutlivesTheDeadline()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var started = DateTime.Now;
        // ping keeps running ~10 s; the 2 s deadline must end the run and its child
        Assert.Null(Cli.Run(cmd, ["/c", "ping -n 10 127.0.0.1 >nul"], timeoutSeconds: 2));
        Assert.True((DateTime.Now - started).TotalSeconds < 8);
        var ok = Cli.Run(cmd, ["/c", "echo hello"], timeoutSeconds: 10);
        Assert.Equal("hello", ok!.Text.Trim());
        Assert.Equal(0, ok.Code);
    }
}
