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
    /// <summary>Game tools → Emulator settings…: player name, language, DLC and LAN peers for one patched game,
    /// written into the steam_settings folder beside its emulator dll. Blank means "use the global setting".</summary>
    public class EmuSettingsForm : ShibaForm
    {
        const int Pad = 24;
        const int CleanH = 108;   // the clean-up card plus its gap, added below the LAN card when needed

        readonly string exe, appIdHint;
        readonly TitleBar titleBar;
        readonly AppCard playerCard, dlcCard, lanCard, cleanCard;
        readonly TextBox nameBox, peersBox;
        readonly ComboBox langBox;
        readonly Toggle tUnlock;
        readonly CheckedListBox dlcList;
        readonly FlatButton fetchBtn, saveBtn, closeBtn, cleanBtn;
        readonly CheckBox chkMigrate;
        readonly Label status;

        EmuSettings.Target target;
        string appId = "";
        string notice = "Reading the game's settings…";
        Color noticeCol = Ui.MutedC;
        EmuSettings.Values global = new EmuSettings.Values();
        readonly List<EmuSettings.DlcEntry> dlc = new List<EmuSettings.DlcEntry>();
        List<string> activated = new List<string>();
        List<string> gameLanguages;

        Task work = Task.FromResult(0);
        CancellationTokenSource fetchCts;
        bool busy, closing, allowClose;

        public EmuSettingsForm(string exePath, string appIdHint)
        {
            exe = exePath;
            this.appIdHint = appIdHint ?? "";

            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(800, 694);
            Text = BuildInfo.AppName + " – emulator settings";
            MinimumSize = Size;

            titleBar = new TitleBar();
            Controls.Add(titleBar);

            // ---- player
            playerCard = new AppCard();
            playerCard.Bounds = new Rectangle(Pad, 108, 800 - Pad * 2, 132);
            playerCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Ui.SectionLabel(g, "PLAYER", new Point(Ui.S(22), Ui.S(14)));
                var f = Ui.F(8.75f, false);
                TextRenderer.DrawText(g, "Player name", f, new Point(Ui.S(22), Ui.S(40)), Ui.TextC, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "Language", f, new Point(Ui.S(388), Ui.S(40)), Ui.TextC, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "Leave blank to use the global settings (" + EmuSettings.GlobalSettingsDir + ").",
                    Ui.F(8.25f, false), new Rectangle(Ui.S(22), Ui.S(102), playerCard.Width - Ui.S(44), Ui.S(20)), Ui.MutedC,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            Controls.Add(playerCard);

            nameBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Inset, ForeColor = Ui.TextC, Font = Ui.F(10f, false), MaxLength = 64 };
            nameBox.Bounds = new Rectangle(22, 62, 340, 28);
            playerCard.Controls.Add(nameBox);

            langBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Ui.Inset, ForeColor = Ui.TextC, Font = Ui.F(10f, false) };
            langBox.Bounds = new Rectangle(388, 62, 340, 28);
            playerCard.Controls.Add(langBox);

            // ---- DLC
            dlcCard = new AppCard();
            dlcCard.Bounds = new Rectangle(Pad, 252, 800 - Pad * 2, 252);
            dlcCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Ui.SectionLabel(g, "DLC", new Point(Ui.S(22), Ui.S(14)));
                TextRenderer.DrawText(g, tUnlock.Checked
                        ? "Every DLC is reported as owned; ticked ones are also listed to the game by name."
                        : "Only the ticked DLC are reported as owned.",
                    Ui.F(8.25f, false), new Rectangle(Ui.S(22), Ui.S(226), dlcCard.Width - Ui.S(44), Ui.S(18)), Ui.MutedC,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            Controls.Add(dlcCard);

            tUnlock = new Toggle("Unlock all DLC", true);
            tUnlock.Bounds = new Rectangle(22, 40, 300, 26);
            tUnlock.CheckedChanged += delegate { dlcCard.Invalidate(); };
            dlcCard.Controls.Add(tUnlock);

            fetchBtn = new FlatButton("Fetch names from Steam store") { TextSize = 9f, Kind = FlatButton.BtnKind.Secondary };
            fetchBtn.Bounds = new Rectangle(752 - 22 - 250, 36, 250, 34);
            fetchBtn.Click += delegate { FetchStoreNames(); };
            dlcCard.Controls.Add(fetchBtn);

            dlcList = new CheckedListBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Inset, ForeColor = Ui.TextC, Font = Ui.F(9f, false), CheckOnClick = true, IntegralHeight = false };
            dlcList.Bounds = new Rectangle(22, 80, 708, 138);
            dlcList.ItemCheck += (s, e) => { if (e.Index >= 0 && e.Index < dlc.Count) dlc[e.Index].Enabled = e.NewValue == CheckState.Checked; };
            dlcCard.Controls.Add(dlcList);

            // ---- LAN peers
            lanCard = new AppCard();
            lanCard.Bounds = new Rectangle(Pad, 516, 800 - Pad * 2, 112);
            lanCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Ui.SectionLabel(g, "LAN PEERS", new Point(Ui.S(22), Ui.S(14)));
                TextRenderer.DrawText(g, "One IP address or host name per line, optionally with :port. Needed for VPNs like Radmin (26.x addresses) "
                    + "or ZeroTier, where the game's LAN broadcasts don't reach the other players. Tailscale and a normal home network work without it.",
                    Ui.F(8.25f, false), new Rectangle(Ui.S(400), Ui.S(38), lanCard.Width - Ui.S(422), Ui.S(66)), Ui.MutedC,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            };
            Controls.Add(lanCard);

            peersBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Inset, ForeColor = Ui.TextC, Font = Ui.F("Consolas", 9.5f, false) };
            peersBox.Bounds = new Rectangle(22, 38, 360, 62);
            lanCard.Controls.Add(peersBox);

            // ---- clean up (only shown when an older version left live samples)
            cleanCard = new AppCard { Visible = false };
            cleanCard.Bounds = new Rectangle(Pad, 640, 800 - Pad * 2, 96);
            cleanCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Ui.SectionLabel(g, "CLEAN UP EXAMPLE FILES", new Point(Ui.S(22), Ui.S(14)));
                string text = activated.Count + " sample file(s) an older Shibaberg switched on are live here ("
                    + string.Join(", ", activated.Take(5).Select(Path.GetFileName).ToArray()) + (activated.Count > 5 ? ", …" : "")
                    + "). They make the game run as AppID 1337, lock DLC behind fake ones, use a fixed account and keep the saves beside the dll.";
                TextRenderer.DrawText(g, text, Ui.F(8.25f, false), new Rectangle(Ui.S(22), Ui.S(36), cleanCard.Width - Ui.S(230), Ui.S(54)), Ui.WarnC,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            };
            Controls.Add(cleanCard);

            cleanBtn = new FlatButton("Clean up") { TextSize = 9.5f };
            cleanBtn.Bounds = new Rectangle(752 - 22 - 180, 14, 180, 36);
            cleanBtn.Click += delegate { CleanUp(); };
            cleanCard.Controls.Add(cleanBtn);

            chkMigrate = new CheckBox { AutoSize = true, Text = "Copy the saves over first", Checked = true, ForeColor = Ui.TextC, BackColor = Ui.Surface, Font = Ui.F(8.5f, false) };
            chkMigrate.Location = new Point(752 - 22 - 180, 60);
            cleanCard.Controls.Add(chkMigrate);

            // ---- buttons
            status = new Label { AutoSize = false, BackColor = Ui.Bg, ForeColor = Ui.MutedC, Font = Ui.F(8.5f, false), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            status.Bounds = new Rectangle(Pad, 640, 420, 40);
            Controls.Add(status);

            closeBtn = new FlatButton("Close") { Kind = FlatButton.BtnKind.Secondary };
            closeBtn.Bounds = new Rectangle(800 - Pad - 120, 640, 120, 40);
            closeBtn.Click += delegate { Close(); };
            Controls.Add(closeBtn);

            saveBtn = new FlatButton("Save");
            saveBtn.Bounds = new Rectangle(800 - Pad - 120 - 12 - 150, 640, 150, 40);
            saveBtn.Click += delegate { SaveValues(); };
            Controls.Add(saveBtn);

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            FormClosing += OnClosing;
            Shown += delegate { BeginLoad(); };
            SetEditable(false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            TextRenderer.DrawText(g, "Emulator settings – " + Path.GetFileNameWithoutExtension(exe), Ui.F(12.75f, true),
                new Rectangle(Ui.S(Pad), Ui.S(48), ClientSize.Width - Ui.S(Pad * 2), Ui.S(26)), Ui.TextC, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Ui.TruncMiddle(g, notice, Ui.F(8.5f, false), ClientSize.Width - Ui.S(Pad * 2)), Ui.F(8.5f, false),
                new Point(Ui.S(Pad), Ui.S(78)), noticeCol, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        void SetNotice(string text, Color col)
        {
            notice = text ?? "";
            noticeCol = col;
            Invalidate(new Rectangle(0, Ui.S(70), ClientSize.Width, Ui.S(30)));
        }

        void SetEditable(bool on)
        {
            nameBox.Enabled = langBox.Enabled = tUnlock.Enabled = dlcList.Enabled = peersBox.Enabled = saveBtn.Enabled = on && !busy;
            fetchBtn.Enabled = on && !busy && appId.Length > 0;
            cleanBtn.Enabled = chkMigrate.Enabled = !busy && target != null && target.IsEmulator;
        }

        // ---------------------------------------------------------- loading

        sealed class Loaded
        {
            public EmuSettings.Target Target;
            public EmuSettings.Values Values;
            public EmuSettings.Values Global;
            public List<string> Activated = new List<string>();
            public List<EmuSettings.DlcEntry> LocalDlc;
            public List<string> Languages;
            public string AppName;
        }

        void BeginLoad()
        {
            busy = true;
            string hint = appIdHint;
            work = Task.Run(() =>
            {
                var r = new Loaded { Target = EmuSettings.Locate(exe) };
                r.Global = EmuSettings.LoadGlobal();
                if (r.Target.SettingsDir.Length > 0)
                {
                    r.Values = EmuSettings.Load(r.Target.SettingsDir);
                    if (r.Target.IsEmulator) r.Activated = SettingsScaffold.FindActivatedExamples(r.Target.SettingsDir);
                }
                string id = r.Target.AppId.Length > 0 ? r.Target.AppId : AppIdDetector.Normalize(hint);
                if (id.Length > 0)
                {
                    r.Target.AppId = id;
                    // appinfo.vdf can be large; the first read indexes it, which is why this is off the UI thread.
                    var app = EmuSettings.LocalAppInfo(id, out r.LocalDlc);
                    if (app != null) { r.Languages = app.Languages; r.AppName = app.Name; }
                }
                return r;
            }).ContinueWith(t => UiInvoke(delegate
            {
                busy = false;
                if (closing) return;
                if (t.Exception != null) { SetNotice("Could not read the game's settings: " + t.Exception.GetBaseException().Message, Ui.ErrC); return; }
                Apply(t.Result);
            }));
        }

        void Apply(Loaded r)
        {
            target = r.Target;
            appId = target.AppId;
            global = r.Global ?? new EmuSettings.Values();
            gameLanguages = r.Languages != null && r.Languages.Count > 0 ? r.Languages : null;

            var v = r.Values ?? new EmuSettings.Values();
            nameBox.Text = v.AccountName;
            if (nameBox.IsHandleCreated)
                AppIdBox.NativeCue.Set(nameBox.Handle, global.AccountName.Length > 0 ? global.AccountName + "  (global)" : "gse orca  (emulator default)");
            FillLanguages(v.Language);
            tUnlock.Checked = v.UnlockAllDlc;
            dlc.Clear();
            dlc.AddRange(v.Dlc);
            if (r.LocalDlc != null)
                foreach (var d in r.LocalDlc)
                {
                    var known = dlc.FirstOrDefault(x => x.Id == d.Id);
                    if (known == null) dlc.Add(d);
                    else if (known.Name.Length == 0 || known.Name == "DLC " + known.Id) known.Name = d.Name;
                }
            RefreshDlcList();
            peersBox.Text = string.Join("\r\n", v.LanPeers.ToArray());

            activated = r.Activated;
            ShowCleanCard(activated.Count > 0);

            string where = target.SettingsDir.Length > 0 ? target.SettingsDir : "";
            string game = !string.IsNullOrEmpty(r.AppName) ? r.AppName + " · " : "";
            if (target.Dll.Length == 0)
                SetNotice("No Steamworks library in this game's folder – patch the game first.", Ui.WarnC);
            else if (target.OnlineFix)
                SetNotice("This game is patched in online-fix mode: it runs Valve's own Steam library, so these settings are not used.", Ui.WarnC);
            else if (!target.IsEmulator)
                SetNotice("Patch this game first – " + Path.GetFileName(target.Dll) + " is still Valve's original, which ignores steam_settings.", Ui.WarnC);
            else
                SetNotice(game + (appId.Length > 0 ? "AppID " + appId + " · " : "") + where, Ui.MutedC);
            SetEditable(target.IsEmulator);
            if (target.IsEmulator && r.LocalDlc == null && appId.Length > 0 && dlc.Count == 0)
                SetStatus("Steam on this PC has no DLC list for this game – use “Fetch names from Steam store”.", Ui.MutedC);
        }

        void FillLanguages(string current)
        {
            langBox.BeginUpdate();
            langBox.Items.Clear();
            langBox.Items.Add("(use global – " + (global.Language.Length > 0 ? global.Language : "english") + ")");
            var list = (gameLanguages ?? EmuSettings.ApiLanguages.ToList()).ToList();
            if (!string.IsNullOrEmpty(current) && !list.Contains(current)) list.Add(current);
            foreach (var l in list) langBox.Items.Add(l);
            langBox.SelectedIndex = string.IsNullOrEmpty(current) ? 0 : langBox.Items.IndexOf(current);
            langBox.EndUpdate();
        }

        void RefreshDlcList()
        {
            dlcList.BeginUpdate();
            dlcList.Items.Clear();
            foreach (var d in dlc) dlcList.Items.Add(d.Id + "  –  " + (d.Name.Length > 0 ? d.Name : "DLC " + d.Id), d.Enabled);
            dlcList.EndUpdate();
        }

        void ShowCleanCard(bool show)
        {
            if (cleanCard.Visible == show) return;
            cleanCard.Visible = show;
            // Runs after load, so the offset is scaled by hand (the constructor's bounds were auto-scaled).
            int dy = Ui.S(CleanH) * (show ? 1 : -1);
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + dy);
            MinimumSize = Size;
            foreach (var c in new Control[] { status, saveBtn, closeBtn }) c.Top += dy;
        }

        // ---------------------------------------------------------- actions

        void SetStatus(string text, Color col)
        {
            status.ForeColor = col;
            status.Text = text;
        }

        void SaveValues()
        {
            if (busy || target == null || !target.IsEmulator) return;
            List<string> invalid;
            var peers = EmuSettings.ParsePeers(peersBox.Text, out invalid);
            if (invalid.Count > 0)
            {
                MessageBox.Show(this, "These LAN peer lines are not an IP address or host name (with an optional :port):\n\n"
                    + string.Join("\n", invalid.Take(10).ToArray()), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string name = EmuSettings.SanitizeName(nameBox.Text);
            if (name != nameBox.Text.Trim()) nameBox.Text = name;
            var v = new EmuSettings.Values
            {
                AccountName = name,
                Language = langBox.SelectedIndex > 0 ? (string)langBox.SelectedItem : "",
                UnlockAllDlc = tUnlock.Checked,
            };
            v.Dlc.AddRange(dlc);
            v.LanPeers.AddRange(peers);
            try
            {
                var changed = EmuSettings.Save(target.SettingsDir, v);
                SetStatus(changed.Count == 0 ? "Nothing changed." : "Saved " + string.Join(", ", changed.Select(Path.GetFileName).ToArray()) + ".", Ui.OkC);
            }
            catch (Exception ex)
            {
                SetStatus("Could not save: " + ex.Message, Ui.ErrC);
            }
        }

        void FetchStoreNames()
        {
            if (busy || appId.Length == 0) return;
            busy = true;
            SetEditable(true);
            SetStatus("Asking the Steam store for the DLC list…", Ui.MutedC);
            fetchCts = new CancellationTokenSource();
            var ct = fetchCts.Token;
            string id = appId;
            work = Task.Run(() => EmuSettings.FetchStoreDlc(id, ct), ct).ContinueWith(t => UiInvoke(delegate
            {
                busy = false;
                if (fetchCts != null) { fetchCts.Dispose(); fetchCts = null; }
                if (closing) return;
                SetEditable(target != null && target.IsEmulator);
                if (t.IsCanceled) return;
                var list = t.Exception == null ? t.Result : null;
                if (list == null) { SetStatus("The Steam store could not be reached (or does not know this AppID).", Ui.WarnC); return; }
                int added = 0, named = 0;
                foreach (var d in list)
                {
                    var known = dlc.FirstOrDefault(x => x.Id == d.Id);
                    if (known == null) { dlc.Add(d); added++; }
                    else if (d.Name.Length > 0 && known.Name != d.Name) { known.Name = d.Name; named++; }
                }
                RefreshDlcList();
                SetStatus("Steam store: " + list.Count + " DLC – " + added + " new, " + named + " renamed. Save to keep them.", Ui.OkC);
            }));
        }

        void CleanUp()
        {
            if (busy || target == null || activated.Count == 0) return;
            if (CloudSaves.GameRunning(PatchRunner.SearchRoot(exe)))
            {
                MessageBox.Show(this, "Close the game first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "Delete these " + activated.Count + " sample file(s)?\n\n" + string.Join("\n", activated.Select(f => PatchRunner.ShortRel(target.SettingsDir, f)).ToArray())
                    + (chkMigrate.Checked ? "\n\nSaves kept beside the dll are copied to %APPDATA%\\GSE Saves first (the originals stay)." : ""),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var r = SettingsScaffold.CleanUpActivatedExamples(target.SettingsDir, appId, chkMigrate.Checked);
            activated = SettingsScaffold.FindActivatedExamples(target.SettingsDir);
            cleanCard.Invalidate();
            string msg = "Removed " + r.Deleted.Count + " sample file(s)." + (r.Messages.Count > 0 ? " " + string.Join(" ", r.Messages.ToArray()) : "");
            SetStatus(msg, r.Kept.Count == 0 ? Ui.OkC : Ui.WarnC);
            if (r.Kept.Count > 0) MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (activated.Count == 0) ShowCleanCard(false);
            // configs.user.ini / configs.app.ini may have gone: show what is in effect now
            var v = EmuSettings.Load(target.SettingsDir);
            nameBox.Text = v.AccountName;
            FillLanguages(v.Language);
            tUnlock.Checked = v.UnlockAllDlc;
        }

        // ---------------------------------------------------------- closing

        async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            if (fetchCts != null) { try { fetchCts.Cancel(); } catch { } }
            SetEditable(false);
            try { await work; } catch { }
            allowClose = true;
            Close();
        }
    }
}
