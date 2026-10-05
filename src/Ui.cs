using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace Gp
{
    public static class Ui
    {
        public static Color FromHex(string h)
        {
            h = h.TrimStart('#');
            return Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
        }

        // Neutrals carry a faint violet cast so the surfaces sit with the accent instead of against it.
        public static readonly Color Bg = FromHex("#0E0C13");
        public static readonly Color Surface = FromHex("#16131E");
        public static readonly Color Surface2 = FromHex("#1F1B2A");
        public static readonly Color Inset = FromHex("#110F18");
        public static readonly Color BorderC = FromHex("#2A2537");
        public static readonly Color TextC = FromHex("#EDEAF5");
        public static readonly Color MutedC = FromHex("#958FA8");
        public static readonly Color Accent = FromHex("#8B5CF6");      // brand violet
        public static readonly Color Accent2 = FromHex("#B79CFF");     // lavender: links, highlights, info chips
        public static readonly Color AccentHi = FromHex("#A07BFF");    // gradient top / hover
        public static readonly Color AccentLo = FromHex("#6D3BEB");    // gradient bottom / pressed
        public static readonly Color OkC = FromHex("#34D399");
        public static readonly Color WarnC = FromHex("#FBBF24");
        public static readonly Color ErrC = FromHex("#F87171");

        // These were ad-hoc Ui.FromHex("#...") literals inside paint and log paths, where each call cost
        // two Substring allocations and three Convert.ToInt32 parses - on every repaint, per control.
        public static readonly Color DisabledC = FromHex("#5C566C");   // disabled label / toggle text
        public static readonly Color KnobOffC = FromHex("#6F6982");    // disabled toggle knob
        public static readonly Color CancelA = FromHex("#C24452");
        public static readonly Color CancelB = FromHex("#9B3440");
        public static readonly Color SuccessA = FromHex("#22A36B");
        public static readonly Color SuccessB = FromHex("#178052");
        public static readonly Color OkBorderC = FromHex("#1E5C44");
        public static readonly Color ErrBorderC = FromHex("#6B2B31");
        public static readonly Color WarnBorderC = FromHex("#6B5623");
        public static readonly Color DimC = FromHex("#6A6479");        // dim log lines
        public static readonly Color LogTextC = FromHex("#BDB7CC");    // normal log lines

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

        static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
        static Ui()
        {
            Application.ApplicationExit += delegate
            {
                foreach (var f in fontCache.Values) f.Dispose();
                fontCache.Clear();
            };
        }
        public static Font F(float size, bool bold) { return F("Segoe UI", size, bold); }
        public static Font F(string fam, float size, bool bold)
        {
            var key = fam + size + (bold ? "b" : "r");
            if (!fontCache.ContainsKey(key))
            {
                FontStyle st = bold ? FontStyle.Bold : FontStyle.Regular;
                try { fontCache[key] = new Font(fam, size, st); }
                catch { fontCache[key] = new Font("Segoe UI", size, st); }
            }
            return fontCache[key];
        }

        /// <summary>Display scale relative to 96 DPI, set once at startup. The forms opt into
        /// <see cref="AutoScaleMode.Dpi"/>, which scales every control's bounds by this same factor, so the
        /// paint code below - which positions things by hand - has to scale its own constants to stay in
        /// step. Fonts need no help: they are created in points and GDI+ already maps those through the
        /// device DPI. The implementation lives in <see cref="Dpi"/> so the self-test can reach it.</summary>
        public static float Scale { get { return Dpi.Scale; } set { Dpi.Scale = value; } }

        public static void InitializeScale() { Dpi.Initialize(); }

        public static int S(int px) { return Dpi.S(px); }
        public static float S(float px) { return Dpi.S(px); }
        public static PointF S(PointF p) { return Dpi.S(p); }
        public static Point S(Point p) { return Dpi.S(p); }

        public static GraphicsPath RoundPath(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            if (rad < 1 || r.Width < 2 || r.Height < 2) { p.AddRectangle(r); return p; }
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillPath(b, ScratchPath(r, rad));
        }
        public static void StrokeRound(Graphics g, Rectangle r, int rad, Color c, float w)
        {
            using (var pen = new Pen(c, w)) g.DrawPath(pen, ScratchPath(r, rad));
        }

        // FillRound/StrokeRound run several times per control per paint, and a control repainting at a
        // steady size fills the SAME rectangle every frame. Reusing one path per thread means those
        // repaints stop allocating a four-arc GraphicsPath each. The fill-then-stroke pattern also now
        // shares a single path, which is the common case.
        //
        // Deliberately not a general (rect -> path) cache: keys are rectangles, so the table would grow
        // with every resize step, and every existing caller disposes what RoundPath hands back - a cache
        // entry handed to one of those would be destroyed underneath it.
        [ThreadStatic] static GraphicsPath scratchPath;
        [ThreadStatic] static string scratchKey;

        static GraphicsPath ScratchPath(Rectangle r, int rad)
        {
            string key = r.X + "," + r.Y + "," + r.Width + "," + r.Height + "," + rad;
            if (scratchPath != null && scratchKey == key) return scratchPath;
            if (scratchPath != null) scratchPath.Dispose();
            scratchPath = RoundPath(r, rad);
            scratchKey = key;
            return scratchPath;
        }

        public static string TruncMiddle(Graphics g, string s, Font f, int maxW)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (maxW <= 0) return "";
            if (g.MeasureString(s, f).Width <= maxW) return s;
            string mid = "…";
            if (g.MeasureString(mid, f).Width > maxW) return "";
            int lo = 1, hi = s.Length - 1;
            string best = mid;
            while (lo <= hi)
            {
                int take = lo + (hi - lo) / 2;
                int left = (take + 1) / 2, right = take / 2;
                var t = s.Substring(0, left) + mid + s.Substring(s.Length - right);
                if (g.MeasureString(t, f).Width <= maxW) { best = t; lo = take + 1; }
                else hi = take - 1;
            }
            return best;
        }

        public static void SpacedText(Graphics g, string text, Font f, Brush b, PointF pt, float spacing)
        {
            float x = pt.X;
            foreach (var ch in text)
            {
                if (ch != ' ') g.DrawString(ch.ToString(), f, b, x, pt.Y, StringFormat.GenericTypographic);
                x += GlyphAdvance(g, ch, f) + spacing;
            }
        }

        public static SizeF MeasureSpaced(Graphics g, string text, Font f, float spacing)
        {
            float w = 0; foreach (var ch in text) w += GlyphAdvance(g, ch, f) + spacing;
            return new SizeF(w, g.MeasureString(text, f).Height);
        }

        // GenericTypographic trims trailing whitespace, so a lone space measures as zero width and spaced
        // labels used to collapse ("STEAM APPID" painted as "STEAMAPPID"). Measure a space between two
        // glyphs instead.
        static float GlyphAdvance(Graphics g, char ch, Font f)
        {
            var fmt = StringFormat.GenericTypographic;
            if (ch != ' ') return g.MeasureString(ch.ToString(), f, Point.Empty, fmt).Width;
            return g.MeasureString("i i", f, Point.Empty, fmt).Width - g.MeasureString("ii", f, Point.Empty, fmt).Width;
        }

        public static void SectionLabel(Graphics g, string text, Point at)
        {
            using (var b = new SolidBrush(MutedC)) SpacedText(g, text, F(7.25f, true), b, new PointF(at.X, at.Y), 1.4f);
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static void DrawChip(Graphics g, ref int x, int y, int h, string text, Color fore, Color back, Color? border)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Measured with TextRenderer because that is what draws it - GDI+ metrics run narrower.
            var sz = TextRenderer.MeasureText(g, text, F(7.75f, true), Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int w = sz.Width + S(18);
            var r = new Rectangle(x, y, w, h);
            FillRound(g, r, S(6), back);
            if (border.HasValue) StrokeRound(g, r, S(6), border.Value, 1f);
            TextRendererHelper(g, text, fore, r);
            x += w + S(8);
        }

        static void TextRendererHelper(Graphics g, string text, Color fore, Rectangle r)
        {
            TextRenderer.DrawText(g, text, F(7.75f, true), r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static Color Tint(Color basec, Color tint, double amt)
        {
            return Color.FromArgb(
                (int)(basec.R * (1 - amt) + tint.R * amt),
                (int)(basec.G * (1 - amt) + tint.G * amt),
                (int)(basec.B * (1 - amt) + tint.B * amt));
        }
    }

    // ─────────────────────────────────────────────── animation

    /// <summary>A 0..1 value that eases toward a target and repaints its owner while it moves. Every
    /// instance shares one UI-thread timer that only runs while something is actually animating, so idle
    /// controls cost nothing. Transitions are short (~120 ms) - long enough to read as motion, short
    /// enough that the UI never feels like it is waiting on itself.</summary>
    public sealed class Anim
    {
        static readonly List<Anim> active = new List<Anim>();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static Timer timer;
        static long lastTick;

        readonly Control owner;
        readonly float durationMs;
        float pos, target;

        public Anim(Control owner, float durationMs = 120f) { this.owner = owner; this.durationMs = Math.Max(1f, durationMs); }

        /// <summary>Linear position; use <see cref="Eased"/> for anything drawn.</summary>
        public float Value { get { return pos; } }
        public float Eased { get { float t = pos; return t * t * (3f - 2f * t); } }

        public float Target
        {
            get { return target; }
            set
            {
                target = Math.Max(0f, Math.Min(1f, value));
                if (pos == target) { active.Remove(this); return; }
                if (!active.Contains(this)) active.Add(this);
                if (timer == null) { timer = new Timer { Interval = 15 }; timer.Tick += delegate { Step(); }; }
                if (!timer.Enabled) { lastTick = clock.ElapsedMilliseconds; timer.Start(); }
            }
        }

        public void Snap(float v) { pos = target = Math.Max(0f, Math.Min(1f, v)); active.Remove(this); }

        static void Step()
        {
            long now = clock.ElapsedMilliseconds;
            float dt = Math.Max(1, now - lastTick);
            lastTick = now;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var a = active[i];
                if (a.owner.IsDisposed) { active.RemoveAt(i); continue; }
                float step = dt / a.durationMs;
                if (Math.Abs(a.target - a.pos) <= step) { a.pos = a.target; active.RemoveAt(i); }
                else a.pos += a.target > a.pos ? step : -step;
                a.owner.Invalidate();
            }
            if (active.Count == 0) timer.Stop();
        }
    }

    // ─────────────────────────────────────────────── card panel

    public class AppCard : Panel
    {
        public int Radius = 12;
        public bool ShowBorder = true;
        public AppCard() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Ui.Bg; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int rad = Ui.S(Radius);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, rad, Ui.Surface);
            if (ShowBorder)
            {
                Ui.StrokeRound(g, r, rad, Ui.BorderC, 1f);
                // A hairline of light along the top edge gives the card a little lift off the background.
                using (var p = new Pen(Color.FromArgb(14, 255, 255, 255), 1f)) g.DrawLine(p, rad, 1, Width - rad - 1, 1);
            }
            base.OnPaint(e);
        }
    }

    // ─────────────────────────────────────────────── title bar

    /// <summary>Caption button drawn with vector glyphs (crisp at any DPI, unlike the "−"/"×" text it
    /// replaces) and a quick fade on hover.</summary>
    public class WindowButton : Button
    {
        public bool IsClose;
        readonly Anim hover;
        bool press;
        public WindowButton(bool isClose)
        {
            IsClose = isClose;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            hover = new Anim(this, 90f);
            TabStop = false;
        }
        protected override void OnMouseEnter(EventArgs e) { hover.Target = 1; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover.Target = 0; press = false; base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { press = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Eased;
            if (h > 0)
            {
                Color hc = IsClose ? Color.FromArgb(232, 17, 35) : Ui.Surface2;
                if (press) hc = IsClose ? Color.FromArgb(241, 112, 122) : Ui.BorderC;
                Ui.FillRound(g, Rectangle.Inflate(ClientRectangle, -Ui.S(2), -Ui.S(2)), Ui.S(6), Ui.Alpha(hc, (int)(255 * h)));
            }
            Color fg = Ui.Lerp(Ui.MutedC, IsClose ? Color.White : Ui.TextC, h);
            float cx = Width / 2f, cy = Height / 2f, s = Ui.S(5f);
            using (var p = new Pen(fg, Math.Max(1f, Ui.S(1.1f))))
            {
                if (IsClose) { g.DrawLine(p, cx - s, cy - s, cx + s, cy + s); g.DrawLine(p, cx - s, cy + s, cx + s, cy - s); }
                else { g.SmoothingMode = SmoothingMode.None; g.DrawLine(p, cx - s, cy, cx + s, cy); }
            }
        }
    }

    public class TitleBar : Control
    {
        readonly WindowButton minimizeButton = new WindowButton(false);
        readonly WindowButton closeButton = new WindowButton(true);
        public TitleBar()
        {
            Dock = DockStyle.Top; Height = 46;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Default;
            TabStop = false;
            int tab = 0;
            foreach (var button in new[] { minimizeButton, closeButton })
            {
                button.TabIndex = tab++;
                Controls.Add(button);
            }
            minimizeButton.AccessibleName = "Minimize";
            closeButton.AccessibleName = "Close";
            minimizeButton.Click += delegate { OnMinimizeClicked(); };
            closeButton.Click += delegate { OnCloseClicked(); };
            LayoutBtns();
        }
        protected override void OnResize(EventArgs e) { LayoutBtns(); base.OnResize(e); }
        void LayoutBtns()
        {
            closeButton.Bounds = new Rectangle(Width - Ui.S(50), Ui.S(8), Ui.S(40), Ui.S(30));
            minimizeButton.Bounds = new Rectangle(Width - Ui.S(92), Ui.S(8), Ui.S(40), Ui.S(30));
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !closeButton.Bounds.Contains(e.Location) && !minimizeButton.Bounds.Contains(e.Location)) DragWindow();
            base.OnMouseDown(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !closeButton.Bounds.Contains(e.Location) && !minimizeButton.Bounds.Contains(e.Location)) OnMinimizeClicked();
            base.OnMouseDoubleClick(e);
        }
        public event Action CloseClicked;
        public event Action MinimizeClicked;
        void OnCloseClicked() { var h = CloseClicked; if (h != null) h(); }
        void OnMinimizeClicked()
        {
            var h = MinimizeClicked;
            if (h != null) h();
            else { var f = FindForm(); if (f != null) f.WindowState = FormWindowState.Minimized; }
        }
        void DragWindow()
        {
            var f = FindForm(); if (f == null) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, ClientRectangle);

            // logo: rounded violet tile with a "G" monogram
            var logoRect = new Rectangle(Ui.S(20), Ui.S(12), Ui.S(22), Ui.S(22));
            using (var lg = new LinearGradientBrush(logoRect, Ui.AccentHi, Ui.AccentLo, 60f)) using (var p = Ui.RoundPath(logoRect, Ui.S(6))) g.FillPath(lg, p);
            TextRenderer.DrawText(g, "G", Ui.F(9.5f, true), logoRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // title + version pill
            var tf = Ui.F(9.25f, true);
            var tsz = TextRenderer.MeasureText(g, "Goldberg Patcher", tf, Size.Empty, TextFormatFlags.NoPadding);
            int tx = Ui.S(52), ty = Height / 2 - tsz.Height / 2;
            TextRenderer.DrawText(g, "Goldberg Patcher", tf, new Point(tx, ty), Ui.TextC, TextFormatFlags.NoPadding);
            string ver = "v" + BuildInfo.Version;
            var vf = Ui.F(7.25f, true);
            var vsz = TextRenderer.MeasureText(g, ver, vf, Size.Empty, TextFormatFlags.NoPadding);
            var vr = new Rectangle(tx + tsz.Width + Ui.S(10), Height / 2 - Ui.S(9), vsz.Width + Ui.S(14), Ui.S(18));
            Ui.FillRound(g, vr, vr.Height / 2, Ui.Tint(Ui.Bg, Ui.Accent, 0.16));
            TextRenderer.DrawText(g, ver, vf, vr, Ui.Accent2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            using (var p = new Pen(Ui.BorderC, 1f)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        /// <summary>Dark native scrollbars (Windows 10 1809+); a bright white scrollbar in the dark log
        /// looked like a rendering glitch. Silently a no-op on systems without the theme.</summary>
        internal static void UseDarkScrollbars(Control c)
        {
            if (c.IsHandleCreated) try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
            else c.HandleCreated += delegate { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
        }
    }

    // ─────────────────────────────────────────────── drop zone

    public class DropZone : Control
    {
        public event Action<string> FileChosen;
        string gamePath = "";
        string archChip = "", sizeChip = "", apiChip = "";
        int apiState = 0; // 0 warn, 1 ok
        bool dragOver = false;
        bool overChange = false;
        Bitmap fileIcon = null;

        public string GamePath { get { return gamePath; } }

        readonly Anim hoverAnim, dragAnim;

        public DropZone()
        {
            AllowDrop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            hoverAnim = new Anim(this, 140f);
            dragAnim = new Anim(this, 110f);
        }

        protected override void OnMouseEnter(EventArgs e) { if (Enabled) hoverAnim.Target = 1; base.OnMouseEnter(e); }

        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        public event Action<string> InvalidFile;

        static bool IsExeDrop(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            return files != null && files.Length == 1 && (files[0] ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        public void ClearGame()
        {
            gamePath = ""; archChip = sizeChip = apiChip = ""; apiState = 0;
            if (fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }
            Invalidate();
        }

        public void UpdateAnalysis(string arch, string size, string api, int state)
        {
            archChip = arch ?? ""; sizeChip = size ?? ""; apiChip = api ?? ""; apiState = state; Invalidate();
        }

        static Bitmap LoadFileIcon(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path)) return icon == null ? null : icon.ToBitmap();
            }
            catch { return null; }
        }

        public void SetGame(string path)
        {
            string nextPath = path ?? "";
            if (!string.Equals(gamePath, nextPath, StringComparison.OrdinalIgnoreCase))
            {
                gamePath = nextPath;
                if (fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }
                fileIcon = LoadFileIcon(gamePath);
            }
            Invalidate();
            var h = FileChosen; if (h != null && gamePath.Length > 0) h(gamePath);
        }

        public void Browse()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select the game executable";
                dlg.Filter = "Program (*.exe)|*.exe";
                if (dlg.ShowDialog() == DialogResult.OK) SetGame(dlg.FileName);
            }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            if (IsExeDrop(e)) { e.Effect = DragDropEffects.Copy; SetDragOver(true); }
            else e.Effect = DragDropEffects.None;
        }
        protected override void OnDragOver(DragEventArgs e)
        {
            bool ok = IsExeDrop(e);
            if (ok != dragOver) SetDragOver(ok);
        }
        protected override void OnDragLeave(EventArgs e) { SetDragOver(false); }
        void SetDragOver(bool on) { dragOver = on; dragAnim.Target = on ? 1 : 0; Invalidate(); }
        protected override void OnDragDrop(DragEventArgs e)
        {
            SetDragOver(false);
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length < 1) return;
            string f0 = files[0] ?? "";
            if (!f0.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { var h = InvalidFile; if (h != null) h(f0); return; }
            SetGame(files[0]);
        }
        protected override void OnMouseClick(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) Browse(); base.OnMouseClick(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool oc = gamePath.Length > 0 && MouseIsOverChange(e.Location);
            if (oc != overChange) { overChange = oc; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { overChange = false; hoverAnim.Target = 0; Invalidate(); base.OnMouseLeave(e); }

        /// <summary>Where the CHANGE link sits. Derived from the width, the font and the DPI scale, so it
        /// is answerable before the control has ever painted. It used to be assigned inside OnPaint and
        /// read by OnMouseMove, which meant the hit region was Rectangle.Empty - and the link unclickable
        /// and never hover-highlighted - until something happened to repaint.</summary>
        Rectangle ChangeLinkBounds()
        {
            int w = TextRenderer.MeasureText("Change", Ui.F(8.25f, true), Size.Empty, TextFormatFlags.NoPadding).Width + Ui.S(24);
            return new Rectangle(Width - Ui.S(16) - w, Ui.S(16), w, Ui.S(26));
        }

        bool MouseIsOverChange(Point pt)
        {
            return ChangeLinkBounds().Contains(pt);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.S(12);
            float hot = Math.Max(hoverAnim.Eased * 0.6f, dragAnim.Eased);
            const TextFormatFlags plain = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            Ui.FillRound(g, rect, rad, Ui.Lerp(Ui.Surface, Ui.Tint(Ui.Surface, Ui.Accent, 0.10), hot));
            if (gamePath.Length == 0)
            {
                var bc = Ui.Lerp(Ui.Tint(Ui.BorderC, Color.White, 0.06), Ui.Accent, hot);
                using (var pen = new Pen(bc, Ui.S(1.5f))) { pen.DashStyle = DashStyle.Dash; pen.DashPattern = new[] { 4f, 3f }; using (var p = Ui.RoundPath(Rectangle.Inflate(rect, -1, -1), rad - 1)) g.DrawPath(pen, p); }

                // icon tile: soft halo + gradient tile + "drop into tray" arrow
                int isz = Ui.S(40);
                var iconR = new Rectangle(Width / 2 - isz / 2, Ui.S(16), isz, isz);
                var glowR = Rectangle.Inflate(iconR, Ui.S(6), Ui.S(6));
                Ui.FillRound(g, glowR, Ui.S(15), Ui.Alpha(Ui.Accent, (int)(22 + 40 * hot)));
                using (var lg = new LinearGradientBrush(iconR, Ui.AccentHi, Ui.AccentLo, 70f)) using (var p = Ui.RoundPath(iconR, Ui.S(11))) g.FillPath(lg, p);
                using (var pen = new Pen(Color.White, Ui.S(2f)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    float cx = iconR.X + iconR.Width / 2f, top = iconR.Y + Ui.S(10f), tip = iconR.Y + Ui.S(23f) + Ui.S(3f) * dragAnim.Eased;
                    g.DrawLine(pen, cx, top, cx, tip);
                    g.DrawLines(pen, new[] { new PointF(cx - Ui.S(5f), tip - Ui.S(5f)), new PointF(cx, tip), new PointF(cx + Ui.S(5f), tip - Ui.S(5f)) });
                    float ty = iconR.Bottom - Ui.S(10f), tx0 = iconR.X + Ui.S(11f), tx1 = iconR.Right - Ui.S(11f);
                    g.DrawLines(pen, new[] { new PointF(tx0, ty - Ui.S(4f)), new PointF(tx0, ty), new PointF(tx1, ty), new PointF(tx1, ty - Ui.S(4f)) });
                }

                var l1 = dragOver ? "Release to select this game" : "Drop the game's .exe here";
                var f1 = Ui.F(11f, true);
                var sz1 = TextRenderer.MeasureText(g, l1, f1, Size.Empty, plain);
                TextRenderer.DrawText(g, l1, f1, new Point(Width / 2 - sz1.Width / 2, Ui.S(64)), Ui.TextC, plain);
                var l2 = "or click to browse  ·  architecture & DRM are detected automatically";
                var f2 = Ui.F(8.5f, false);
                var sz2 = TextRenderer.MeasureText(g, l2, f2, Size.Empty, plain);
                TextRenderer.DrawText(g, l2, f2, new Point(Width / 2 - sz2.Width / 2, Ui.S(88)), Ui.MutedC, plain);
            }
            else
            {
                Ui.StrokeRound(g, rect, rad, Ui.Lerp(Ui.BorderC, Ui.Accent, dragAnim.Eased), 1f);
                using (var p = new Pen(Color.FromArgb(14, 255, 255, 255), 1f)) g.DrawLine(p, rad, 1, Width - rad - 1, 1);
                int pad = Ui.S(18);
                var iconR = new Rectangle(pad, Ui.S(16), Ui.S(40), Ui.S(40));
                if (fileIcon != null)
                {
                    Ui.FillRound(g, iconR, Ui.S(10), Ui.Surface2);
                    Ui.StrokeRound(g, iconR, Ui.S(10), Ui.BorderC, 1f);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(fileIcon, new Rectangle(iconR.X + Ui.S(4), iconR.Y + Ui.S(4), Ui.S(32), Ui.S(32)));
                }
                else
                {
                    Ui.FillRound(g, iconR, Ui.S(10), Ui.Tint(Ui.Surface2, Ui.Accent, 0.22));
                    TextRenderer.DrawText(g, "EXE", Ui.F(7.5f, true), iconR, Ui.Accent2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                int textX = iconR.Right + Ui.S(14);
                var changeRect = ChangeLinkBounds();
                int textMaxW = Math.Max(0, changeRect.Left - Ui.S(12) - textX);
                string name = Path.GetFileName(gamePath);
                TextRenderer.DrawText(g, name, Ui.F(10.5f, true), new Rectangle(textX, Ui.S(17), textMaxW, Ui.S(22)), Ui.TextC,
                    plain | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

                var dirF = Ui.F(8.25f, false);
                string dir = Path.GetDirectoryName(gamePath);
                // Measure with the paint Graphics – creating a separate one during OnPaint is wasteful.
                string shownDir = Ui.TruncMiddle(g, dir ?? "", dirF, textMaxW);
                TextRenderer.DrawText(g, shownDir, dirF, new Point(textX, Ui.S(40)), Ui.MutedC, plain);

                // "Change" pill, top-right
                Ui.FillRound(g, changeRect, Ui.S(8), overChange ? Ui.Tint(Ui.Surface2, Ui.Accent, 0.18) : Ui.Surface2);
                Ui.StrokeRound(g, changeRect, Ui.S(8), overChange ? Ui.Alpha(Ui.Accent, 170) : Ui.BorderC, 1f);
                TextRenderer.DrawText(g, "Change", Ui.F(8.25f, true), changeRect, overChange ? Ui.TextC : Ui.MutedC,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                // chips row
                int cx = textX; int cy = Height - Ui.S(36);
                if (!string.IsNullOrEmpty(archChip))
                    Ui.DrawChip(g, ref cx, cy, Ui.S(22), archChip, Ui.Accent2, Ui.Tint(Ui.Surface2, Ui.Accent, 0.20), null);
                if (!string.IsNullOrEmpty(sizeChip))
                    Ui.DrawChip(g, ref cx, cy, Ui.S(22), sizeChip, Ui.MutedC, Ui.Surface2, Ui.BorderC);
                if (!string.IsNullOrEmpty(apiChip))
                {
                    var col = apiState == 1 ? Ui.OkC : Ui.WarnC;
                    Ui.DrawChip(g, ref cx, cy, Ui.S(22), apiChip, col, Ui.Tint(Ui.Surface2, col, 0.12), Ui.Tint(Ui.Surface2, col, 0.28));
                }
            }

            if (!Enabled)
                using (var b = new SolidBrush(Color.FromArgb(150, Ui.Bg.R, Ui.Bg.G, Ui.Bg.B))) g.FillRectangle(b, ClientRectangle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }
            base.Dispose(disposing);
        }
    }

    // ─────────────────────────────────────────────── toggle

    public class Toggle : CheckBox
    {
        bool hover = false, press = false;
        readonly Anim knob, hoverAnim;
        public Toggle(string label, bool initial)
        {
            knob = new Anim(this, 130f); knob.Snap(initial ? 1 : 0);
            hoverAnim = new Anim(this, 100f);
            Text = label; Checked = initial;
            AccessibleName = label; AutoSize = false; TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Ui.Surface; // match the card so no black/unpainted area shows behind the pill
            Cursor = Cursors.Hand; Height = 24;
        }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; if (!Enabled) hoverAnim.Target = 0; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { if (Enabled && !press) { hover = true; hoverAnim.Target = 1; } base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; press = false; hoverAnim.Target = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) { press = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { press = false; hover = Enabled && ClientRectangle.Contains(e.Location); hoverAnim.Target = hover ? 1 : 0; Invalidate(); }
            base.OnMouseUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { press = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { press = false; Invalidate(); base.OnLostFocus(e); }
        // The native BUTTON window draws straight onto the screen when it gains/loses focus, is enabled or
        // disabled, or its check, highlight or focus-cue state changes - outside WM_PAINT - and left a stray
        // line across the top of the toggles after a patch run. Repaint over it whenever one goes through.
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            switch (m.Msg)
            {
                case 0x0007: case 0x0008:   // WM_SETFOCUS / WM_KILLFOCUS (native XOR focus rectangle)
                case 0x000A:                // WM_ENABLE
                case 0x00F1: case 0x00F3:   // BM_SETCHECK / BM_SETSTATE
                case 0x0128:                // WM_UPDATEUISTATE
                    Invalidate(); break;
            }
        }
        protected override void OnCheckedChanged(EventArgs e)
        {
            // Slide only when the user can see it; a toggle set while hidden or before first paint just jumps.
            if (knob != null) { if (Visible && IsHandleCreated) knob.Target = Checked ? 1 : 0; else knob.Snap(Checked ? 1 : 0); }
            Invalidate(); base.OnCheckedChanged(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.Surface)) g.FillRectangle(b, ClientRectangle); // no black/unpainted area behind the pill
            int pillH = Ui.S(20), pillW = Ui.S(36);
            var pill = new Rectangle(0, Height / 2 - pillH / 2, pillW, pillH);
            float on = knob.Eased, hv = hoverAnim.Eased;

            Color offFill = Ui.Lerp(Ui.Surface2, Ui.Tint(Ui.Surface2, Color.White, 0.06), hv);
            Color offBorder = Ui.Lerp(Ui.Tint(Ui.BorderC, Color.White, 0.05), Ui.Tint(Ui.BorderC, Ui.Accent, 0.45), hv);
            Color onFill = Ui.Lerp(Ui.Accent, Ui.AccentHi, hv);
            Color trackFill = Ui.Lerp(offFill, onFill, on);
            Color trackBorder = Ui.Lerp(offBorder, onFill, on);
            if (!Enabled)
            {
                trackFill = Ui.Tint(trackFill, Ui.Bg, Checked ? 0.55 : 0.3);
                trackBorder = Ui.Tint(trackBorder, Ui.Bg, Checked ? 0.6 : 0.3);
            }

            Ui.FillRound(g, pill, pillH / 2, trackFill);
            Ui.StrokeRound(g, pill, pillH / 2, trackBorder, 1f);
            if (press && Enabled) Ui.FillRound(g, pill, pillH / 2, Color.FromArgb(40, 0, 0, 0));
            int knobD = pillH - Ui.S(6);
            float kx = pill.X + Ui.S(3) + (pill.Width - knobD - Ui.S(6)) * on;
            var knobR = new RectangleF(kx, pill.Y + Ui.S(3), knobD, knobD);
            if (Enabled) using (var b = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillEllipse(b, knobR.X, knobR.Y + 1, knobR.Width, knobR.Height);
            using (var b = new SolidBrush(Enabled ? Ui.Lerp(Ui.Tint(Color.White, Ui.MutedC, 0.25), Color.White, on) : Ui.KnobOffC)) g.FillEllipse(b, knobR);

            TextRenderer.DrawText(g, Text, Ui.F(8.75f, false), new Rectangle(pill.Right + Ui.S(12), 0, Math.Max(0, Width - pill.Right - Ui.S(12)), Height),
                Enabled ? Ui.TextC : Ui.DisabledC, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) Ui.StrokeRound(g, Rectangle.Inflate(pill, Ui.S(2), Ui.S(2)), pillH / 2 + Ui.S(2), Ui.Alpha(Ui.Accent2, 200), 1.5f);
        }
    }

    // ─────────────────────────────────────────────── gradient button

    public class GradientButton : Button
    {
        public enum BtnKind { Primary, Cancel, Success, Secondary }
        BtnKind kind = BtnKind.Primary;
        public float TextSize = 10f;
        public int CornerRadius = 10;
        public BtnKind Kind { get { return kind; } set { if (kind != value) { kind = value; Invalidate(); } } }
        bool press;
        readonly Anim hoverAnim;
        public GradientButton(string text)
        {
            hoverAnim = new Anim(this, 110f);
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnMouseLeave(EventArgs e) { press = false; hoverAnim.Target = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseEnter(EventArgs e) { hoverAnim.Target = 1; base.OnMouseEnter(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) { press = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { press = false; Invalidate(); } base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { press = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.S(CornerRadius);
            float hv = Enabled ? hoverAnim.Eased : 0f;
            Color fill1, fill2, txt;
            if (!Enabled) { fill1 = fill2 = Ui.Surface2; txt = Ui.DisabledC; }
            else if (Kind == BtnKind.Cancel) { fill1 = Ui.CancelA; fill2 = Ui.CancelB; txt = Color.White; }
            else if (Kind == BtnKind.Success) { fill1 = Ui.SuccessA; fill2 = Ui.SuccessB; txt = Color.White; }
            else if (Kind == BtnKind.Secondary) { fill1 = fill2 = Ui.Lerp(Ui.Surface, Ui.Surface2, 0.6f + 0.4f * hv); txt = Ui.TextC; }
            else { fill1 = Ui.AccentHi; fill2 = Ui.AccentLo; txt = Color.White; }

            if (Enabled && Kind != BtnKind.Secondary)
            {
                // brighten on hover, deepen on press
                fill1 = Ui.Tint(fill1, Color.White, 0.10 * hv);
                fill2 = Ui.Tint(fill2, Color.White, 0.10 * hv);
                if (press) { fill1 = Ui.Tint(fill1, Color.Black, 0.14); fill2 = Ui.Tint(fill2, Color.Black, 0.14); }
            }
            else if (Enabled && press) { fill1 = fill2 = Ui.Tint(fill1, Color.Black, 0.2); }

            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), fill1, fill2, 90f)) using (var p = Ui.RoundPath(rect, rad)) g.FillPath(lg, p);

            if (Kind == BtnKind.Secondary || !Enabled)
                Ui.StrokeRound(g, rect, rad, Enabled ? Ui.Lerp(Ui.Tint(Ui.BorderC, Color.White, 0.06), Ui.Alpha(Ui.Accent, 200), hv) : Ui.BorderC, 1f);
            else
            {
                // glossy top edge + faint outline so the coloured fill reads as a raised surface
                Ui.StrokeRound(g, rect, rad, Color.FromArgb(40, 255, 255, 255), 1f);
                using (var p = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawLine(p, rect.X + rad, rect.Y + 1, rect.Right - rad, rect.Y + 1);
            }
            if (Focused && Enabled && ShowFocusCues)
                Ui.StrokeRound(g, Rectangle.Inflate(rect, -Ui.S(3), -Ui.S(3)), rad - Ui.S(3), Color.FromArgb(150, 255, 255, 255), 1.2f);

            var textRect = rect; if (press && Enabled) textRect.Offset(0, 1);
            TextRenderer.DrawText(g, Text, Ui.F(TextSize, true), textRect, txt,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    // ─────────────────────────────────────────────── progress bar

    public class ProgressBarLite : Control
    {
        int target = 0;
        double shown = 0;
        Timer timer;
        public ProgressBarLite()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            timer = new Timer(); timer.Interval = 16; timer.Tick += delegate
            {
                if (Math.Abs(shown - target) < 0.5) { shown = target; timer.Stop(); }
                else shown += (target - shown) * 0.25;
                Invalidate();
            };
        }
        public void SetValue(int v) { if (IsDisposed || Disposing || timer == null) return; target = Math.Max(0, Math.Min(100, v)); timer.Start(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            base.Dispose(disposing);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            // Idle (0%) shows nothing at all - a permanently empty track just read as a stray divider line.
            if (shown < 0.5 && target == 0) return;
            var r = ClientRectangle;
            Ui.FillRound(g, r, r.Height / 2, Ui.Surface2);
            int w = (int)(r.Width * shown / 100.0);
            if (w > r.Height / 2 + 1)
            {
                var fr = new Rectangle(r.X, r.Y, w, r.Height);
                using (var lg = new LinearGradientBrush(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), r.Height), Ui.AccentLo, Ui.Accent2, 0f)) using (var p = Ui.RoundPath(fr, r.Height / 2)) g.FillPath(lg, p);
            }
        }
    }

    // ─────────────────────────────────────────────── banner

    public class Banner : Control
    {
        public enum BannerKind { Success, Error, Warn }
        public         BannerKind Kind = BannerKind.Success;
        string message = "";
        public string MessageText { get { return message; } }
        public event Action<int> ActionClicked;
        readonly List<GradientButton> actionButtons = new List<GradientButton>();
        readonly ToolTip tip = new ToolTip { AutoPopDelay = 15000 };

        /// <summary>Action label to draw as the filled (primary) button, e.g. "Play game". Every other
        /// action is drawn as a quiet secondary button.</summary>
        public string PrimaryAction;

        public Banner()
        {
            Visible = false; TabStop = false;
            BackColor = Ui.Surface;   // ambient colour for the action buttons' corners
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        public void Show(BannerKind kind, string msg, params string[] buttons)
        {
            ClearActions();
            Kind = kind; message = msg ?? ""; AccessibleName = message;
            tip.SetToolTip(this, message);   // the text may be cut short; the full message is on hover
            foreach (var text in buttons ?? new string[0])
            {
                int index = actionButtons.Count;
                var button = new GradientButton(text ?? "");
                button.AccessibleName = text ?? "";
                button.Kind = text == PrimaryAction ? GradientButton.BtnKind.Primary : GradientButton.BtnKind.Secondary;
                button.TextSize = 8.5f; button.CornerRadius = 8;
                button.TabIndex = index;
                button.Click += delegate { var h = ActionClicked; if (h != null) h(index); };
                actionButtons.Add(button); Controls.Add(button);
            }
            LayoutActions(); Visible = true; Invalidate();
        }
        void ClearActions()
        {
            foreach (var button in actionButtons) { Controls.Remove(button); button.Dispose(); }
            actionButtons.Clear();
        }
        public void HideBanner() { Visible = false; ClearActions(); Invalidate(); }
        protected override void OnResize(EventArgs e) { LayoutActions(); base.OnResize(e); }
        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
        void LayoutActions()
        {
            int ax = Width - Ui.S(14);
            int bh = Ui.S(30);
            for (int i = actionButtons.Count - 1; i >= 0; i--)
            {
                var button = actionButtons[i];
                int bw = TextRenderer.MeasureText(button.Text, Ui.F(button.TextSize, true), Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Ui.S(28);
                ax -= bw;
                button.Bounds = new Rectangle(ax, Height / 2 - bh / 2, bw, bh);
                ax -= Ui.S(8);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color fg = Kind == BannerKind.Success ? Ui.OkC : Kind == BannerKind.Error ? Ui.ErrC : Ui.WarnC;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Ui.Bg)) g.FillRectangle(b, ClientRectangle);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.S(12);

            // Same surface as the other cards; the status colour lives only in a slim accent and the badge,
            // so a result reads as part of the window instead of a pasted-on coloured slab.
            Ui.FillRound(g, rect, rad, Ui.Surface);
            Ui.StrokeRound(g, rect, rad, Ui.BorderC, 1f);
            using (var p = new Pen(Color.FromArgb(14, 255, 255, 255), 1f)) g.DrawLine(p, rad, 1, Width - rad - 1, 1);
            var stripe = new Rectangle(Ui.S(1), Ui.S(12), Ui.S(3), Math.Max(1, rect.Height - Ui.S(24)));
            Ui.FillRound(g, stripe, Ui.S(1), fg);

            // status badge: tinted circle with a vector mark
            int bdg = Ui.S(28);
            var badge = new Rectangle(Ui.S(18), rect.Height / 2 - bdg / 2, bdg, bdg);
            using (var b = new SolidBrush(Ui.Alpha(fg, 34))) g.FillEllipse(b, badge);
            using (var pen = new Pen(fg, Ui.S(1.8f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                float cx = badge.X + bdg / 2f, cy = badge.Y + bdg / 2f, u = Ui.S(4.5f);
                if (Kind == BannerKind.Success) g.DrawLines(pen, new[] { new PointF(cx - u, cy), new PointF(cx - u * 0.25f, cy + u * 0.8f), new PointF(cx + u, cy - u * 0.8f) });
                else if (Kind == BannerKind.Error) { g.DrawLine(pen, cx - u * 0.8f, cy - u * 0.8f, cx + u * 0.8f, cy + u * 0.8f); g.DrawLine(pen, cx - u * 0.8f, cy + u * 0.8f, cx + u * 0.8f, cy - u * 0.8f); }
                else { g.DrawLine(pen, cx, cy - u, cx, cy + u * 0.25f); using (var b = new SolidBrush(fg)) g.FillEllipse(b, cx - Ui.S(1.2f), cy + u * 0.75f, Ui.S(2.4f), Ui.S(2.4f)); }
            }

            int textX = badge.Right + Ui.S(14);
            int textRight = actionButtons.Count > 0 ? actionButtons[0].Left - Ui.S(16) : Width - Ui.S(16);
            int textMaxW = Math.Max(0, textRight - textX);
            if (textMaxW == 0) return;

            const TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var head = Ui.F(8.75f, true); var body = Ui.F(8.25f, false);
            int nl = message.IndexOf('\n');
            if (nl >= 0)
            {
                // "headline\ndetail": headline on top, the rest muted underneath
                string l1 = message.Substring(0, nl), l2 = message.Substring(nl + 1).Replace("\n", "  ");
                int top = rect.Height / 2 - Ui.S(18);
                TextRenderer.DrawText(g, l1, head, new Rectangle(textX, top, textMaxW, Ui.S(18)), Ui.TextC, one);
                TextRenderer.DrawText(g, l2, body, new Rectangle(textX, top + Ui.S(19), textMaxW, Ui.S(18)), Ui.MutedC, one);
            }
            else
            {
                // one long sentence: wrap onto two lines rather than cutting it off after a few words
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
                var sz = TextRenderer.MeasureText(g, message, head, new Size(textMaxW, int.MaxValue), flags);
                int h = Math.Min(sz.Height, Ui.S(38));
                TextRenderer.DrawText(g, message, head, new Rectangle(textX, rect.Height / 2 - h / 2, textMaxW, h), Ui.TextC, flags);
            }
        }
    }

    // ─────────────────────────────────────────────── log view

    public class LogView : RichTextBox
    {
        // Long batch runs used to grow this control without bound (memory + UI lag). Cap the line count
        // and drop the oldest lines in batches when the cap is exceeded.
        const int MaxLines = 1500;
        bool trimming;

        /// <summary>Running count of the lines in the buffer. Maintaining it is what turns trimming from a
        /// full-text scan per append into an O(1) check.</summary>
        int lineCount;

        public LogView()
        {
            ReadOnly = true; BorderStyle = System.Windows.Forms.BorderStyle.None;
            BackColor = Ui.Surface; ForeColor = Ui.TextC;
            Font = Ui.F("Consolas", 8.75f, false);
            HideSelection = false;
            NativeMethods.UseDarkScrollbars(this);
        }
        public void AppendLine(string msg) { AppendLine(msg, LogLevel.Info); }

        public void AppendLine(string msg, LogLevel level)
        {
            Color c;
            switch (level)
            {
                case LogLevel.Ok: c = Ui.OkC; break;
                case LogLevel.Warn: c = Ui.WarnC; break;
                case LogLevel.Error: c = Ui.ErrC; break;
                case LogLevel.Dim: c = Ui.DimC; break;
                default: c = Ui.LogTextC; break;
            }
            string text = msg + Environment.NewLine;
            lineCount += CountLines(text);
            SelectionStart = TextLength;
            SelectionLength = 0;
            SelectionColor = c;
            AppendText(text);
            TrimLines();
            SelectionColor = ForeColor;
            SelectionStart = TextLength;
            SelectionLength = 0;
            ScrollToCaret();
        }

        static int CountLines(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\r') { n++; if (i + 1 < s.Length && s[i + 1] == '\n') i++; }
                else if (s[i] == '\n') n++;
            }
            return n;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            if (trimming) return;
            TrimLines();
            base.OnTextChanged(e);
        }

        void TrimLines()
        {
            if (trimming) return;
            // The buffer can be emptied from outside; trust the text over the counter when they disagree.
            if (TextLength == 0) { lineCount = 0; return; }
            if (lineCount <= MaxLines) return;

            // Drop the oldest lines down to half the cap, in one pass. GetFirstCharIndexFromLine is O(1),
            // so there is no need to walk the whole buffer looking for line breaks - which this used to do
            // twice per appended line (AppendLine called it, and so did OnTextChanged).
            int drop = lineCount - MaxLines / 2;
            int cut = GetFirstCharIndexFromLine(drop);
            if (cut <= 0) return;
            int start = SelectionStart, end = start + SelectionLength;
            trimming = true;
            bool wasReadOnly = ReadOnly;
            try
            {
                ReadOnly = false;
                Select(0, cut);
                SelectedText = "";
                int newStart = Math.Max(0, start - cut);
                Select(newStart, Math.Max(0, end - cut - newStart));
                lineCount -= drop;
            }
            finally { trimming = false; ReadOnly = wasReadOnly; }
        }
    }
}
