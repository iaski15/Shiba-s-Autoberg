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
            Ui.FillRound(g, r, 9, Enabled ? Ui.Surface2 : Ui.Tint(Ui.Surface2, Ui.Bg, 0.35));
            Ui.StrokeRound(g, r, 9, focused && Enabled ? Ui.Accent : Ui.BorderC, 1.4f);
            if (focused && Enabled) Ui.StrokeRound(g, Rectangle.Inflate(r, -2, -2), 7, Color.FromArgb(80, Ui.Accent.R, Ui.Accent.G, Ui.Accent.B), 1f);
        }
    }

    // ─────────────────────────────────────────────── status bar

    public class StatusBarCtl : Control
    {
        public string StatusText = "Ready";
        public Color DotColor = Ui.MutedC;
        public string RightText = "goldberg emu · steamless";

        bool _pulse = false, pulseOn = false;
        readonly System.Windows.Forms.Timer pulseTimer;
        public bool Pulse
        {
            get { return _pulse; }
            set
            {
                if (_pulse == value) return;
                _pulse = value;
                if (value) pulseTimer.Start();
                else { pulseOn = false; pulseTimer.Stop(); Invalidate(); }
            }
        }

        public StatusBarCtl()
        {
            Dock = DockStyle.Bottom; Height = 30;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            pulseTimer = new System.Windows.Forms.Timer();
            pulseTimer.Interval = 650;
            pulseTimer.Tick += delegate { pulseOn = !pulseOn; Invalidate(); };
        }
        public void Set(string text, Color dot) { StatusText = text; DotColor = dot; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Ui.BorderC, 1f)) g.DrawLine(p, 0, 0, Width, 0);
            if (_pulse && pulseOn)
                using (var b = new SolidBrush(Color.FromArgb(70, DotColor))) g.FillEllipse(b, 23, Height / 2 - 7, 14, 14);
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
        readonly Toggle tUnpack, tBackup, tAppid, tSettings, tOnlineFix, tLookup;
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

        // appid card live state: "" = default hint, otherwise a status line (auto-detect result)
        string appidNote = "";
        Color appidNoteCol = Ui.MutedC;
        bool appidBusy = false;
        bool notePulseOn = true;
        System.Windows.Forms.Timer notePulse;
        ToolTip zoneTip;

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

            zoneTip = new ToolTip();
            zone = new DropZone();
            zone.Bounds = new Rectangle(Pad, 116, 820 - Pad * 2, 116);
            zone.FileChosen += OnGameSelected;
            zone.InvalidFile += OnInvalidDropped;
            Controls.Add(zone);

            appIdCard = new AppCard();
            appIdCard.Bounds = new Rectangle(Pad, 244, 820 - Pad * 2, 86);
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
                int ty = appIdCard.Height / 2 - 8;

                string t1 = "Find your game's AppID on";
                string t2 = "steamdb.info ↗";
                var s1 = TextRenderer.MeasureText(t1, f8, Size.Empty, TextFormatFlags.NoPadding);
                var s2 = TextRenderer.MeasureText(t2, f8, Size.Empty, TextFormatFlags.NoPadding);
                int hintX = appIdCard.Width - (s1.Width + 12 + s2.Width) - 26;

                // status line (auto-detect result) left of the hint area
                if (!appidBusy && appidNote.Length > 0)
                {
                    var glyph = appidNoteCol == Ui.OkC ? "\u2714" : "!";
                    using (var b = new SolidBrush(appidNoteCol)) g.DrawString(glyph, Ui.F(8.5f, true), b, 300, ty - 1);
                    int noteMaxW = hintX - 316 - 12;
                    string shownNote = noteMaxW > 70 ? Ui.TruncMiddle(g, appidNote, f8, noteMaxW) : "";
                    TextRenderer.DrawText(g, shownNote, f8, new Point(316, ty), appidNoteCol, TextFormatFlags.NoPadding);
                }

                if (appidBusy)
                {
                    string t = "searching Steam Store…";
                    var sz = TextRenderer.MeasureText(t, f8, Size.Empty, TextFormatFlags.NoPadding);
                    int bx = appIdCard.Width - sz.Width - 26;
                    float a = notePulseOn ? 1f : 0.45f;
                    using (var b = new SolidBrush(Color.FromArgb((int)(235 * a), Ui.Accent.R, Ui.Accent.G, Ui.Accent.B)))
                        g.FillEllipse(b, bx - 14, ty + 5, 7, 7);
                    TextRenderer.DrawText(g, t, f8, new Point(bx, ty), Color.FromArgb((int)(235 * a), Ui.TextC.R, Ui.TextC.G, Ui.TextC.B), TextFormatFlags.NoPadding);
                    dbRect = Rectangle.Empty;
                }
                else
                {
                    int tx = hintX;
                    TextRenderer.DrawText(g, t1, f8, new Point(tx, ty), Ui.MutedC, TextFormatFlags.NoPadding);
                    int lx = tx + s1.Width + 12;
                    TextRenderer.DrawText(g, t2, f8, new Point(lx, ty), Ui.Accent2, TextFormatFlags.NoPadding);
                    if (dbHover) using (var p = new Pen(Ui.Accent2, 1f)) g.DrawLine(p, lx, ty + 15, lx + s2.Width, ty + 15);
                    dbRect = new Rectangle(lx - 4, ty - 5, s2.Width + 8, 27);
                }
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

            notePulse = new System.Windows.Forms.Timer();
            notePulse.Interval = 550;
            notePulse.Tick += delegate { if (!appidBusy) return; notePulseOn = !notePulseOn; appIdCard.Invalidate(); };
            SetAppIdBusy(false);

            optionsCard = new AppCard();
            optionsCard.Bounds = new Rectangle(Pad, 342, 820 - Pad * 2, 160);
            Controls.Add(optionsCard);

            optionsCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Ui.SpacedText(g, "OPTIONS", Ui.F(7.5f, true), new SolidBrush(Ui.MutedC), new PointF(22, 13), 1.5f);
            };

            tUnpack = new Toggle("Auto-unpack Steam DRM (Steamless)", settings.UnpackDrm);
            tBackup = new Toggle("Back up replaced files", settings.Backup);
            tAppid = new Toggle("Write steam_appid.txt", settings.WriteAppIdTxt);
            tSettings = new Toggle("Create steam_settings folder", settings.CreateSettings);
            tOnlineFix = new Toggle("Generic online-fix (show game as Spacewar on Steam)", settings.OnlineFix);
            tLookup = new Toggle("Auto-detect Steam AppID online", settings.LookupAppId);
            tOnlineFix.CheckedChanged += delegate
            {
                appIdBox.Enabled = !running && !tOnlineFix.Checked;
                RecalcLog();
            };
            tUnpack.Bounds = new Rectangle(24, 40, 370, 24);
            tBackup.Bounds = new Rectangle(408, 40, 330, 24);
            tAppid.Bounds = new Rectangle(24, 80, 370, 24);
            tSettings.Bounds = new Rectangle(408, 80, 340, 24);
            tOnlineFix.Bounds = new Rectangle(24, 120, 370, 24);
            tLookup.Bounds = new Rectangle(408, 120, 340, 24);
            foreach (Control c in new Control[] { tUnpack, tBackup, tAppid, tSettings, tOnlineFix, tLookup }) optionsCard.Controls.Add(c);

            patchBtn = new GradientButton("Patch Game");
            patchBtn.Bounds = new Rectangle(Pad, 514, 820 - Pad * 2, 52);
            patchBtn.Click += delegate { if (running) CancelPatch(); else StartPatch(); };
            Controls.Add(patchBtn);

            progress = new ProgressBarLite();
            progress.Bounds = new Rectangle(Pad, 574, 820 - Pad * 2, 5);
            Controls.Add(progress);

            banner = new Banner();
            banner.Bounds = new Rectangle(Pad, 588, 820 - Pad * 2, 58);
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
            int top = banner.Visible ? 654 : 596;
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

            if (Payload.Count > 0)
            {
                int restored = Payload.ExtractMissing().Count;
                Log(LogLevel.Dim, restored > 0
                    ? "Self-contained payload: restored " + restored + "/" + Payload.Count + " bundled file(s)."
                    : "Self-contained payload: all " + Payload.Count + " bundled file(s) verified.");
            }

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
                    // wait for AppID resolution (local cache / steam_appid.txt / online detection) before patching
                    autoTimer = new System.Windows.Forms.Timer();
                    autoTimer.Interval = 300;
                    int waitedMs = 0;
                    autoTimer.Tick += delegate
                    {
                        waitedMs += 300;
                        bool boxHasId = appIdBox.Text.Trim().Length > 0;
                        bool go = boxHasId || (selectionResolved && !lookupPending) || waitedMs >= 30000;
                        if (go && waitedMs >= 600) { autoTimer.Stop(); StartPatch(); }
                    };
                    autoTimer.Start();
                }
            }
        }

        // ---------------------------------------------------------- game selection

        void OnGameSelected(string path)
        {
            banner.HideBanner();
            RecalcLog();
            SetAppIdNote("", Ui.MutedC);
            lookupPending = false; // any in-flight detection from the previous game no longer matters
            appIdBox.Text = "";    // fresh AppID resolution on EVERY selection (folder cache → steam_appid.txt → online store)
            zoneTip.SetToolTip(zone, "Full path:\n" + path);
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
                    ApplyApiSearch(gen, dir, archChip, sizeChip, apis);
                });
            });
        }

        int selectGeneration;
        bool lookupPending = false;
        bool selectionResolved = false; // ApplyApiSearch finished its AppID resolution for the current game

        void SetAppIdNote(string note, Color col)
        {
            appidNote = note ?? ""; appidNoteCol = col;
            if (appidBusy) SetAppIdBusy(false);
            appIdCard.Invalidate();
        }
        void SetAppIdBusy(bool busy)
        {
            if (appidBusy == busy) return;
            appidBusy = busy;
            if (busy) { notePulseOn = true; if (notePulse != null) notePulse.Start(); }
            else if (notePulse != null) notePulse.Stop();
            appIdCard.Invalidate();
        }

        void StartOnlineLookup(int gen, string dir)
        {
            var titles = SteamLookup.CandidateTitles(zone.GamePath);
            if (titles.Count == 0) return;
            Log(LogLevel.Dim, "No AppID found locally – searching the Steam Store for \"" + titles[0] + "\"…");

            lookupPending = true;
            SetAppIdBusy(true);
            System.Threading.Tasks.Task.Run(() =>
            {
                SteamMatch match = null;
                foreach (var t in titles)
                {
                    match = SteamLookup.Search(t);
                    if (match != null) break;
                }
                BeginInvoke((MethodInvoker)delegate
                {
                    if (gen != selectGeneration || IsDisposed) return;
                    lookupPending = false;
                    if (appIdBox.Text.Trim().Length > 0) { SetAppIdBusy(false); return; } // user typed something meanwhile – don't clobber it
                    if (match == null) { Log(LogLevel.Dim, "No Steam Store match found – enter the AppID manually."); SetAppIdNote("no store match – type it in manually", Ui.MutedC); }
                    else { appIdBox.Text = match.AppId; Log(LogLevel.Ok, "Auto-detected Steam AppID " + match.AppId + "  (" + match.GameName + ") from the Steam Store – double-check it's the right game."); SetAppIdNote("matched · " + match.GameName, Ui.OkC); }
                });
            });
        }

        void OnInvalidDropped(string path)
        {
            if (running) return;
            var name = Path.GetFileName(path);
            banner.Show(Banner.BannerKind.Warn, "That doesn't look like a Windows executable.\nDrop the game's .exe file (" + name + ") instead.");
            ShowBannerLayout(true);
            statusBar.Set("Waiting for input", Ui.WarnC);
        }

        void ApplyApiSearch(int gen, string dir, string archChip, string sizeChip, System.Collections.Generic.List<string> apis)
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

            // nothing local (cache / steam_appid.txt) found the id – try the Steam Store online
            if (appIdBox.Text.Trim().Length == 0 && tLookup.Checked) StartOnlineLookup(gen, dir);
            selectionResolved = true;
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
            settings.LookupAppId = tLookup.Checked;
            if (id.Length > 0) settings.AppIdsByFolder[Path.GetDirectoryName(opts.GameExe)] = id; // don't cache empty ids
            settings.Save();

            running = true;
            cts = new CancellationTokenSource();
            patchBtn.Kind = GradientButton.BtnKind.Cancel;
            patchBtn.Text = "Cancel";
            zone.Enabled = false;
            appIdBox.Enabled = false;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = tLookup.Enabled = false;
            banner.HideBanner();
            ShowBannerLayout(false);
            progress.SetValue(1);
            statusBar.Pulse = true;
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
            statusBar.Pulse = false;
            lastResult = res;
            patchBtn.Kind = GradientButton.BtnKind.Primary;
            patchBtn.Text = "Patch Game";
            zone.Enabled = true;
            appIdBox.Enabled = true;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = tLookup.Enabled = true;
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

        static void AmbientGlow(Graphics g, Rectangle clipRect, float cx, float cy, float radius, Color c, int alpha)
        {
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2));
                using (var b = new PathGradientBrush(p))
                {
                    b.CenterColor = Color.FromArgb(alpha, c.R, c.G, c.B);
                    b.SurroundColors = new[] { Color.Transparent };
                    g.FillRectangle(b, clipRect);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);

            AmbientGlow(g, ClientRectangle, Width / 2f + 40f, 150f, 430f, Ui.Accent, 18);
            AmbientGlow(g, ClientRectangle, (float)Width - 60f, Height - 210f, 400f, Ui.Accent2, 12);

            // hero title in the brand gradient
            string title = "Patch a Steam game";
            var tf = Ui.F(15.5f, true);
            try
            {
                float tw = (float)g.MeasureString(title, tf).Width;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    using (var lg = new LinearGradientBrush(new PointF(Pad, 0), new PointF(Pad + Math.Max(tw, 1f), 0), Ui.Accent, Ui.Accent2))
                        g.DrawString(title, tf, lg, new PointF(Pad, 54f), StringFormat.GenericTypographic);
            }
            catch { TextRenderer.DrawText(g, title, tf, new Point(Pad, 58), Ui.TextC, TextFormatFlags.NoPadding); }
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            TextRenderer.DrawText(g, "Unpack SteamStub DRM  ·  install Goldberg emulator  ·  configure AppID — automatically",
                Ui.F(8.75f, false), new Point(Pad, 88), Ui.MutedC, TextFormatFlags.NoPadding);
        }
    }
}
