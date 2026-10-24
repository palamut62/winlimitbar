using System.IO;
using System.Text.Json.Nodes;

namespace LimitBar;

/// One thing LimitBar reads: a vendor's default login, or an extra profile of Claude Code / Codex that keeps
/// its sign-in in its own folder (CLAUDE_CONFIG_DIR / CODEX_HOME). Profile "" is the default login.
public sealed record Account(Vendor Vendor, string Profile, string Dir)
{
    public string Key => Profile.Length == 0 ? Vendor.Key() : $"{Vendor.Key()}:{Profile}";
    public string Title => Profile.Length == 0 ? Vendor.Name() : $"{Vendor.Name()} · {Profile}";

    /// The environment variable that points the CLI at this profile's folder.
    public Dictionary<string, string>? Env => Dir.Length == 0 ? null
        : new() { [Vendor == Vendor.Claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME"] = Dir };

    public static bool CanHaveProfiles(Vendor v) => v is Vendor.Claude or Vendor.Codex;
}

/// `%LOCALAPPDATA%\limitbar\settings.json`: {"codexPath": "...", "hidden": ["gemini"],
/// "profiles": [{"vendor": "claude", "name": "Work", "dir": "C:\\..."}]}.
public static class Settings
{
    public static readonly string FilePath = Path.Combine(Paths.Cache, "settings.json");

    public static JsonObject Read()
    {
        try { return Parse.Obj(File.ReadAllText(FilePath)) ?? new JsonObject(); } catch (IOException) { return new JsonObject(); }
    }

    public static void Save(JsonObject root) => Paths.Write(FilePath, root.ToJsonString(new() { WriteIndented = true }));

    /// Each vendor's default login, followed by its profiles; hand-edited garbage entries are skipped.
    public static List<Account> Accounts(JsonObject? root = null)
    {
        root ??= Read();
        var profiles = (root["profiles"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(ParseProfile).OfType<Account>().ToList();
        var list = new List<Account>();
        foreach (var v in VendorInfo.All)
        {
            list.Add(new Account(v, "", ""));
            foreach (var p in profiles.Where(p => p.Vendor == v))
                if (!list.Any(a => a.Key == p.Key)) list.Add(p);
        }
        return list;
    }

    static Account? ParseProfile(JsonObject p)
    {
        var name = Parse.Str(p["name"]);
        var dir = Parse.Str(p["dir"]);
        if (!Enum.TryParse<Vendor>(Parse.Str(p["vendor"]), true, out var v) || !Account.CanHaveProfiles(v)) return null;
        if (string.IsNullOrEmpty(name) || dir == null || !Path.IsPathFullyQualified(dir)) return null;
        return new Account(v, name, dir);
    }

    public static HashSet<string> Hidden(JsonObject? root = null) =>
        (( root ?? Read())["hidden"] as JsonArray ?? new JsonArray()).Select(Parse.Str).OfType<string>().ToHashSet();
}
