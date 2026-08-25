using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Gp
{
    public class StartupArgs
    {
        public string Exe = "";
        public string AppId = "";
        public bool Auto;
        public bool ExitWhenDone;

        public static StartupArgs Parse(string[] a)
        {
            var r = new StartupArgs();
            if (a == null) return r;
            for (int i = 0; i < a.Length; i++)
            {
                var s = (a[i] ?? "").ToLowerInvariant();
                try
                {
                    if (s == "--exe" && i + 1 < a.Length) r.Exe = a[++i];
                    else if (s == "--appid" && i + 1 < a.Length) r.AppId = a[++i];
                    else if (s == "--auto") r.Auto = true;
                    else if (s == "--exit-when-done") r.ExitWhenDone = true;
                }
                catch { }
            }
            return r;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { if (!SetProcessDpiAwarenessContext((IntPtr)(-4))) SetProcessDPIAware(); }
            catch { try { SetProcessDPIAware(); } catch { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldbergPatcher");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "errors.log"),
                        DateTime.Now + "\n" + e.Exception + "\n\n");
                }
                catch { }
                MessageBox.Show(e.Exception.Message, "Goldberg Patcher – unexpected error");
            };
            Application.Run(new MainForm(StartupArgs.Parse(args)));
        }
    }

    // ─────────────────────────────────────────────── appid textbox

    public class AppIdBox : Control
    {
        readonly TextBox box;
        bool focused;

        public override string Text
        {
            get { return box.Text.Trim(); }
            set { box.Text = value ?? ""; }
        }

        public AppIdBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Surface2;
            box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Surface2,
                ForeColor = Ui.TextC,
                Font = Ui.F("Consolas", 10.5f, false),
            };
            Controls.Add(box);
            box.TextChanged += delegate { Invalidate(); };
            box.GotFocus += delegate { focused = true; Invalidate(); };
            box.LostFocus += delegate { focused = false; Invalidate(); };
            box.KeyPress += (s, e) =>
            {
                if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true;
            };
            box.HandleCreated += delegate { NativeCue.Set(box.Handle, "e.g. 1245620"); };
            Height = 38;
        }

        internal static class NativeCue
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
            public static void Set(IntPtr handle, string cue)
            {
                SendMessage(handle, 0x1501, (IntPtr)1, cue); // EM_SETCUEBANNER
            }
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (box == null) return;
            box.SetBounds(12, (Height - box.PreferredHeight) / 2, Width - 24, box.PreferredHeight);
        }
        protected override void OnEnabledChanged(EventArgs e) { box.Enabled = Enabled; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnClick(EventArgs e) { box.Focus(); base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = ClientRectangle;
            Ui.FillRound(g, r, 9, Ui.Surface2);
            Ui.StrokeRound(g, r, 9, focused ? Ui.Accent : Ui.BorderC, 1.4f);
        }
    }

    // ─────────────────────────────────────────────── status bar

    public class StatusBarCtl : Control
    {
        public string StatusText = "Ready";
        public Color DotColor = Ui.MutedC;
        public string RightText = "goldberg emu · steamless";

        public StatusBarCtl()
        {
            Dock = DockStyle.Bottom; Height = 30;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        public void Set(string text, Color dot) { StatusText = text; DotColor = dot; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Ui.BorderC, 1f)) g.DrawLine(p, 0, 0, Width, 0);
            using (var b = new SolidBrush(DotColor)) g.FillEllipse(b, 26, Height / 2 - 4, 8, 8);
            TextRenderer.DrawText(g, StatusText, Ui.F(8.25f, false), new Point(44, Height / 2 - 8), Ui.MutedC, TextFormatFlags.NoPadding);
            var sz = TextRenderer.MeasureText(RightText, Ui.F(7.75f, false), Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, RightText, Ui.F(7.75f, false), new Point(Width - sz.Width - 26, Height / 2 - 8), Ui.FromHex("#5A6373"), TextFormatFlags.NoPadding);
        }
    }

    // ─────────────────────────────────────────────── main form

    public class MainForm : Form
    {
        const int Pad = 28;
        readonly TitleBar titleBar;
        readonly DropZone zone;
        readonly AppCard appIdCard;
        readonly AppIdBox appIdBox;
        readonly AppCard optionsCard;
        readonly Toggle tUnpack, tBackup, tAppid, tSettings, tOnlineFix;
        readonly GradientButton patchBtn;
        readonly ProgressBarLite progress;
        readonly Banner banner;
        readonly AppCard logCard;
        readonly LogView log;
        readonly StatusBarCtl statusBar;

        readonly AppSettings settings;
        readonly StartupArgs startup;
        PatchRunner runner;
        CancellationTokenSource cts;
        volatile bool running;
        PatchResult lastResult;
        string[] lastActions = new string[0];

        System.Windows.Forms.Timer autoTimer;

        public MainForm(StartupArgs sa)
        {
            startup = sa;
            settings = AppSettings.Load();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 780);
            BackColor = Ui.Bg;
            Text = "Goldberg Patcher";
            KeyPreview = true;
            DoubleBuffered = true;
            MinimumSize = Size;

            titleBar = new TitleBar();
            Controls.Add(titleBar);

            zone = new DropZone();
            zone.Bounds = new Rectangle(Pad, 128, 820 - Pad * 2, 116);
            zone.FileChosen += OnGameSelected;
            Controls.Add(zone);

            appIdCard = new AppCard();
            appIdCard.Bounds = new Rectangle(Pad, 256, 820 - Pad * 2, 86);
            Controls.Add(appIdCard);

            appIdBox = new AppIdBox();
            appIdBox.Bounds = new Rectangle(22, 36, 250, 38);
            appIdCard.Controls.Add(appIdBox);

            Rectangle dbRect = Rectangle.Empty;
            bool dbHover = false;
            appIdCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Ui.SpacedText(g, "STEAM APPID", Ui.F(7.5f, true), new SolidBrush(Ui.MutedC), new PointF(22, 14), 1.5f);

                var f8 = Ui.F(8.25f, false);
                string t1 = "Find your game's AppID on";
                string t2 = "steamdb.info ↗";
                var s1 = TextRenderer.MeasureText(t1, f8, Size.Empty, TextFormatFlags.NoPadding);
                var s2 = TextRenderer.MeasureText(t2, f8, Size.Empty, TextFormatFlags.NoPadding);
                int tx = appIdCard.Width - (s1.Width + 12 + s2.Width) - 26;
                int ty = appIdCard.Height / 2 - 8;
                TextRenderer.DrawText(g, t1, f8, new Point(tx, ty), Ui.MutedC, TextFormatFlags.NoPadding);
                int lx = tx + s1.Width + 12;
                TextRenderer.DrawText(g, t2, f8, new Point(lx, ty), Ui.Accent2, TextFormatFlags.NoPadding);
                if (dbHover) using (var p = new Pen(Ui.Accent2, 1f)) g.DrawLine(p, lx, ty + 15, lx + s2.Width, ty + 15);
                dbRect = new Rectangle(lx - 4, ty - 5, s2.Width + 8, 27);
            };
            appIdCard.MouseMove += (s2b, e2) =>
            {
                bool h = dbRect.Contains(e2.Location);
                if (h != dbHover)
                {
                    dbHover = h;
                    appIdCard.Cursor = h ? Cursors.Hand : Cursors.Default;
                    appIdCard.Invalidate();
                }
            };
            appIdCard.MouseLeave += (s2b, e2) => { if (dbHover) { dbHover = false; appIdCard.Invalidate(); } };
            appIdCard.MouseClick += (s2b, e2) =>
            {
                if (dbRect.Contains(e2.Location)) try { Process.Start("https://steamdb.info"); } catch { }
            };

            optionsCard = new AppCard();
            optionsCard.Bounds = new Rectangle(Pad, 354, 820 - Pad * 2, 138);
            Controls.Add(optionsCard);

            tUnpack = new Toggle("Auto-unpack Steam DRM (Steamless)", settings.UnpackDrm);
            tBackup = new Toggle("Back up replaced files", settings.Backup);
            tAppid = new Toggle("Write steam_appid.txt", settings.WriteAppIdTxt);
            tSettings = new Toggle("Create steam_settings folder", settings.CreateSettings);
            tOnlineFix = new Toggle("Generic online-fix (force Spacewar AppID 480)", settings.OnlineFix);
            tOnlineFix.CheckedChanged += delegate
            {
                appIdBox.Enabled = !running && !tOnlineFix.Checked;
                RecalcLog();
            };
            tUnpack.Bounds = new Rectangle(24, 16, 370, 24);
            tBackup.Bounds = new Rectangle(408, 16, 330, 24);
            tAppid.Bounds = new Rectangle(24, 56, 370, 24);
            tSettings.Bounds = new Rectangle(408, 56, 340, 24);
            tOnlineFix.Bounds = new Rectangle(24, 96, 370, 24);
            foreach (Control c in new Control[] { tUnpack, tBackup, tAppid, tSettings, tOnlineFix }) optionsCard.Controls.Add(c);

            patchBtn = new GradientButton("Patch Game");
            patchBtn.Bounds = new Rectangle(Pad, 504, 820 - Pad * 2, 52);
            patchBtn.Click += delegate { if (running) CancelPatch(); else StartPatch(); };
            Controls.Add(patchBtn);

            progress = new ProgressBarLite();
            progress.Bounds = new Rectangle(Pad, 564, 820 - Pad * 2, 5);
            Controls.Add(progress);

            banner = new Banner();
            banner.Bounds = new Rectangle(Pad, 578, 820 - Pad * 2, 58);
            banner.ActionClicked += OnBannerAction;
            Controls.Add(banner);

            logCard = new AppCard();
            logCard.Bounds = LogBounds();
            Controls.Add(logCard);

            log = new LogView();
            logCard.Controls.Add(log);

            statusBar = new StatusBarCtl();
            statusBar.RightText = "goldberg emu · steamless · offline";
            Controls.Add(statusBar);

            titleBar.CloseClicked += delegate { Close(); };
            titleBar.MinimizeClicked += delegate { WindowState = FormWindowState.Minimized; };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && running) CancelPatch(); };

            Shown += OnShownFirst;
            FormClosing += (s, e) => { if (running) { try { cts.Cancel(); } catch { } } };
            Resize += delegate { RecalcLog(); };
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        Rectangle LogBounds()
        {
            int top = banner.Visible ? 644 : 586;
            return new Rectangle(Pad, top, ClientSize.Width - Pad * 2, ClientSize.Height - top - 40);
        }
        void RecalcLog()
        {
            logCard.Bounds = LogBounds();
            log.SetBounds(12, 12, logCard.Width - 24, logCard.Height - 24);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int dark = 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                DwmSetWindowAttribute(Handle, 19, ref dark, 4);
            }
            catch { }
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RecalcLog();
        }

        void OnShownFirst(object s, EventArgs e)
        {
            Log(LogLevel.Dim, "Goldberg Patcher ready. Drop a game .exe to begin.");
            var missing = Tools.Missing();
            if (missing.Count > 0)
            {
                Log(LogLevel.Error, "Missing bundled tools: " + string.Join(", ", missing));
                statusBar.Set("Setup incomplete", Ui.ErrC);
            }

            if (!string.IsNullOrEmpty(startup.Exe))
            {
                zone.SetGame(startup.Exe);
                if (!string.IsNullOrEmpty(startup.AppId)) appIdBox.Text = startup.AppId;
                if (startup.Auto)
                {
                    autoTimer = new System.Windows.Forms.Timer();
                    autoTimer.Interval = 600;
                    autoTimer.Tick += delegate { autoTimer.Stop(); StartPatch(); };
                    autoTimer.Start();
                }
            }
        }

        // ---------------------------------------------------------- game selection

        void OnGameSelected(string path)
        {
            banner.HideBanner();
            RecalcLog();
            settings.LastExe = path;
            settings.Save();

            string dir = Path.GetDirectoryName(path);
            PeInfo pe = null;
            try { pe = PeReader.Analyze(path); }
            catch { }

            string archChip, sizeChip;
            if (pe != null)
            {
                archChip = pe.MachineText.ToUpperInvariant() + (pe.Managed ? " ·NET" : "");
                sizeChip = (pe.SizeBytes / 1048576.0).ToString("0.#") + " MB";
            }
            else
            {
                archChip = "INVALID EXE"; sizeChip = "";
            }

            zone.UpdateAnalysis(archChip, sizeChip, "searching game folder for steam_api dlls…", 0);

            // deep scan can take a moment on big installs – run it off the UI thread
            int gen = ++selectGeneration;
            System.Threading.Tasks.Task.Run(() => PatchRunner.FindSteamApiFiles(dir)).ContinueWith(t =>
            {
                if (gen != selectGeneration || IsDisposed) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (gen != selectGeneration) return;
                    var apis = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                        ? t.Result : new System.Collections.Generic.List<string>();
                    ApplyApiSearch(dir, archChip, sizeChip, apis);
                });
            });
        }

        int selectGeneration;

        void ApplyApiSearch(string dir, string archChip, string sizeChip, System.Collections.Generic.List<string> apis)
        {
            string apiChip; int apiState;
            if (apis.Count > 0)
            {
                apiState = 1;
                var first = apis[0];
                var relDir = PatchRunner.ShortRel(dir, Path.GetDirectoryName(first));
                var label = Path.GetFileName(first);
                if (!string.IsNullOrEmpty(relDir)) label += "  @ " + relDir;
                if (apis.Count > 1) label += "  (+" + (apis.Count - 1) + " more)";
                apiChip = label;
            }
            else
            {
                apiState = 0;
                apiChip = "no steam_api dll in game folder – will be placed beside exe";
            }
            zone.UpdateAnalysis(archChip, sizeChip, apiChip, apiState);

            if (appIdBox.Text.Length == 0)
            {
                string cached;
                if (settings.AppIdsByFolder.TryGetValue(dir ?? "", out cached) && !string.IsNullOrEmpty(cached))
                    appIdBox.Text = cached;
                else
                {
                    var dirs = apis.Select(Path.GetDirectoryName).ToList();
                    dirs.Add(dir);
                    appIdBox.Text = runner_FillAppId(dirs);
                }
            }
        }

        string runner_FillAppId(System.Collections.Generic.List<string> dirs)
        {
            var tmp = new PatchRunner();
            return tmp.FindExistingAppId(dirs.ToArray());
        }

        // ---------------------------------------------------------- patching

        void StartPatch()
        {
            if (running) return;

            if (string.IsNullOrEmpty(zone.GamePath))
            {
                banner.Show(Banner.BannerKind.Warn, "Pick a game executable first.\nDrag & drop the game's .exe into the box above.");
                ShowBannerLayout(true);
                statusBar.Set("Waiting for input", Ui.WarnC);
                return;
            }
            var ofix = tOnlineFix.Checked;
            var id = appIdBox.Text.Trim();
            if (!ofix && (id.Length == 0 || !id.All(char.IsDigit)))
            {
                banner.Show(Banner.BannerKind.Warn, "Enter a valid numeric Steam AppID.\nYou can find it on steamdb.info by searching your game's name.");
                ShowBannerLayout(true);
                statusBar.Set("Waiting for input", Ui.WarnC);
                return;
            }

            var opts = new PatchOptions
            {
                GameExe = zone.GamePath,
                AppId = ofix ? "480" : id,
                UnpackDrm = tUnpack.Checked,
                Backup = tBackup.Checked,
                WriteAppIdTxt = tAppid.Checked,
                CreateSettings = tSettings.Checked,
                GenerateInterfaces = true,
                OnlineFix = ofix,
            };
            settings.LastAppId = id;
            settings.UnpackDrm = tUnpack.Checked;
            settings.Backup = tBackup.Checked;
            settings.WriteAppIdTxt = tAppid.Checked;
            settings.CreateSettings = tSettings.Checked;
            settings.OnlineFix = ofix;
            settings.AppIdsByFolder[Path.GetDirectoryName(opts.GameExe)] = id;
            settings.Save();

            running = true;
            cts = new CancellationTokenSource();
            patchBtn.Kind = GradientButton.BtnKind.Cancel;
            patchBtn.Text = "Cancel";
            zone.Enabled = false;
            appIdBox.Enabled = false;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = false;
            banner.HideBanner();
            ShowBannerLayout(false);
            progress.SetValue(1);
            statusBar.Set("Patching… (Esc to cancel)", Ui.Accent);

            runner = new PatchRunner();
            runner.LogLine += e => BeginInvoke((MethodInvoker)delegate
            {
                log.AppendLine(e.Message, e.Level);
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldbergPatcher");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "last_run.log"), DateTime.Now.ToString("HH:mm:ss") + "  " + e.Message + Environment.NewLine);
                }
                catch { }
            });
            runner.ProgressChanged += p => BeginInvoke((MethodInvoker)delegate { progress.SetValue(p); });

            var token = cts.Token;
            runner.RunAsync(opts, token).ContinueWith(t =>
            {
                var res = t.Status == TaskStatus.RanToCompletion ? t.Result : new PatchResult { Success = false, Summary = "Internal error." };
                BeginInvoke((MethodInvoker)delegate { OnPatchDone(res); });
            });
        }

        void CancelPatch()
        {
            if (!running) return;
            try { cts.Cancel(); } catch { }
            statusBar.Set("Cancelling…", Ui.WarnC);
        }

        void OnPatchDone(PatchResult res)
        {
            running = false;
            lastResult = res;
            patchBtn.Kind = GradientButton.BtnKind.Primary;
            patchBtn.Text = "Patch Game";
            zone.Enabled = true;
            appIdBox.Enabled = true;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = true;
            appIdBox.Enabled = !tOnlineFix.Checked;

            if (res.Success)
            {
                progress.SetValue(100);
                var actions = new List<string> { "Open folder" };
                if (!string.IsNullOrEmpty(res.FinalExe) && File.Exists(res.FinalExe)) actions.Add("Play game");
                lastActions = actions.ToArray();
                banner.Show(Banner.BannerKind.Success, res.Summary, lastActions);
                ShowBannerLayout(true);
                statusBar.Set("Done – game patched successfully", Ui.OkC);
            }
            else if (res.Summary == "Cancelled.")
            {
                progress.SetValue(0);
                lastActions = new string[0];
                banner.Show(Banner.BannerKind.Warn, "Patch cancelled.", new string[0]);
                ShowBannerLayout(true);
                statusBar.Set("Cancelled", Ui.WarnC);
            }
            else
            {
                progress.SetValue(0);
                lastActions = res.NeedsAdmin ? new[] { "Retry as admin" } : new string[0];
                banner.Show(Banner.BannerKind.Error, res.Summary + "\nSee the log below for details.", lastActions);
                ShowBannerLayout(true);
                statusBar.Set("Failed", Ui.ErrC);
            }

            if (startup.ExitWhenDone)
            {
                Environment.ExitCode = res.Success ? 0 : 3;
                var t = new System.Windows.Forms.Timer(); t.Interval = 900;
                t.Tick += delegate { t.Stop(); Close(); };
                t.Start();
            }
        }

        void OnBannerAction(int idx)
        {
            if (idx < 0 || idx >= lastActions.Length) return;
            switch (lastActions[idx])
            {
                case "Open folder":
                    try
                    {
                        var target = lastResult != null && !string.IsNullOrEmpty(lastResult.FinalExe) ? lastResult.FinalExe :
                                     !string.IsNullOrEmpty(zone.GamePath) ? zone.GamePath : null;
                        if (target != null) Process.Start("explorer.exe", "/select,\"" + target + "\"");
                        else if (lastResult != null && Directory.Exists(lastResult.InstallDir)) Process.Start(lastResult.InstallDir);
                    }
                    catch { }
                    break;
                case "Play game":
                    try
                    {
                        if (lastResult != null && File.Exists(lastResult.FinalExe))
                            Process.Start(new ProcessStartInfo(lastResult.FinalExe) { WorkingDirectory = Path.GetDirectoryName(lastResult.FinalExe), UseShellExecute = true });
                    }
                    catch (Exception ex) { MessageBox.Show(ex.Message); }
                    break;
                case "Retry as admin":
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            Arguments = "--exe \"" + zone.GamePath + "\" --appid " + appIdBox.Text + " --auto --exit-when-done",
                            UseShellExecute = true,
                            Verb = "runas",
                        };
                        Process.Start(psi);
                        Close();
                    }
                    catch { }
                    break;
            }
        }

        void ShowBannerLayout(bool show)
        {
            banner.Visible = show && banner.MessageText.Length > 0;
            RecalcLog();
        }

        void Log(LogLevel lvl, string msg)
        {
            log.AppendLine(msg, lvl);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            TextRenderer.DrawText(g, "Patch a Steam game", Ui.F(15.5f, true), new Point(Pad, 62), Ui.TextC, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Unpack SteamStub DRM  ·  install Goldberg emulator  ·  configure AppID — automatically",
                Ui.F(8.75f, false), new Point(Pad, 94), Ui.MutedC, TextFormatFlags.NoPadding);
        }
    }
}
