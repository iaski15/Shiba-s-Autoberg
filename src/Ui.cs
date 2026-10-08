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

        // Shiba palette: cream background, white cards, fur orange as the one accent, dark-brown "ink" text.
        public static readonly Color Bg = FromHex("#FFF6EA");          // cream
        public static readonly Color Surface = FromHex("#FFFFFF");     // cards
        public static readonly Color Surface2 = FromHex("#FBEBD8");    // inputs, secondary buttons, off toggles
        public static readonly Color Inset = FromHex("#FFFBF5");       // text fields
        public static readonly Color BorderC = FromHex("#EFD9BF");
        public static readonly Color TextC = FromHex("#3A2418");       // the shiba's outline colour
        public static readonly Color MutedC = FromHex("#9B7A60");
        public static readonly Color Accent = FromHex("#E8924A");      // fur
        public static readonly Color Accent2 = FromHex("#C4652A");     // deeper fur: links, accent text
        public static readonly Color FurDark = FromHex("#D27A34");     // sidebar shading
        public static readonly Color CreamText = FromHex("#FFF2DF");   // text on fur
        public static readonly Color OkC = FromHex("#3F9A58");
        public static readonly Color WarnC = FromHex("#D48A12");
        public static readonly Color ErrC = FromHex("#CF4A3F");

        public static readonly Color DisabledC = FromHex("#C8B39E");   // disabled label / toggle text
        public static readonly Color KnobOffC = FromHex("#DCCBB8");    // disabled toggle knob
        public static readonly Color DimC = FromHex("#B59A82");        // dim log lines
        public static readonly Color LogTextC = FromHex("#5A3E2B");    // normal log lines

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

        /// <summary>Scales hand-positioned paint geometry by the display DPI (<see cref="Dpi"/>). The forms use
        /// <see cref="AutoScaleMode.Dpi"/> for control bounds; fonts are in points and need no help.</summary>
        public static int S(int px) { return Dpi.S(px); }
        public static float S(float px) { return Dpi.S(px); }

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
        [ThreadStatic] static Rectangle scratchRect;
        [ThreadStatic] static int scratchRad;

        static GraphicsPath ScratchPath(Rectangle r, int rad)
        {
            if (scratchPath != null && scratchRect == r && scratchRad == rad) return scratchPath;
            if (scratchPath != null) scratchPath.Dispose();
            scratchPath = RoundPath(r, rad);
            scratchRect = r; scratchRad = rad;
            return scratchPath;
        }

        public static string TruncMiddle(Graphics g, string s, Font f, int maxW)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (maxW <= 0) return "";
            Func<string, int> width = t => TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            if (width(s) <= maxW) return s;
            string mid = "…";
            if (width(mid) > maxW) return "";
            int lo = 1, hi = s.Length - 1;
            string best = mid;
            while (lo <= hi)
            {
                int take = lo + (hi - lo) / 2;
                int left = (take + 1) / 2, right = take / 2;
                var t = s.Substring(0, left) + mid + s.Substring(s.Length - right);
                if (width(t) <= maxW) { best = t; lo = take + 1; }
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
            TextRenderer.DrawText(g, text, F(7.75f, true), r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            x += w + S(8);
        }

        public static Color Tint(Color basec, Color tint, double amt)
        {
            return Color.FromArgb(
                (int)(basec.R * (1 - amt) + tint.R * amt),
                (int)(basec.G * (1 - amt) + tint.G * amt),
                (int)(basec.B * (1 - amt) + tint.B * amt));
        }
    }

    // ─────────────────────────────────────────────── shiba

    public enum ShibaMood { Neutral, Happy, Sad, Sleepy }

    /// <summary>The mascot: a chibi shiba head drawn as vectors in a 100x100 design space, so one drawing
    /// serves the 16px title-bar logo, the drop zone and the app icon (make_icon.ps1 calls this too).</summary>
    public static class Shiba
    {
        static readonly Color Fur = Ui.FromHex("#E8924A");
        static readonly Color Cream = Ui.FromHex("#FFF2DF");
        static readonly Color Line = Ui.FromHex("#3A2418");
        static readonly Color Ink = Ui.FromHex("#2A1A12");
        static readonly Color Blush = Color.FromArgb(150, 0xFF, 0x8F, 0xA3);
        static readonly Color Tongue = Ui.FromHex("#FF7C8C");

        public static void Draw(Graphics g, RectangleF box, ShibaMood mood)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Math.Min(box.Width, box.Height) / 100f;
            g.TranslateTransform(box.X + (box.Width - 100 * k) / 2, box.Y + (box.Height - 100 * k) / 2);
            g.ScaleTransform(k, k);
            float lw = 3.2f;   // outline width in design units; scales with the drawing
            using (var line = new Pen(Line, lw) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var fur = new SolidBrush(Fur))
            using (var cream = new SolidBrush(Cream))
            using (var ink = new SolidBrush(Ink))
            {
                // ears (behind the head)
                foreach (bool left in new[] { true, false })
                {
                    PointF[] ear = Mirror(left, new PointF(13, 44), new PointF(19, 9), new PointF(45, 27));
                    PointF[] inner = Mirror(left, new PointF(20, 37), new PointF(23, 18), new PointF(36, 29));
                    using (var p = new GraphicsPath()) { p.AddClosedCurve(ear, 0.25f); g.FillPath(fur, p); g.DrawPath(line, p); }
                    using (var p = new GraphicsPath()) { p.AddClosedCurve(inner, 0.3f); g.FillPath(cream, p); }
                }
                // head: wide chibi oval
                var head = new RectangleF(7, 22, 86, 70);
                g.FillEllipse(fur, head);
                // cream muzzle/cheeks and the two "eyebrow" spots shibas have
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(head);
                    var outer = g.Clip;
                    g.SetClip(clip, CombineMode.Intersect);   // keep the caller's clip, add the head's
                    g.FillEllipse(cream, 17, 54, 66, 37);
                    g.Clip = outer;
                }
                g.FillEllipse(cream, 28, 41, 10, 6);
                g.FillEllipse(cream, 62, 41, 10, 6);
                g.DrawEllipse(line, head);

                // blush
                using (var b = new SolidBrush(Blush)) { g.FillEllipse(b, 16, 63, 13, 7); g.FillEllipse(b, 71, 63, 13, 7); }

                // eyes
                switch (mood)
                {
                    case ShibaMood.Happy:
                        g.DrawArc(line, 27, 51, 12, 10, 200, 140);
                        g.DrawArc(line, 61, 51, 12, 10, 200, 140);
                        break;
                    case ShibaMood.Sleepy:
                        g.DrawArc(line, 27, 50, 12, 9, 20, 140);
                        g.DrawArc(line, 61, 50, 12, 9, 20, 140);
                        break;
                    default:
                        g.FillEllipse(ink, 29, 50, 9, 11);
                        g.FillEllipse(ink, 62, 50, 9, 11);
                        g.FillEllipse(Brushes.White, 31.5f, 52, 3.5f, 3.5f);
                        g.FillEllipse(Brushes.White, 64.5f, 52, 3.5f, 3.5f);
                        if (mood == ShibaMood.Sad)
                        {
                            g.DrawLine(line, 26, 45, 37, 42);   // worried brows
                            g.DrawLine(line, 74, 45, 63, 42);
                        }
                        break;
                }

                // nose + mouth
                using (var nose = new GraphicsPath())
                {
                    nose.AddClosedCurve(new[] { new PointF(45, 63), new PointF(55, 63), new PointF(50, 68.5f) }, 0.45f);
                    g.FillPath(ink, nose);
                }
                if (mood == ShibaMood.Happy)
                {
                    using (var mouth = new GraphicsPath())
                    {
                        mouth.AddArc(42, 64, 16, 16, 0, 180);
                        mouth.CloseFigure();
                        using (var t = new SolidBrush(Tongue)) g.FillPath(t, mouth);
                        g.DrawPath(line, mouth);
                    }
                }
                else if (mood == ShibaMood.Sad)
                {
                    g.DrawArc(line, 43, 72, 14, 9, 200, 140);
                }
                else
                {
                    g.DrawArc(line, 41, 64, 9, 8, 0, 180);   // the little "w"
                    g.DrawArc(line, 50, 64, 9, 8, 0, 180);
                }
                if (mood == ShibaMood.Sleepy)
                    using (var f = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var zb = new SolidBrush(Ui.Accent2))
                        g.DrawString("z", f, zb, 82, 14);

            }
            g.Restore(state);
        }

        static PointF[] Mirror(bool left, params PointF[] pts)
        {
            if (left) return pts;
            var m = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++) m[i] = new PointF(100 - pts[i].X, pts[i].Y);
            return m;
        }

        /// <summary>Square transparent bitmap of the mascot - for the window icon and make_icon.ps1.</summary>
        public static Bitmap Render(int size, ShibaMood mood)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, new RectangleF(0, 0, size, size), mood);
            }
            return bmp;
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

    // ─────────────────────────────────────────────── window base

    /// <summary>Borderless, DPI-scaled window with a drop shadow and Windows 11 rounded corners - the chrome
    /// every Shibaberg window shares.</summary>
    public class ShibaForm : Form
    {
        public ShibaForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            // The process is PerMonitorV2 DPI-aware: Dpi auto-scaling grows the control bounds with the fonts.
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Ui.Bg;
            KeyPreview = true;
            DoubleBuffered = true;
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

        /// <summary>Runs <paramref name="a"/> on the UI thread from a worker. Drops it once the form is closed:
        /// BeginInvoke would otherwise throw on the worker thread, where nobody observes it.</summary>
        protected void UiInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) a(); }); }
            catch (InvalidOperationException) { }
        }
    }

    // ─────────────────────────────────────────────── card panel

    public class AppCard : Panel
    {
        public int Radius = 12;
        public AppCard() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Ui.Bg; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int rad = Ui.S(Radius);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, rad, Ui.Surface);
            Ui.StrokeRound(g, r, rad, Ui.BorderC, 1f);
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
        /// <summary>Logo + name on the left. Off in the main window, whose sidebar carries the brand.</summary>
        public bool ShowBrand = true;
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
            minimizeButton.Click += delegate { Minimize(); };
            closeButton.Click += delegate { var f = FindForm(); if (f != null) f.Close(); };
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
            if (e.Button == MouseButtons.Left && !closeButton.Bounds.Contains(e.Location) && !minimizeButton.Bounds.Contains(e.Location)) Minimize();
            base.OnMouseDoubleClick(e);
        }
        void Minimize() { var f = FindForm(); if (f != null) f.WindowState = FormWindowState.Minimized; }
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

            if (!ShowBrand) { using (var p = new Pen(Ui.BorderC, 1f)) g.DrawLine(p, 0, Height - 1, Width, Height - 1); return; }
            Shiba.Draw(g, new RectangleF(Ui.S(16), Ui.S(9), Ui.S(28), Ui.S(28)), ShibaMood.Neutral);

            // title + version pill
            var tf = Ui.F(9.75f, true);
            var tsz = TextRenderer.MeasureText(g, BuildInfo.AppName, tf, Size.Empty, TextFormatFlags.NoPadding);
            int tx = Ui.S(52), ty = Height / 2 - tsz.Height / 2;
            TextRenderer.DrawText(g, BuildInfo.AppName, tf, new Point(tx, ty), Ui.TextC, TextFormatFlags.NoPadding);
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

        /// <summary>Explorer-themed native scrollbars instead of the classic ones. No-op where unavailable.</summary>
        internal static void UseExplorerScrollbars(Control c)
        {
            if (c.IsHandleCreated) try { SetWindowTheme(c.Handle, "Explorer", null); } catch { }
            else c.HandleCreated += delegate { try { SetWindowTheme(c.Handle, "Explorer", null); } catch { } };
        }
    }

    // ─────────────────────────────────────────────── drop zone

    public class DropZone : Control
    {
        public event Action<string> FileChosen;
        string gamePath = "";
        string archChip = "", sizeChip = "", apiChip = "", warnChip = "";
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

        public void UpdateAnalysis(string arch, string size, string api, int state, string warning = null)
        {
            archChip = arch ?? ""; sizeChip = size ?? ""; apiChip = api ?? ""; apiState = state;
            if (warning != null) warnChip = warning;
            Invalidate();
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
            bool oc = gamePath.Length > 0 && ChangeLinkBounds().Contains(e.Location);
            if (oc != overChange) { overChange = oc; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { overChange = false; hoverAnim.Target = 0; Invalidate(); base.OnMouseLeave(e); }

        /// <summary>Where the Change link sits; computed, not cached in OnPaint, so hit-testing works before the first paint.</summary>
        Rectangle ChangeLinkBounds()
        {
            int w = TextRenderer.MeasureText("Change", Ui.F(8.25f, true), Size.Empty, TextFormatFlags.NoPadding).Width + Ui.S(24);
            return new Rectangle(Width - Ui.S(16) - w, Ui.S(16), w, Ui.S(26));
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

                // the shiba perks up (and hops a little) while something is hovered or dragged over it
                float isz = Ui.S(50f), hop = Ui.S(3f) * hot;
                Shiba.Draw(g, new RectangleF(Width / 2f - isz / 2, Ui.S(10f) - hop, isz, isz), hot > 0.3f ? ShibaMood.Happy : ShibaMood.Neutral);

                var l1 = dragOver ? "Release to fetch this game!" : "Drop the game's .exe here";
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
                if (!string.IsNullOrEmpty(warnChip))
                    Ui.DrawChip(g, ref cx, cy, Ui.S(22), warnChip, Ui.ErrC, Ui.Tint(Ui.Surface2, Ui.ErrC, 0.12), Ui.Tint(Ui.Surface2, Ui.ErrC, 0.30));
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

    /// <summary>On/off switch. A plain Control, not a CheckBox: the native BUTTON window under a CheckBox draws
    /// its own frame and focus marks outside WM_PAINT, which left stray lines over the toggles.</summary>
    public class Toggle : Control
    {
        bool isChecked, press;
        readonly Anim knob, hoverAnim;
        public event EventHandler CheckedChanged;

        public Toggle(string label, bool initial)
        {
            knob = new Anim(this, 130f); knob.Snap(initial ? 1 : 0);
            hoverAnim = new Anim(this, 100f);
            Text = label; isChecked = initial;
            AccessibleName = label; AccessibleRole = AccessibleRole.CheckButton; TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // fast clicks each toggle, none become a double-click
            BackColor = Ui.Surface;
            Cursor = Cursors.Hand; Height = 24;
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                // Slide only when the user can see it; a toggle set while hidden or before first paint just jumps.
                if (Visible && IsHandleCreated) knob.Target = value ? 1 : 0; else knob.Snap(value ? 1 : 0);
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e) { if (Enabled) { Focus(); Checked = !Checked; } base.OnClick(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (Enabled && e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyUp(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override AccessibleObject CreateAccessibilityInstance() { return new ToggleAccessible(this); }

        sealed class ToggleAccessible : ControlAccessibleObject
        {
            readonly Toggle toggle;
            public ToggleAccessible(Toggle t) : base(t) { toggle = t; }
            public override AccessibleStates State { get { return base.State | (toggle.Checked ? AccessibleStates.Checked : AccessibleStates.None); } }
            public override string DefaultAction { get { return toggle.Checked ? "Uncheck" : "Check"; } }
            public override void DoDefaultAction() { if (toggle.Enabled) toggle.Checked = !toggle.Checked; }
        }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; if (!Enabled) hoverAnim.Target = 0; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { if (Enabled && !press) hoverAnim.Target = 1; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { press = false; hoverAnim.Target = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) { press = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { press = false; hoverAnim.Target = Enabled && ClientRectangle.Contains(e.Location) ? 1 : 0; Invalidate(); }
            base.OnMouseUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { press = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { press = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.Surface)) g.FillRectangle(b, ClientRectangle); // no black/unpainted area behind the pill
            int pillH = Ui.S(20), pillW = Ui.S(36);
            var pill = new Rectangle(0, Height / 2 - pillH / 2, pillW, pillH);
            float on = knob.Eased, hv = hoverAnim.Eased;

            Color offFill = Ui.Lerp(Ui.Surface2, Ui.Tint(Ui.Surface2, Ui.Accent, 0.12), hv);
            Color offBorder = Ui.Lerp(Ui.BorderC, Ui.Accent, 0.5f * hv);
            Color onFill = Ui.Tint(Ui.Accent, Color.White, 0.12 * hv);
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
            using (var b = new SolidBrush(Enabled ? Color.White : Ui.KnobOffC)) g.FillEllipse(b, knobR);
            if (Enabled) using (var p = new Pen(Ui.Alpha(Ui.TextC, 40), 1f)) g.DrawEllipse(p, knobR);

            TextRenderer.DrawText(g, Text, Ui.F(8.75f, false), new Rectangle(pill.Right + Ui.S(12), 0, Math.Max(0, Width - pill.Right - Ui.S(12)), Height),
                Enabled ? Ui.TextC : Ui.DisabledC, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) Ui.StrokeRound(g, Rectangle.Inflate(pill, Ui.S(2), Ui.S(2)), pillH / 2 + Ui.S(2), Ui.Alpha(Ui.Accent2, 200), 1.5f);
        }
    }

    // ─────────────────────────────────────────────── button

    public class FlatButton : Button
    {
        public enum BtnKind { Primary, Cancel, Secondary }
        BtnKind kind = BtnKind.Primary;
        public float TextSize = 10f;
        public int CornerRadius = 10;
        public BtnKind Kind { get { return kind; } set { if (kind != value) { kind = value; Invalidate(); } } }
        bool press;
        readonly Anim hoverAnim;
        public FlatButton(string text)
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
            Color fill, txt;
            if (!Enabled) { fill = Ui.Surface2; txt = Ui.DisabledC; }
            else if (Kind == BtnKind.Cancel) { fill = Ui.ErrC; txt = Color.White; }
            else if (Kind == BtnKind.Secondary) { fill = Ui.Lerp(Ui.Surface, Ui.Surface2, 0.6f + 0.4f * hv); txt = Ui.TextC; }
            else { fill = Ui.Accent; txt = Color.White; }
            if (Enabled && Kind != BtnKind.Secondary) fill = Ui.Tint(fill, Color.White, 0.10 * hv);   // lighten on hover
            if (Enabled && press) fill = Ui.Tint(fill, Color.Black, 0.16);                             // deepen on press
            Ui.FillRound(g, rect, rad, fill);
            if (Kind == BtnKind.Secondary || !Enabled)
                Ui.StrokeRound(g, rect, rad, Enabled ? Ui.Lerp(Ui.BorderC, Ui.Accent, hv) : Ui.BorderC, 1f);
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
                Ui.FillRound(g, fr, r.Height / 2, Ui.Accent);
            }
        }
    }

    // ─────────────────────────────────────────────── banner

    public class Banner : Control
    {
        public enum BannerKind { Success, Error, Warn }
        public BannerKind Kind = BannerKind.Success;
        string message = "";
        public string MessageText { get { return message; } }
        public event Action<int> ActionClicked;
        readonly List<FlatButton> actionButtons = new List<FlatButton>();
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
                var button = new FlatButton(text ?? "");
                button.AccessibleName = text ?? "";
                button.Kind = text == PrimaryAction ? FlatButton.BtnKind.Primary : FlatButton.BtnKind.Secondary;
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
            var stripe = new Rectangle(Ui.S(1), Ui.S(12), Ui.S(3), Math.Max(1, rect.Height - Ui.S(24)));
            Ui.FillRound(g, stripe, Ui.S(1), fg);

            // the shiba reports the result: happy, worried or just curious
            int bdg = Ui.S(38);
            var badge = new Rectangle(Ui.S(14), rect.Height / 2 - bdg / 2, bdg, bdg);
            Shiba.Draw(g, badge, Kind == BannerKind.Success ? ShibaMood.Happy : Kind == BannerKind.Error ? ShibaMood.Sad : ShibaMood.Neutral);

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
            NativeMethods.UseExplorerScrollbars(this);
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
            AppendText(text);   // OnTextChanged trims
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

            // Drop the oldest lines down to half the cap, in one pass. This runs only once every ~750
            // appended lines, so one scan of the text here is cheap. GetFirstCharIndexFromLine is not usable:
            // with word wrap on it counts *display* lines, so long wrapped lines made it cut far less than
            // the counter assumed, the counter drifted low, and the buffer grew well past the cap.
            int drop = lineCount - MaxLines / 2;
            string all = Text;
            int cut = 0;
            for (int seen = 0; seen < drop; seen++)
            {
                int nl = all.IndexOf('\n', cut);
                if (nl < 0) break;
                cut = nl + 1;
            }
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
                lineCount = CountLines(Text);   // resynchronise with what is actually left
            }
            finally { trimming = false; ReadOnly = wasReadOnly; }
        }
    }
}
