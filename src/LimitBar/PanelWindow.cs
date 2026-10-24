using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LimitBar;

/// The hover panel: 328 pt card, one section per vendor, bars in brand colours, Refresh / Launch at Login /
/// Quit footer. Never activated, so it never steals focus from the app you are in.
public sealed class PanelWindow : System.Windows.Window
{
    public const double CardWidth = 328;
    public const double Shadow = 14;
    const double StaleAfter = 45 * 60;

    readonly Store store;
    readonly Border card;
    readonly Border shadow;
    readonly DispatcherTimer tick;

    public PanelWindow(Store store)
    {
        this.store = store;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = CardWidth + 2 * Shadow;
        Title = "LimitBar";
        shadow = new Border
        {
            CornerRadius = new CornerRadius(16), Margin = new Thickness(Shadow), Background = Brushes.Black,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.35 },
        };
        card = new Border
        {
            CornerRadius = new CornerRadius(16), Margin = new Thickness(Shadow), BorderThickness = new Thickness(0.5),
        };
        var root = new Grid();
        root.Children.Add(shadow);
        root.Children.Add(card);
        Content = root;
        SourceInitialized += (_, _) => Native.MakeToolWindow(this);
        // TimelineView(.periodic(by: 30)): ages and reset texts stay current while open.
        tick = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        IsVisibleChanged += (_, _) => { if (IsVisible) tick.Start(); else tick.Stop(); };
        store.Changed += () => { if (IsVisible) Render(); };
    }

    public void Render()
    {
        card.Background = Theme.PanelBackground;
        card.BorderBrush = Theme.PanelBorder;
        shadow.Opacity = Theme.Light ? 0.5 : 1;
        var col = new StackPanel();
        var installed = store.Installed;
        for (var i = 0; i < installed.Count; i++)
        {
            if (i > 0) col.Children.Add(Hairline());
            var a = installed[i];
            col.Children.Add(VendorSection(a, store.Snaps.GetValueOrDefault(a.Key), store.Busy.Contains(a.Key)));
        }
        var missing = store.MissingVendors;
        if (missing.Count > 0)
        {
            if (installed.Count > 0) col.Children.Add(Hairline());
            col.Children.Add(MissingRow(missing));
        }
        col.Children.Add(Hairline());
        col.Children.Add(Footer());
        card.Child = col;
    }

    /// Lays the card out above (or below, for a top taskbar) `anchor`, in device-independent units.
    public void Place(Rect anchor, Rect workArea)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(Width, double.PositiveInfinity));
        var h = root.DesiredSize.Height;
        double x, y;
        if (anchor.Width > anchor.Height)
        {
            // horizontal taskbar: centred under / over the strip
            x = Math.Min(Math.Max(anchor.Left + anchor.Width / 2 - CardWidth / 2, workArea.Left + 8), workArea.Right - CardWidth - 8);
            y = anchor.Top < workArea.Top + workArea.Height / 2 ? anchor.Bottom + 5 : anchor.Top - 5 - (h - 2 * Shadow);
        }
        else
        {
            // vertical taskbar: beside the strip, bottom-aligned with it, kept on screen
            x = anchor.Left < workArea.Left + workArea.Width / 2 ? anchor.Right + 5 : anchor.Left - 5 - CardWidth;
            y = Math.Max(workArea.Top + 8, Math.Min(anchor.Bottom - (h - 2 * Shadow), workArea.Bottom - (h - 2 * Shadow) - 8));
        }
        x -= Shadow;
        y -= Shadow;
        Left = x;
        Top = y;
        Height = h;
    }

    static bool IsStale(Snapshot? s) =>
        s?.MeasuredAt is not { } m || (DateTimeOffset.Now - m).TotalSeconds > StaleAfter;

    static FrameworkElement Hairline() => new Rectangle { Height = 0.5, Fill = Theme.Fg(0.12) };

    // MARK: vendor section

    static FrameworkElement VendorSection(Account acct, Snapshot? snap, bool busy)
    {
        var v = acct.Vendor;
        var sp = new StackPanel { Margin = new Thickness(12, 8, 12, 9) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new Border
        {
            Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = Marks.Image(v, 16, v switch
            {
                Vendor.Claude => Theme.Claude, Vendor.Gemini => Theme.GeminiGradient, _ => Theme.Primary,
            }),
        };
        var titles = new StackPanel();
        titles.Children.Add(Theme.Text(acct.Title, 13, Theme.Primary, FontWeights.SemiBold));
        var plan = snap?.Plan ?? "";
        var planLine = Theme.Text(plan.Length == 0 ? v.Product() : $"{v.Product()} · {plan}", 11, Theme.Secondary);
        planLine.Margin = new Thickness(0, 1, 0, 0);
        titles.Children.Add(planLine);
        var age = Theme.Text(AgeText(snap, busy), 11, Theme.Tertiary);
        age.Margin = new Thickness(8, 1, 0, 0);
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(age, 2);
        header.Children.Add(mark);
        header.Children.Add(titles);
        header.Children.Add(age);
        sp.Children.Add(header);

        var windows = snap?.Windows ?? [];
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var style = i == 0 ? Kind.Primary : w.Label is "5-hour" or "Week" ? Kind.Normal : Kind.Quiet;
            var row = MeterRow(w, v, style);
            row.Margin = new Thickness(0, 7, 0, 0);
            row.Opacity = IsStale(snap) ? 0.48 : 1;
            sp.Children.Add(row);
        }
        if (snap?.State == VendorState.SignedOut)
        {
            // selectable, so the sign-in command can be copied
            sp.Children.Add(new TextBox
            {
                Text = acct.Profile.Length == 0 ? $"Signed out — {v.LoginHint()}"
                    : "Signed out — right-click the strip > Settings > Terminal, then sign in", IsReadOnly = true, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = Theme.Secondary, FontSize = 12, FontFamily = Theme.Font,
                Padding = new Thickness(0), Margin = new Thickness(-2, 7, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (windows.Count == 0)
        {
            var t = Theme.Text(busy ? "Reading…" : "—", 12, Theme.Secondary);
            t.Margin = new Thickness(0, 7, 0, 0);
            sp.Children.Add(t);
        }
        foreach (var n in snap?.Notes ?? [])
        {
            var t = Theme.Text(n, 11, Theme.Tertiary);
            t.Margin = new Thickness(0, 6, 0, 0);
            t.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t);
        }
        if (snap?.Error is { } e)
        {
            var t = Theme.Text(e, 11, Theme.Secondary);
            t.Margin = new Thickness(0, 6, 0, 0);
            t.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t);
        }
        return sp;
    }

    static string AgeText(Snapshot? snap, bool busy)
    {
        if (busy && snap?.MeasuredAt == null) return "reading…";
        if (snap?.MeasuredAt is not { } m) return "";
        var s = (int)(DateTimeOffset.Now - m).TotalSeconds;
        var t = s < 60 ? "just now" : s < 3600 ? $"{s / 60} min ago" : s < 86400 ? $"{s / 3600} h ago" : $"{s / 86400} d ago";
        return "updated " + t;
    }

    /// CLIs that aren't installed: one quiet line, so the panel says what limitbar can read once they are.
    static FrameworkElement MissingRow(List<Vendor> vendors)
    {
        var sp = new StackPanel { Margin = new Thickness(12, 8, 12, 9) };
        sp.Children.Add(Theme.Text("Not installed", 11, Theme.Secondary, FontWeights.Medium));
        var list = Theme.Text(string.Join(" · ", vendors.Select(v => $"{v.Name()} ({v.Product()})")), 11, Theme.Tertiary);
        list.TextWrapping = TextWrapping.Wrap;
        list.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(list);
        return sp;
    }

    // MARK: meter row

    enum Kind { Primary, Normal, Quiet }

    static FrameworkElement MeterRow(Window w, Vendor v, Kind style)
    {
        var due = w.ResetPassed;
        var hot = !due && (w.Used ?? 0) >= 90;
        var quiet = style == Kind.Quiet;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = Theme.Text(w.Label, quiet ? 11 : 12, quiet ? Theme.Secondary : Theme.Primary,
            style == Kind.Primary ? FontWeights.SemiBold : FontWeights.Normal);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        left.Children.Add(label);
        var reset = Theme.Text(ResetText(w, v, due), 11, due ? Theme.Secondary : Theme.Tertiary);
        reset.Margin = new Thickness(0, 1, 0, 0);
        reset.TextTrimming = TextTrimming.CharacterEllipsis;
        left.Children.Add(reset);
        g.Children.Add(left);

        if (!due)
        {
            var fill = hot ? Theme.Hot : v switch
            {
                Vendor.Claude => Theme.Claude, Vendor.Gemini => Theme.GeminiGradient, _ => Theme.Primary,
            };
            var track = new Track((w.Used ?? 0) / 100, quiet ? 4 : 5, fill)
            {
                Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(track, 1);
            g.Children.Add(track);
        }

        var pct = Theme.Text(due || w.Used == null ? "—" : Pct.Text(w.Used) + "%", quiet ? 12 : 13,
            hot ? Theme.Hot : quiet || due ? Theme.Secondary : Theme.Primary,
            hot ? FontWeights.Bold : style == Kind.Primary ? FontWeights.SemiBold : FontWeights.Medium);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        pct.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(pct, 2);
        g.Children.Add(pct);
        return g;
    }

    static string ResetText(Window w, Vendor v, bool due)
    {
        if (due) return "reset — awaiting update";
        // Gemini rows are named after the model group, so the window length goes on this line.
        var kind = v == Vendor.Gemini ? (w.Short == "5h" ? "5-hour · " : "week · ") : "";
        if (w.ResetsAt is not { } r) return kind.Length == 0 ? "" : kind[..^3];
        var l = r.ToLocalTime();
        var fmt = l.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm";
        return kind + (v == Vendor.Grok ? "ends " : "resets ") + l.ToString(fmt, Theme.En);
    }

    // MARK: footer

    FrameworkElement Footer()
    {
        var g = new Grid { Margin = new Thickness(8, 6, 8, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idle = store.Busy.Count == 0;
        g.Children.Add(FooterButton(idle ? "Refresh" : "Refreshing…", idle, () => store.Refresh(force: true)));

        var login = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        login.Children.Add(Theme.Text("Launch at Login", 12, Theme.Primary));
        login.Children.Add(Switch(Login.Enabled, on =>
        {
            try { Login.Enabled = on; } catch (Exception) { }
            Render();
        }));
        Grid.SetColumn(login, 1);
        g.Children.Add(login);

        var quit = FooterButton("Quit", true, () => Application.Current.Shutdown());
        Grid.SetColumn(quit, 2);
        g.Children.Add(quit);
        return g;
    }

    static FrameworkElement FooterButton(string title, bool enabled, Action action)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 6, 4), Background = Brushes.Transparent,
            Child = Theme.Text(title, 12, Theme.Primary), Opacity = enabled ? 1 : 0.5, Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, title);
        b.MouseEnter += (_, _) => b.Background = Theme.Fg(0.1);
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => { if (enabled) action(); };
        return b;
    }

    /// The mini switch (SwiftUI .toggleStyle(.switch).controlSize(.mini)).
    static FrameworkElement Switch(bool on, Action<bool> changed)
    {
        var knob = new Ellipse
        {
            Width = 13, Height = 13, Fill = Brushes.White, Margin = new Thickness(1),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0.5, Opacity = 0.3 },
        };
        var b = new Border
        {
            Width = 26, Height = 15, CornerRadius = new CornerRadius(7.5), Margin = new Thickness(6, 0, 0, 0),
            Background = on ? Theme.Accent : Theme.Fg(0.22), Child = knob, Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, "Launch at Login");
        b.MouseLeftButtonUp += (_, _) => changed(!on);
        return b;
    }
}
