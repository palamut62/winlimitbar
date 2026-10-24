using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace LimitBar;

/// Settings: which accounts the strip shows, extra Claude Code / Codex accounts (profiles), CLI locations.
/// A profile is just a folder the CLI keeps its sign-in in (CLAUDE_CONFIG_DIR / CODEX_HOME), so LimitBar still
/// never touches a credential: you sign in inside the CLI, in a terminal this window opens for that profile.
public sealed class SettingsWindow : System.Windows.Window
{
    static SettingsWindow? open;

    readonly Store store;
    readonly JsonObject root = Settings.Read();
    readonly HashSet<string> hidden;
    readonly List<Account> profiles;
    readonly Dictionary<Vendor, TextBox> paths = [];
    readonly StackPanel body = new() { Margin = new Thickness(16) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    ComboBox kind = null!;
    TextBox name = null!;

    public static void Open(Store store)
    {
        if (open is { } w) { w.Activate(); return; }
        open = new SettingsWindow(store);
        open.Closed += (_, _) => open = null;
        open.Show();
    }

    SettingsWindow(Store store)
    {
        this.store = store;
        hidden = Settings.Hidden(root);
        profiles = Settings.Accounts(root).Where(a => a.Profile.Length > 0).ToList();
        Title = "LimitBar Settings";
        Width = 560;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Light ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)) : new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x27));
        Foreground = Theme.Primary;
        FontFamily = Theme.Font;
        FontSize = 13;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        Build();
    }

    static Brush Field => Theme.Light ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x38));

    TextBlock Heading(string t) => Theme.Text(t, 14, Theme.Primary, FontWeights.SemiBold).With(x => x.Margin = new Thickness(0, 14, 0, 6));

    TextBlock Note(string t) => Theme.Text(t, 11, Theme.Secondary).With(x => { x.TextWrapping = TextWrapping.Wrap; x.Margin = new Thickness(0, 0, 0, 6); });

    Button Btn(string text, Action a, bool enabled = true)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), IsEnabled = enabled };
        b.Click += (_, _) => a();
        return b;
    }

    TextBox Box(string text) => new()
    {
        Text = text, Background = Field, Foreground = Theme.Primary, CaretBrush = Theme.Primary, Padding = new Thickness(4, 3, 4, 3),
        BorderBrush = Theme.Fg(0.25), VerticalContentAlignment = VerticalAlignment.Center,
    };

    void Build()
    {
        var typed = paths.ToDictionary(p => p.Key, p => p.Value.Text);
        paths.Clear();
        body.Children.Clear();

        body.Children.Add(Heading("Accounts"));
        body.Children.Add(Note("Tick the ones the taskbar strip should show. Extra Claude Code / Codex accounts each keep their own sign-in " +
            "in their own folder. Add one, press Terminal, sign in there (/login in Claude Code), and it appears on the strip with its first letter."));
        var accounts = Settings.Accounts(new JsonObject { ["profiles"] = ProfilesJson() });
        foreach (var a in accounts) body.Children.Add(AccountRow(a));

        var add = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.ColumnDefinitions.Add(new ColumnDefinition());
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        kind = new ComboBox { ItemsSource = new[] { "Claude", "Codex" }, SelectedIndex = 0, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        name = Box("");
        name.ToolTip = "Account name, e.g. Work";
        Grid.SetColumn(name, 1);
        var addBtn = Btn("Add account", AddProfile);
        Grid.SetColumn(addBtn, 2);
        add.Children.Add(kind);
        add.Children.Add(name);
        add.Children.Add(addBtn);
        body.Children.Add(add);

        body.Children.Add(Heading("CLI locations"));
        body.Children.Add(Note("Only needed when a CLI is installed somewhere LimitBar does not look. Leave empty to detect it automatically."));
        foreach (var v in VendorInfo.All)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Theme.Text(v.Cli(), 12, Theme.Primary).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            var tb = Box(typed.GetValueOrDefault(v) ?? Parse.Str(root[$"{v.Cli()}Path"]) ?? "");
            tb.ToolTip = Cli.Find(v.Cli()) is { Path: { } found } ? $"Detected: {found}" : "Not found in the usual folders";
            paths[v] = tb;
            Grid.SetColumn(tb, 1);
            var browse = Btn("Browse…", () =>
            {
                var d = new OpenFileDialog { Filter = "Programs|*.exe;*.cmd|All files|*.*", Title = $"Where is {v.Cli()}?" };
                if (d.ShowDialog() == true) tb.Text = d.FileName;
            });
            Grid.SetColumn(browse, 2);
            row.Children.Add(tb);
            row.Children.Add(browse);
            body.Children.Add(row);
        }

        status.Foreground = Theme.Secondary;
        status.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(status);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(Btn("Save", Save));
        bar.Children.Add(Btn("Close", Close));
        body.Children.Add(bar);
    }

    FrameworkElement AccountRow(Account a)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var show = new CheckBox { IsChecked = !hidden.Contains(a.Key), VerticalAlignment = VerticalAlignment.Center };
        show.Checked += (_, _) => hidden.Remove(a.Key);
        show.Unchecked += (_, _) => hidden.Add(a.Key);
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(show);
        label.Children.Add(Theme.Text(a.Title, 13, Theme.Primary).With(t => t.Margin = new Thickness(8, 0, 0, 0)));
        if (a.Profile.Length > 0)
            label.Children.Add(Theme.Text(a.Dir, 10, Theme.Tertiary).With(t => { t.Margin = new Thickness(8, 2, 0, 0); t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxWidth = 200; t.ToolTip = a.Dir; }));
        g.Children.Add(label);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (Account.CanHaveProfiles(a.Vendor)) buttons.Children.Add(Btn("Terminal", () => OpenTerminal(a)));
        if (a.Profile.Length > 0) buttons.Children.Add(Btn("Remove", () => { profiles.RemoveAll(p => p.Key == a.Key); hidden.Remove(a.Key); Build(); }));
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);
        return g;
    }

    JsonArray ProfilesJson() => new(profiles.Select(p => (JsonNode)new JsonObject
        { ["vendor"] = p.Vendor.Key(), ["name"] = p.Profile, ["dir"] = p.Dir }).ToArray());

    void AddProfile()
    {
        var v = kind.SelectedIndex == 0 ? Vendor.Claude : Vendor.Codex;
        var n = new string(name.Text.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        if (n.Length == 0) { Say("Give the account a name first (letters and digits)."); return; }
        var dir = Path.Combine(Cli.Home, ".limitbar", "profiles", $"{v.Key()}-{n.Replace(' ', '-')}");
        var acct = new Account(v, n, dir);
        if (Settings.Accounts(new JsonObject { ["profiles"] = ProfilesJson() }).Any(a => a.Key == acct.Key)) { Say($"{acct.Title} already exists."); return; }
        Directory.CreateDirectory(dir);
        profiles.Add(acct);
        Build();
        Say($"{acct.Title} added. Press Terminal on its row, sign in, then Save.");
    }

    /// A terminal running the CLI with this account's folder, so signing in or working there uses that account.
    void OpenTerminal(Account a)
    {
        if (Cli.Find(a.Vendor.Cli()) is not { Kind: FoundKind.Ok, Path: { } bin }) { Say($"{a.Vendor.Cli()} was not found. Set its location below."); return; }
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, WorkingDirectory = Cli.Home, Arguments = $"/k \"\"{bin}\"\"" };
        foreach (var (k, v) in a.Env ?? []) psi.Environment[k] = v;
        try { Process.Start(psi); Say(a.Profile.Length == 0 ? $"{a.Vendor.Cli()} opened." : $"{a.Vendor.Cli()} opened for {a.Title}. Sign in there; nothing is stored by LimitBar."); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { Say("Could not open a terminal."); }
    }

    void Save()
    {
        root["profiles"] = ProfilesJson();
        root["hidden"] = new JsonArray(hidden.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray());
        foreach (var (v, tb) in paths)
        {
            var p = tb.Text.Trim();
            if (p.Length == 0) root.Remove($"{v.Cli()}Path");
            else if (Path.IsPathFullyQualified(p)) root[$"{v.Cli()}Path"] = p;
            else { Say($"{v.Cli()}: the location must be a full path."); return; }
        }
        Settings.Save(root);
        store.Reload();
        Say("Saved.");
    }

    void Say(string t) => status.Text = t;
}
