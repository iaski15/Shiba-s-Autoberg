using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

        /// <summary>Ignore the payload verification cache and hash every bundled file.</summary>
        public bool VerifyPayload;

        /// <summary>Batch-mode overrides. Without these, --batch could only ever run one fixed
        /// configuration while the GUI batch dialog exposed all five options.</summary>
        public bool BatchOnlineFix;
        public bool BatchNoUnpack;
        public bool BatchSettings;

        // "C:\game1\g1.exe|480;C:\game2\g2.exe" – the AppID part may be omitted (auto-detected locally) or empty (skipped)
        public string Batch = "";
        public string Initialization = "";
        public const string Usage = "Usage: Goldberg Patcher.exe --exe <game.exe> [--appid <id>] [--auto] [--exit-when-done]\n       Goldberg Patcher.exe --batch \"<game.exe>|<id>;<game.exe>\" [--online-fix] [--no-unpack] [--settings]\n       Goldberg Patcher.exe --verify-payload\nBatch exits: 0 = every entry patched; 1 = invalid input, failures or partial completion; 2 = nothing patched (skipped/cancelled only).\nSingle-game exits: 0 = patched, 1 = bad arguments or failure, 3 = --auto could not resolve an AppID.\n--verify-payload exits: 0 = payload intact, 1 = missing or corrupt files.";

        public static StartupArgs Parse(string[] a)
        {
            var r = new StartupArgs();
            if (a == null) return r;
            for (int i = 0; i < a.Length; i++)
            {
                var s = (a[i] ?? "").ToLowerInvariant();
                if (s == "--exe" || s == "--appid" || s == "--batch")
                {
                    // Any leading dash means a flag, not a value. The guard used to reject only "--", so a
                    // mistyped "-appid" was silently accepted as the value of the previous flag.
                    if (i + 1 >= a.Length || string.IsNullOrWhiteSpace(a[i + 1]) || a[i + 1].StartsWith("-"))
                        throw new ArgumentException("Missing value for " + s);
                    string value = a[++i];
                    if (s == "--exe") r.Exe = value;
                    else if (s == "--appid")
                    {
                        if (!AppIdDetector.TryNormalize(value, out r.AppId)) throw new ArgumentException("Invalid --appid value.");
                    }
                    else r.Batch = value;
                }
                else if (s == "--auto") r.Auto = true;
                else if (s == "--exit-when-done") r.ExitWhenDone = true;
                else if (s == "--verify-payload") r.VerifyPayload = true;
                else if (s == "--online-fix") r.BatchOnlineFix = true;
                else if (s == "--no-unpack") r.BatchNoUnpack = true;
                else if (s == "--settings") r.BatchSettings = true;
                else throw new ArgumentException("Unknown argument: " + s);
            }
            if (r.Batch.Length > 0 && (r.Exe.Length > 0 || r.AppId.Length > 0 || r.Auto || r.ExitWhenDone))
                throw new ArgumentException("--batch cannot be combined with single-game flags.");
            if (r.Batch.Length == 0 && (r.BatchOnlineFix || r.BatchNoUnpack || r.BatchSettings))
                throw new ArgumentException("--online-fix, --no-unpack and --settings require --batch.");
            if (r.Batch.Length == 0 && r.Exe.Length == 0 && (r.AppId.Length > 0 || r.Auto || r.ExitWhenDone))
                throw new ArgumentException("Single-game flags require --exe.");
            if (r.Exe.Length > 0 && (!File.Exists(r.Exe) || !r.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("--exe must name an existing .exe file.");
            return r;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int stdHandle);

        const uint AttachParentProcess = 0xFFFFFFFF;
        const int StdOutputHandle = -11;
        const int StdErrorHandle = -12;

        /// <summary>The app is built /target:winexe, so when a shell starts it the process has no console
        /// of its own and every Console.WriteLine from the documented CLI modes is discarded - the batch
        /// engine was effectively undebuggable from a command line. Attach to the parent's console and
        /// reopen the streams. Handles that the parent already redirected (a pipe) are valid and are left
        /// exactly as they are, so piping the output keeps working.</summary>
        static void AttachParentConsole()
        {
            try
            {
                IntPtr stdout = GetStdHandle(StdOutputHandle);
                IntPtr stderr = GetStdHandle(StdErrorHandle);
                bool stdoutValid = stdout != IntPtr.Zero && stdout != (IntPtr)(-1);
                bool stderrValid = stderr != IntPtr.Zero && stderr != (IntPtr)(-1);
                if (stdoutValid && stderrValid) return;
                if (!AttachConsole(AttachParentProcess)) return;
                if (!stdoutValid)
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                if (!stderrValid)
                    Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
            catch { }
        }

        /// <summary>Appends a crash to errors.log with the full stack. Used by every unhandled-exception
        /// path so a failure off the UI thread leaves a trace instead of vanishing.</summary>
        internal static void LogFatal(string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.StateDir);
                File.AppendAllText(Path.Combine(AppPaths.StateDir, "errors.log"),
                    DateTime.Now + "  [" + context + "]" + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { if (!SetProcessDpiAwarenessContext((IntPtr)(-4))) SetProcessDPIAware(); }
            catch { try { SetProcessDPIAware(); } catch { } }
            Ui.InitializeScale();

            // Any of the CLI modes may be run from a shell that gave this process no console.
            if (args != null && args.Length > 0)
            {
                AttachParentConsole();
                // The CLI output uses box-drawing characters and ticks; on a non-UTF-8 console code page
                // they render as mojibake, which made the batch log look corrupted rather than misconfigured.
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            }

            StartupArgs sa;
            try { sa = StartupArgs.Parse(args); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message + Environment.NewLine + StartupArgs.Usage);
                Environment.ExitCode = 1;
                return;
            }
            string initialization;
            if (!InitializePayload(out initialization, sa.VerifyPayload))
            {
                Console.Error.WriteLine(initialization);
                if (args == null || args.Length == 0) MessageBox.Show(initialization, "Setup incomplete");
                Environment.ExitCode = 1;
                return;
            }
            sa.Initialization = initialization;

            // --verify-payload on its own is a payload check, not a patch: report and stop.
            if (sa.VerifyPayload && sa.Batch.Length == 0 && sa.Exe.Length == 0)
            {
                Console.WriteLine(initialization);
                Environment.ExitCode = Payload.LastErrors != null && Payload.LastErrors.Count > 0 ? 1 : 0;
                return;
            }

            SweepStaleRecovery();
            if (sa.Batch.Length > 0)
            {
                Environment.Exit(RunBatchCli(sa));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
            {
                LogFatal("UI thread", e.Exception);
                MessageBox.Show(e.Exception.Message, "Goldberg Patcher – unexpected error");
            };
            // Without these two, an exception on a background thread or an unobserved task fault kills the
            // process with no errors.log entry and no message at all - the batch and scan paths both run
            // off the UI thread, so this is reachable in normal use.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogFatal("AppDomain (terminating)", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogFatal("Unobserved task", e.Exception);
                e.SetObserved();
            };
            Application.Run(new MainForm(sa));
        }

        /// <summary>Clears out .gp-recovery areas left behind by a run that died before it could record or
        /// collect them. Looks only at the app's own state directory and the last game's folder, and never
        /// recurses into the game tree. Best-effort: a failure here must never stop the app starting.</summary>
        static void SweepStaleRecovery()
        {
            try
            {
                var roots = new List<string>();
                string lastExe = AppSettings.Load().LastExe;
                if (!string.IsNullOrEmpty(lastExe))
                {
                    string dir = Path.GetDirectoryName(lastExe);
                    if (!string.IsNullOrEmpty(dir)) roots.Add(dir);
                }
                Recovery.SweepStale(roots, 7);
            }
            catch { }
        }

        internal static bool InitializePayload(out string message, bool forceVerify = false)
        {
            try
            {
                int restored = Payload.Count > 0 ? Payload.ExtractMissing(forceVerify).Count : 0;
                var errors = new List<string>();
                if (Payload.LastErrors != null) errors.AddRange(Payload.LastErrors);
                errors.AddRange(Tools.Missing());
                // The parenthetical describes how the pass ran, which only means anything when nothing
                // needed rewriting - "80 restored (cached)" said two contradictory things at once.
                message = errors.Count > 0 ? "Payload initialization failed: " + string.Join("; ", errors)
                    : "Payload verified: " + Payload.Count + " bundled files, " + restored + " restored"
                      + (restored > 0 ? "." : (Payload.LastPassHashed ? " (hashed)." : " (cached)."));
                return errors.Count == 0;
            }
            catch (Exception ex) { message = "Payload initialization failed: " + ex.Message; return false; }
        }

        static int RunBatchCli(StartupArgs sa)
        {
            if (!string.IsNullOrEmpty(sa.Initialization)) Console.WriteLine(sa.Initialization);
            var items = new List<BatchInput>();
            int invalid = 0;
            foreach (var raw in (sa.Batch ?? "").Split(';'))
            {
                string entry = (raw ?? "").Trim();
                if (entry.Length == 0) continue;
                int bar = entry.IndexOf('|');
                string exePart, appId;
                if (bar < 0) { exePart = entry; appId = ""; }
                else { exePart = entry.Substring(0, bar).Trim(); appId = entry.Substring(bar + 1).Trim(); }

                string reason;
                string full = ValidateBatchEntry(exePart, appId, out reason);
                if (full == null)
                {
                    Console.WriteLine("FAIL " + exePart + "   (" + reason + ")");
                    invalid++;
                    continue;
                }
                items.Add(new BatchInput { Exe = full, AppId = appId });
            }

            if (items.Count == 0)
            {
                Console.WriteLine("Goldberg Patcher --batch: no usable game entries.");
                Console.WriteLine(StartupArgs.Usage);
                return invalid > 0 ? 1 : 2;
            }

            // resolve missing AppIDs from local sources (saved id / steam_appid.txt in the tree)
            foreach (var it in items)
                if (string.IsNullOrEmpty(it.AppId))
                    try { var d = AppIdDetector.Detect(it.Exe, "", false); if (d.Found) it.AppId = d.AppId; } catch { }

            Console.WriteLine("Goldberg Patcher --batch: " + items.Count + (items.Count == 1 ? " game" : " games")
                + (invalid > 0 ? ", " + invalid + " invalid entr" + (invalid == 1 ? "y" : "ies") + " (counted as failures)" : ""));
            var prefs = new BatchPrefs
            {
                UnpackDrm = !sa.BatchNoUnpack,
                Backup = true,
                WriteAppIdTxt = true,
                CreateSettings = sa.BatchSettings,
                OnlineFix = sa.BatchOnlineFix,
            };
            var patcher = new BatchPatcher();
            patcher.LogLine += e => Console.WriteLine("  [" + e.Level.ToString().ToLowerInvariant() + "] " + e.Message);

            List<BatchItemOutcome> results;
            try { results = patcher.RunAsync(items, prefs, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine("Batch error: " + ex.Message); return 1; }

            int ok = 0, bad = 0, skip = 0;
            foreach (var o in results ?? new List<BatchItemOutcome>())
            {
                if (o.Success) { ok++; continue; }
                if (o.Skipped || o.Cancelled) { skip++; Console.WriteLine("SKIP " + o.Exe + "   (" + o.Summary + ")"); continue; }
                bad++;
                Console.WriteLine("FAIL " + o.Exe + "   (" + o.Summary + ")");
            }

            Console.WriteLine("--batch done: " + ok + " patched, " + skip + " skipped, " + bad + " failed"
                + (invalid > 0 ? ", " + invalid + " invalid" : "") + ".");
            if (bad > 0 || invalid > 0) return 1;
            if (ok == 0) return 2;
            return skip > 0 || ok < items.Count ? 1 : 0;
        }

        static string ValidateBatchEntry(string exePart, string appId, out string reason)
        {
            reason = "";
            if (exePart.Length == 0) { reason = "empty path"; return null; }
            if (appId.Length > 0 && !AppIdDetector.IsValid(appId)) { reason = "invalid AppID \"" + appId + "\""; return null; }
            string full;
            try { full = Path.GetFullPath(exePart); }
            catch { reason = "bad path"; return null; }
            if (!File.Exists(full)) { reason = "file not found"; return null; }
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { reason = "not an .exe file"; return null; }
            return full;
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
            box.SetBounds(Ui.S(12), (Height - box.PreferredHeight) / 2, Width - Ui.S(24), box.PreferredHeight);
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
        protected override void Dispose(bool disposing)
        {
            if (disposing) { pulseTimer.Stop(); pulseTimer.Dispose(); }
            base.Dispose(disposing);
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
            TextRenderer.DrawText(g, RightText, Ui.F(7.75f, false), new Point(Width - sz.Width - 26, Height / 2 - 8), Ui.DisabledC, TextFormatFlags.NoPadding);
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
        readonly GradientButton batchBtn;
        readonly ProgressBarLite progress;
        readonly Banner banner;
        readonly AppCard logCard;
        readonly LogView log;
        readonly StatusBarCtl statusBar;

        readonly AppSettings settings;
        readonly StartupArgs startup;
        PatchRunner runner;
        CancellationTokenSource cts;
        Task patchTask = Task.FromResult(0);
        volatile bool running;
        bool waitingClose, allowClose;
        PatchResult lastResult;
        string[] lastActions = new string[0];

        System.Windows.Forms.Timer autoTimer;
        Task autoTimerTask = Task.FromResult(0);
        TaskCompletionSource<bool> autoSignal;
        System.Windows.Forms.Timer exitTimer;
        volatile bool closing;

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
            // The process opts into PerMonitorV2 DPI awareness, so without this the fixed-pixel layout stays
            // put while the point-sized fonts grow: clipped labels and a window that is tiny on a HiDPI
            // panel. AutoScaleMode.Dpi makes WinForms scale every control's bounds by the same factor
            // Ui.Scale uses for the hand-positioned paint geometry.
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
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
                using (var b = new SolidBrush(Ui.MutedC))
                    Ui.SpacedText(g, "STEAM APPID", Ui.F(7.5f, true), b, new PointF(22, 14), 1.5f);

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
                using (var b = new SolidBrush(Ui.MutedC))
                    Ui.SpacedText(g, "OPTIONS", Ui.F(7.5f, true), b, new PointF(22, 13), 1.5f);
            };

            tUnpack = new Toggle("Auto-unpack Steam DRM (Steamless)", settings.UnpackDrm);
            tBackup = new Toggle("Back up replaced files", settings.Backup);
            tAppid = new Toggle("Write steam_appid.txt", settings.WriteAppIdTxt);
            tSettings = new Toggle("Create steam_settings folder", settings.CreateSettings);
            tOnlineFix = new Toggle("Generic online-fix (show game as Spacewar on Steam)", settings.OnlineFix);
            tLookup = new Toggle("Auto-detect Steam AppID online", settings.LookupAppId);
            tOnlineFix.CheckedChanged += delegate
            {
                appIdBox.Enabled = !closing && !running && !tOnlineFix.Checked;
                RecalcLog();
            };
            tUnpack.Bounds = new Rectangle(24, 40, 370, 24);
            tBackup.Bounds = new Rectangle(408, 40, 330, 24);
            tAppid.Bounds = new Rectangle(24, 80, 370, 24);
            tSettings.Bounds = new Rectangle(408, 80, 340, 24);
            tOnlineFix.Bounds = new Rectangle(24, 120, 370, 24);
            tLookup.Bounds = new Rectangle(408, 120, 340, 24);
            foreach (Control c in new Control[] { tUnpack, tBackup, tAppid, tSettings, tOnlineFix, tLookup }) optionsCard.Controls.Add(c);

            int rowW = 820 - Pad * 2;
            int batchW = 210, gap = 14;
            patchBtn = new GradientButton("Patch Game");
            patchBtn.Bounds = new Rectangle(Pad, 514, rowW - batchW - gap, 52);
            patchBtn.Click += delegate { if (running) CancelPatch(); else StartPatch(); };
            Controls.Add(patchBtn);

            batchBtn = new GradientButton("Batch Patch…");
            batchBtn.Kind = GradientButton.BtnKind.Secondary;
            batchBtn.Bounds = new Rectangle(Pad + rowW - batchW, 514, batchW, 52);
            batchBtn.Click += delegate { OpenBatch(); };
            Controls.Add(batchBtn);

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
            FormClosing += OnFormClosing;
            Resize += delegate { RecalcLog(); };
        }

        async void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (waitingClose) return;
            waitingClose = true;
            closing = true;
            StopAutoTimer();
            StopExitTimer();
            patchBtn.Enabled = batchBtn.Enabled = zone.Enabled = appIdBox.Enabled = banner.Enabled = false;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = tLookup.Enabled = false;
            CancelSelectionWork();
            SetAppIdBusy(false);
            CancelPatch();
            statusBar.Set("Stopping safely – waiting for outstanding work…", Ui.WarnC);
            var work = Task.WhenAll(selectionTasks.Concat(new[] { patchTask, autoTimerTask }));
            await Task.Yield();
            try { await work; }
            catch (Exception ex) { if (!IsDisposed) Log(LogLevel.Error, "Shutdown: " + ex.Message); }
            if (IsDisposed) return;
            statusBar.Pulse = false;
            if (cts != null) { cts.Dispose(); cts = null; }
            allowClose = true;
            Close();
        }

        void StopAutoTimer()
        {
            if (autoTimer != null) { autoTimer.Stop(); autoTimer.Dispose(); autoTimer = null; }
            if (autoSignal != null) autoSignal.TrySetResult(true);
        }

        void StopExitTimer()
        {
            if (exitTimer != null) { exitTimer.Stop(); exitTimer.Dispose(); exitTimer = null; }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        Rectangle LogBounds()
        {
            // Scaled explicitly: WinForms' auto-scale sizes the controls once at load, but this runs again on
            // every resize and banner toggle, so the constants here have to be scaled by hand to match.
            int pad = Ui.S(Pad);
            int top = Ui.S(banner.Visible ? 654 : 596);
            return new Rectangle(pad, top, ClientSize.Width - pad * 2, ClientSize.Height - top - Ui.S(40));
        }
        void RecalcLog()
        {
            logCard.Bounds = LogBounds();
            log.SetBounds(Ui.S(12), Ui.S(12), logCard.Width - Ui.S(24), logCard.Height - Ui.S(24));
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
            if (closing || IsDisposed) return;
            Log(LogLevel.Dim, "Goldberg Patcher ready. Drop a game .exe to begin.");

            Log(LogLevel.Dim, startup.Initialization);

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
                    autoSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    autoTimer.Tick += delegate
                    {
                        waitedMs += 300;
                        bool boxHasId = appIdBox.Text.Trim().Length > 0;
                        bool go = boxHasId || (selectionResolved && !lookupPending) || waitedMs >= 30000;
                        if (!go || waitedMs < 600) return;
                        StopAutoTimer();
                        if (closing) return;
                        if (!StartPatch())
                        {
                            // Validation bailed (e.g. the AppID never resolved). With --exit-when-done the
                            // documented contract is exit code 3 – don't hang forever; without it, keep the
                            // window open so the user can type an AppID and retry.
                            Log(LogLevel.Error, "Auto-patch aborted – no valid Steam AppID could be resolved.");
                            if (startup.ExitWhenDone) { Environment.ExitCode = 3; BeginAutoExit(); }
                        }
                    };
                    autoTimer.Start();
                    autoTimerTask = AutoPatchAsync();
                }
            }

            // The undo journal lives outside the game folder, so a patch from an earlier session can
            // still be reverted. Skip the offer in headless auto mode so CLI runs stay unattended.
            if (!startup.Auto && Recovery.HasLastPatch())
            {
                lastActions = new[] { "Undo patch" };
                banner.Show(Banner.BannerKind.Warn,
                    "A previous patch can still be undone.\nChoose 'Undo patch' to restore the files it replaced.", lastActions);
                ShowBannerLayout(true);
                statusBar.Set("The last patch can be undone", Ui.WarnC);
            }
        }

        Task AutoPatchAsync()
        {
            return autoSignal != null ? autoSignal.Task : Task.FromResult(false);
        }

        void BeginAutoExit()
        {
            if (closing || IsDisposed || exitTimer != null) return;
            var t = new System.Windows.Forms.Timer { Interval = 900 };
            t.Tick += delegate
            {
                t.Stop();
                StopExitTimer();
                if (!closing && !IsDisposed) Close();
            };
            exitTimer = t;
            t.Start();
        }

        // ---------------------------------------------------------- game selection

        void OnGameSelected(string path)
        {
            if (closing || running || IsDisposed) return;
            selectionResolved = false;
            banner.HideBanner();
            RecalcLog();
            SetAppIdNote("", Ui.MutedC);
            CancelSelectionWork();
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
            var scanSource = new CancellationTokenSource();
            selectionCts = scanSource;
            selectionTasks.RemoveAll(t => t.IsCompleted);
            selectionTasks.Add(ScanSelectionAsync(gen, dir, archChip, sizeChip, scanSource));
        }

        async Task ScanSelectionAsync(int gen, string dir, string archChip, string sizeChip, CancellationTokenSource source)
        {
            var apis = new List<string>();
            try { apis = await Task.Run(() => PatchRunner.FindSteamApiFiles(dir, source.Token), source.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (!closing && gen == selectGeneration) Log(LogLevel.Warn, ex.Message); }
            finally
            {
                if (selectionCts == source) selectionCts = null;
                source.Dispose();
            }
            if (!closing && !IsDisposed && gen == selectGeneration) ApplyApiSearch(gen, dir, archChip, sizeChip, apis);
        }

        void CancelSelectionWork()
        {
            selectGeneration++;
            var source = Interlocked.Exchange(ref selectionCts, null);
            if (source != null) { try { source.Cancel(); } catch { } }
        }

        readonly List<Task> selectionTasks = new List<Task>();
        int selectGeneration;
        CancellationTokenSource selectionCts;
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
            var lookupSource = new CancellationTokenSource();
            selectionCts = lookupSource;
            selectionTasks.RemoveAll(t => t.IsCompleted);
            selectionTasks.Add(LookupSelectionAsync(gen, titles, lookupSource));
        }

        async Task LookupSelectionAsync(int gen, List<string> titles, CancellationTokenSource source)
        {
            SteamMatch match = null;
            try
            {
                match = await Task.Run(() =>
                {
                    foreach (var title in titles)
                    {
                        var result = SteamLookup.Search(title, source.Token);
                        if (result != null) return result;
                    }
                    return null;
                }, source.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (!closing && gen == selectGeneration) Log(LogLevel.Warn, ex.Message); }
            finally
            {
                if (selectionCts == source) selectionCts = null;
                source.Dispose();
            }
            if (closing || gen != selectGeneration || IsDisposed) return;
            lookupPending = false;
            if (appIdBox.Text.Trim().Length > 0) { SetAppIdBusy(false); return; }
            if (match == null) { Log(LogLevel.Dim, "No Steam Store match found – enter the AppID manually."); SetAppIdNote("no store match – type it in manually", Ui.MutedC); }
            else { appIdBox.Text = match.AppId; Log(LogLevel.Ok, "Auto-detected Steam AppID " + match.AppId + "  (" + match.GameName + ") from the Steam Store – double-check it's the right game."); SetAppIdNote("matched · " + match.GameName, Ui.OkC); }
        }

        void OnInvalidDropped(string path)
        {
            if (running || closing) return;
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
                    // The exe's own folder first – that's the steam_appid.txt Steam actually reads;
                    // a stale copy in some deep subfolder must not beat it.
                    var dirs = new List<string> { dir };
                    foreach (var a in apis) dirs.Add(Path.GetDirectoryName(a));
                    appIdBox.Text = PatchRunner.FindExistingAppId(dirs.ToArray());
                }
            }

            // nothing local (cache / steam_appid.txt) found the id – try the Steam Store online
            if (appIdBox.Text.Trim().Length == 0 && tLookup.Checked) StartOnlineLookup(gen, dir);
            selectionResolved = true;
        }

        // ---------------------------------------------------------- patching

        bool StartPatch()
        {
            if (running || closing || IsDisposed || Disposing || !patchTask.IsCompleted) return false;

            if (string.IsNullOrEmpty(zone.GamePath))
            {
                banner.Show(Banner.BannerKind.Warn, "Pick a game executable first.\nDrag & drop the game's .exe into the box above.");
                ShowBannerLayout(true);
                statusBar.Set("Waiting for input", Ui.WarnC);
                return false;
            }
            var ofix = tOnlineFix.Checked;
            var id = AppIdDetector.Normalize(appIdBox.Text);
            if (!ofix && !AppIdDetector.IsValid(id))
            {
                banner.Show(Banner.BannerKind.Warn, "Enter a valid numeric Steam AppID.\nYou can find it on steamdb.info by searching your game's name.");
                ShowBannerLayout(true);
                statusBar.Set("Waiting for input", Ui.WarnC);
                return false;
            }

            var opts = new PatchOptions
            {
                GameExe = zone.GamePath,
                AppId = ofix ? "480" : id,
                UnpackDrm = tUnpack.Checked,
                Backup = tBackup.Checked,
                WriteAppIdTxt = tAppid.Checked,
                CreateSettings = tSettings.Checked,
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
            string saveError;
            if (!settings.Save(out saveError)) Log(LogLevel.Error, saveError);

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
            var patchLog = new BufferedRunLog(log, "");
            runner.LogLine += e => patchLog.Append(e.Message, e.Level);
            runner.ProgressChanged += p => UiInvoke(delegate { progress.SetValue(p); });

            var token = cts.Token;
            patchTask = CompletePatchAsync(runner.RunAsync(opts, token), patchLog);
            return true;
        }

        async Task CompletePatchAsync(Task<PatchResult> task, BufferedRunLog patchLog)
        {
            try
            {
                PatchResult res;
                try { res = await task; }
                catch (OperationCanceledException) { res = new PatchResult { Summary = "Cancelled.", Cancelled = true }; }
                catch (Exception ex) { res = new PatchResult { Summary = "Internal error: " + ex.Message }; }
                await patchLog.CompleteAsync();
                if (!IsDisposed && !Disposing) OnPatchDone(res);
            }
            finally
            {
                patchLog.Dispose();
                running = false;
                if (cts != null) { cts.Dispose(); cts = null; }
            }
        }

        // Marshals an action to the UI thread. Swallows ObjectDisposedException when a callback from a
        // worker thread arrives after the form has already closed – BeginInvoke itself would throw on the
        // pool thread (unobserved) because IsDisposed can only be checked inside the delegate.
        void UiInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) a(); }); }
            catch (InvalidOperationException) { }
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
            patchBtn.Enabled = batchBtn.Enabled = zone.Enabled = !closing;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = tLookup.Enabled = !closing;
            appIdBox.Enabled = !closing && !tOnlineFix.Checked;

            if (res.Success)
            {
                progress.SetValue(100);
                var actions = new List<string> { "Open folder" };
                if (!string.IsNullOrEmpty(res.FinalExe) && File.Exists(res.FinalExe)) actions.Add("Play game");
                if (Recovery.HasLastPatch()) actions.Add("Undo patch");
                lastActions = actions.ToArray();
                banner.Show(Banner.BannerKind.Success, res.Summary, lastActions);
                ShowBannerLayout(true);
                statusBar.Set("Done – game patched successfully", Ui.OkC);
            }
            else if (res.Cancelled)
            {
                progress.SetValue(0);
                lastActions = new string[0];
                banner.Show(Banner.BannerKind.Warn, res.Summary + "\nSee the log below for details.", new string[0]);
                ShowBannerLayout(true);
                statusBar.Set(res.PartialChanges ? "Cancelled – partial changes remain"
                    : res.RolledBack ? "Cancelled – changes undone" : "Cancelled", Ui.WarnC);
            }
            else
            {
                progress.SetValue(0);
                var failedActions = new List<string>();
                if (res.NeedsAdmin) failedActions.Add("Retry as admin");
                if (Recovery.HasLastPatch()) failedActions.Add("Undo patch");
                lastActions = failedActions.ToArray();
                banner.Show(Banner.BannerKind.Error, res.Summary + "\nSee the log below for details.", lastActions);
                ShowBannerLayout(true);
                statusBar.Set(res.RolledBack ? "Failed – changes undone" : "Failed", Ui.ErrC);
            }

            if (startup.ExitWhenDone)
            {
                Environment.ExitCode = res.Success ? 0 : 3;
                if (!closing) BeginAutoExit();
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
                        // Omit --appid entirely when the box is empty (online-fix mode): otherwise the flag
                        // would swallow "--auto" as its value and the relaunched process would fail validation.
                        var retryArgs = new StringBuilder();
                        retryArgs.Append("--exe \"").Append(zone.GamePath).Append("\"");
                        string retryId = appIdBox.Text.Trim();
                        if (retryId.Length > 0) retryArgs.Append(" --appid ").Append(retryId);
                        retryArgs.Append(" --auto --exit-when-done");
                        var psi = new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            Arguments = retryArgs.ToString(),
                            UseShellExecute = true,
                            Verb = "runas",
                        };
                        Process.Start(psi);
                        Close();
                    }
                    catch { }
                    break;
                case "Undo patch":
                    StartUndo();
                    break;
            }
        }

        // ---------------------------------------------------------- undo last patch

        /// <summary>Replays the persisted undo journal so the game goes back to how it was before the
        /// last patch. Runs off the UI thread; the log lines are collected and flushed in one go.</summary>
        void StartUndo()
        {
            if (running || closing || IsDisposed) return;
            if (!Recovery.HasLastPatch())
            {
                statusBar.Set("There is nothing to undo", Ui.MutedC);
                return;
            }

            running = true;
            banner.HideBanner();
            ShowBannerLayout(false);
            patchBtn.Enabled = batchBtn.Enabled = zone.Enabled = appIdBox.Enabled = false;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = tLookup.Enabled = false;
            progress.SetValue(10);
            statusBar.Pulse = true;
            statusBar.Set("Undoing the last patch…", Ui.WarnC);

            Task.Run(delegate
            {
                var lines = new List<PatchLogEntry>();
                Action<LogLevel, string> sink = delegate(LogLevel level, string message)
                {
                    lines.Add(new PatchLogEntry { Time = DateTime.Now, Level = level, Message = message });
                };
                RecoveryReport report;
                try
                {
                    report = Recovery.RollbackLastPatch(sink);
                    // Keep the journal when part of the undo failed, so it can be retried.
                    if (report.Failed == 0) Recovery.ClearLastPatch();
                }
                catch (Exception ex)
                {
                    report = new RecoveryReport();
                    report.Failed++;
                    report.Messages.Add(ex.Message);
                }
                lines.Add(new PatchLogEntry
                {
                    Time = DateTime.Now,
                    Level = report.Failed == 0 ? LogLevel.Ok : LogLevel.Warn,
                    Message = "Undo finished: " + report.Summary
                });
                UiInvoke(delegate
                {
                    foreach (var line in lines) Log(line.Level, line.Message);
                    OnUndoDone(report);
                });
            });
        }

        void OnUndoDone(RecoveryReport report)
        {
            running = false;
            statusBar.Pulse = false;
            progress.SetValue(0);
            patchBtn.Enabled = batchBtn.Enabled = zone.Enabled = !closing;
            tUnpack.Enabled = tBackup.Enabled = tAppid.Enabled = tSettings.Enabled = tOnlineFix.Enabled = tLookup.Enabled = !closing;
            appIdBox.Enabled = !closing && !tOnlineFix.Checked;
            lastActions = new string[0];

            if (report.Failed == 0 && report.ChangedAnything)
            {
                banner.Show(Banner.BannerKind.Success,
                    "Undone – " + report.Summary + "\nThe game is back to its previous state.", new string[0]);
                ShowBannerLayout(true);
                statusBar.Set("Undone – " + report.Summary, Ui.OkC);
            }
            else
            {
                string detail = report.Messages.Count > 0 ? "\n" + string.Join("\n", report.Messages.ToArray()) : "";
                // A partial undo keeps its journal, so offer to retry rather than leaving a dead end.
                lastActions = report.Failed > 0 && Recovery.HasLastPatch() ? new[] { "Undo patch" } : new string[0];
                banner.Show(report.Failed > 0 ? Banner.BannerKind.Error : Banner.BannerKind.Warn,
                    "Undo finished: " + report.Summary + detail, lastActions);
                ShowBannerLayout(true);
                statusBar.Set("Undo finished – " + report.Summary, report.Failed > 0 ? Ui.ErrC : Ui.WarnC);
            }
        }

        // ---------------------------------------------------------- batch patching

        void OpenBatch()
        {
            if (running || closing) return;

            var prefs = new BatchPrefs
            {
                UnpackDrm = tUnpack.Checked,
                Backup = tBackup.Checked,
                WriteAppIdTxt = tAppid.Checked,
                CreateSettings = tSettings.Checked,
                OnlineFix = tOnlineFix.Checked,
            };

            batchBtn.Enabled = false;
            var f = new BatchForm(settings, prefs);
            try { f.ShowDialog(this); } finally { f.Dispose(); batchBtn.Enabled = true; }

            if (!f.HasRun) return;
            string summary = f.SummaryLine();
            Log(LogLevel.Info, "Batch: " + summary);
            tLookup.Checked = settings.LookupAppId; // may have been changed inside the dialog

            if (f.OkCount > 0 && f.FailCount == 0 && f.SkipCount == 0)
            {
                banner.Show(Banner.BannerKind.Success, "Batch complete – all " + f.TotalGames + " game(s) patched.", new string[0]);
                ShowBannerLayout(true);
                statusBar.Set("Batch done – " + summary, Ui.OkC);
            }
            else if (f.OkCount > 0)
            {
                banner.Show(Banner.BannerKind.Warn, "Batch complete with problems: " + summary, new string[0]);
                ShowBannerLayout(true);
                statusBar.Set("Batch done – " + summary, f.FailCount > 0 ? Ui.ErrC : Ui.WarnC);
            }
            else
            {
                banner.Show(Banner.BannerKind.Error, "Batch finished without patching anything. " + summary + "\nSee the batch dialog log for details.", new string[0]);
                ShowBannerLayout(true);
                statusBar.Set("Batch failed", Ui.ErrC);
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

        Bitmap glowCache;
        Size glowCacheSize;

        /// <summary>Both ambient glows are whole-client-rectangle gradient rasterisations, and this form
        /// repaints on every resize tick, so rendering them inline was two full-window gradient fills per
        /// frame. Render them once per size into a bitmap and blit it.</summary>
        void PaintAmbientGlow(Graphics g)
        {
            if (glowCache == null || glowCacheSize != ClientSize)
            {
                if (glowCache != null) { glowCache.Dispose(); glowCache = null; }
                if (ClientSize.Width > 0 && ClientSize.Height > 0)
                {
                    var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
                    using (var bg = Graphics.FromImage(bmp))
                    {
                        var full = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                        AmbientGlow(bg, full, Width / 2f + 40f, 150f, 430f, Ui.Accent, 18);
                        AmbientGlow(bg, full, (float)Width - 60f, Height - 210f, 400f, Ui.Accent2, 12);
                    }
                    glowCache = bmp;
                    glowCacheSize = ClientSize;
                }
            }
            if (glowCache != null) g.DrawImageUnscaled(glowCache, 0, 0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true;
                StopAutoTimer();
                StopExitTimer();
                CancelSelectionWork();
                if (glowCache != null) { glowCache.Dispose(); glowCache = null; }
                if (notePulse != null) { notePulse.Dispose(); notePulse = null; }
                if (zoneTip != null) { zoneTip.Dispose(); zoneTip = null; }
                if (cts != null) { try { cts.Dispose(); } catch { } cts = null; }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);

            PaintAmbientGlow(g);

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
