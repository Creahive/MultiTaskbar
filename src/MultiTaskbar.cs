// MultiTaskbar - full taskbar (pinned apps, per-monitor windows, volume, clock, show desktop)
// on every secondary monitor. Replaces the Windows 11 secondary taskbars while running and
// restores them on exit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MultiTaskbar
{
    static class Program
    {
        public static BarManager Manager;
        public static string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiTaskbar", "MultiTaskbar.log");

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "Local\\MultiTaskbar_SingleInstance", out created))
            {
                if (!created) return;
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { Log(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log(e.ExceptionObject as Exception);
                    if (Manager != null) Manager.RestoreTaskbars();
                };
                Manager = new BarManager();
                try { Application.Run(Manager); }
                finally { Manager.RestoreTaskbars(); }
            }
        }

        public static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now + "  " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ start with Windows

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string SettingsKey = @"Software\MultiTaskbar";
        const string Name = "MultiTaskbar";

        static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(Name, Command);
                    else k.DeleteValue(Name, false);
                }
            }
        }

        // If the exe was moved, point the existing startup entry at the new location.
        public static void FixPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null && k.GetValue(Name) != null && !Command.Equals(k.GetValue(Name) as string, StringComparison.OrdinalIgnoreCase))
                        k.SetValue(Name, Command);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static void AskOnFirstRun()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (k.GetValue("AskedAutostart") != null) return;
                    k.SetValue("AskedAutostart", 1);
                }
                if (Enabled) return;
                using (var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
                {
                    owner.Show();
                    var answer = MessageBox.Show(owner,
                        "Start MultiTaskbar automatically when you sign in to Windows?\n\n" +
                        "You can change this later: right-click an empty spot on a MultiTaskbar bar " +
                        "and choose \"Start with Windows\".",
                        "MultiTaskbar", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer == DialogResult.Yes) Enabled = true;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    // ------------------------------------------------------------------ manager

    class BarManager : ApplicationContext
    {
        readonly List<Bar> bars = new List<Bar>();
        readonly Timer hideTimer = new Timer();
        bool restored;

        public BarManager()
        {
            BuildBars();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionEnding += delegate { RestoreTaskbars(); };
            hideTimer.Interval = 1000;
            hideTimer.Tick += delegate { HideSecondaryTaskbars(); };
            hideTimer.Start();
            HideSecondaryTaskbars();

            Autostart.FixPath();
            var ask = new Timer { Interval = 1000 };
            ask.Tick += delegate { ask.Stop(); ask.Dispose(); Autostart.AskOnFirstRun(); };
            ask.Start();
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            var t = new Timer { Interval = 1500 };
            t.Tick += delegate { t.Stop(); t.Dispose(); BuildBars(); };
            t.Start();
        }

        void BuildBars()
        {
            foreach (var b in bars) { b.AllowClose = true; b.Close(); b.Dispose(); }
            bars.Clear();
            foreach (var s in Screen.AllScreens)
            {
                if (s.Primary) continue;
                var bar = new Bar(s);
                bars.Add(bar);
                bar.Show();
            }
        }

        public void HideSecondaryTaskbars()
        {
            if (restored) return;
            foreach (var h in Native.FindTopWindows("Shell_SecondaryTrayWnd"))
                if (Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_HIDE);
        }

        public void RestoreTaskbars()
        {
            if (restored) return;
            restored = true;
            hideTimer.Stop();
            foreach (var h in Native.FindTopWindows("Shell_SecondaryTrayWnd"))
                Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
        }

        public void ExitApp()
        {
            RestoreTaskbars();
            foreach (var b in bars) { b.AllowClose = true; b.Close(); }
            bars.Clear();
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ bar

    class Item
    {
        public string Kind; // start, pin, win, volume, clock, desktop
        public Rectangle Rect;
        public Bitmap Icon;
        public Pins.Pin Pin;
        public string Exe;
        public string Label; // window title when the button is labeled; null for icon-only
        public List<IntPtr> Windows = new List<IntPtr>();
    }

    class Bar : Form
    {
        const int BTN = 44, LABEL_MAX = 180, LABEL_MIN = 80;
        readonly Screen screen;
        readonly IntPtr hmon;
        readonly Timer timer = new Timer();
        readonly ToolTip tip = new ToolTip();
        readonly Font clockFont = new Font("Segoe UI", 9f);
        readonly Font labelFont = new Font("Segoe UI", 9f);
        readonly Font glyphFont;
        public bool AllowClose;
        List<Item> items = new List<Item>();
        Item hover, pressed;
        string lastSig = "";
        IntPtr lastFg;
        bool hiddenForFullscreen;
        VolumePopup volPopup;

        public Bar(Screen s)
        {
            screen = s;
            var c = new Native.POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
            hmon = Native.MonitorFromPoint(c, 2);
            glyphFont = Glyphs.MakeFont(12f);

            Text = "MultiTaskbar";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            int h = s.Bounds.Bottom - s.WorkingArea.Bottom;
            if (h < 32) h = 48;
            Bounds = new Rectangle(s.Bounds.Left, s.Bounds.Bottom - h, s.Bounds.Width, h);

            tip.ShowAlways = true;
            tip.InitialDelay = 400;
            timer.Interval = 400;
            timer.Tick += delegate { try { Tick(); } catch (Exception ex) { Program.Log(ex); } };
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Tick(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose) { e.Cancel = true; return; }
            timer.Stop();
            base.OnFormClosing(e);
        }

        void Tick()
        {
            IntPtr fg = Native.GetForegroundWindow();
            bool fs = Native.IsFullscreen(fg, hmon, screen.Bounds);
            if (fs != hiddenForFullscreen)
            {
                hiddenForFullscreen = fs;
                Native.ShowWindow(Handle, fs ? Native.SW_HIDE : Native.SW_SHOWNOACTIVATE);
            }
            if (fs) return;

            if (fg != lastFg)
            {
                lastFg = fg;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }

            Pins.RefreshIfChanged();
            Volume.Poll();
            var wins = WindowScanner.WindowsOn(hmon);
            bool labels = TaskbarPrefs.ShowLabels;
            string sig = string.Join(",", wins.Select(w => w.ToInt64() + (labels ? ":" + WindowScanner.Title(w) : "")).ToArray())
                + "|" + fg + "|" + Pins.Version + "|" + DateTime.Now.ToString("t d") + "|" + labels
                + "|" + Volume.Signature + "|" + Theme.Light + Theme.Accent.ToArgb();
            if (sig != lastSig)
            {
                lastSig = sig;
                Relayout(wins);
                Invalidate();
            }
        }

        void Relayout(List<IntPtr> wins)
        {
            var list = new List<Item>();
            int W = ClientSize.Width, H = ClientSize.Height;

            var desk = new Item { Kind = "desktop", Rect = new Rectangle(W - 12, 0, 12, H) };
            var clock = new Item { Kind = "clock", Rect = new Rectangle(desk.Rect.Left - 92, 0, 88, H) };
            var vol = new Item { Kind = "volume", Rect = new Rectangle(clock.Rect.Left - 42, 0, 40, H) };
            int limit = vol.Rect.Left - 8;

            int x = 6;
            list.Add(new Item { Kind = "start", Rect = new Rectangle(x, 0, BTN, H) });
            x += BTN;

            if (TaskbarPrefs.ShowLabels)
            {
                var labeled = LabeledApps(wins);
                int iconOnly = labeled.Count(i => i.Label == null);
                int nLabeled = labeled.Count - iconOnly;
                int w = nLabeled == 0 ? 0 : Math.Min(LABEL_MAX, (limit - x - iconOnly * BTN) / nLabeled);
                if (nLabeled == 0 || w >= LABEL_MIN)
                {
                    foreach (var it in labeled)
                    {
                        int iw = it.Label == null ? BTN : w;
                        if (x + iw > limit) break;
                        it.Rect = new Rectangle(x, 0, iw, H);
                        list.Add(it);
                        x += iw;
                    }
                    FinishLayout(list, vol, clock, desk);
                    return;
                }
                // Too many windows to label: fall back to combined icons, like Windows does.
            }

            var remaining = new List<IntPtr>(wins);
            foreach (var p in Pins.Items)
            {
                if (x + BTN > limit) break;
                var it = new Item { Kind = "pin", Pin = p, Icon = p.Icon, Rect = new Rectangle(x, 0, BTN, H) };
                foreach (var w in wins)
                {
                    if (!remaining.Contains(w)) continue;
                    if (p.Matches(WindowScanner.ExePath(w), WindowScanner.AppId(w)))
                    {
                        it.Windows.Add(w);
                        remaining.Remove(w);
                    }
                }
                list.Add(it);
                x += BTN;
            }

            // Unpinned windows, grouped by executable (in z-order of first appearance)
            var groups = new List<Item>();
            foreach (var w in remaining)
            {
                string exe = WindowScanner.ExePath(w) ?? ("hwnd:" + w);
                var g = groups.FirstOrDefault(i => string.Equals(i.Exe, exe, StringComparison.OrdinalIgnoreCase));
                if (g == null)
                {
                    g = new Item { Kind = "win", Exe = exe, Icon = WindowScanner.Icon(w) };
                    groups.Add(g);
                }
                g.Windows.Add(w);
            }
            foreach (var g in groups)
            {
                if (x + BTN > limit) break;
                g.Rect = new Rectangle(x, 0, BTN, H);
                list.Add(g);
                x += BTN;
            }

            FinishLayout(list, vol, clock, desk);
        }

        void FinishLayout(List<Item> list, Item vol, Item clock, Item desk)
        {
            list.Add(vol);
            list.Add(clock);
            list.Add(desk);

            // keep hover pointing at the equivalent item
            Item newHover = null;
            if (hover != null)
                newHover = list.FirstOrDefault(i => i.Rect == hover.Rect && i.Kind == hover.Kind);
            hover = newHover;
            items = list;
        }

        // "Never combine" layout: one labeled button per window. Pins without windows stay icon-only;
        // unpinned windows follow, kept next to other windows of the same app.
        static List<Item> LabeledApps(List<IntPtr> wins)
        {
            var result = new List<Item>();
            var remaining = new List<IntPtr>(wins);
            foreach (var p in Pins.Items)
            {
                var mine = remaining.Where(w => p.Matches(WindowScanner.ExePath(w), WindowScanner.AppId(w))).ToList();
                if (mine.Count == 0) { result.Add(new Item { Kind = "pin", Pin = p, Icon = p.Icon }); continue; }
                foreach (var w in mine)
                {
                    remaining.Remove(w);
                    var it = new Item { Kind = "pin", Pin = p, Icon = p.Icon, Label = WindowScanner.Title(w) };
                    it.Windows.Add(w);
                    result.Add(it);
                }
            }
            var order = new List<string>();
            foreach (var w in remaining)
            {
                string exe = WindowScanner.ExePath(w) ?? ("hwnd:" + w);
                if (!order.Contains(exe, StringComparer.OrdinalIgnoreCase)) order.Add(exe);
            }
            foreach (var exe in order)
                foreach (var w in remaining.Where(w => string.Equals(WindowScanner.ExePath(w) ?? ("hwnd:" + w), exe, StringComparison.OrdinalIgnoreCase)))
                {
                    var it = new Item { Kind = "win", Exe = exe, Icon = WindowScanner.Icon(w), Label = WindowScanner.Title(w) };
                    it.Windows.Add(w);
                    result.Add(it);
                }
            return result;
        }

        // ---------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool light = Theme.Light;
            Color bg = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28);
            Color fgc = light ? Color.Black : Color.White;
            g.Clear(bg);
            using (var p = new Pen(light ? Color.FromArgb(215, 215, 215) : Color.FromArgb(50, 50, 50)))
                g.DrawLine(p, 0, 0, Width, 0);

            IntPtr fgw = Native.GetForegroundWindow();
            foreach (var it in items)
            {
                bool hov = it == hover;
                bool active = it.Windows.Contains(fgw);
                var r = Rectangle.Inflate(it.Rect, -2, -4);

                if (it.Kind != "desktop" && (hov || active || it.Label != null))
                {
                    int a = (it == pressed) ? 12 : (active ? (hov ? 36 : 28) : (hov ? 20 : 10));
                    using (var b = new SolidBrush(Color.FromArgb(a, fgc)))
                    using (var path = RoundRect(r, 5))
                        g.FillPath(b, path);
                }

                switch (it.Kind)
                {
                    case "start": DrawStart(g, r); break;
                    case "pin":
                    case "win": DrawApp(g, it, r, active, fgc); break;
                    case "volume": DrawCentered(g, Glyphs.ForVolume(), glyphFont, fgc, r); break;
                    case "clock": DrawClock(g, r, fgc); break;
                    case "desktop":
                        using (var p = new Pen(Color.FromArgb(hov ? 140 : 70, fgc)))
                            g.DrawLine(p, it.Rect.Left + 5, 12, it.Rect.Left + 5, Height - 12);
                        break;
                }
            }
        }

        static void DrawStart(Graphics g, Rectangle r)
        {
            int s = 9, gap = 2;
            int x0 = r.Left + (r.Width - (s * 2 + gap)) / 2;
            int y0 = r.Top + (r.Height - (s * 2 + gap)) / 2;
            using (var b = new SolidBrush(Color.FromArgb(0, 120, 212)))
            {
                g.FillRectangle(b, x0, y0, s, s);
                g.FillRectangle(b, x0 + s + gap, y0, s, s);
                g.FillRectangle(b, x0, y0 + s + gap, s, s);
                g.FillRectangle(b, x0 + s + gap, y0 + s + gap, s, s);
            }
        }

        void DrawApp(Graphics g, Item it, Rectangle r, bool active, Color fgc)
        {
            const int sz = 24;
            int iconLeft = it.Label != null ? r.Left + 10 : r.Left + (r.Width - sz) / 2;
            var ir = new Rectangle(iconLeft, r.Top + (r.Height - sz) / 2 - 1, sz, sz);
            if (it.Icon != null) g.DrawImage(it.Icon, ir);
            else using (var b = new SolidBrush(Color.FromArgb(90, fgc))) g.FillEllipse(b, ir);

            if (it.Label != null)
            {
                var tr = new Rectangle(ir.Right + 8, r.Top, r.Right - ir.Right - 14, r.Height);
                if (tr.Width > 8)
                    TextRenderer.DrawText(g, it.Label, labelFont, tr, fgc,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (it.Windows.Count > 0)
            {
                int w = active ? 16 : 6;
                var ind = new Rectangle(ir.Left + (ir.Width - w) / 2, r.Bottom - 3, w, 3);
                using (var b = new SolidBrush(active ? Theme.Accent : Color.FromArgb(150, fgc)))
                using (var path = RoundRect(ind, 1))
                    g.FillPath(b, path);
            }
        }

        void DrawClock(Graphics g, Rectangle r, Color fgc)
        {
            var now = DateTime.Now;
            using (var b = new SolidBrush(fgc))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                var top = new Rectangle(r.Left, r.Top, r.Width - 6, r.Height / 2 + 1);
                var bot = new Rectangle(r.Left, r.Top + r.Height / 2 - 1, r.Width - 6, r.Height / 2);
                sf.LineAlignment = StringAlignment.Far;
                g.DrawString(now.ToString("t"), clockFont, b, top, sf);
                sf.LineAlignment = StringAlignment.Near;
                g.DrawString(now.ToString("d"), clockFont, b, bot, sf);
            }
        }

        static void DrawCentered(Graphics g, string text, Font f, Color c, Rectangle r)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, b, r, sf);
        }

        public static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------------------------------------------------- input

        Item HitTest(Point p) { return items.FirstOrDefault(i => i.Rect.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var it = HitTest(e.Location);
            if (it != hover)
            {
                hover = it;
                Invalidate();
                tip.Hide(this);
                tip.SetToolTip(this, TooltipFor(it));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = null; pressed = null;
            tip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            pressed = HitTest(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var it = HitTest(e.Location);
            var was = pressed;
            pressed = null;
            Invalidate();
            if (it == null || it != was)
            {
                if (it == null && e.Button == MouseButtons.Right) ShowBarMenu(e.Location);
                return;
            }
            try { OnItemClick(it, e); } catch (Exception ex) { Program.Log(ex); }
            lastSig = ""; // force refresh
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var it = HitTest(e.Location);
            if (it != null && it.Kind == "volume")
            {
                Volume.Change(e.Delta > 0 ? 0.02f : -0.02f);
                lastSig = "";
            }
        }

        string TooltipFor(Item it)
        {
            if (it == null) return null;
            switch (it.Kind)
            {
                case "start": return "Start";
                case "desktop": return "Show desktop";
                case "clock": return DateTime.Now.ToString("D") + "\n" + DateTime.Now.ToString("T");
                case "volume":
                    if (!Volume.Available) return "No audio device";
                    return "Volume: " + (int)Math.Round(Volume.Level * 100) + "%" + (Volume.Muted ? " (muted)" : "");
                case "pin":
                case "win":
                    var lines = new List<string>();
                    if (it.Pin != null) lines.Add(it.Pin.Name);
                    foreach (var w in it.Windows) lines.Add((it.Pin != null ? "  " : "") + WindowScanner.Title(w));
                    return string.Join("\n", lines.ToArray());
            }
            return null;
        }

        void OnItemClick(Item it, MouseEventArgs e)
        {
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            switch (it.Kind)
            {
                case "start":
                    if (e.Button == MouseButtons.Right) { Launch("explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}"); return; }
                    Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
                    Native.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
                    return;
                case "desktop":
                    Shell.ToggleDesktop();
                    return;
                case "clock":
                    Launch("ms-settings:dateandtime", null);
                    return;
                case "volume":
                    if (e.Button == MouseButtons.Middle) { Volume.ToggleMute(); return; }
                    if (e.Button == MouseButtons.Right) { Launch("ms-settings:sound", null); return; }
                    ToggleVolumePopup(it);
                    return;
                case "pin":
                case "win":
                    if (e.Button == MouseButtons.Right) { ShowAppMenu(it, e.Location); return; }
                    if (e.Button == MouseButtons.Middle || (shift && e.Button == MouseButtons.Left) || it.Windows.Count == 0)
                    {
                        LaunchNew(it);
                        return;
                    }
                    ToggleOrCycle(it.Windows);
                    return;
            }
        }

        static void ToggleOrCycle(List<IntPtr> wins)
        {
            IntPtr fg = Native.GetForegroundWindow();
            int idx = wins.IndexOf(fg);
            if (wins.Count == 1)
            {
                if (idx == 0 && !Native.IsIconic(fg)) Native.ShowWindow(fg, Native.SW_MINIMIZE);
                else Native.Activate(wins[0]);
            }
            else
            {
                Native.Activate(idx >= 0 ? wins[(idx + 1) % wins.Count] : wins[0]);
            }
        }

        static void LaunchNew(Item it)
        {
            if (it.Pin != null) it.Pin.Launch();
            else if (it.Exe != null && File.Exists(it.Exe)) Launch(it.Exe, null);
        }

        static void Launch(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                Process.Start(psi);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void ToggleVolumePopup(Item it)
        {
            if (volPopup != null && !volPopup.IsDisposed && volPopup.Visible) { volPopup.Close(); return; }
            volPopup = new VolumePopup();
            var pt = PointToScreen(new Point(it.Rect.Left + it.Rect.Width / 2, 0));
            int x = Math.Max(screen.Bounds.Left + 8, Math.Min(pt.X - volPopup.Width / 2, screen.Bounds.Right - volPopup.Width - 8));
            volPopup.Location = new Point(x, Top - volPopup.Height - 10);
            volPopup.Show();
            volPopup.Activate();
            Native.SetForegroundWindow(volPopup.Handle);
        }

        void ShowAppMenu(Item it, Point at)
        {
            var m = new ContextMenuStrip();
            foreach (var w in it.Windows)
            {
                var hw = w;
                string t = WindowScanner.Title(hw);
                if (t.Length > 60) t = t.Substring(0, 57) + "...";
                m.Items.Add(t, null, delegate { Native.Activate(hw); });
            }
            if (it.Windows.Count > 0) m.Items.Add(new ToolStripSeparator());
            string name = it.Pin != null ? it.Pin.Name : (it.Exe != null ? Path.GetFileNameWithoutExtension(it.Exe) : "app");
            m.Items.Add("Open new " + name, null, delegate { LaunchNew(it); });
            if (it.Windows.Count > 0)
            {
                var ws = new List<IntPtr>(it.Windows);
                m.Items.Add(ws.Count > 1 ? "Close all windows" : "Close window", null, delegate
                {
                    foreach (var w in ws) Native.PostMessage(w, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                });
            }
            m.Items.Add(new ToolStripSeparator());
            AddBarItems(m);
            ShowMenu(m, at);
        }

        void ShowBarMenu(Point at)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Task Manager", null, delegate { Launch("taskmgr.exe", null); });
            m.Items.Add(new ToolStripSeparator());
            AddBarItems(m);
            ShowMenu(m, at);
        }

        void AddBarItems(ContextMenuStrip m)
        {
            var auto = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.Enabled };
            auto.Click += delegate
            {
                try { Autostart.Enabled = !auto.Checked; } catch (Exception ex) { Program.Log(ex); }
            };
            m.Items.Add(auto);
            m.Items.Add("Taskbar settings", null, delegate { Launch("ms-settings:taskbar", null); });
            m.Items.Add("Exit MultiTaskbar", null, delegate { Program.Manager.ExitApp(); });
        }

        void ShowMenu(ContextMenuStrip m, Point at)
        {
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            Native.SetForegroundWindow(Handle);
            m.Show(this, at, ToolStripDropDownDirection.AboveRight);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); tip.Dispose(); clockFont.Dispose(); labelFont.Dispose(); glyphFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ volume popup

    class VolumePopup : Form
    {
        readonly Timer timer = new Timer();
        readonly Font glyph = Glyphs.MakeFont(14f);
        readonly Font text = new Font("Segoe UI", 10f);
        readonly Rectangle speaker = new Rectangle(10, 12, 40, 40);
        readonly Rectangle track = new Rectangle(64, 30, 186, 4);
        bool dragging;

        public VolumePopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(318, 64);
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            timer.Interval = 300;
            timer.Tick += delegate { Volume.Poll(); Invalidate(); };
            timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right) Volume.Change(0.02f);
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left) Volume.Change(-0.02f);
            else if (e.KeyCode == Keys.M || e.KeyCode == Keys.Space) Volume.ToggleMute();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            bool light = Theme.Light;
            Color fgc = light ? Color.Black : Color.White;
            g.Clear(light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44));
            using (var p = new Pen(light ? Color.FromArgb(210, 210, 210) : Color.FromArgb(70, 70, 70)))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            using (var b = new SolidBrush(fgc))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(Glyphs.ForVolume(), glyph, b, speaker, sf);
                string pct = Volume.Available ? ((int)Math.Round(Volume.Level * 100)).ToString() : "--";
                g.DrawString(pct, text, b, new Rectangle(track.Right + 8, 0, Width - track.Right - 14, Height), sf);
            }

            float lvl = Volume.Available ? Volume.Level : 0f;
            int fillX = track.Left + (int)(track.Width * lvl);
            using (var b = new SolidBrush(Color.FromArgb(light ? 110 : 140, fgc)))
            using (var path = Bar.RoundRect(track, 2)) g.FillPath(b, path);
            var fill = new Rectangle(track.Left, track.Top, Math.Max(4, fillX - track.Left), track.Height);
            Color accent = Volume.Muted ? Color.FromArgb(130, 130, 130) : Theme.Accent;
            using (var b = new SolidBrush(accent))
            using (var path = Bar.RoundRect(fill, 2)) g.FillPath(b, path);
            var thumb = new Rectangle(fillX - 10, track.Top + track.Height / 2 - 10, 20, 20);
            using (var b = new SolidBrush(light ? Color.White : Color.FromArgb(69, 69, 69))) g.FillEllipse(b, thumb);
            var inner = Rectangle.Inflate(thumb, -5, -5);
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, inner);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (speaker.Contains(e.Location)) { Volume.ToggleMute(); Invalidate(); return; }
            if (Rectangle.Inflate(track, 12, 16).Contains(e.Location)) { dragging = true; Capture = true; SetFromX(e.X); }
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Volume.Change(e.Delta > 0 ? 0.02f : -0.02f);
            Invalidate();
        }

        void SetFromX(int x)
        {
            float v = (x - track.Left) / (float)track.Width;
            Volume.Set(Math.Max(0f, Math.Min(1f, v)));
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); glyph.Dispose(); text.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ pinned apps

    static class Pins
    {
        public class Pin
        {
            public string Name, LinkPath, Target, AppId;
            public Bitmap Icon;

            public bool Matches(string exe, string appId)
            {
                if (AppId != null) return appId != null && string.Equals(AppId, appId, StringComparison.OrdinalIgnoreCase);
                return Target != null && exe != null && string.Equals(Target, exe, StringComparison.OrdinalIgnoreCase);
            }

            public void Launch()
            {
                try
                {
                    if (AppId != null) Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + AppId) { UseShellExecute = true });
                    else Process.Start(new ProcessStartInfo(LinkPath) { UseShellExecute = true });
                }
                catch (Exception ex) { Program.Log(ex); }
            }
        }

        public static List<Pin> Items = new List<Pin>();
        public static int Version;
        static string lastSig;
        static DateTime lastCheck = DateTime.MinValue;
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

        public static void RefreshIfChanged()
        {
            if ((DateTime.Now - lastCheck).TotalSeconds < 3) return;
            lastCheck = DateTime.Now;
            byte[] blob = ReadBlob();
            var files = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.lnk") : new string[0];
            string sig = string.Join("|", files.Select(f => f + File.GetLastWriteTimeUtc(f).Ticks).ToArray())
                + "#" + (blob == null ? "" : Convert.ToBase64String(System.Security.Cryptography.MD5.Create().ComputeHash(blob)));
            if (sig == lastSig) return;
            lastSig = sig;
            try { Items = Load(files, blob); } catch (Exception ex) { Program.Log(ex); }
            Version++;
        }

        static byte[] ReadBlob()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(TaskbandKey))
                return k == null ? null : k.GetValue("Favorites") as byte[];
        }

        // Order and packaged-app pins come from the Taskband "Favorites" blob, which stores the pins in
        // taskbar order. Each entry carries either the .lnk file name or a packaged app's AUMID.
        static List<Pin> Load(string[] files, byte[] blob)
        {
            var found = new List<KeyValuePair<int, Pin>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lnkNames = files.ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);

            if (blob != null)
            {
                var aumid = new Regex(@"[\w\.\-]+_[a-z0-9]{13}![\w\.\-]+");
                for (int off = 0; off <= 1; off++)
                {
                    string s = Encoding.Unicode.GetString(blob, off, blob.Length - off);
                    foreach (Match m in aumid.Matches(s))
                    {
                        if (!seen.Add("aumid:" + m.Value)) continue;
                        found.Add(new KeyValuePair<int, Pin>(m.Index * 2 + off, MakeAumidPin(m.Value)));
                    }
                    foreach (var kv in lnkNames)
                    {
                        int i = s.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase);
                        if (i < 0 || !seen.Add("lnk:" + kv.Key)) continue;
                        found.Add(new KeyValuePair<int, Pin>(i * 2 + off, MakeLinkPin(kv.Value)));
                    }
                }
            }
            // Shortcuts not referenced by the blob go at the end
            int tail = int.MaxValue / 2;
            foreach (var f in files.OrderBy(f => f))
                if (seen.Add("lnk:" + Path.GetFileName(f)))
                    found.Add(new KeyValuePair<int, Pin>(tail++, MakeLinkPin(f)));

            return found.OrderBy(kv => kv.Key).Select(kv => kv.Value).Where(p => p != null).ToList();
        }

        static Pin MakeLinkPin(string lnk)
        {
            var p = new Pin { LinkPath = lnk, Name = Path.GetFileNameWithoutExtension(lnk) };
            p.Target = Shell.ShortcutTarget(lnk);
            if (string.IsNullOrEmpty(p.Target) && p.Name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase))
                p.Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (string.IsNullOrEmpty(p.Target)) p.Target = null;
            p.Icon = ShellIcons.Get(lnk, 32);
            return p;
        }

        static Pin MakeAumidPin(string id)
        {
            string parsing = "shell:AppsFolder\\" + id;
            var icon = ShellIcons.Get(parsing, 32);
            string name = ShellIcons.DisplayName(parsing);
            if (icon == null && name == null) return null; // app no longer installed
            return new Pin { AppId = id, Name = name ?? id, Icon = icon };
        }
    }

    // ------------------------------------------------------------------ windows

    static class WindowScanner
    {
        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
        static readonly Dictionary<uint, string> exeCache = new Dictionary<uint, string>();
        static readonly Dictionary<uint, string> appIdCache = new Dictionary<uint, string>();
        static readonly Dictionary<IntPtr, Bitmap> iconCache = new Dictionary<IntPtr, Bitmap>();
        static readonly HashSet<string> skipClasses = new HashSet<string> { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };
        static DateTime lastPurge = DateTime.Now;

        public static List<IntPtr> WindowsOn(IntPtr hmon)
        {
            var list = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (IsTaskbarWindow(h) && Native.MonitorFromWindow(h, 2) == hmon) list.Add(h);
                return true;
            }, IntPtr.Zero);
            if ((DateTime.Now - lastPurge).TotalSeconds > 30) Purge();
            return list;
        }

        static bool IsTaskbarWindow(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (!appWindow)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
                if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
            }
            if (Native.GetWindowTextLength(h) == 0) return false;
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out cloaked, 4) == 0 && cloaked != 0) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == myPid) return false;
            if (skipClasses.Contains(Native.ClassName(h))) return false;
            return true;
        }

        // For UWP windows hosted by ApplicationFrameHost, the real app lives in a child window.
        static uint RealPid(IntPtr h)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (Native.ClassName(h) != "ApplicationFrameWindow") return pid;
            uint child = 0;
            Native.EnumChildWindows(h, delegate(IntPtr c, IntPtr l)
            {
                uint cp;
                Native.GetWindowThreadProcessId(c, out cp);
                if (cp != pid) { child = cp; return false; }
                return true;
            }, IntPtr.Zero);
            return child != 0 ? child : pid;
        }

        public static string ExePath(IntPtr h)
        {
            uint pid = RealPid(h);
            string p;
            if (exeCache.TryGetValue(pid, out p)) return p;
            p = null;
            IntPtr hp = Native.OpenProcess(0x1000, false, pid);
            if (hp != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (Native.QueryFullProcessImageName(hp, 0, sb, ref size)) p = sb.ToString();
                uint len = 512;
                var id = new StringBuilder((int)len);
                appIdCache[pid] = Native.GetApplicationUserModelId(hp, ref len, id) == 0 ? id.ToString() : null;
                Native.CloseHandle(hp);
            }
            exeCache[pid] = p;
            return p;
        }

        public static string AppId(IntPtr h)
        {
            uint pid = RealPid(h);
            ExePath(h);
            string id;
            return appIdCache.TryGetValue(pid, out id) ? id : null;
        }

        public static string Title(IntPtr h)
        {
            int n = Native.GetWindowTextLength(h);
            var sb = new StringBuilder(n + 1);
            Native.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static Bitmap Icon(IntPtr h)
        {
            Bitmap b;
            if (iconCache.TryGetValue(h, out b)) return b;
            b = null;
            string exe = ExePath(h);
            string appId = AppId(h);
            if (appId != null) b = ShellIcons.Get("shell:AppsFolder\\" + appId, 32);
            if (b == null)
            {
                IntPtr hi = GetWindowIcon(h);
                if (hi != IntPtr.Zero) { try { using (var ic = System.Drawing.Icon.FromHandle(hi)) b = ic.ToBitmap(); } catch { } }
            }
            if (b == null && exe != null) b = ShellIcons.Get(exe, 32);
            iconCache[h] = b;
            return b;
        }

        static IntPtr GetWindowIcon(IntPtr h)
        {
            IntPtr r;
            foreach (int kind in new[] { 1 /*ICON_BIG*/, 2 /*ICON_SMALL2*/, 0 /*ICON_SMALL*/ })
            {
                if (Native.SendMessageTimeout(h, 0x007F /*WM_GETICON*/, new IntPtr(kind), IntPtr.Zero, 2 /*SMTO_ABORTIFHUNG*/, 100, out r) != IntPtr.Zero && r != IntPtr.Zero)
                    return r;
            }
            r = Native.GetClassLongPtr(h, -14 /*GCLP_HICON*/);
            if (r != IntPtr.Zero) return r;
            return Native.GetClassLongPtr(h, -34 /*GCLP_HICONSM*/);
        }

        static void Purge()
        {
            lastPurge = DateTime.Now;
            foreach (var h in iconCache.Keys.ToList())
                if (!Native.IsWindow(h)) iconCache.Remove(h);
            exeCache.Clear();
            appIdCache.Clear();
        }
    }

    // ------------------------------------------------------------------ shell helpers

    static class Shell
    {
        static object shellApp, wsh;

        public static void ToggleDesktop()
        {
            try
            {
                if (shellApp == null) shellApp = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                shellApp.GetType().InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shellApp, null);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static string ShortcutTarget(string lnk)
        {
            try
            {
                if (wsh == null) wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                object sc = wsh.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, wsh, new object[] { lnk });
                string t = (string)sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                Marshal.ReleaseComObject(sc);
                return t;
            }
            catch { return null; }
        }
    }

    static class ShellIcons
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(Native.SIZE size, int flags, out IntPtr phbm);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        static object Create(string parsing, Guid iid)
        {
            object o;
            return SHCreateItemFromParsingName(parsing, IntPtr.Zero, ref iid, out o) == 0 ? o : null;
        }

        public static string DisplayName(string parsing)
        {
            try
            {
                var item = Create(parsing, typeof(IShellItem).GUID) as IShellItem;
                if (item == null) return null;
                IntPtr p;
                string name = null;
                if (item.GetDisplayName(0 /*SIGDN_NORMALDISPLAY*/, out p) == 0) { name = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); }
                Marshal.ReleaseComObject(item);
                return name;
            }
            catch { return null; }
        }

        public static Bitmap Get(string parsing, int size)
        {
            try
            {
                var f = Create(parsing, typeof(IShellItemImageFactory).GUID) as IShellItemImageFactory;
                if (f == null) return null;
                IntPtr hbm;
                int hr = f.GetImage(new Native.SIZE { cx = size, cy = size }, 0x4 /*SIIGBF_ICONONLY*/, out hbm);
                Marshal.ReleaseComObject(f);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                try { return FromHBitmap(hbm); }
                finally { Native.DeleteObject(hbm); }
            }
            catch { return null; }
        }

        // Copy a 32bpp DIB section keeping its alpha channel (Image.FromHbitmap drops it).
        static Bitmap FromHBitmap(IntPtr hbm)
        {
            var ds = new Native.DIBSECTION();
            if (Native.GetObject(hbm, Marshal.SizeOf(typeof(Native.DIBSECTION)), ref ds) == 0 || ds.dsBm.bmBitsPixel != 32 || ds.dsBm.bmBits == IntPtr.Zero)
                return Image.FromHbitmap(hbm);
            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            bool bottomUp = ds.dsBmih.biHeight > 0;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int src = bottomUp ? (h - 1 - y) : y;
                Marshal.Copy(new IntPtr(ds.dsBm.bmBits.ToInt64() + (long)src * stride), row, 0, stride);
                Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), Math.Min(stride, data.Stride));
            }
            bmp.UnlockBits(data);
            return bmp;
        }
    }

    // ------------------------------------------------------------------ theme & glyphs

    static class Theme
    {
        static DateTime last = DateTime.MinValue;
        static bool light;
        static Color accent = Color.FromArgb(0, 120, 212);

        static void Refresh()
        {
            if ((DateTime.Now - last).TotalSeconds < 2) return;
            last = DateTime.Now;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int)
                    {
                        uint c = unchecked((uint)(int)v); // ABGR
                        accent = Color.FromArgb(255, (int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
                    }
                }
            }
            catch { }
        }

        public static bool Light { get { Refresh(); return light; } }
        public static Color Accent { get { Refresh(); return accent; } }
    }

    // Mirrors Windows' "Combine taskbar buttons and hide labels" setting for other taskbars:
    // 0 = always combine (icons only), 1 = when taskbar is full, 2 = never (labels).
    static class TaskbarPrefs
    {
        static DateTime last = DateTime.MinValue;
        static bool labels;

        public static bool ShowLabels
        {
            get
            {
                if ((DateTime.Now - last).TotalSeconds >= 2)
                {
                    last = DateTime.Now;
                    try
                    {
                        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                        {
                            object v = k == null ? null : (k.GetValue("MMTaskbarGlomLevel") ?? k.GetValue("TaskbarGlomLevel"));
                            labels = v is int && (int)v != 0;
                        }
                    }
                    catch { }
                }
                return labels;
            }
        }
    }

    static class Glyphs
    {
        public static Font MakeFont(float size)
        {
            var f = new Font("Segoe Fluent Icons", size);
            if (f.Name != "Segoe Fluent Icons") { f.Dispose(); f = new Font("Segoe MDL2 Assets", size); }
            return f;
        }

        public static string ForVolume()
        {
            if (!Volume.Available || Volume.Muted) return "";
            float v = Volume.Level;
            if (v <= 0.001f) return "";
            if (v < 0.34f) return "";
            if (v < 0.67f) return "";
            return "";
        }
    }

    // ------------------------------------------------------------------ audio

    static class Volume
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        public static float Level;
        public static bool Muted, Available;
        static IAudioEndpointVolume ep;
        static DateTime lastPoll = DateTime.MinValue, lastEndpoint = DateTime.MinValue;

        public static string Signature { get { return Available + ":" + (int)Math.Round(Level * 100) + ":" + Muted; } }

        static IAudioEndpointVolume Endpoint()
        {
            // re-acquire periodically so a change of default device is picked up
            if (ep != null && (DateTime.Now - lastEndpoint).TotalSeconds < 3) return ep;
            lastEndpoint = DateTime.Now;
            try
            {
                if (ep != null) { Marshal.ReleaseComObject(ep); ep = null; }
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o) != 0) return null;
                ep = o as IAudioEndpointVolume;
                Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
            catch { ep = null; }
            return ep;
        }

        public static void Poll()
        {
            if ((DateTime.Now - lastPoll).TotalMilliseconds < 250) return;
            lastPoll = DateTime.Now;
            Read();
        }

        static void Read()
        {
            var e = Endpoint();
            if (e == null) { Available = false; return; }
            float l; bool m;
            if (e.GetMasterVolumeLevelScalar(out l) != 0 || e.GetMute(out m) != 0) { Available = false; ep = null; return; }
            Level = l; Muted = m; Available = true;
        }

        public static void Set(float v)
        {
            var e = Endpoint();
            if (e == null) return;
            Guid g = Guid.Empty;
            e.SetMasterVolumeLevelScalar(v, ref g);
            if (Muted && v > 0) e.SetMute(false, ref g);
            Read();
        }

        public static void Change(float delta)
        {
            Read();
            if (Available) Set(Math.Max(0f, Math.Min(1f, (float)Math.Round((Level + delta) * 50) / 50f)));
        }

        public static void ToggleMute()
        {
            var e = Endpoint();
            if (e == null) return;
            Read();
            Guid g = Guid.Empty;
            e.SetMute(!Muted, ref g);
            Read();
        }
    }

    // ------------------------------------------------------------------ native

    static class Native
    {
        public const int SW_HIDE = 0, SW_MINIMIZE = 6, SW_SHOWNOACTIVATE = 4, SW_RESTORE = 9;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        public const uint GW_OWNER = 4;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DIBSECTION
        {
            public BITMAP dsBm;
            public BITMAPINFOHEADER dsBmih;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] dsBitfields;
            public IntPtr dshSection;
            public uint dsOffset;
        }

        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetApplicationUserModelId(IntPtr h, ref uint len, StringBuilder sb);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);

        public static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static List<IntPtr> FindTopWindows(string cls)
        {
            var list = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l) { if (ClassName(h) == cls) list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static void Activate(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            if (SetForegroundWindow(h) && GetForegroundWindow() == h) return;
            uint dummy;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out dummy);
            uint me = GetCurrentThreadId();
            AttachThreadInput(me, fgThread, true);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            AttachThreadInput(me, fgThread, false);
        }

        public static bool IsFullscreen(IntPtr fg, IntPtr hmon, Rectangle b)
        {
            if (fg == IntPtr.Zero) return false;
            string cls = ClassName(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            if (MonitorFromWindow(fg, 2) != hmon) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }
    }
}
