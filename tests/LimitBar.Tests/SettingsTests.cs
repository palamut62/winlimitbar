using System.Text.Json.Nodes;
using LimitBar;

namespace LimitBar.Tests;

public class SettingsTests
{
    static JsonObject Root(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void ProfilesFollowTheirVendorAndGarbageIsSkipped()
    {
        var root = Root("""
            {"profiles": [
              {"vendor": "codex", "name": "Work", "dir": "C:\\p\\codex-work"},
              {"vendor": "claude", "name": "Work", "dir": "C:\\p\\claude-work"},
              {"vendor": "claude", "name": "Work", "dir": "C:\\p\\duplicate"},
              {"vendor": "gemini", "name": "X", "dir": "C:\\p\\g"},
              {"vendor": "claude", "name": "", "dir": "C:\\p\\noname"},
              {"vendor": "claude", "name": "Rel", "dir": "relative\\dir"},
              "nonsense"
            ]}
            """);
        var keys = Settings.Accounts(root).Select(a => a.Key);
        Assert.Equal(["claude", "claude:Work", "gemini", "codex", "codex:Work", "grok"], keys);
        Assert.Equal(@"C:\p\claude-work", Settings.Accounts(root)[1].Dir);
    }

    [Fact]
    public void ProfileEnvPointsTheCliAtItsFolder()
    {
        Assert.Null(new Account(Vendor.Claude, "", "").Env);
        Assert.Equal(@"C:\p\c", new Account(Vendor.Claude, "Work", @"C:\p\c").Env!["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(@"C:\p\x", new Account(Vendor.Codex, "Work", @"C:\p\x").Env!["CODEX_HOME"]);
        Assert.Equal("Claude · Work", new Account(Vendor.Claude, "Work", @"C:\p\c").Title);
    }

    [Fact]
    public void StatuslineFilesStayApartPerProfile()
    {
        var main = Fetch.ClaudeFilePath(new Account(Vendor.Claude, "", ""));
        var work = Fetch.ClaudeFilePath(new Account(Vendor.Claude, "Work 2/x", ""));
        Assert.EndsWith("claude-status.json", main);
        Assert.EndsWith("claude-status-Work_2_x.json", work);
    }

    [Fact]
    public void HiddenListIgnoresNonStrings()
    {
        Assert.Equal(["gemini", "claude:Work"], Settings.Hidden(Root("""{"hidden": ["gemini", 3, "claude:Work"]}""")));
        Assert.Empty(Settings.Hidden(new JsonObject()));
    }
}
