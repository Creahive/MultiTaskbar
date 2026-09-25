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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("MultiTaskbar")]
[assembly: AssemblyProduct("MultiTaskbar")]
[assembly: AssemblyVersion(MultiTaskbar.Program.Version)]
[assembly: AssemblyFileVersion(MultiTaskbar.Program.Version)]

namespace MultiTaskbar
{
    static class Program
    {
        public const string Version = "1.4.0";
        public const string Repo = "Creahive/MultiTaskbar";

        public static BarManager Manager;
        public static string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiTaskbar", "MultiTaskbar.log");

        [STAThread]
        static void Main(string[] args)
        {
            // Each bar sizes itself to the monitor it sits on, so the process must be told about
            // per-monitor DPI before any window exists.
            // The call exists from Windows 10 1703 on; older builds get the single-DPI version.
            try
            {
                if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4) /*PER_MONITOR_AWARE_V2*/))
                    Native.SetProcessDPIAware();
            }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            // Updates the exe in place and exits, for scripted deployments. Bars already running
            // keep the old build until they are restarted.
            if (Array.IndexOf(args, "--update") >= 0) { Updater.RunHeadless(); return; }
            if (Array.IndexOf(args, "--uninstall") >= 0) { Uninstall.Run(); return; }

            // Sits outside the app, waiting for it to end, and gives the Windows taskbars back if
            // it ended without doing so itself.
            int watch = Arg(args, "--watch");
            if (watch > 0) { Guard.Watch(watch); return; }

            bool waitForPrevious = Array.IndexOf(args, "--wait") >= 0;
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "Local\\MultiTaskbar_SingleInstance", out created))
            {
                // After an update the old copy is still shutting down, so wait for it to let go.
                if (!created && waitForPrevious)
                    try { created = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
                    catch (System.Threading.AbandonedMutexException) { created = true; }
                if (!created) return;
                Updater.CleanUp();
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

        static int Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            int value;
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out value) ? value : 0;
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

    // ------------------------------------------------------------------ settings

    static class Settings
    {
        public const string Key = @"Software\MultiTaskbar";

        public static object Get(string name)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? null : k.GetValue(name); }
            catch { return null; }
        }

        public static void Set(string name, object value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value); }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static bool Flag(string name, bool fallback)
        {
            object v = Get(name);
            return v is int ? (int)v != 0 : fallback;
        }
    }

    // ------------------------------------------------------------------ watchdog

    // The Windows taskbars on the other monitors stay hidden while MultiTaskbar runs. If the
    // process is killed, nothing inside it can bring them back, so a second, idle copy waits for
    // the first to end and restores them when it did not exit cleanly.
    static class Guard
    {
        const string RunningFlag = "Running";

        public static void MarkRunning() { Settings.Set(RunningFlag, 1); }
        public static void MarkStopped() { Settings.Set(RunningFlag, 0); }

        public static void Start()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                    "--watch " + Process.GetCurrentProcess().Id)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static void Watch(int pid)
        {
            try
            {
                Process p = null;
                try { p = Process.GetProcessById(pid); } catch { }
                if (p != null) { p.WaitForExit(); p.Dispose(); }
                if (!Settings.Flag(RunningFlag, false)) return;   // it put the taskbars back itself
                ShowTaskbars();
                MarkStopped();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static void ShowTaskbars()
        {
            foreach (var h in Native.FindTopWindows("Shell_SecondaryTrayWnd"))
                Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
        }
    }

    // ------------------------------------------------------------------ updates

    // Asks GitHub once a day for the newest release, and installs it when the user says so.
    // Nothing but the request itself leaves this machine, and the check can be turned off.
    static class Updater
    {
        const string AutoSetting = "CheckForUpdates";
        const string LastCheckSetting = "LastUpdateCheck";
        static readonly object gate = new object();
        static bool busy;

        public static string NewVersion;   // tag of a newer release, once one is found
        static string downloadUrl, checksumUrl;

        public static bool AutoCheck
        {
            get { return Settings.Flag(AutoSetting, true); }
            set { Settings.Set(AutoSetting, value ? 1 : 0); }
        }

        // The previous copy of the exe, left behind by an update because a running file
        // can be renamed but not deleted.
        public static void CleanUp()
        {
            try
            {
                string old = Application.ExecutablePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }

        public static void CheckDaily(Control ui)
        {
            if (!AutoCheck || NewVersion != null) return;
            var last = Settings.Get(LastCheckSetting) as string;
            DateTime when;
            if (last != null && DateTime.TryParse(last, out when) && (DateTime.UtcNow - when).TotalHours < 24) return;
            Settings.Set(LastCheckSetting, DateTime.UtcNow.ToString("o"));
            Check(ui, null);
        }

        // done(newerTagOrNull, error) runs on the UI thread; pass null to check quietly.
        public static void Check(Control ui, Action<string, Exception> done)
        {
            lock (gate)
            {
                if (busy) return;
                busy = true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null;
                Exception error = null;
                try { tag = Fetch(); }
                catch (Exception ex) { error = ex; Program.Log(ex); }
                lock (gate) busy = false;
                string result = tag;
                try
                {
                    if (ui != null && ui.IsHandleCreated)
                        ui.BeginInvoke(new Action(delegate { if (done != null) done(result, error); }));
                    else if (done != null) done(result, error);
                }
                catch (Exception ex) { Program.Log(ex); }
            });
        }

        static string Fetch()
        {
            string json = Get("https://api.github.com/repos/" + Program.Repo + "/releases/latest");
            // The payload is small and comes from a repository we control, so it is read with
            // patterns rather than pulling in a JSON library.
            var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tagMatch.Success) return null;
            string tag = tagMatch.Groups[1].Value;
            Version latest, current = new Version(Program.Version);
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out latest)) return null;
            if (latest <= current) return null;

            downloadUrl = checksumUrl = null;
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string url = m.Groups[1].Value;
                if (url.EndsWith("MultiTaskbar.exe", StringComparison.OrdinalIgnoreCase)) downloadUrl = url;
                else if (url.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) checksumUrl = url;
            }
            if (downloadUrl == null) return null;
            NewVersion = tag;
            return tag;
        }

        // done(installed, error) runs on the UI thread. On success the app restarts itself.
        public static void Install(Control ui, Action<bool, Exception> done)
        {
            if (downloadUrl == null) return;
            string url = downloadUrl, sums = checksumUrl;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string temp = null;
                Exception error = null;
                try
                {
                    temp = Path.Combine(Path.GetTempPath(), "MultiTaskbar.update.exe");
                    Download(url, temp);
                    if (sums != null) Verify(temp, Get(sums));
                }
                catch (Exception ex) { error = ex; Program.Log(ex); }
                string file = temp;
                Exception err = error;
                try
                {
                    ui.BeginInvoke(new Action(delegate
                    {
                        if (err != null) { done(false, err); return;  }
                        try { Swap(file, true); done(true, null); }
                        catch (Exception ex) { Program.Log(ex); done(false, ex); }
                    }));
                }
                catch (Exception ex) { Program.Log(ex); }
            });
        }

        // A running exe cannot be replaced, but it can be renamed out of the way.
        static void Swap(string newExe, bool restart)
        {
            string exe = Application.ExecutablePath, old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(newExe, exe); }
            catch { File.Move(old, exe); throw; }
            try { File.Delete(newExe); } catch { }
            if (!restart) return;
            Process.Start(new ProcessStartInfo(exe, "--wait") { UseShellExecute = false });
            Program.Manager.ExitApp();
        }

        public static void RunHeadless()
        {
            try
            {
                CleanUp();
                string tag = Fetch();
                if (tag == null) { Note("already at the latest version (" + Program.Version + ")"); return; }
                string temp = Path.Combine(Path.GetTempPath(), "MultiTaskbar.update.exe");
                Download(downloadUrl, temp);
                if (checksumUrl != null) Verify(temp, Get(checksumUrl));
                Swap(temp, false);
                Note("updated to " + tag);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        static void Note(string what)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Program.LogPath));
                File.AppendAllText(Program.LogPath, DateTime.Now + "  update: " + what + Environment.NewLine);
            }
            catch { }
        }

        static void Verify(string file, string sums)
        {
            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var s = File.OpenRead(file))
                actual = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
            foreach (var line in sums.Split('\n'))
            {
                if (line.IndexOf("MultiTaskbar.exe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string expected = line.Trim().Split(' ')[0];
                if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidDataException("The downloaded file does not match its published checksum.");
            }
            throw new InvalidDataException("The release has no checksum for MultiTaskbar.exe.");
        }

        static void Prepare()
        {
            // .NET Framework 4 still defaults to older, refused protocols.
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; }
            catch { }
        }

        static string Get(string url)
        {
            Prepare();
            var req = Request(url);
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream()))
                return r.ReadToEnd();
        }

        static void Download(string url, string path)
        {
            Prepare();
            var req = Request(url);
            using (var resp = req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(path))
                src.CopyTo(dst);
        }

        static System.Net.HttpWebRequest Request(string url)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.UserAgent = "MultiTaskbar/" + Program.Version;   // GitHub refuses requests without one
            req.Timeout = 20000;
            req.ReadWriteTimeout = 60000;
            return req;
        }
    }

    // ------------------------------------------------------------------ update dialogs

    static class UpdateUi
    {
        public static void Check(Control ui)
        {
            Updater.Check(ui, delegate(string tag, Exception error)
            {
                if (error != null)
                    Msg(ui, "Could not reach GitHub to check for updates.\n\n" + error.Message, MessageBoxIcon.Warning);
                else if (tag == null)
                    Msg(ui, "MultiTaskbar " + Program.Version + " is the latest version.", MessageBoxIcon.Information);
                else if (Ask(ui, "MultiTaskbar " + tag + " is available. You have " + Program.Version + ".\n\n" +
                                 "Download it and restart now?"))
                    Install(ui);
            });
        }

        public static void Install(Control ui)
        {
            Updater.Install(ui, delegate(bool ok, Exception error)
            {
                if (!ok) Msg(ui, "The update could not be installed.\n\n" + error.Message, MessageBoxIcon.Warning);
            });
        }

        public static void Msg(Control ui, string text, MessageBoxIcon icon)
        {
            Native.SetForegroundWindow(ui.Handle);
            MessageBox.Show(text, "MultiTaskbar", MessageBoxButtons.OK, icon);
        }

        public static bool Ask(Control ui, string text)
        {
            Native.SetForegroundWindow(ui.Handle);
            return MessageBox.Show(text, "MultiTaskbar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
    }

    // ------------------------------------------------------------------ tray icon

    // A handle on the app that does not depend on the bars: if a bar fails to appear, or ends up on
    // a monitor that is no longer there, this is still reachable from the main taskbar.
    class Tray : IDisposable
    {
        readonly NotifyIcon icon = new NotifyIcon();
        // One menu, refilled before each showing: disposing a drop-down from its own Closed event
        // makes WinForms reach for it again afterwards.
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly BarManager manager;

        public Tray(BarManager m)
        {
            manager = m;
            icon.Icon = LoadIcon();
            icon.Text = "MultiTaskbar " + Program.Version;
            icon.Visible = true;
            icon.MouseUp += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left) Show(); };
        }

        static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        void Show()
        {
            var m = menu;
            while (m.Items.Count > 0) { var item = m.Items[0]; m.Items.RemoveAt(0); item.Dispose(); }
            m.Items.Add(new ToolStripMenuItem("MultiTaskbar " + Program.Version) { Enabled = false });
            m.Items.Add(new ToolStripSeparator());

            var auto = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.Enabled };
            auto.Click += delegate { try { Autostart.Enabled = !auto.Checked; } catch (Exception ex) { Program.Log(ex); } };
            m.Items.Add(auto);

            if (manager.HasUpdate)
            {
                var up = new ToolStripMenuItem("Update to " + Updater.NewVersion + " and restart");
                up.Font = new Font(up.Font, FontStyle.Bold);
                up.Click += delegate { manager.InstallUpdate(); };
                m.Items.Add(up);
            }
            else m.Items.Add("Check for updates", null, delegate { manager.CheckForUpdates(); });

            m.Items.Add("Open file location", null, delegate
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Application.ExecutablePath + "\"") { UseShellExecute = true }); }
                catch (Exception ex) { Program.Log(ex); }
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Uninstall MultiTaskbar", null, delegate { Uninstall.Launch(); });
            m.Items.Add("Exit MultiTaskbar", null, delegate { manager.ExitApp(); });

            Native.SetForegroundWindow(manager.Sync.Handle);
            m.Show(Control.MousePosition);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }
    }

    // ------------------------------------------------------------------ uninstall

    static class Uninstall
    {
        const string ArpKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MultiTaskbar";

        // Listing the app in Settings > Apps is for copies the user placed themselves; when winget
        // installed it, winget keeps its own entry and owns the removal.
        public static void Register()
        {
            try
            {
                if (Application.ExecutablePath.IndexOf(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase) >= 0) return;
                using (var k = Registry.CurrentUser.CreateSubKey(ArpKey))
                {
                    k.SetValue("DisplayName", "MultiTaskbar");
                    k.SetValue("DisplayVersion", Program.Version);
                    k.SetValue("Publisher", "Creahive");
                    k.SetValue("DisplayIcon", Application.ExecutablePath);
                    k.SetValue("InstallLocation", Path.GetDirectoryName(Application.ExecutablePath));
                    k.SetValue("URLInfoAbout", "https://github.com/" + Program.Repo);
                    k.SetValue("UninstallString", "\"" + Application.ExecutablePath + "\" --uninstall");
                    k.SetValue("NoModify", 1);
                    k.SetValue("NoRepair", 1);
                    try { k.SetValue("EstimatedSize", (int)(new FileInfo(Application.ExecutablePath).Length / 1024)); } catch { }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // Started from the running app, so the removal happens in a process of its own.
        public static void Launch()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--uninstall") { UseShellExecute = false }); }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static void Run()
        {
            if (MessageBox.Show(
                    "Remove MultiTaskbar?\n\n" +
                    "The Windows taskbars come back on every monitor, the startup entry and settings are " +
                    "removed, and the program deletes itself.",
                    "Uninstall MultiTaskbar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            foreach (var p in Process.GetProcessesByName("MultiTaskbar"))
            {
                if (p.Id == Process.GetCurrentProcess().Id) continue;
                try { p.Kill(); p.WaitForExit(4000); } catch (Exception ex) { Program.Log(ex); }
            }
            Guard.ShowTaskbars();

            try { Autostart.Enabled = false; } catch (Exception ex) { Program.Log(ex); }
            try { Registry.CurrentUser.DeleteSubKeyTree(Settings.Key, false); } catch (Exception ex) { Program.Log(ex); }
            try { Registry.CurrentUser.DeleteSubKeyTree(ArpKey, false); } catch (Exception ex) { Program.Log(ex); }
            try { Directory.Delete(Path.GetDirectoryName(Program.LogPath), true); } catch { }

            // A running exe cannot delete itself, so a short shell command does it once this exits.
            string exe = Application.ExecutablePath;
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 > nul & del /f /q \"" + exe + "\" & rmdir \"" + Path.GetDirectoryName(exe) + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    // ------------------------------------------------------------------ start with Windows

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string SettingsKey = Settings.Key;
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
        readonly Timer updateTimer = new Timer();
        readonly Form sync = new Form();   // never shown; owns the thread that background work returns to
        Tray tray;
        bool restored;

        public Control Sync { get { return sync; } }

        public BarManager()
        {
            BuildBars();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionEnding += delegate { RestoreTaskbars(); };
            hideTimer.Interval = 1000;
            hideTimer.Tick += delegate { SyncTaskbars(); };
            hideTimer.Start();
            SyncTaskbars();
            Guard.MarkRunning();
            Guard.Start();
            tray = new Tray(this);

            Autostart.FixPath();
            Uninstall.Register();
            var ask = new Timer { Interval = 1000 };
            ask.Tick += delegate { ask.Stop(); ask.Dispose(); Autostart.AskOnFirstRun(); };
            ask.Start();

            if (!sync.IsHandleCreated) { var h = sync.Handle; }
            updateTimer.Interval = 1000 * 60 * 60;   // the check itself is limited to once a day
            updateTimer.Tick += delegate { Updater.CheckDaily(sync); };
            updateTimer.Start();
            var firstCheck = new Timer { Interval = 60000 };
            firstCheck.Tick += delegate { firstCheck.Stop(); firstCheck.Dispose(); Updater.CheckDaily(sync); };
            firstCheck.Start();
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

        // A Windows taskbar is hidden only while a bar of ours is actually up on that same monitor,
        // and comes back as soon as it is not, so a screen is never left without one.
        public void SyncTaskbars()
        {
            if (restored) return;
            foreach (var h in Native.FindTopWindows("Shell_SecondaryTrayWnd"))
            {
                IntPtr mon = Native.MonitorFromWindow(h, 2);
                bool covered = bars.Exists(b => b.Monitor == mon && b.IsUp);
                bool visible = Native.IsWindowVisible(h);
                if (covered && visible) Native.ShowWindow(h, Native.SW_HIDE);
                else if (!covered && !visible) Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
            }
        }

        public void RestoreTaskbars()
        {
            if (restored) return;
            restored = true;
            hideTimer.Stop();
            updateTimer.Stop();
            Guard.ShowTaskbars();
            Guard.MarkStopped();
        }

        public void ExitApp()
        {
            RestoreTaskbars();
            if (tray != null) { tray.Dispose(); tray = null; }
            foreach (var b in bars) { b.AllowClose = true; b.Close(); }
            bars.Clear();
            ExitThread();
        }

        public bool HasUpdate { get { return Updater.NewVersion != null; } }

        public void CheckForUpdates() { UpdateUi.Check(sync); }
        public void InstallUpdate() { UpdateUi.Install(sync); }
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
        // Sizes are given for a 96 dpi screen and scaled to whichever monitor the bar lands on.
        const int BTN_96 = 44, LABEL_MAX_96 = 180, LABEL_MIN_96 = 80;
        readonly Screen screen;
        readonly IntPtr hmon;
        readonly Timer timer = new Timer();
        readonly ToolTip tip = new ToolTip();
        Font clockFont, labelFont, glyphFont;
        float dpiScale = 1f;
        int BTN = BTN_96, LABEL_MAX = LABEL_MAX_96, LABEL_MIN = LABEL_MIN_96;
        public bool AllowClose;
        List<Item> items = new List<Item>();
        Item hover, pressed;
        string lastSig = "";
        IntPtr lastFg;
        bool hiddenForFullscreen;
        VolumePopup volPopup;
        CalendarPopup calendar;
        PreviewPopup preview;
        Item previewFor;
        DateTime hoverSince = DateTime.MaxValue;
        ShellMenu shellMenu;

        public Bar(Screen s)
        {
            screen = s;
            var c = new Native.POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
            hmon = Native.MonitorFromPoint(c, 2);
            ApplyDpi(Native.DpiForMonitor(hmon));

            Text = "MultiTaskbar";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            int h = s.Bounds.Bottom - s.WorkingArea.Bottom;
            if (h < Scale(32)) h = Scale(48);   // auto-hidden, or a taskbar that is not along the bottom
            Bounds = new Rectangle(s.Bounds.Left, s.Bounds.Bottom - h, s.Bounds.Width, h);

            tip.ShowAlways = true;
            tip.InitialDelay = 400;
            timer.Interval = 400;
            timer.Tick += delegate { try { Tick(); } catch (Exception ex) { Program.Log(ex); } };
            timer.Start();
        }

        public IntPtr Monitor { get { return hmon; } }

        // Whether this bar is holding its monitor's taskbar slot. Stepping aside for a full-screen
        // window counts as holding it: Windows hides its own taskbar then too, and showing it again
        // would only have the two of us fighting over it once a second.
        public bool IsUp
        {
            get
            {
                if (IsDisposed || !IsHandleCreated) return false;
                if (Native.MonitorFromWindow(Handle, 2) != hmon) return false;
                return hiddenForFullscreen || Native.IsWindowVisible(Handle);
            }
        }

        int Scale(int px96) { return (int)Math.Round(px96 * dpiScale); }

        void ApplyDpi(int dpi)
        {
            dpiScale = dpi / 96f;
            BTN = Scale(BTN_96);
            LABEL_MAX = Scale(LABEL_MAX_96);
            LABEL_MIN = Scale(LABEL_MIN_96);
            if (clockFont != null) clockFont.Dispose();
            if (labelFont != null) labelFont.Dispose();
            if (glyphFont != null) glyphFont.Dispose();
            clockFont = new Font("Segoe UI", 9f * dpiScale);
            labelFont = new Font("Segoe UI", 9f * dpiScale);
            glyphFont = Glyphs.MakeFont(12f * dpiScale);
            lastSig = "";
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
                if (fs) ClosePreview();
            }
            if (fs) return;

            UpdatePreview();

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

            var desk = new Item { Kind = "desktop", Rect = new Rectangle(W - Scale(12), 0, Scale(12), H) };
            var clock = new Item { Kind = "clock", Rect = new Rectangle(desk.Rect.Left - Scale(92), 0, Scale(88), H) };
            var vol = new Item { Kind = "volume", Rect = new Rectangle(clock.Rect.Left - Scale(42), 0, Scale(40), H) };
            int limit = vol.Rect.Left - Scale(8);

            int x = Scale(6);
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

        void DrawStart(Graphics g, Rectangle r)
        {
            int s = Scale(9), gap = Scale(2);
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
            int sz = Scale(24);
            int iconLeft = it.Label != null ? r.Left + Scale(10) : r.Left + (r.Width - sz) / 2;
            var ir = new Rectangle(iconLeft, r.Top + (r.Height - sz) / 2 - 1, sz, sz);
            if (it.Icon != null) g.DrawImage(it.Icon, ir);
            else using (var b = new SolidBrush(Color.FromArgb(90, fgc))) g.FillEllipse(b, ir);

            if (it.Label != null)
            {
                var tr = new Rectangle(ir.Right + Scale(8), r.Top, r.Right - ir.Right - Scale(14), r.Height);
                if (tr.Width > Scale(8))
                    TextRenderer.DrawText(g, it.Label, labelFont, tr, fgc,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (it.Windows.Count > 0)
            {
                int w = Scale(active ? 16 : 6), t = Scale(3);
                var ind = new Rectangle(ir.Left + (ir.Width - w) / 2, r.Bottom - t, w, t);
                using (var b = new SolidBrush(active ? Theme.Accent : Color.FromArgb(150, fgc)))
                using (var path = RoundRect(ind, Scale(1)))
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
                hoverSince = DateTime.Now;
                Invalidate();
                tip.Hide(this);
                tip.SetToolTip(this, TooltipFor(it));
                if (preview != null && it != previewFor) ClosePreview();
            }
        }

        // Previews stand in for the tooltip on app buttons, and only once the pointer has settled.
        void UpdatePreview()
        {
            var it = hover;
            bool wanted = it != null && (it.Kind == "pin" || it.Kind == "win") && it.Windows.Count > 0
                && (DateTime.Now - hoverSince).TotalMilliseconds > 450 && !hiddenForFullscreen;

            if (preview != null && (preview.IsDisposed || !preview.Visible)) { preview = null; previewFor = null; }

            if (!wanted)
            {
                // Keep it open while the pointer is inside it.
                if (preview != null && !preview.Owns(Cursor.Position) && !Bounds.Contains(Cursor.Position)) ClosePreview();
                return;
            }
            if (preview != null && previewFor == it) return;
            ClosePreview();

            previewFor = it;
            preview = new PreviewPopup(it.Windows, it.Icon, dpiScale);
            var anchor = PointToScreen(new Point(it.Rect.Left + it.Rect.Width / 2, 0));
            int x = Math.Max(screen.Bounds.Left + Scale(8),
                Math.Min(anchor.X - preview.Width / 2, screen.Bounds.Right - preview.Width - Scale(8)));
            preview.Location = new Point(x, Top - preview.Height - Scale(8));
            preview.Show();
            Native.SetWindowPos(preview.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void ClosePreview()
        {
            if (preview != null && !preview.IsDisposed) preview.Close();
            preview = null;
            previewFor = null;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = null; pressed = null;
            hoverSince = DateTime.MaxValue;
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
            ClosePreview();
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
                    // Open windows get previews instead; the tooltip is only for the pin itself.
                    if (it.Windows.Count > 0) return null;
                    return it.Pin != null ? it.Pin.Name
                        : (it.Exe != null ? Path.GetFileNameWithoutExtension(it.Exe) : null);
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
                    if (e.Button == MouseButtons.Right) { Launch("ms-settings:dateandtime", null); return; }
                    ToggleCalendar(it);
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

        void ToggleCalendar(Item it)
        {
            if (calendar != null && !calendar.IsDisposed && calendar.Visible) { calendar.Close(); return; }
            calendar = new CalendarPopup(dpiScale);
            var pt = PointToScreen(new Point(it.Rect.Right, 0));
            int x = Math.Max(screen.Bounds.Left + 8, Math.Min(pt.X - calendar.Width, screen.Bounds.Right - calendar.Width - 8));
            calendar.Location = new Point(x, Top - calendar.Height - Scale(10));
            calendar.Show();
            calendar.Activate();
            Native.SetForegroundWindow(calendar.Handle);
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

        // Menus are built as plain Windows menus rather than WinForms ones, because that is the only
        // way to show the shell's own entries for an app (Run as administrator, Properties, Unpin)
        // in the same menu as ours.
        const int ID_WINDOW_FIRST = 1, ID_WINDOW_LAST = 0x0FFF;
        const int ID_RECENT_FIRST = 0x8100, ID_RECENT_LAST = 0x81FF;
        const int ID_OPEN_NEW = 0x9001, ID_CLOSE = 0x9002, ID_TASKMGR = 0x9010, ID_AUTOSTART = 0x9011,
                  ID_UPDATE = 0x9012, ID_AUTOUPDATE = 0x9013, ID_SETTINGS = 0x9014, ID_EXIT = 0x9015;
        List<Recent.Entry> recent = new List<Recent.Entry>();

        void ShowAppMenu(Item it, Point at)
        {
            IntPtr menu = Native.CreatePopupMenu();
            try
            {
                recent = Recent.For(Recent.AppIdOf(ShellTarget(it), it.Windows.Count > 0 ? it.Windows[0] : IntPtr.Zero));
                if (recent.Count > 0)
                {
                    Native.AppendMenu(menu, Native.MF_STRING | Native.MF_GRAYED, IntPtr.Zero, "Recent");
                    for (int i = 0; i < recent.Count && i <= ID_RECENT_LAST - ID_RECENT_FIRST; i++)
                    {
                        string n = recent[i].Name;
                        if (n.Length > 60) n = n.Substring(0, 57) + "...";
                        Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_RECENT_FIRST + i), "   " + n);
                    }
                    Separator(menu);
                }

                for (int i = 0; i < it.Windows.Count && i < ID_WINDOW_LAST; i++)
                {
                    string t = WindowScanner.Title(it.Windows[i]);
                    if (t.Length > 60) t = t.Substring(0, 57) + "...";
                    Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_WINDOW_FIRST + i), t);
                }
                Separator(menu);

                using (var shell = ShellMenu.For(ShellTarget(it), menu, Handle))
                {
                    if (shell == null)
                    {
                        string name = it.Pin != null ? it.Pin.Name
                            : (it.Exe != null ? Path.GetFileNameWithoutExtension(it.Exe) : "app");
                        Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_OPEN_NEW), "Open new " + name);
                    }
                    if (it.Windows.Count > 0)
                    {
                        Separator(menu);
                        Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_CLOSE),
                            it.Windows.Count > 1 ? "Close all windows" : "Close window");
                    }
                    Separator(menu);
                    AddBarItems(menu);
                    shellMenu = shell;
                    int id = Track(menu, at);
                    shellMenu = null;
                    if (shell != null && shell.Handles(id)) shell.Invoke(id, Handle);
                    else Dispatch(id, it);
                }
            }
            finally { Native.DestroyMenu(menu); }
        }

        void ShowBarMenu(Point at)
        {
            IntPtr menu = Native.CreatePopupMenu();
            try
            {
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_TASKMGR), "Task Manager");
                Separator(menu);
                AddBarItems(menu);
                Dispatch(Track(menu, at), null);
            }
            finally { Native.DestroyMenu(menu); }
        }

        static void Separator(IntPtr menu)
        {
            if (Native.GetMenuItemCount(menu) > 0) Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
        }

        void AddBarItems(IntPtr menu)
        {
            Native.AppendMenu(menu, Native.MF_STRING | (Autostart.Enabled ? Native.MF_CHECKED : 0),
                new IntPtr(ID_AUTOSTART), "Start with Windows");
            if (Updater.NewVersion != null)
            {
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_UPDATE),
                    "Update to " + Updater.NewVersion + " and restart");
                Native.SetMenuDefaultItem(menu, (uint)ID_UPDATE, false);
            }
            else Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_UPDATE), "Check for updates");
            Native.AppendMenu(menu, Native.MF_STRING | (Updater.AutoCheck ? Native.MF_CHECKED : 0),
                new IntPtr(ID_AUTOUPDATE), "Check for updates automatically");
            Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_SETTINGS), "Taskbar settings");
            Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(ID_EXIT), "Exit MultiTaskbar");
        }

        int Track(IntPtr menu, Point at)
        {
            var pt = PointToScreen(at);
            Native.SetForegroundWindow(Handle);
            int id = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN,
                pt.X, Top, Handle, IntPtr.Zero);
            Native.PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);   // lets the menu close cleanly
            return id;
        }

        void Dispatch(int id, Item it)
        {
            if (id <= 0) return;
            if (it != null && id >= ID_WINDOW_FIRST && id - ID_WINDOW_FIRST < it.Windows.Count)
            {
                Native.Activate(it.Windows[id - ID_WINDOW_FIRST]);
                return;
            }
            if (id >= ID_RECENT_FIRST && id - ID_RECENT_FIRST < recent.Count)
            {
                Launch(recent[id - ID_RECENT_FIRST].Path, null);
                return;
            }
            switch (id)
            {
                case ID_OPEN_NEW: LaunchNew(it); break;
                case ID_CLOSE:
                    foreach (var w in new List<IntPtr>(it.Windows))
                        Native.PostMessage(w, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                    break;
                case ID_TASKMGR: Launch("taskmgr.exe", null); break;
                case ID_AUTOSTART:
                    try { Autostart.Enabled = !Autostart.Enabled; } catch (Exception ex) { Program.Log(ex); }
                    break;
                case ID_UPDATE:
                    if (Updater.NewVersion != null) InstallUpdate(); else CheckForUpdates();
                    break;
                case ID_AUTOUPDATE: Updater.AutoCheck = !Updater.AutoCheck; break;
                case ID_SETTINGS: Launch("ms-settings:taskbar", null); break;
                case ID_EXIT: Program.Manager.ExitApp(); break;
            }
        }

        static string ShellTarget(Item it)
        {
            if (it.Pin != null)
                return it.Pin.AppId != null ? "shell:AppsFolder\\" + it.Pin.AppId : it.Pin.LinkPath;
            return it.Exe != null && Path.IsPathRooted(it.Exe) && File.Exists(it.Exe) ? it.Exe : null;
        }

        // Owner-drawn entries added by shell extensions need these messages forwarded to them.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0 /*WM_DPICHANGED*/)
            {
                ApplyDpi((int)(m.WParam.ToInt64() & 0xFFFF));
                int h = screen.Bounds.Bottom - screen.WorkingArea.Bottom;
                if (h < Scale(32)) h = Scale(48);
                Bounds = new Rectangle(screen.Bounds.Left, screen.Bounds.Bottom - h, screen.Bounds.Width, h);
                Invalidate();
                return;
            }
            if (shellMenu != null && shellMenu.HandleMessage(ref m)) return;
            base.WndProc(ref m);
        }

        void CheckForUpdates() { UpdateUi.Check(this); }

        void InstallUpdate() { UpdateUi.Install(this); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClosePreview();
                if (calendar != null && !calendar.IsDisposed) calendar.Close();
                timer.Dispose(); tip.Dispose(); clockFont.Dispose(); labelFont.Dispose(); glyphFont.Dispose();
            }
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

    // ------------------------------------------------------------------ window previews

    // Live thumbnails of an app's windows, the way the real taskbar shows them on hover. The
    // pictures are drawn by the desktop compositor itself: the popup only tells it where to put
    // each one, so nothing is copied or redrawn here.
    class PreviewPopup : Form
    {
        class Entry
        {
            public IntPtr Window, Thumb;
            public Rectangle Cell, Thumbnail;
            public string Title;
            public Bitmap Icon;
            public bool Minimized;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly float scale;
        readonly Font font;
        Entry hover;

        public PreviewPopup(List<IntPtr> windows, Bitmap icon, float dpiScale)
        {
            scale = dpiScale;
            font = new Font("Segoe UI", 8.5f * scale);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            int cellW = S(196), cellH = S(150), pad = S(8), title = S(22);
            int n = Math.Min(windows.Count, 6);
            for (int i = 0; i < n; i++)
            {
                var e = new Entry
                {
                    Window = windows[i],
                    Title = WindowScanner.Title(windows[i]),
                    Icon = icon ?? WindowScanner.Icon(windows[i]),
                    Minimized = Native.IsIconic(windows[i]),
                    Cell = new Rectangle(pad + i * (cellW + pad), pad, cellW, cellH),
                };
                e.Thumbnail = new Rectangle(e.Cell.Left + S(6), e.Cell.Top + title, e.Cell.Width - S(12), e.Cell.Height - title - S(6));
                entries.Add(e);
            }
            Size = new Size(pad + n * (cellW + pad), cellH + 2 * pad);
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        public bool Owns(Point screenPoint) { return !IsDisposed && Visible && Bounds.Contains(screenPoint); }

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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            foreach (var entry in entries) Register(entry);
        }

        void Register(Entry e)
        {
            if (e.Minimized) return;   // a minimized window has nothing to show
            try
            {
                IntPtr thumb;
                if (Native.DwmRegisterThumbnail(Handle, e.Window, out thumb) != 0 || thumb == IntPtr.Zero) return;
                e.Thumb = thumb;
                var props = new Native.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 /*DESTINATION*/ | 0x4 /*OPACITY*/ | 0x8 /*VISIBLE*/ | 0x10 /*CLIENT AREA ONLY*/,
                    rcDestination = new Native.RECT
                    {
                        Left = e.Thumbnail.Left,
                        Top = e.Thumbnail.Top,
                        Right = e.Thumbnail.Right,
                        Bottom = e.Thumbnail.Bottom,
                    },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = true,
                };
                Native.DwmUpdateThumbnailProperties(thumb, ref props);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            bool light = Theme.Light;
            Color fg = light ? Color.Black : Color.White;
            g.Clear(light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44));
            using (var p = new Pen(light ? Color.FromArgb(210, 210, 210) : Color.FromArgb(70, 70, 70)))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            foreach (var entry in entries)
            {
                if (entry == hover)
                    using (var b = new SolidBrush(Color.FromArgb(28, fg)))
                    using (var path = Bar.RoundRect(entry.Cell, S(6)))
                        g.FillPath(b, path);

                var titleRect = new Rectangle(entry.Cell.Left + S(6), entry.Cell.Top + S(3), entry.Cell.Width - S(12), S(18));
                if (entry.Icon != null)
                {
                    int sz = S(14);
                    g.DrawImage(entry.Icon, new Rectangle(titleRect.Left, titleRect.Top + (titleRect.Height - sz) / 2, sz, sz));
                    titleRect = new Rectangle(titleRect.Left + sz + S(5), titleRect.Top, titleRect.Width - sz - S(5), titleRect.Height);
                }
                TextRenderer.DrawText(g, entry.Title, font, titleRect, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // The compositor paints live windows itself; a minimized one gets its icon instead.
                if (entry.Thumb == IntPtr.Zero)
                {
                    using (var b = new SolidBrush(Color.FromArgb(light ? 18 : 30, fg)))
                    using (var path = Bar.RoundRect(entry.Thumbnail, S(4)))
                        g.FillPath(b, path);
                    if (entry.Icon != null)
                    {
                        int sz = S(32);
                        g.DrawImage(entry.Icon, new Rectangle(
                            entry.Thumbnail.Left + (entry.Thumbnail.Width - sz) / 2,
                            entry.Thumbnail.Top + (entry.Thumbnail.Height - sz) / 2, sz, sz));
                    }
                }
            }
        }

        Entry At(Point p) { return entries.Find(e => e.Cell.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = At(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != null) { hover = null; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var entry = At(e.Location);
            if (entry == null) return;
            if (e.Button == MouseButtons.Middle)
                Native.PostMessage(entry.Window, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
            else if (e.Button == MouseButtons.Left)
                Native.Activate(entry.Window);
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            foreach (var e in entries)
                if (e.Thumb != IntPtr.Zero) { try { Native.DwmUnregisterThumbnail(e.Thumb); } catch { } e.Thumb = IntPtr.Zero; }
            if (disposing) font.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ calendar

    // The small month the real taskbar shows when the clock is clicked.
    class CalendarPopup : Form
    {
        readonly float scale;
        readonly Font dayFont, headFont, titleFont, weekFont;
        DateTime month = DateTime.Today;
        Rectangle prev, next;
        int hoverDay;

        const int COLS = 7, ROWS = 6;

        public CalendarPopup(float dpiScale)
        {
            scale = dpiScale;
            dayFont = new Font("Segoe UI", 9.5f * scale);
            weekFont = new Font("Segoe UI", 8.5f * scale);
            headFont = new Font("Segoe UI Semibold", 11f * scale, FontStyle.Bold);
            titleFont = new Font("Segoe UI", 20f * scale);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(S(320), S(400));
        }

        int S(int px) { return (int)Math.Round(px * scale); }

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
            else if (e.KeyCode == Keys.Left) { month = month.AddMonths(-1); Invalidate(); }
            else if (e.KeyCode == Keys.Right) { month = month.AddMonths(1); Invalidate(); }
            else if (e.KeyCode == Keys.Home) { month = DateTime.Today; Invalidate(); }
        }

        Rectangle Grid { get { return new Rectangle(S(12), S(130), Width - S(24), Height - S(142)); } }

        Rectangle CellAt(int col, int row)
        {
            var g = Grid;
            int cw = g.Width / COLS, ch = (g.Height - S(24)) / ROWS;
            return new Rectangle(g.Left + col * cw, g.Top + S(24) + row * ch, cw, ch);
        }

        DateTime FirstCell()
        {
            var first = new DateTime(month.Year, month.Month, 1);
            int start = (int)DateTimeFormatInfo.CurrentInfo.FirstDayOfWeek;
            int shift = ((int)first.DayOfWeek - start + 7) % 7;
            return first.AddDays(-shift);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Grey antialiasing: subpixel rendering leaves colour fringes on the small digits.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            bool light = Theme.Light;
            Color fg = light ? Color.Black : Color.White;
            Color dim = Color.FromArgb(120, fg);
            g.Clear(light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44));
            using (var p = new Pen(light ? Color.FromArgb(210, 210, 210) : Color.FromArgb(70, 70, 70)))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            var now = DateTime.Now;
            using (var b = new SolidBrush(fg))
            {
                g.DrawString(now.ToString("t"), titleFont, b, S(14), S(12));
                g.DrawString(now.ToString("D"), dayFont, new SolidBrush(dim), S(16), S(58));
                g.DrawString(month.ToString("MMMM yyyy"), headFont, b, S(14), S(92));
            }

            // Month arrows
            prev = new Rectangle(Width - S(76), S(88), S(28), S(28));
            next = new Rectangle(Width - S(44), S(88), S(28), S(28));
            DrawChevron(g, prev, fg, true);
            DrawChevron(g, next, fg, false);

            // Weekday row
            var names = DateTimeFormatInfo.CurrentInfo.AbbreviatedDayNames;
            int start = (int)DateTimeFormatInfo.CurrentInfo.FirstDayOfWeek;
            using (var b = new SolidBrush(dim))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                for (int c = 0; c < COLS; c++)
                {
                    var cell = CellAt(c, 0);
                    g.DrawString(names[(start + c) % 7].Substring(0, Math.Min(2, names[(start + c) % 7].Length)),
                        weekFont, b, new Rectangle(cell.Left, Grid.Top, cell.Width, S(24)), sf);
                }

            var day = FirstCell();
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                for (int r = 0; r < ROWS; r++)
                    for (int c = 0; c < COLS; c++, day = day.AddDays(1))
                    {
                        var cell = CellAt(c, r);
                        bool thisMonth = day.Month == month.Month;
                        bool today = day.Date == DateTime.Today;
                        int size = Math.Min(cell.Width, cell.Height) - S(6);
                        var dot = new Rectangle(cell.Left + (cell.Width - size) / 2, cell.Top + (cell.Height - size) / 2, size, size);

                        if (today)
                            using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, dot);
                        else if (hoverDay == DayKey(day))
                            using (var b = new SolidBrush(Color.FromArgb(30, fg))) g.FillEllipse(b, dot);

                        Color text = today ? Color.White : (thisMonth ? fg : dim);
                        using (var b = new SolidBrush(text))
                            g.DrawString(day.Day.ToString(), dayFont, b, cell, sf);
                    }
        }

        static int DayKey(DateTime d) { return d.Year * 10000 + d.Month * 100 + d.Day; }

        void DrawChevron(Graphics g, Rectangle r, Color fg, bool left)
        {
            using (var p = new Pen(Color.FromArgb(hoverArrow == (left ? 1 : 2) ? 255 : 160, fg), Math.Max(1.4f, 1.4f * scale)))
            {
                int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, d = S(4);
                if (left)
                {
                    g.DrawLine(p, cx + d / 2, cy - d, cx - d / 2, cy);
                    g.DrawLine(p, cx - d / 2, cy, cx + d / 2, cy + d);
                }
                else
                {
                    g.DrawLine(p, cx - d / 2, cy - d, cx + d / 2, cy);
                    g.DrawLine(p, cx + d / 2, cy, cx - d / 2, cy + d);
                }
            }
        }

        int hoverArrow;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int arrow = prev.Contains(e.Location) ? 1 : (next.Contains(e.Location) ? 2 : 0);
            int day = 0;
            var d = FirstCell();
            for (int r = 0; r < ROWS && day == 0; r++)
                for (int c = 0; c < COLS; c++, d = d.AddDays(1))
                    if (CellAt(c, r).Contains(e.Location)) { day = DayKey(d); break; }
            if (arrow != hoverArrow || day != hoverDay) { hoverArrow = arrow; hoverDay = day; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (prev.Contains(e.Location)) { month = month.AddMonths(-1); Invalidate(); }
            else if (next.Contains(e.Location)) { month = month.AddMonths(1); Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            month = month.AddMonths(e.Delta > 0 ? -1 : 1);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { dayFont.Dispose(); weekFont.Dispose(); headFont.Dispose(); titleFont.Dispose(); }
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
                if (Target == null || exe == null) return false;
                if (string.Equals(Target, exe, StringComparison.OrdinalIgnoreCase)) return true;
                // Elevated processes only give up their name, not their path.
                return !Path.IsPathRooted(exe) && string.Equals(Path.GetFileName(Target), exe, StringComparison.OrdinalIgnoreCase);
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
            // A window of a process running as administrator cannot be opened for its path, but its
            // name is still readable, which is enough to pair it with a pinned app.
            if (p == null)
                try { p = Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { }
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

    // ------------------------------------------------------------------ recent documents

    // The files an app opened lately, the same list the real taskbar shows. Windows keeps it per
    // app id, and hands it over through the shell's own document-list object, so nothing here
    // parses the jump list files by hand. Empty when the user has turned off
    // "Show recently opened items in Start, Jump Lists and File Explorer".
    static class Recent
    {
        [ComImport, Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationDocumentLists
        {
            [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            [PreserveSig] int GetList(int listType, uint count, ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IObjectArray
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out Native.PROPERTYKEY key);
            [PreserveSig] int GetValue(ref Native.PROPERTYKEY key, out Native.PROPVARIANT value);
            [PreserveSig] int SetValue(ref Native.PROPERTYKEY key, ref Native.PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object obj);
        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object store);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bind, int flags, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object store);
        [DllImport("propsys.dll")]
        static extern int PropVariantToStringAlloc(ref Native.PROPVARIANT value, out IntPtr str);
        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref Native.PROPVARIANT value);

        public class Entry
        {
            public string Name, Path;
        }

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                        return k == null || Convert.ToInt32(k.GetValue("Start_TrackDocs", 1)) != 0;
                }
                catch { return true; }
            }
        }

        public static List<Entry> For(string appId)
        {
            var list = new List<Entry>();
            if (string.IsNullOrEmpty(appId) || !Enabled) return list;
            try
            {
                var clsid = new Guid("86bec222-30f2-47e0-9f25-60d11cd75c28"); // CLSID_ApplicationDocumentLists
                var iid = typeof(IApplicationDocumentLists).GUID;
                object o;
                if (CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out o) != 0) return list;
                var lists = (IApplicationDocumentLists)o;
                try
                {
                    if (lists.SetAppID(appId) != 0) return list;
                    var arrayIid = typeof(IObjectArray).GUID;
                    IntPtr ptr;
                    if (lists.GetList(0 /*ADLT_RECENT*/, 10, ref arrayIid, out ptr) != 0 || ptr == IntPtr.Zero) return list;
                    var arr = (IObjectArray)Marshal.GetObjectForIUnknown(ptr);
                    Marshal.Release(ptr);
                    try
                    {
                        uint count;
                        arr.GetCount(out count);
                        var itemIid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr ip;
                            if (arr.GetAt(i, ref itemIid, out ip) != 0 || ip == IntPtr.Zero) continue;
                            var item = (ShellIcons.IShellItem)Marshal.GetObjectForIUnknown(ip);
                            Marshal.Release(ip);
                            try
                            {
                                var e = new Entry { Name = Name(item, 0 /*SIGDN_NORMALDISPLAY*/), Path = Name(item, unchecked((uint)0x80058000) /*SIGDN_DESKTOPABSOLUTEPARSING*/) };
                                if (e.Name != null && e.Path != null) list.Add(e);
                            }
                            finally { Marshal.ReleaseComObject(item); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(arr); }
                }
                finally { Marshal.ReleaseComObject(lists); }
            }
            catch (Exception ex) { Program.Log(ex); }
            return list;
        }

        static string Name(ShellIcons.IShellItem item, uint kind)
        {
            IntPtr p;
            if (item.GetDisplayName(kind, out p) != 0 || p == IntPtr.Zero) return null;
            string s = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            return s;
        }

        // The id Windows files an app's documents under: the one the app declares for itself, or
        // the one its window carries.
        public static string AppIdOf(string linkOrExe, IntPtr window)
        {
            string id = FromStore(delegate(ref Guid iid, out object store)
            {
                store = null;
                if (string.IsNullOrEmpty(linkOrExe)) return unchecked((int)0x80004005);
                return SHGetPropertyStoreFromParsingName(linkOrExe, IntPtr.Zero, 0 /*GPS_DEFAULT*/, ref iid, out store);
            });
            if (id == null && window != IntPtr.Zero)
                id = FromStore(delegate(ref Guid iid, out object store) { return SHGetPropertyStoreForWindow(window, ref iid, out store); });
            return id;
        }

        delegate int OpenStore(ref Guid iid, out object store);

        static string FromStore(OpenStore open)
        {
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                object o = null;
                if (open(ref iid, out o) != 0 || o == null) return null;
                var store = (IPropertyStore)o;
                try
                {
                    // PKEY_AppUserModel_ID
                    var key = new Native.PROPERTYKEY { fmtid = new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), pid = 5 };
                    Native.PROPVARIANT v;
                    if (store.GetValue(ref key, out v) != 0) return null;
                    try
                    {
                        IntPtr str;
                        if (PropVariantToStringAlloc(ref v, out str) != 0 || str == IntPtr.Zero) return null;
                        string s = Marshal.PtrToStringUni(str);
                        Marshal.FreeCoTaskMem(str);
                        return string.IsNullOrEmpty(s) ? null : s;
                    }
                    finally { PropVariantClear(ref v); }
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }
    }

    // ------------------------------------------------------------------ shell context menu

    // Borrows the menu Explorer shows for a file, a shortcut or an installed app, and merges its
    // entries into a menu of ours. Entries it owns answer to ids in its own range.
    class ShellMenu : IDisposable
    {
        const uint First = 0x1000, Last = 0x6FFF;

        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
            [PreserveSig] int InvokeCommand(ref Native.CMINVOKECOMMANDINFO ici);
            [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
        }

        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
            [PreserveSig] int InvokeCommand(ref Native.CMINVOKECOMMANDINFO ici);
            [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        }

        IContextMenu menu;
        IContextMenu2 menu2;

        public static ShellMenu For(string parsingName, IntPtr hmenu, IntPtr owner)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            try
            {
                var item = ShellIcons.Item(parsingName);
                if (item == null) return null;
                Guid bhid = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5"); // BHID_SFUIObject
                Guid iid = typeof(IContextMenu).GUID;
                IntPtr ptr;
                item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out ptr);
                Marshal.ReleaseComObject(item);
                if (ptr == IntPtr.Zero) return null;
                var m = new ShellMenu { menu = (IContextMenu)Marshal.GetObjectForIUnknown(ptr) };
                Marshal.Release(ptr);
                m.menu2 = m.menu as IContextMenu2;
                uint index = (uint)Native.GetMenuItemCount(hmenu);
                if (m.menu.QueryContextMenu(hmenu, index, First, Last, 0 /*CMF_NORMAL*/) < 0) { m.Dispose(); return null; }
                return m;
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }

        public bool Handles(int id) { return id >= First && id <= Last; }

        public void Invoke(int id, IntPtr owner)
        {
            try
            {
                var ici = new Native.CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf(typeof(Native.CMINVOKECOMMANDINFO)),
                    hwnd = owner,
                    lpVerb = new IntPtr(id - First),
                    nShow = Native.SW_SHOWNORMAL,
                };
                menu.InvokeCommand(ref ici);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public bool HandleMessage(ref Message m)
        {
            if (menu2 == null) return false;
            if (m.Msg != 0x0117 /*WM_INITMENUPOPUP*/ && m.Msg != 0x002C /*WM_MEASUREITEM*/ && m.Msg != 0x002B /*WM_DRAWITEM*/)
                return false;
            try { return menu2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam) == 0; }
            catch { return false; }
        }

        public void Dispose()
        {
            try { if (menu != null) Marshal.ReleaseComObject(menu); } catch { }
            menu = null; menu2 = null;
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
        internal interface IShellItem
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

        public static IShellItem Item(string parsing)
        {
            try { return Create(parsing, typeof(IShellItem).GUID) as IShellItem; }
            catch (Exception ex) { Program.Log(ex); return null; }
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
        public const int SW_HIDE = 0, SW_MINIMIZE = 6, SW_SHOWNOACTIVATE = 4, SW_RESTORE = 9, SW_SHOWNORMAL = 1;
        public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8, MF_GRAYED = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p, p2; }
        public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_BOTTOMALIGN = 0x20;
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

        [StructLayout(LayoutKind.Sequential)]
        public struct CMINVOKECOMMANDINFO
        {
            public int cbSize, fMask;
            public IntPtr hwnd, lpVerb, lpParameters, lpDirectory;
            public int nShow, dwHotKey;
            public IntPtr hIcon;
        }

        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr h, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetMenuDefaultItem(IntPtr h, uint item, bool byPosition);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr h, uint flags, int x, int y, IntPtr owner, IntPtr parms);

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
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
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
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination, rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible, fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
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
            // A maximized window is not full screen, however far its frame reaches. Treating it as
            // one would make the bar disappear whenever a window is maximized on that monitor.
            if (IsZoomed(fg)) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        public static int DpiForMonitor(IntPtr hmon)
        {
            try
            {
                uint x, y;
                if (GetDpiForMonitor(hmon, 0 /*MDT_EFFECTIVE_DPI*/, out x, out y) == 0 && x > 0) return (int)x;
            }
            catch { }
            return 96;
        }
    }
}
