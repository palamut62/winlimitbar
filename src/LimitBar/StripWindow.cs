using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LimitBar;

/// The menu-bar item, on the Windows taskbar: mark + % used per vendor, just left of the notification area.
/// Hover opens the panel after 150 ms, leaving the strip or the panel closes it after 250 ms; a click pins
/// it open (click again, or click elsewhere, to close). Neither window is ever activated.
public sealed class StripWindow : System.Windows.Window
{
    const double StaleAfter = 45 * 60;

    readonly Store store;
    readonly PanelWindow panel;
    readonly Border item;
    readonly DispatcherTimer keepOnTop, hover;
    bool pinned;
    DateTime? enteredAt, leftAt;
    bool clickClosed;   // closed by a click: no hover re-open until the cursor leaves the strip
    Native.RECT lastTaskbar;

    public StripWindow(Store store)
    {
        this.store = store;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "LimitBar";
        item = new Border
        {
            // nearly transparent, so the whole strip takes the hover and the click
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(item, "AI subscription limits");
        Content = item;
        panel = new PanelWindow(store);

        keepOnTop = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Position(), Dispatcher);
        hover = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) => Track(), Dispatcher);

        item.MouseEnter += (_, _) => hover.Start();
        item.MouseLeftButtonUp += (_, _) => Clicked();
        item.ContextMenu = AboutMenu();

        SourceInitialized += (_, _) => { Native.MakeToolWindow(this); Render(); keepOnTop.Start(); };
        store.Changed += Render;
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => { Theme.Reload(); Render(); panel.Render(); });
    }

    // MARK: strip

    void Render()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var installed = store.Installed;
        for (var i = 0; i < installed.Count; i++)
        {
            var acct = installed[i];
            var v = acct.Vendor;
            var cell = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(i > 0 ? 8 : 0, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var mark = Marks.Image(v, 13, Theme.Primary);
            mark.Margin = new Thickness(0, 0, 3, 0);
            cell.Children.Add(mark);
            if (acct.Profile.Length > 0)   // which account: the profile's first letter
                cell.Children.Add(Theme.Text(acct.Profile[..1].ToUpperInvariant(), 9, Theme.Secondary, FontWeights.SemiBold)
                    .With(t => { t.Margin = new Thickness(0, 0, 3, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
            cell.Children.Add(Digit(store.Snaps.GetValueOrDefault(acct.Key)));
            row.Children.Add(cell);
        }
        item.Child = row;
        Position(force: true);
        if (panel.IsVisible) PlacePanel();
    }

    /// % used, no "%" sign, superscript w for weekly, red >= 90, "!" when another window is hot, dimmed if stale.
    static FrameworkElement Digit(Snapshot? s)
    {
        var w = s?.State == VendorState.Ok ? s.Bar : null;
        double? shown = w == null || w.ResetPassed ? null : w.Used;
        var crit = (shown ?? 0) >= 90;
        var weekly = w?.Short == "w";
        var bang = s?.OtherHot ?? false;
        var t = Theme.Text("", 13, crit ? Theme.Hot : Theme.Primary, crit ? FontWeights.SemiBold : FontWeights.Normal);
        t.Inlines.Add(new Run(Pct.Text(shown)));
        if (weekly) t.Inlines.Add(new Run("w") { FontSize = 8.5, FontWeight = FontWeights.Medium, BaselineAlignment = BaselineAlignment.Superscript });
        if (bang) t.Inlines.Add(new Run("!") { FontSize = 8.5, FontWeight = FontWeights.Black, BaselineAlignment = BaselineAlignment.Superscript });
        var stale = s?.MeasuredAt is not { } m || (DateTimeOffset.Now - m).TotalSeconds > StaleAfter;
        t.Opacity = stale && shown != null ? 0.5 : 1;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    /// Sits on the taskbar just left of the notification area; re-asserted every 250 ms because clicking the
    /// taskbar raises it above every other topmost window. Hidden while a full-screen app is in front.
    void Position(bool force = false)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (Native.FullScreenAppInFront())
        {
            if (IsVisible) { HidePanel(evenIfPinned: true); Hide(); }
            return;
        }
        if (!IsVisible) Show();
        var tb = Native.Taskbar(out var notify);
        if (!force && tb.Equals(lastTaskbar) && Native.IsTopmostAbove(hwnd))
            return;
        lastTaskbar = tb;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wDip = Math.Ceiling(item.DesiredSize.Width);
        var horizontal = tb.Width >= tb.Height;
        // shortcut: a vertical taskbar gets a fixed 28-pt-high strip above the tray; a deskband-like layout would need more.
        var hPx = horizontal ? tb.Height : (int)(28 * scale);
        var wPx = (int)Math.Ceiling(wDip * scale);
        var x = horizontal ? notify.Left - wPx - (int)(4 * scale) : tb.Left + (tb.Width - wPx) / 2;
        var y = horizontal ? tb.Top : notify.Top - hPx;
        Width = wDip;
        Height = hPx / scale;
        Native.SetTopmost(hwnd, x, y, wPx, hPx);
    }

    /// The strip's own rectangle, in device-independent units.
    Rect StripRect()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var r = Native.Rect(new WindowInteropHelper(this).Handle);
        return new Rect(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);
    }

    void PlacePanel()
    {
        panel.Render();
        panel.Place(StripRect(), SystemParameters.WorkArea);
    }

    // MARK: panel

    void ShowPanel()
    {
        if (panel.IsVisible) return;
        store.Refresh(olderThan: 90);      // hover refresh: only sources older than 90 s
        PlacePanel();
        panel.Show();
        item.Background = Theme.Fg(0.1);
    }

    void HidePanel(bool evenIfPinned)
    {
        if (pinned && !evenIfPinned) return;
        pinned = false;
        leftAt = null;
        panel.Hide();
        item.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    }

    void Clicked()
    {
        if (pinned)
        {
            HidePanel(evenIfPinned: true);
            clickClosed = true;
            return;
        }
        ShowPanel();
        pinned = true;
        Native.ClearClicks();
        hover.Start();
    }

    /// Hover and outside clicks, by polling the cursor while the strip is hovered or the panel is open: WPF's
    /// MouseLeave is unreliable on never-activated layered windows. Stops itself once idle. The global mouse
    /// monitor of the original becomes "a button went down outside both windows".
    void Track()
    {
        var p = Native.Cursor();
        var overItem = Native.Rect(new WindowInteropHelper(this).Handle).Contains(p);
        var overPanel = panel.IsVisible && Native.Rect(new WindowInteropHelper(panel).Handle).Contains(p);
        var now = DateTime.Now;
        enteredAt = overItem ? enteredAt ?? now : null;
        if (!overItem) clickClosed = false;
        if (!panel.IsVisible)
        {
            if (!overItem) hover.Stop();
            else if (!clickClosed && now >= enteredAt!.Value.AddMilliseconds(150)) ShowPanel();
            return;
        }
        if (pinned)
        {
            if (Native.MousePressed() && !overItem && !overPanel) HidePanel(evenIfPinned: true);
            return;
        }
        leftAt = overItem || overPanel ? null : leftAt ?? now;
        if (leftAt is { } l && now >= l.AddMilliseconds(250)) HidePanel(evenIfPinned: false);
    }

    /// Right-click: who made this and where it comes from (the panel itself stays as the original).
    ContextMenu AboutMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string header, Action? a = null)
        {
            var i = new MenuItem { Header = header, IsEnabled = a != null };
            if (a != null) i.Click += (_, _) => a();
            m.Items.Add(i);
            return i;
        }
        Item($"LimitBar {Program.Version} for Windows");
        Item("Product Owner: Umut Çelik");
        Item("X: @palamut62", () => Theme.Open("https://x.com/palamut62"));
        Item("GitHub: palamut62", () => Theme.Open("https://github.com/palamut62"));
        Item("Based on dancik/limitbar (MIT)", () => Theme.Open("https://github.com/dancik/limitbar"));
        m.Items.Add(new Separator());
        Item("Settings…", () => SettingsWindow.Open(store));
        Item("Quit LimitBar", () => Application.Current.Shutdown());
        // open away from the taskbar, never under it
        item.ContextMenuOpening += (_, _) =>
        {
            HidePanel(evenIfPinned: true);
            clickClosed = true;   // the panel must not reopen over the menu
            m.PlacementTarget = item;
            m.Placement = StripRect().Top < SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height / 2
                ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                : System.Windows.Controls.Primitives.PlacementMode.Top;
        };
        return m;
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32")] static extern int GetSystemMetrics(int i);
    [DllImport("user32")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    /// Hidden from Alt+Tab, never activated by a click.
    public static void MakeToolWindow(System.Windows.Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public static RECT Rect(IntPtr h) { GetWindowRect(h, out var r); return r; }

    /// The primary taskbar and its notification area (falls back to the bottom-right of the screen).
    public static RECT Taskbar(out RECT notify)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var tb))
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            tb = new RECT { Left = 0, Top = h - 48, Right = w, Bottom = h };
        }
        var n = tray == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (n == IntPtr.Zero || !GetWindowRect(n, out notify))
            notify = tb with { Left = tb.Right - 200, Top = tb.Bottom - 48 };
        return tb;
    }

    public static void SetTopmost(IntPtr h, int x, int y, int w, int hgt) =>
        SetWindowPos(h, HWND_TOPMOST, x, y, w, hgt, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    /// True when nothing topmost and visible sits above `h` (the taskbar raises itself when clicked).
    public static bool IsTopmostAbove(IntPtr h)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        for (var w = GetWindow(h, 3 /* GW_HWNDPREV */); w != IntPtr.Zero; w = GetWindow(w, 3))
            if (w == tray) return false;
        return true;
    }

    public static bool FullScreenAppInFront()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsWindowVisible(fg) || !GetWindowRect(fg, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        return r.Left <= 0 && r.Top <= 0 && r.Right >= GetSystemMetrics(0) && r.Bottom >= GetSystemMetrics(1);
    }

    public static bool MousePressed() => ((GetAsyncKeyState(0x01) | GetAsyncKeyState(0x02)) & 0x8001) != 0;

    /// Drops the "pressed since last call" bit left over from the click that pinned the panel.
    public static void ClearClicks() { GetAsyncKeyState(0x01); GetAsyncKeyState(0x02); }

    public static POINT Cursor() { GetCursorPos(out var p); return p; }
}
