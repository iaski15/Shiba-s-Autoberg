using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Gp
{
    // ─────────────────────────────────────────────── one game row in the batch list

    public class BatchRow : UserControl
    {
        public enum RowState { Queued, Detecting, Ready, NoId, Patching, Ok, Failed, Skipped }

        readonly TextBox idBox;
        Rectangle removeRect = Rectangle.Empty;
        bool hoverRemove = false;
        bool updatingText = false;

        string detectedId = "";
        RowState state = RowState.Queued;
        string statusText = "queued";

        public string ExePath { get; private set; }
        public bool Locked { get; set; }
        public event Action<BatchRow> Removed;

        public BatchRow(string exePath)
        {
            ExePath = exePath ?? "";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Ui.Surface;

            idBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Surface2,
                ForeColor = Ui.TextC,
                Font = Ui.F("Consolas", 10f, false),
                MaxLength = 10,
            };
            Controls.Add(idBox);
            idBox.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            idBox.TextChanged += delegate
            {
                if (updatingText || Locked) return;
                bool has = idBox.Text.Trim().Length > 0;
                if (state == RowState.Ready || state == RowState.NoId || state == RowState.Queued)
                    SetState(has ? RowState.Ready : RowState.NoId,
                        has ? "AppID · entered manually" : "no AppID found – type one in the box");
            };

            Height = 64; // last: triggers OnResize, which needs idBox to exist
        }

        public string AppId { get { return idBox.Text.Trim(); } }

        public void ApplyDetection(string id, string source)
        {
            detectedId = id ?? "";
            updatingText = true;
            idBox.Text = detectedId;
            updatingText = false;
            if (detectedId.Length > 0) SetState(RowState.Ready, "AppID · " + source);
            else SetState(RowState.NoId, "no AppID found – type one in the box");
        }

        public void Note(string text) { statusText = text ?? ""; Invalidate(); }

        public void SetIdBoxEnabled(bool enabled) { idBox.Enabled = enabled; }
        public RowState GetState() { return state; }
        public string StatusHint() { return statusText; }

        public void SetState(RowState st) { SetState(st, DefaultStatus(st)); }

        static string DefaultStatus(RowState st)
        {
            switch (st)
            {
                case RowState.Detecting: return "detecting AppID…";
                case RowState.Patching: return "patching…";
                case RowState.NoId: return "no AppID found – type one in the box";
                case RowState.Queued: return "queued";
                default: return "";
            }
        }

        public void SetState(RowState st, string status)
        {
            state = st;
            if (status != null) statusText = status;
            else switch (st)
            {
                case RowState.Detecting: statusText = "detecting AppID…"; break;
                case RowState.Patching: statusText = "patching…"; break;
                case RowState.Queued: statusText = "queued"; break;
            }
            Invalidate();
        }

        Color StateColor()
        {
            switch (state)
            {
                case RowState.Detecting: return Ui.Accent;
                case RowState.Ready: return Ui.OkC;
                case RowState.NoId: return Ui.WarnC;
                case RowState.Patching: return Ui.Accent2;
                case RowState.Ok: return Ui.OkC;
                case RowState.Failed: return Ui.ErrC;
                default: return Ui.MutedC;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            removeRect = new Rectangle(Width - 32, Height / 2 - 12, 24, 24);
            idBox.SetBounds(Width - 32 - 8 - 106, (Height - 30) / 2, 106, 30);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverRemove = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = !Locked && removeRect.Contains(e.Location);
            if (h != hoverRemove) { hoverRemove = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!Locked && removeRect.Contains(e.Location))
            {
                var h = Removed; if (h != null) h(this);
            }
            base.OnMouseUp(e);
        }

        static string Trunc(Graphics g, string s, Font f, int maxW)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (g.MeasureString(s, f).Width <= maxW) return s;
            while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxW) s = s.Substring(0, s.Length - 1);
            return s + "…";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var b = new SolidBrush(Ui.Surface)) g.FillRectangle(b, ClientRectangle);
            if (!Locked)
                using (var p = new Pen(Color.FromArgb(46, Ui.BorderC.R, Ui.BorderC.G, Ui.BorderC.B), 1f))
                    g.DrawLine(p, 0, Height - 1, Width, Height - 1);

            // status dot
            var col = StateColor();
            using (var b = new SolidBrush(col)) g.FillEllipse(b, 14, Height / 2 - 4, 8, 8);
            if (state == RowState.Patching || state == RowState.Detecting)
                using (var p = new Pen(Color.FromArgb(90, col.R, col.G, col.B), 1.5f)) g.DrawEllipse(p, 11, Height / 2 - 7, 14, 14);

            int textMaxW = Math.Max(60, Width - removeRect.X - 8 - 30);
            TextRenderer.DrawText(g, Path.GetFileName(ExePath), Ui.F(9.5f, true),
                new Rectangle(32, 7, textMaxW, 18), Ui.TextC, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            string shown = Trunc(g, statusText, Ui.F(8f, false), textMaxW);
            TextRenderer.DrawText(g, shown, Ui.F(8f, false), new Rectangle(32, 30, textMaxW, 16), col,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            // appid box chrome (the TextBox itself paints on top)
            var br = idBox.Bounds;
            Ui.FillRound(g, Rectangle.Inflate(br, -2, -2), 8, Ui.Surface2);
            Ui.StrokeRound(g, Rectangle.Inflate(br, -2, -2), 8, Ui.BorderC, 1f);

            // remove button
            using (var b = new SolidBrush(hoverRemove && !Locked ? Ui.Accent : Ui.MutedC))
                g.DrawString("\u00D7", Ui.F(11.5f, true), b, removeRect.X + 6, Height / 2 - 13);

            if (Locked)
                using (var b = new SolidBrush(Color.FromArgb(140, Ui.Bg.R, Ui.Bg.G, Ui.Bg.B))) g.FillRectangle(b, ClientRectangle);
        }
    }

    // ─────────────────────────────────────────────── batch dialog

    public class BatchForm : Form
    {
        const int Pad = 24;
        const int RowH = 64;

        readonly AppSettings settings;
        readonly BatchPrefs prefs;
        readonly TitleBar titleBar;
        readonly AppCard listCard;
        readonly Panel rowsPanel;
        readonly Label emptyHint;
        readonly GradientButton addBtn, clearBtn, runBtn;
        readonly CheckBox chkOnline;
        readonly ProgressBarLite progress;
        readonly Label sumLbl;
        readonly AppCard logCard;
        readonly LogView log;
        readonly ToolTip rowTip;

        readonly List<BatchRow> rows = new List<BatchRow>();
        CancellationTokenSource cts;
        volatile bool running;

        public bool HasRun { get; private set; }
        public int TotalGames, OkCount, FailCount, SkipCount;

        public string SummaryLine() { return OkCount + " patched · " + FailCount + " failed · " + SkipCount + " skipped"; }

        string Subtitle()
        {
            if (prefs.OnlineFix)
                return "ONLINE-FIX mode is on in the main window – every game will be patched as Spacewar (AppID 480). No AppIDs needed.";
            return "Add several game .exe files – each Steam AppID is detected automatically and can be edited before patching.";
        }

        public BatchForm(AppSettings settings, BatchPrefs prefs)
        {
            this.settings = settings ?? new AppSettings();
            this.prefs = prefs ?? new BatchPrefs();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(800, 672);
            BackColor = Ui.Bg;
            Text = "Goldberg Patcher – batch";
            KeyPreview = true;
            DoubleBuffered = true;
            MinimumSize = Size;

            titleBar = new TitleBar();
            Controls.Add(titleBar);
            titleBar.CloseClicked += delegate { Close(); };
            titleBar.MinimizeClicked += delegate { WindowState = FormWindowState.Minimized; };

            listCard = new AppCard();
            listCard.Bounds = new Rectangle(Pad, 116, 800 - Pad * 2, 318);
            listCard.AllowDrop = true;
            listCard.DragEnter += (s, e) => { e.Effect = DropHasExe(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; };
            listCard.DragOver += (s, e) => { if (DropHasExe(e.Data)) e.Effect = DragDropEffects.Copy; else if (e.Effect != DragDropEffects.None) e.Effect = DragDropEffects.None; };
            listCard.DragDrop += (s, e) => AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            Controls.Add(listCard);

            rowsPanel = new Panel();
            rowsPanel.AutoScroll = true;
            rowsPanel.BackColor = Ui.Surface;
            rowsPanel.Bounds = new Rectangle(10, 8, listCard.Width - 20, listCard.Height - 16);
            rowsPanel.SizeChanged += delegate { LayoutRows(); };
            listCard.Controls.Add(rowsPanel);

            emptyHint = new Label();
            emptyHint.AutoSize = false;
            emptyHint.TextAlign = ContentAlignment.MiddleCenter;
            emptyHint.BackColor = Ui.Surface;
            emptyHint.ForeColor = Ui.MutedC;
            emptyHint.Font = Ui.F(9f, false);
            emptyHint.Text = "Drop game .exe files here, or click “Add games…”";
            listCard.Controls.Add(emptyHint);

            progress = new ProgressBarLite();
            progress.Bounds = new Rectangle(Pad, 448, 560, 6);
            Controls.Add(progress);

            sumLbl = new Label();
            sumLbl.AutoSize = false;
            sumLbl.TextAlign = ContentAlignment.MiddleRight;
            sumLbl.BackColor = Ui.Bg;
            sumLbl.ForeColor = Ui.MutedC;
            sumLbl.Font = Ui.F(8.25f, false);
            sumLbl.Bounds = new Rectangle(592, 437, 184, 20);
            Controls.Add(sumLbl);

            addBtn = new GradientButton("Add games…");
            addBtn.Kind = GradientButton.BtnKind.Secondary;
            addBtn.Bounds = new Rectangle(Pad, 474, 150, 40);
            addBtn.Click += delegate { BrowseAdd(); };
            Controls.Add(addBtn);

            clearBtn = new GradientButton("Clear");
            clearBtn.Kind = GradientButton.BtnKind.Secondary;
            clearBtn.Bounds = new Rectangle(Pad + 158, 474, 90, 40);
            clearBtn.Click += delegate { if (!running) ClearAll(); };
            Controls.Add(clearBtn);

            chkOnline = new CheckBox();
            chkOnline.AutoSize = true;
            chkOnline.Text = "Auto-detect missing AppIDs online";
            chkOnline.ForeColor = Ui.TextC;
            chkOnline.BackColor = Ui.Bg;
            chkOnline.Font = Ui.F(8.75f, false);
            chkOnline.Checked = settings.LookupAppId; // same global "auto-detect online" option as the main window
            chkOnline.Location = new Point(Pad + 260, 483);
            if (prefs.OnlineFix) chkOnline.Visible = false;
            Controls.Add(chkOnline);

            runBtn = new GradientButton("Patch games");
            runBtn.Bounds = new Rectangle(776 - 210, 474, 210, 40);
            runBtn.Click += delegate { if (running) CancelBatch(); else StartBatch(); };
            Controls.Add(runBtn);

            logCard = new AppCard();
            int logTop = 528;
            logCard.Bounds = new Rectangle(Pad, logTop, 800 - Pad * 2, ClientSize.Height - logTop - 14);
            Controls.Add(logCard);

            log = new LogView();
            log.SetBounds(10, 10, logCard.Width - 20, logCard.Height - 20);
            logCard.Controls.Add(log);

            rowTip = new ToolTip();
            rowTip.AutoPopDelay = 8000;

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { if (running) CancelBatch(); else Close(); } };
            FormClosing += (s, e) =>
            {
                if (!running) return;
                var r = MessageBox.Show(this, "The batch is still running – closing will cancel it.\nClose anyway?",
                    "Goldberg Patcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) e.Cancel = true;
            };

            LayoutRows();
            RefreshRunButton();
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
                NativeMethods.DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int dark = 1;
                NativeMethods.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                NativeMethods.DwmSetWindowAttribute(Handle, 19, ref dark, 4);
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);

            string title = "Patch several games";
            var tf = Ui.F(12.75f, true);
            try
            {
                float tw = (float)g.MeasureString(title, tf).Width;
                using (var lg = new LinearGradientBrush(new PointF(Pad, 0), new PointF(Pad + Math.Max(tw, 1f), 0), Ui.Accent, Ui.Accent2))
                    g.DrawString(title, tf, lg, new PointF(Pad, 46f), StringFormat.GenericTypographic);
            }
            catch { TextRenderer.DrawText(g, title, tf, new Point(Pad, 50), Ui.TextC, TextFormatFlags.NoPadding); }

            var sub = Subtitle();
            int subMaxW = Width - Pad * 2;
            if (g.MeasureString(sub, Ui.F(8.5f, false)).Width > subMaxW)
                while (sub.Length > 1 && g.MeasureString(sub + "…", Ui.F(8.5f, false)).Width > subMaxW) sub = sub.Substring(0, sub.Length - 1);
            TextRenderer.DrawText(g, sub, Ui.F(8.5f, false), new Point(Pad, 74), prefs.OnlineFix ? Ui.WarnC : Ui.MutedC, TextFormatFlags.NoPadding);
        }

        // ---------------------------------------------------------- list management

        static bool DropHasExe(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = (string[])data.GetData(DataFormats.FileDrop);
            return files != null && files.Any(f => (f ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        void BrowseAdd()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select game executables";
                dlg.Filter = "Program (*.exe)|*.exe";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
            }
        }

        void ClearAll()
        {
            foreach (var r in rows.ToArray()) RemoveRow(r, false);
        }

        void AddPaths(string[] paths)
        {
            if (running || paths == null) return;
            int added = 0, bad = 0;
            foreach (var p in paths)
            {
                string full;
                try { full = Path.GetFullPath(p ?? ""); } catch { continue; }
                if (!File.Exists(full)) { bad++; continue; }
                if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { bad++; continue; }
                bool dup = false;
                foreach (var r in rows) if (string.Equals(r.ExePath, full, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (dup) continue;

                var row = new BatchRow(full);
                if (prefs.OnlineFix)
                {
                    row.SetIdBoxEnabled(false);
                    row.Note("online-fix mode – AppID not needed");
                }
                row.Removed += OnRowRemoved;
                row.MouseEnter += (s, e) => rowTip.SetToolTip(row, full + "\n" + row.StatusHint());
                rowsPanel.Controls.Add(row);
                rows.Add(row);
                added++;
            }
            if (bad > 0) log.AppendLine(bad + " file(s) skipped – only existing .exe files can be patched.", LogLevel.Warn);
            LayoutRows();
            RefreshRunButton();
            foreach (var r in rows) Detect(r); // no-op for rows that already resolved / were edited manually
        }

        void OnRowRemoved(BatchRow row) { RemoveRow(row, true); }

        void RemoveRow(BatchRow row, bool logIt)
        {
            if (running || rows.IndexOf(row) < 0) return;
            rows.Remove(row);
            rowsPanel.Controls.Remove(row);
            row.Dispose();
            LayoutRows();
            RefreshRunButton();
        }

        void LayoutRows()
        {
            int w = Math.Max(200, rowsPanel.ClientSize.Width);
            for (int i = 0; i < rows.Count; i++)
                rows[i].SetBounds(0, i * RowH, w, RowH);
            emptyHint.Bounds = new Rectangle(8, 96, w - 16, 40);
            emptyHint.Visible = rows.Count == 0;
        }

        // ---------------------------------------------------------- appid detection

        void Detect(BatchRow r)
        {
            if (prefs.OnlineFix) return;
            var st = r.GetState();
            if (st != BatchRow.RowState.Queued && st != BatchRow.RowState.NoId) return;
            r.SetState(BatchRow.RowState.Detecting);

            string exe = r.ExePath;
            bool online = chkOnline.Checked; // read on the UI thread only

            Func<AppIdDetection> job = delegate
            {
                string cached = "";
                try
                {
                    var dir = Path.GetDirectoryName(exe);
                    settings.AppIdsByFolder.TryGetValue(dir ?? "", out cached);
                }
                catch { }
                return AppIdDetector.Detect(exe, cached, online);
            };

            Task.Run(job).ContinueWith(delegate(Task<AppIdDetection> t)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || rows.IndexOf(r) < 0) return;
                    var d = t.IsCompleted ? t.Result : new AppIdDetection();
                    if (d.Found)
                    {
                        r.ApplyDetection(d.AppId, d.Source);
                        log.AppendLine(Path.GetFileName(exe) + " → AppID " + d.AppId + " (" + d.Source + ")");
                        try
                        {
                            var dir = Path.GetDirectoryName(exe);
                            settings.AppIdsByFolder[dir ?? ""] = d.AppId;
                            settings.Save();
                        }
                        catch { }
                    }
                    else
                    {
                        r.ApplyDetection("", "");
                        log.AppendLine(Path.GetFileName(exe) + ": no AppID found locally" + (chkOnline.Checked ? " or in the Steam Store." : "."), LogLevel.Warn);
                    }
                    RefreshRunButton();
                });
            });
        }

        // ---------------------------------------------------------- running the batch

        void RefreshRunButton()
        {
            if (running) return;
            runBtn.Enabled = rows.Count > 0;
            runBtn.Text = rows.Count == 1 ? "Patch this game" : "Patch " + rows.Count + " games";
        }

        void StartBatch()
        {
            int total = rows.Count;
            if (running || total == 0) return;

            int missing = 0;
            foreach (var r in rows) if (!prefs.OnlineFix && r.AppId.Length == 0) missing++;

            string q = "Patch " + total + (total == 1 ? " game" : " games") + "?\n\nThe main window's OPTIONS apply to every game."
                + (missing > 0 ? "\n\n" + missing + " have no AppID yet and will be SKIPPED." : "")
                + (prefs.OnlineFix ? "\n\nONLINE-FIX is on – every game gets Spacewar (AppID 480)." : "");
            if (MessageBox.Show(this, q, "Goldberg Patcher – batch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            running = true;
            settings.LookupAppId = chkOnline.Checked; // keep the global option in sync with the main window toggle
            cts = new CancellationTokenSource();
            runBtn.Kind = GradientButton.BtnKind.Cancel;
            runBtn.Text = "Cancel";
            addBtn.Enabled = false;
            clearBtn.Enabled = false;
            chkOnline.Enabled = false;
            foreach (var r in rows) { r.Locked = true; if (!prefs.OnlineFix) r.SetIdBoxEnabled(false); }

            log.AppendLine("── Batch start: " + total + (total == 1 ? " game" : " games") + " ──────────────────────────────");

            var items = new List<BatchInput>();
            foreach (var r in rows)
                items.Add(new BatchInput { Exe = r.ExePath, AppId = prefs.OnlineFix ? "" : r.AppId });

            var patcher = new BatchPatcher();
            patcher.LogLine += e => BeginInvoke((MethodInvoker)delegate
            {
                log.AppendLine(e.Message, e.Level);
                AppendRunLog(e.Message);
            });
            patcher.GameStarted += (i, n) => BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed) return;
                progress.SetValue(n > 0 ? (int)((i - 1) * 100.0 / n) : 0);
                if (i >= 1 && i <= rows.Count) rows[i - 1].SetState(BatchRow.RowState.Patching);
            });
            patcher.GamePercent += (i, pct) => BeginInvoke((MethodInvoker)delegate
            {
                if (!running || total == 0) return;
                progress.SetValue((int)Math.Min(99, ((i - 1 + pct / 100.0) / total * 100.0)));
            });
            patcher.ItemCompleted += o => BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) ApplyOutcome(o); });

            patcher.RunAsync(items, prefs, cts.Token).ContinueWith(t => BeginInvoke((MethodInvoker)delegate
            {
                List<BatchItemOutcome> res = null; Exception ex = null;
                try { if (t.IsCompleted) { res = t.Result; ex = t.Exception; } } catch { }
                FinishBatch(res, ex);
            }));
        }

        void CancelBatch()
        {
            if (!running || cts == null) return;
            try { cts.Cancel(); } catch { }
            runBtn.Text = "Cancelling…";
        }

        readonly HashSet<string> appliedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ApplyOutcome(BatchItemOutcome o)
        {
            BatchRow row = null;
            foreach (var r in rows) if (string.Equals(r.ExePath, o.Exe, StringComparison.OrdinalIgnoreCase)) { row = r; break; }
            appliedExes.Add(o.Exe);

            if (o.Success)
            {
                OkCount++;
                if (row != null) row.SetState(BatchRow.RowState.Ok, "patched · AppID " + o.AppIdUsed);
                log.AppendLine(Path.GetFileName(o.Exe) + ": done ✔  (AppID " + o.AppIdUsed + ")");
                try
                {
                    if (!prefs.OnlineFix && o.AppIdUsed.Length > 0)
                        settings.AppIdsByFolder[Path.GetDirectoryName(o.Exe)] = o.AppIdUsed;
                }
                catch { }
            }
            else if (o.Skipped || o.Cancelled)
            {
                SkipCount++;
                if (row != null) row.SetState(BatchRow.RowState.Skipped, o.Summary.Length > 0 ? o.Summary : "skipped");
            }
            else
            {
                FailCount++;
                string why = o.Summary ?? "";
                if (why.Length > 120) why = why.Substring(0, 117) + "…";
                if (row != null) row.SetState(BatchRow.RowState.Failed, why);
            }
        }

        void FinishBatch(List<BatchItemOutcome> results, Exception ex)
        {
            bool cancelled = cts != null && cts.IsCancellationRequested;

            foreach (var r in rows)
                if (!appliedExes.Contains(r.ExePath))
                    r.SetState(BatchRow.RowState.Skipped, cancelled ? "not started – batch cancelled" : "no result");

            running = false;
            cts = null;
            runBtn.Kind = GradientButton.BtnKind.Primary;
            addBtn.Enabled = true;
            clearBtn.Enabled = true;
            chkOnline.Enabled = !prefs.OnlineFix;
            foreach (var r in rows) { r.Locked = false; if (!prefs.OnlineFix) r.SetIdBoxEnabled(true); }

            TotalGames = rows.Count;
            HasRun = true;
            string line = OkCount + " patched · " + FailCount + " failed · " + SkipCount + " skipped";
            sumLbl.Text = (cancelled ? "Cancelled – " : "") + line;
            sumLbl.ForeColor = ex != null ? Ui.ErrC : (FailCount > 0 ? Ui.WarnC : (OkCount > 0 ? Ui.OkC : Ui.MutedC));
            if (ex != null) log.AppendLine("Batch error: " + ex.Message, LogLevel.Error);
            if (cancelled && results != null)
                log.AppendLine("Batch cancelled – " + (results.Count) + "/" + TotalGames + " games reached.", LogLevel.Warn);

            try { settings.Save(); } catch { }
            RefreshRunButton();
        }

        static void AppendRunLog(string msg)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldbergPatcher");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "last_run.log"), DateTime.Now.ToString("HH:mm:ss") + "  [batch] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

}
