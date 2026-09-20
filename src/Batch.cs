using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
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
        int detectionGeneration;
        readonly Button removeButton;

        string detectedId = "";
        RowState state = RowState.Queued;
        string statusText = "queued";

        public string ExePath { get; private set; }
        bool locked;
        public bool Locked
        {
            get { return locked; }
            set
            {
                locked = value;
                if (value) InvalidateDetection();
                if (removeButton != null) removeButton.Enabled = !value;
            }
        }
        public int BeginDetection() { SetState(RowState.Detecting); return ++detectionGeneration; }
        public void InvalidateDetection() { detectionGeneration++; }
        public bool CanApplyDetection(int generation)
        {
            return !IsDisposed && !Locked && generation == detectionGeneration && state == RowState.Detecting;
        }
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
            idBox.AccessibleName = "Steam AppID for " + Path.GetFileName(ExePath);
            idBox.KeyPress += (s, e) => { if ((e.KeyChar < '0' || e.KeyChar > '9') && !char.IsControl(e.KeyChar)) e.Handled = true; };
            idBox.TextChanged += delegate
            {
                if (updatingText || Locked) return;
                InvalidateDetection();
                bool has = AppIdDetector.IsValid(idBox.Text);
                SetState(has ? RowState.Ready : RowState.NoId,
                    has ? "AppID · entered manually" : "enter a valid Steam AppID");
            };

            removeButton = new Button
            {
                Text = "×", TabStop = true, FlatStyle = FlatStyle.Flat,
                ForeColor = Ui.MutedC, BackColor = Ui.Surface,
                AccessibleName = "Remove " + Path.GetFileName(ExePath),
                AccessibleRole = AccessibleRole.PushButton
            };
            removeButton.FlatAppearance.BorderSize = 0;
            removeButton.Click += delegate { if (!Locked) { var h = Removed; if (h != null) h(this); } };
            Controls.Add(removeButton);
            Height = 64;
        }

        public string AppId { get { return idBox.Text.Trim(); } }

        public void ApplyDetection(int generation, string id, string source)
        {
            if (generation != detectionGeneration || Locked || IsDisposed) return;
            detectedId = AppIdDetector.Normalize(id);
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
            removeRect = new Rectangle(Width - Ui.S(32), Height / 2 - Ui.S(12), Ui.S(24), Ui.S(24));
            if (removeButton != null) removeButton.Bounds = removeRect;
            if (idBox != null) idBox.SetBounds(Width - Ui.S(32) - Ui.S(8) - Ui.S(106), (Height - Ui.S(30)) / 2, Ui.S(106), Ui.S(30));
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
            if (e.Button == MouseButtons.Left && !Locked && removeRect.Contains(e.Location))
            {
                var h = Removed; if (h != null) h(this);
            }
            base.OnMouseUp(e);
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

            int textMaxW = Math.Max(0, idBox.Left - Ui.S(12) - Ui.S(32));
            if (textMaxW > 0)
            {
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(g, Path.GetFileName(ExePath), Ui.F(9.5f, true),
                    new Rectangle(Ui.S(32), Ui.S(7), textMaxW, Ui.S(18)), Ui.TextC, flags);
                TextRenderer.DrawText(g, statusText, Ui.F(8f, false), new Rectangle(Ui.S(32), Ui.S(30), textMaxW, Ui.S(16)), col, flags);
            }

            // appid box chrome (the TextBox itself paints on top)
            var br = idBox.Bounds;
            Ui.FillRound(g, Rectangle.Inflate(br, -Ui.S(2), -Ui.S(2)), Ui.S(8), Ui.Surface2);
            Ui.StrokeRound(g, Rectangle.Inflate(br, -Ui.S(2), -Ui.S(2)), Ui.S(8), Ui.BorderC, 1f);

            // remove button
            using (var b = new SolidBrush(hoverRemove && !Locked ? Ui.Accent : Ui.MutedC))
                g.DrawString("\u00D7", Ui.F(11.5f, true), b, removeRect.X + 6, Height / 2 - 13);

            if (Locked)
                using (var b = new SolidBrush(Color.FromArgb(140, Ui.Bg.R, Ui.Bg.G, Ui.Bg.B))) g.FillRectangle(b, ClientRectangle);
        }
    }

    // ─────────────────────────────────────────────── batch dialog

    internal sealed class BufferedRunLog : IDisposable
    {
        readonly LogView view;
        readonly string prefix;
        readonly ConcurrentQueue<PatchLogEntry> pending = new ConcurrentQueue<PatchLogEntry>();
        readonly BlockingCollection<string> disk = new BlockingCollection<string>(2048);
        readonly System.Windows.Forms.Timer timer;
        readonly Task writer;
        static readonly object fileLock = new object();
        static string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldbergPatcher");
        int pendingCount, dropped;
        bool completed;
        string writeError;

        public BufferedRunLog(LogView view, string prefix)
        {
            this.view = view;
            this.prefix = prefix;
            writer = Task.Run((Action)WriteLoop);
            timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += delegate { Drain(); };
            timer.Start();
        }

        public static IDisposable UseLogDirectory(string dir)
        {
            var prev = Interlocked.Exchange(ref logDirectory, dir);
            return new LogDirScope(prev);
        }

        sealed class LogDirScope : IDisposable
        {
            readonly string restore;
            public LogDirScope(string restoreTo) { restore = restoreTo; }
            public void Dispose() { Interlocked.Exchange(ref logDirectory, restore); }
        }

        public void Append(string message, LogLevel level = LogLevel.Info)
        {
            if (completed) return;
            using (var reader = new StringReader(message ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) Enqueue("", level);
                    for (int start = 0; start < line.Length; start += 2048)
                        Enqueue(line.Substring(start, Math.Min(2048, line.Length - start)), level);
                }
            }
        }

        void Enqueue(string line, LogLevel level)
        {
            if (Interlocked.Increment(ref pendingCount) <= 2048)
                pending.Enqueue(new PatchLogEntry { Message = line, Level = level });
            else { Interlocked.Decrement(ref pendingCount); Interlocked.Increment(ref dropped); }
            try
            {
                if (!disk.TryAdd(DateTime.Now.ToString("HH:mm:ss") + "  " + prefix + line))
                    Interlocked.Increment(ref dropped);
            }
            catch (InvalidOperationException) { }
        }

        void DiscardPending()
        {
            PatchLogEntry entry;
            while (pending.TryDequeue(out entry)) Interlocked.Decrement(ref pendingCount);
            Interlocked.Exchange(ref dropped, 0);
            Interlocked.Exchange(ref writeError, null);
        }

        public void Drain()
        {
            if (view.IsDisposed || view.Disposing) { DiscardPending(); return; }
            try
            {
                PatchLogEntry entry;
                int count = 0;
                while (count++ < 256 && pending.TryDequeue(out entry))
                {
                    Interlocked.Decrement(ref pendingCount);
                    if (view.IsDisposed || view.Disposing) { DiscardPending(); return; }
                    view.AppendLine(entry.Message, entry.Level);
                }
                if (view.IsDisposed || view.Disposing) { DiscardPending(); return; }
                int lost = Interlocked.Exchange(ref dropped, 0);
                if (lost > 0) view.AppendLine("Log queue limit reached: " + lost + " UI/disk entries omitted.", LogLevel.Warn);
                string error = Interlocked.Exchange(ref writeError, null);
                if (error != null && !view.IsDisposed && !view.Disposing) view.AppendLine("Persistent log unavailable: " + error, LogLevel.Error);
            }
            catch (Exception) when (view.IsDisposed || view.Disposing) { DiscardPending(); }
        }

        void WriteLoop()
        {
            foreach (string first in disk.GetConsumingEnumerable())
            {
                var text = new StringBuilder().AppendLine(first);
                string next;
                for (int i = 0; i < 127 && disk.TryTake(out next); i++) text.AppendLine(next);
                try
                {
                    lock (fileLock)
                    {
                        var dir = logDirectory;
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, "last_run.log");
                        if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(text.ToString()) > 2 * 1024 * 1024)
                        {
                            var previous = path + ".1";
                            if (File.Exists(previous)) File.Delete(previous);
                            File.Move(path, previous);
                        }
                        using (var output = new StreamWriter(path, true, new UTF8Encoding(false))) output.Write(text.ToString());
                    }
                }
                catch (Exception ex) { Interlocked.Exchange(ref writeError, ex.Message); }
            }
        }

        public async Task CompleteAsync()
        {
            if (!completed)
            {
                completed = true;
                timer.Stop();
                disk.CompleteAdding();
            }
            await writer;
            while (!pending.IsEmpty) Drain();
            Drain();
        }

        public void Dispose()
        {
            timer.Dispose();
            if (!completed) { completed = true; disk.CompleteAdding(); }
            if (writer.IsCompleted) disk.Dispose();
            else writer.ContinueWith(t => disk.Dispose(), TaskScheduler.Default);
        }
    }

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
        bool closing, allowClose;
        Task runTask = Task.FromResult(0);
        readonly SemaphoreSlim detectionSlots = new SemaphoreSlim(3);
        readonly ConcurrentDictionary<BatchRow, CancellationTokenSource> detectionSources = new ConcurrentDictionary<BatchRow, CancellationTokenSource>();
        readonly List<Task> detectionTasks = new List<Task>();
        readonly BufferedRunLog runLog;
        Task shutdownTask;

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
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
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
            runLog = new BufferedRunLog(log, "[batch] ");

            rowTip = new ToolTip();
            rowTip.AutoPopDelay = 8000;

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { if (running) CancelBatch(); else Close(); } };
            FormClosing += OnClosing;

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
            var subFont = Ui.F(8.5f, false);
            // Ui.TruncMiddle binary-searches; the loop this replaced did a MeasureString plus a string
            // allocation per character removed, on every repaint including every resize tick.
            sub = Ui.TruncMiddle(g, sub, subFont, subMaxW);
            TextRenderer.DrawText(g, sub, subFont, new Point(Pad, 74), prefs.OnlineFix ? Ui.WarnC : Ui.MutedC, TextFormatFlags.NoPadding);
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
            if (running || closing || paths == null) return;
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
            row.InvalidateDetection();
            CancellationTokenSource source;
            if (detectionSources.TryGetValue(row, out source)) source.Cancel();
            rows.Remove(row);
            rowsPanel.Controls.Remove(row);
            row.Dispose();
            LayoutRows();
            RefreshRunButton();
        }

        void LayoutRows()
        {
            // Rows are created at runtime, long after WinForms' one-off auto-scale pass, so every constant
            // here has to be scaled by hand or the list collapses to design-size rows in a scaled panel.
            int rowH = Ui.S(RowH);
            int w = Math.Max(Ui.S(200), rowsPanel.ClientSize.Width);
            for (int i = 0; i < rows.Count; i++)
                rows[i].SetBounds(0, i * rowH, w, rowH);
            emptyHint.Bounds = new Rectangle(Ui.S(8), Ui.S(96), w - Ui.S(16), Ui.S(40));
            emptyHint.Visible = rows.Count == 0;
        }

        // ---------------------------------------------------------- appid detection

        void Detect(BatchRow r)
        {
            if (prefs.OnlineFix || running || closing || detectionSources.ContainsKey(r)) return;
            if (r.GetState() != BatchRow.RowState.Queued) return;
            int generation = r.BeginDetection();
            string cached;
            settings.AppIdsByFolder.TryGetValue(Path.GetDirectoryName(r.ExePath) ?? "", out cached);
            var source = new CancellationTokenSource();
            detectionSources[r] = source;
            detectionTasks.RemoveAll(t => t.IsCompleted);
            detectionTasks.Add(DetectAsync(r, generation, cached, chkOnline.Checked, source));
        }

        async Task DetectAsync(BatchRow row, int generation, string cached, bool online, CancellationTokenSource source)
        {
            bool entered = false;
            try
            {
                await detectionSlots.WaitAsync(source.Token);
                entered = true;
                var result = await Task.Run(() => AppIdDetector.Detect(row.ExePath, cached, online, source.Token), source.Token);
                await RunOnUi(delegate
                {
                    if (closing || running || source.IsCancellationRequested || !rows.Contains(row) || !row.CanApplyDetection(generation)) return;
                    row.ApplyDetection(generation, result.AppId, result.Source);
                    if (result.Found)
                    {
                        settings.AppIdsByFolder[Path.GetDirectoryName(row.ExePath) ?? ""] = result.AppId;
                        string error;
                        if (!settings.Save(out error)) runLog.Append(error, LogLevel.Error);
                        runLog.Append(Path.GetFileName(row.ExePath) + ": AppID " + result.AppId + " (" + result.Source + ")");
                    }
                    else runLog.Append(Path.GetFileName(row.ExePath) + ": no AppID found" + (online ? " locally or online." : " locally."), LogLevel.Warn);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                bool report = !closing && !running && rows.Contains(row) && row.CanApplyDetection(generation);
                if (report) await RunOnUi(delegate
                {
                    row.ApplyDetection(generation, "", "");
                    runLog.Append(Path.GetFileName(row.ExePath) + ": AppID detection failed – " + ex.Message, LogLevel.Warn);
                });
            }
            finally
            {
                if (entered) detectionSlots.Release();
                CancellationTokenSource removed;
                detectionSources.TryRemove(row, out removed);
                source.Dispose();
                await RunOnUi(delegate { if (!closing) RefreshRunButton(); });
            }
        }

        Task RunOnUi(Action action)
        {
            var completion = new TaskCompletionSource<bool>();
            UiInvoke(delegate
            {
                try { action(); }
                catch (Exception ex) { completion.TrySetException(ex); return; }
                completion.TrySetResult(true);
            });
            return completion.Task;
        }

        void CancelDetection()
        {
            foreach (var row in rows) row.InvalidateDetection();
            foreach (var source in detectionSources.Values.ToArray()) source.Cancel();
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
            CancelDetection();
            OkCount = FailCount = SkipCount = 0;
            appliedExes.Clear();
            outcomes.Clear();
            TotalGames = total;
            settings.LookupAppId = chkOnline.Checked;
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
            patcher.LogLine += e => runLog.Append(e.Message, e.Level);
            patcher.GameStarted += (i, n) => UiInvoke(delegate
            {
                if (IsDisposed) return;
                progress.SetValue(n > 0 ? (int)((i - 1) * 100.0 / n) : 0);
                if (i >= 1 && i <= rows.Count) rows[i - 1].SetState(BatchRow.RowState.Patching);
            });
            patcher.GamePercent += (i, pct) => UiInvoke(delegate
            {
                if (!running || total == 0) return;
                progress.SetValue((int)Math.Min(99, ((i - 1 + pct / 100.0) / total * 100.0)));
            });
            patcher.ItemCompleted += o => UiInvoke(delegate { if (!IsDisposed) ApplyOutcome(o); });

            runTask = CompleteRunAsync(patcher.RunAsync(items, prefs, cts.Token));
        }

        async Task CompleteRunAsync(Task<List<BatchItemOutcome>> task)
        {
            List<BatchItemOutcome> results = null;
            Exception error = null;
            bool cancelled = false;
            try
            {
                try { await task; } catch { }
                if (task.Status == TaskStatus.Faulted) error = task.Exception.Flatten();
                else if (task.Status == TaskStatus.Canceled) cancelled = true;
                else if (task.Status == TaskStatus.RanToCompletion) results = task.Result;
                FinishBatch(results, error, cancelled);
            }
            finally
            {
                running = false;
                if (cts != null) { cts.Dispose(); cts = null; }
                runBtn.Kind = GradientButton.BtnKind.Primary;
                addBtn.Enabled = clearBtn.Enabled = !closing;
                chkOnline.Enabled = !closing && !prefs.OnlineFix;
                foreach (var row in rows) { row.Locked = closing; row.SetIdBoxEnabled(!closing && !prefs.OnlineFix); }
                if (!closing) RefreshRunButton();
            }
        }

        void CancelBatch()
        {
            if (!running || cts == null) return;
            try { cts.Cancel(); } catch { }
            runBtn.Text = "Cancelling…";
        }

        // Marshals an action to the UI thread from a worker thread. Swallows ObjectDisposedException when a
        // callback arrives after the form has closed – BeginInvoke itself would otherwise throw on the pool
        // thread (unobserved) because IsDisposed can only be checked inside the delegate.
        void UiInvoke(Action a)
        {
            // Wrap in an anonymous method – Action and MethodInvoker are unrelated delegate types, so a
            // direct cast is not allowed.
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) a(); }); }
            catch (InvalidOperationException) { }
        }

        readonly HashSet<string> appliedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, BatchItemOutcome> outcomes = new Dictionary<string, BatchItemOutcome>(StringComparer.OrdinalIgnoreCase);

        void ApplyOutcome(BatchItemOutcome o)
        {
            if (o == null || !appliedExes.Add(o.Exe)) return;
            outcomes[o.Exe] = o;
            BatchRow row = null;
            foreach (var r in rows) if (string.Equals(r.ExePath, o.Exe, StringComparison.OrdinalIgnoreCase)) { row = r; break; }

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

        void FinishBatch(List<BatchItemOutcome> results, Exception ex, bool taskCancelled)
        {
            bool cancelled = taskCancelled || (cts != null && cts.IsCancellationRequested);
            if (results != null) foreach (var outcome in results) ApplyOutcome(outcome);
            foreach (var row in rows)
                if (!appliedExes.Contains(row.ExePath))
                    ApplyOutcome(new BatchItemOutcome
                    {
                        Exe = row.ExePath, Skipped = true, Cancelled = cancelled,
                        Summary = cancelled ? "not started – batch cancelled" : "not reached – no worker result"
                    });
            OkCount = outcomes.Values.Count(o => o.Success);
            SkipCount = outcomes.Values.Count(o => !o.Success && (o.Skipped || o.Cancelled));
            FailCount = outcomes.Count - OkCount - SkipCount;
            HasRun = true;
            sumLbl.Text = (cancelled ? "Cancelled – " : "") + SummaryLine();
            sumLbl.ForeColor = ex != null ? Ui.ErrC : (FailCount > 0 ? Ui.WarnC : (OkCount > 0 ? Ui.OkC : Ui.MutedC));
            if (ex != null) runLog.Append("Batch error: " + ex, LogLevel.Error);
            runLog.Append((cancelled ? "Batch cancelled: " : "Batch complete: ") + SummaryLine());
            string error;
            if (!settings.Save(out error)) runLog.Append(error, LogLevel.Error);
        }

        async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            if (running && MessageBox.Show(this, "Cancel the batch and wait for a safe stopping point?",
                "Goldberg Patcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await ShutdownAsync();
            allowClose = true;
            Close();
        }

        public Task ShutdownAsync()
        {
            if (shutdownTask == null) shutdownTask = StopAsync();
            return shutdownTask;
        }

        async Task StopAsync()
        {
            closing = true;
            addBtn.Enabled = clearBtn.Enabled = runBtn.Enabled = chkOnline.Enabled = false;
            CancelBatch();
            CancelDetection();
            var work = Task.WhenAll(detectionTasks.Concat(new[] { runTask }));
            if (await Task.WhenAny(work, Task.Delay(10000)) != work)
                sumLbl.Text = "Still stopping safely – waiting for outstanding work";
            try { await work; }
            catch (Exception ex) { runLog.Append("Shutdown: " + ex, LogLevel.Error); }
            await runLog.CompleteAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rowTip.Dispose();
                runLog.Dispose();
                detectionSlots.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
