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
    /// <summary>One patched game in the cloud-saves list: name, its save folders, and the actions.</summary>
    public class SaveRow : UserControl
    {
        public readonly string GameDir, AppId, GameName;
        public List<string> Folders = new List<string>();
        public readonly FlatButton BackupBtn, RestoreBtn, FolderBtn;

        public SaveRow(string gameDir, string appId)
        {
            GameDir = gameDir;
            AppId = appId;
            GameName = Path.GetFileName(gameDir.TrimEnd('\\'));
            BackColor = Ui.Surface;
            DoubleBuffered = true;
            BackupBtn = new FlatButton("Back up") { TextSize = 9f };
            RestoreBtn = new FlatButton("Restore…") { TextSize = 9f, Kind = FlatButton.BtnKind.Secondary };
            FolderBtn = new FlatButton("+ Folder") { TextSize = 9f, Kind = FlatButton.BtnKind.Secondary };
            Controls.AddRange(new Control[] { BackupBtn, RestoreBtn, FolderBtn });
            Height = 70;   // after the buttons exist: resizing runs OnLayout
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int y = (Height - Ui.S(34)) / 2, x = Width - Ui.S(12);
            foreach (var b in new[] { BackupBtn, RestoreBtn, FolderBtn })
            {
                int w = Ui.S(b == BackupBtn ? 92 : 96);
                x -= w;
                b.SetBounds(x, y, w, Ui.S(34));
                x -= Ui.S(8);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            int textW = Math.Max(0, FolderBtn.Left - Ui.S(28));
            TextRenderer.DrawText(g, Ui.TruncMiddle(g, GameName, Ui.F(10f, true), textW), Ui.F(10f, true), new Point(Ui.S(14), Ui.S(12)), Ui.TextC, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            string sub = Folders.Count == 0
                ? "AppID " + AppId + " · no save folders found – add one with “+ Folder”"
                : "AppID " + AppId + " · " + string.Join("  ·  ", Folders.Select(SaveLocator.Tokenize));
            TextRenderer.DrawText(g, Ui.TruncMiddle(g, sub, Ui.F(8.25f, false), textW), Ui.F(8.25f, false), new Point(Ui.S(14), Ui.S(38)),
                Folders.Count == 0 ? Ui.WarnC : Ui.MutedC, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            using (var p = new Pen(Ui.BorderC)) g.DrawLine(p, Ui.S(12), Height - 1, Width - Ui.S(12), Height - 1);
        }
    }

    /// <summary>Cloud saves: sign in to Google, then back up / restore each patched game's saves.</summary>
    public class CloudForm : Form
    {
        const int Pad = 24;

        readonly AppSettings settings;
        readonly TitleBar titleBar;
        readonly AppCard accountCard, listCard, logCard;
        readonly FlatButton signBtn;
        readonly Panel rowsPanel;
        readonly Label emptyHint;
        readonly LogView log;
        readonly List<SaveRow> rows = new List<SaveRow>();

        GoogleDrive drive;
        CancellationTokenSource signInCts;
        bool busy;

        public CloudForm(AppSettings settings)
        {
            this.settings = settings ?? new AppSettings();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(800, 660);
            BackColor = Ui.Bg;
            Text = BuildInfo.AppName + " – cloud saves";
            KeyPreview = true;
            DoubleBuffered = true;
            MinimumSize = Size;

            titleBar = new TitleBar();
            Controls.Add(titleBar);
            titleBar.CloseClicked += delegate { Close(); };
            titleBar.MinimizeClicked += delegate { WindowState = FormWindowState.Minimized; };

            accountCard = new AppCard();
            accountCard.Bounds = new Rectangle(Pad, 108, 800 - Pad * 2, 72);
            accountCard.Paint += PaintAccount;
            Controls.Add(accountCard);

            signBtn = new FlatButton("Sign in with Google") { TextSize = 9.5f };
            signBtn.Bounds = new Rectangle(accountCard.Width - 22 - 200, 16, 200, 40);
            signBtn.Click += delegate { SignInOrOut(); };
            accountCard.Controls.Add(signBtn);

            listCard = new AppCard();
            listCard.Bounds = new Rectangle(Pad, 194, 800 - Pad * 2, 290);
            Controls.Add(listCard);

            rowsPanel = new Panel();
            rowsPanel.AutoScroll = true;
            rowsPanel.BackColor = Ui.Surface;
            NativeMethods.UseDarkScrollbars(rowsPanel);
            rowsPanel.Bounds = new Rectangle(10, 8, listCard.Width - 20, listCard.Height - 16);
            rowsPanel.SizeChanged += delegate { LayoutRows(); };
            listCard.Controls.Add(rowsPanel);

            emptyHint = new Label();
            emptyHint.AutoSize = false;
            emptyHint.TextAlign = ContentAlignment.MiddleCenter;
            emptyHint.BackColor = Ui.Surface;
            emptyHint.ForeColor = Ui.MutedC;
            emptyHint.Font = Ui.F(9f, false);
            emptyHint.Text = "Games you patch with Shibaberg show up here.";
            rowsPanel.Controls.Add(emptyHint);

            logCard = new AppCard();
            logCard.Bounds = new Rectangle(Pad, 498, 800 - Pad * 2, ClientSize.Height - 498 - 14);
            Controls.Add(logCard);
            log = new LogView();
            log.SetBounds(10, 10, logCard.Width - 20, logCard.Height - 20);
            logCard.Controls.Add(log);

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            FormClosing += delegate { if (signInCts != null) signInCts.Cancel(); };

            drive = GoogleDrive.Load();
            LoadGames();
            RefreshAccount();
            if (!GoogleDrive.Configured)
                Log(LogLevel.Warn, "Google sign-in isn't set up in this build: put google_client.json in the repo and rebuild.");
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
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            TextRenderer.DrawText(g, "Cloud saves", Ui.F(12.75f, true), new Point(Pad, 48), Ui.TextC, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Your saves, zipped into your own Google Drive (folder “" + CloudSaves.RootFolder + "”). The last "
                + CloudSaves.Keep + " backups per game are kept.", Ui.F(8.5f, false), new Point(Pad, 74), Ui.MutedC, TextFormatFlags.NoPadding);
        }

        void PaintAccount(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Shiba.Draw(g, new RectangleF(Ui.S(18), Ui.S(14), Ui.S(44), Ui.S(44)), drive != null ? ShibaMood.Happy : ShibaMood.Sleepy);
            string head, sub;
            if (!GoogleDrive.Configured) { head = "Google sign-in isn't set up"; sub = "This build has no Google client (google_client.json)."; }
            else if (signInCts != null) { head = "Waiting for Google…"; sub = "Finish signing in in your browser."; }
            else if (drive == null) { head = "Not signed in"; sub = "Sign in to back your saves up to your Google Drive."; }
            else { head = "Signed in" + (drive.Email.Length > 0 ? " as " + drive.Email : ""); sub = "Shibaberg can only see the files it creates in your Drive."; }
            int textW = Math.Max(0, signBtn.Left - Ui.S(90));
            TextRenderer.DrawText(g, Ui.TruncMiddle(g, head, Ui.F(10f, true), textW), Ui.F(10f, true), new Point(Ui.S(74), Ui.S(16)), Ui.TextC, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Ui.TruncMiddle(g, sub, Ui.F(8.25f, false), textW), Ui.F(8.25f, false), new Point(Ui.S(74), Ui.S(40)), Ui.MutedC, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        // ---------------------------------------------------------- games

        void LoadGames()
        {
            // Every folder Shibaberg patched with a known AppID; Unreal exe folders resolve to the game root.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in settings.AppIdsByFolder.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (kv.Value.Length == 0 || !Directory.Exists(kv.Key)) continue;
                string gameDir = SaveLocator.GameDir(kv.Key);
                if (!seen.Add(gameDir)) continue;
                var row = new SaveRow(gameDir, kv.Value);
                row.BackupBtn.Click += delegate { Backup(row); };
                row.RestoreBtn.Click += delegate { ChooseRestore(row); };
                row.FolderBtn.Click += delegate { AddFolder(row); };
                DetectFolders(row);
                rows.Add(row);
                rowsPanel.Controls.Add(row);
            }
            LayoutRows();
            RefreshButtons();
        }

        void DetectFolders(SaveRow row)
        {
            List<string> extra;
            settings.SaveDirs.TryGetValue(row.AppId, out extra);
            row.Folders = SaveLocator.Detect(row.GameDir, row.AppId, extra);
            row.Invalidate();
        }

        void LayoutRows()
        {
            emptyHint.Visible = rows.Count == 0;
            emptyHint.Bounds = new Rectangle(0, 0, rowsPanel.ClientSize.Width, rowsPanel.ClientSize.Height);
            int y = rowsPanel.AutoScrollPosition.Y;
            foreach (var r in rows)
            {
                r.SetBounds(0, y, rowsPanel.ClientSize.Width, Ui.S(70));
                y += r.Height;
            }
        }

        void AddFolder(SaveRow row)
        {
            using (var dlg = new FolderBrowserDialog { Description = "Pick a folder where " + row.GameName + " keeps its saves" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!SaveLocator.IsSafeRoot(dlg.SelectedPath))
                {
                    MessageBox.Show(this, "That folder is too broad – pick the game's own save folder, not a drive or a whole AppData folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                List<string> list;
                if (!settings.SaveDirs.TryGetValue(row.AppId, out list)) settings.SaveDirs[row.AppId] = list = new List<string>();
                if (!list.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase)) list.Add(dlg.SelectedPath);
                string err;
                if (!settings.Save(out err)) Log(LogLevel.Error, err);
                DetectFolders(row);
                Log(LogLevel.Ok, row.GameName + ": added save folder " + dlg.SelectedPath);
            }
        }

        // ---------------------------------------------------------- account

        void SignInOrOut()
        {
            if (signInCts != null) { signInCts.Cancel(); return; }
            if (drive != null)
            {
                if (MessageBox.Show(this, "Sign out of Google? Your backups stay in your Drive.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var d = drive;
                drive = null;
                Task.Run(() => d.SignOut());
                Log(LogLevel.Info, "Signed out of Google.");
                RefreshAccount();
                return;
            }
            signInCts = new CancellationTokenSource();
            var ct = signInCts.Token;
            Log(LogLevel.Info, "Opening Google sign-in in your browser…");
            RefreshAccount();
            Task.Run(() => GoogleDrive.SignIn(ct)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                signInCts.Dispose();
                signInCts = null;
                if (t.IsCanceled || t.Exception != null && t.Exception.GetBaseException() is OperationCanceledException) Log(LogLevel.Dim, "Sign-in cancelled.");
                else if (t.Exception != null) Log(LogLevel.Error, "Sign-in failed: " + t.Exception.GetBaseException().Message);
                else
                {
                    drive = t.Result;
                    Log(LogLevel.Ok, "Signed in" + (drive.Email.Length > 0 ? " as " + drive.Email : "") + ".");
                }
                RefreshAccount();
            })));
        }

        void RefreshAccount()
        {
            signBtn.Text = signInCts != null ? "Cancel" : drive != null ? "Sign out" : "Sign in with Google";
            signBtn.Kind = drive != null || signInCts != null ? FlatButton.BtnKind.Secondary : FlatButton.BtnKind.Primary;
            signBtn.Enabled = GoogleDrive.Configured && !busy;
            accountCard.Invalidate();
            RefreshButtons();
        }

        void RefreshButtons()
        {
            foreach (var r in rows)
            {
                r.BackupBtn.Enabled = r.RestoreBtn.Enabled = drive != null && !busy;
                r.FolderBtn.Enabled = !busy;
            }
        }

        // ---------------------------------------------------------- backup / restore

        void Backup(SaveRow row)
        {
            if (CloudSaves.GameRunning(row.GameDir)
                && MessageBox.Show(this, row.GameName + " is running, so its saves may be mid-write. Back up anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            var d = drive;
            var folders = row.Folders.ToList();
            Log(LogLevel.Info, row.GameName + ": backing up " + folders.Count + " folder(s)…");
            Run(row.GameName + " backup", () => CloudSaves.Backup(d, row.GameName, row.AppId, folders, LogAny));
        }

        void ChooseRestore(SaveRow row)
        {
            var d = drive;
            List<DriveFile> backups = null;
            Run("Listing " + row.GameName + " backups", () => backups = CloudSaves.Backups(d, row.GameName, row.AppId), () =>
            {
                if (backups.Count == 0) { Log(LogLevel.Warn, row.GameName + ": no backups in your Drive yet."); return; }
                var menu = new ContextMenuStrip { Font = Ui.F(9f, false), ShowImageMargin = false };
                foreach (var b in backups)
                {
                    var pick = b;
                    menu.Items.Add(string.Format("{0}   ({1:N0} KB)", Path.GetFileNameWithoutExtension(b.Name), b.Size / 1024.0), null, delegate { Restore(row, pick); });
                }
                menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
                menu.Show(row.RestoreBtn, new Point(0, row.RestoreBtn.Height));
            });
        }

        void Restore(SaveRow row, DriveFile backup)
        {
            if (CloudSaves.GameRunning(row.GameDir))
            {
                MessageBox.Show(this, "Close " + row.GameName + " first – restoring under a running game can corrupt the save.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "Replace " + row.GameName + "'s current saves with the backup from " + Path.GetFileNameWithoutExtension(backup.Name)
                    + "?\n\nThe current saves are zipped to " + CloudSaves.SafetyDir + " first.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var d = drive;
            Log(LogLevel.Info, row.GameName + ": restoring " + backup.Name + "…");
            Run(row.GameName + " restore", () => CloudSaves.Restore(d, backup, row.GameName, row.AppId, row.GameDir, LogAny), () => DetectFolders(row));
        }

        void Run(string what, Action work, Action done = null)
        {
            if (busy || drive == null) return;
            busy = true;
            RefreshAccount();
            Task.Run(work).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                busy = false;
                if (t.Exception != null)
                {
                    var ex = t.Exception.GetBaseException();
                    if (ex is GoogleSignInExpiredException && drive != null)
                    {
                        var d = drive;
                        drive = null;
                        Task.Run(() => d.SignOut());
                    }
                    Log(LogLevel.Error, what + " failed: " + ex.Message);
                }
                else if (done != null) done();
                RefreshAccount();
            })));
        }

        // ---------------------------------------------------------- log

        void LogAny(LogLevel level, string msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((Action)(() => Log(level, msg)));
            else Log(level, msg);
        }

        void Log(LogLevel level, string msg)
        {
            if (!IsDisposed) log.AppendLine(msg, level);
        }
    }
}
