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

        public static readonly Color Bg = FromHex("#0D0F14");
        public static readonly Color Surface = FromHex("#151A23");
        public static readonly Color Surface2 = FromHex("#1B2130");
        public static readonly Color Inset = FromHex("#0B0E13");
        public static readonly Color BorderC = FromHex("#262D3D");
        public static readonly Color TextC = FromHex("#E7EAF2");
        public static readonly Color MutedC = FromHex("#8B93A7");
        public static readonly Color Accent = FromHex("#7C5CFF");
        public static readonly Color Accent2 = FromHex("#4D9FFF");
        public static readonly Color OkC = FromHex("#34D399");
        public static readonly Color WarnC = FromHex("#FBBF24");
        public static readonly Color ErrC = FromHex("#F87171");

        static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
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
            using (var b = new SolidBrush(c)) using (var p = RoundPath(r, rad)) g.FillPath(b, p);
        }
        public static void StrokeRound(Graphics g, Rectangle r, int rad, Color c, float w)
        {
            using (var pen = new Pen(c, w)) using (var p = RoundPath(r, rad)) g.DrawPath(pen, p);
        }

        public static string TruncMiddle(Graphics g, string s, Font f, int maxW)
        {
            if (string.IsNullOrEmpty(s) || g.MeasureString(s, f).Width <= maxW) return s;
            string mid = "…";
            int a = 0, b = s.Length - 1;
            while (a < b && a < s.Length && b > 0)
            {
                var t = s.Substring(0, a + 1) + mid + s.Substring(b);
                if (g.MeasureString(t, f).Width > maxW) break;
                a++; if (a >= b) break;
                t = s.Substring(0, a + 1) + mid + s.Substring(b);
                if (g.MeasureString(t, f).Width > maxW) { b--; }
            }
            return s.Substring(0, Math.Max(a, 1)) + mid + s.Substring(Math.Max(b, 0));
        }

        public static void SpacedText(Graphics g, string text, Font f, Brush b, PointF pt, float spacing)
        {
            float x = pt.X;
            foreach (var ch in text)
            {
                g.DrawString(ch.ToString(), f, b, x, pt.Y, StringFormat.GenericTypographic);
                x += g.MeasureString(ch.ToString(), f, Point.Empty, StringFormat.GenericTypographic).Width + spacing;
            }
        }

        public static SizeF MeasureSpaced(Graphics g, string text, Font f, float spacing)
        {
            float w = 0; foreach (var ch in text) w += g.MeasureString(ch.ToString(), f, Point.Empty, StringFormat.GenericTypographic).Width + spacing;
            return new SizeF(w, g.MeasureString(text, f).Height);
        }

        public static void DrawChip(Graphics g, ref int x, int y, int h, string text, Color fore, Color back, Color? border)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sz = g.MeasureString(text, F(7.75f, true));
            int w = (int)Math.Ceiling(sz.Width) + 18;
            var r = new Rectangle(x, y, w, h);
            FillRound(g, r, h / 2, back);
            if (border.HasValue) StrokeRound(g, Rectangle.Inflate(r, 0, 0), h / 2, border.Value, 1f);
            TextRendererHelper(g, text, fore, r);
            x += w + 8;
        }

        static void TextRendererHelper(Graphics g, string text, Color fore, Rectangle r)
        {
            TextRenderer.DrawText(g, text, F(7.75f, true), r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static Color Tint(Color basec, Color tint, double amt)
        {
            return Color.FromArgb(
                (int)(basec.R * (1 - amt) + tint.R * amt),
                (int)(basec.G * (1 - amt) + tint.G * amt),
                (int)(basec.B * (1 - amt) + tint.B * amt));
        }
    }

    // ─────────────────────────────────────────────── card panel

    public class AppCard : Panel
    {
        public int Radius = 14;
        public bool ShowBorder = true;
        public AppCard() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Ui.Bg; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.FillRound(g, ClientRectangle, Radius, Ui.Surface);
            if (ShowBorder) Ui.StrokeRound(g, ClientRectangle, Radius, Ui.BorderC, 1f);
            base.OnPaint(e);
        }
    }

    // ─────────────────────────────────────────────── title bar

    public class TitleBar : Control
    {
        Rectangle minR, closeR;
        int hoverBtn = -1;
        public TitleBar()
        {
            Dock = DockStyle.Top; Height = 46;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Default;
        }
        protected override void OnResize(EventArgs e) { LayoutBtns(); base.OnResize(e); }
        void LayoutBtns()
        {
            closeR = new Rectangle(Width - 50, 8, 42, 30);
            minR = new Rectangle(Width - 96, 8, 42, 30);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverBtn = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = closeR.Contains(e.Location) ? 2 : minR.Contains(e.Location) ? 1 : 0;
            if (h != hoverBtn) { hoverBtn = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (hoverBtn == 0) DragWindow();
            base.OnMouseDown(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (!closeR.Contains(e.Location) && !minR.Contains(e.Location)) OnMinimizeClicked();
            base.OnMouseDoubleClick(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (closeR.Contains(e.Location)) OnCloseClicked();
            else if (minR.Contains(e.Location)) OnMinimizeClicked();
            base.OnMouseUp(e);
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

            // logo: gradient circle + play triangle
            var logoRect = new Rectangle(22, 13, 20, 20);
            using (var lg = new LinearGradientBrush(logoRect, Ui.Accent, Ui.Accent2, 45f)) using (var p = Ui.RoundPath(logoRect, 10)) g.FillPath(lg, p);
            var tri = new PointF[] { new PointF(29.5f, 18.5f), new PointF(29.5f, 27.5f), new PointF(37.5f, 23f) };
            using (var b = new SolidBrush(Color.White)) g.FillPolygon(b, tri);

            // title
            var tsz = Ui.MeasureSpaced(g, "GOLDBERG PATCHER", Ui.F(9, true), 1.6f);
            Ui.SpacedText(g, "GOLDBERG PATCHER", Ui.F(9, true), Brushes.White, new PointF(52, 15), 1.6f);
            TextRenderer.DrawText(g, "v0.3", Ui.F(7.75f, false), new Rectangle((int)(52 + tsz.Width + 10), 17, 60, 16), Ui.MutedC, TextFormatFlags.NoPadding);

            // buttons
            if (hoverBtn == 1) { Ui.FillRound(g, minR, 6, Ui.Tint(Ui.Bg, Color.White, 0.07)); }
            if (hoverBtn == 2) { Ui.FillRound(g, closeR, 6, Color.FromArgb(210, 40, 55)); }
            using (var p = new Pen(hoverBtn == 2 ? Color.White : Ui.MutedC, 1.6f))
            {
                g.DrawLine(p, closeR.X + 15, closeR.Y + 10, closeR.Right - 15, closeR.Bottom - 10);
                g.DrawLine(p, closeR.Right - 15, closeR.Y + 10, closeR.X + 15, closeR.Bottom - 10);
            }
            using (var p = new Pen(hoverBtn == 1 ? Ui.TextC : Ui.MutedC, 1.6f))
                g.DrawLine(p, minR.X + 12, minR.Bottom - 11, minR.Right - 12, minR.Bottom - 11);

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
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        internal static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, string lpszFile);
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
        Icon fileIcon = null;

        public string GamePath { get { return gamePath; } }

        public DropZone()
        {
            AllowDrop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

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

        static Icon LoadFileIcon(string path)
        {
            try
            {
                var h = NativeMethods.ExtractAssociatedIcon(IntPtr.Zero, path);
                if (h == IntPtr.Zero) return null;
                using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); // own the copy so we can dispose later
            }
            catch { return null; }
        }

        public void SetGame(string path)
        {
            gamePath = path ?? "";
            if (fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }
            fileIcon = LoadFileIcon(gamePath);
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
            if (IsExeDrop(e)) { e.Effect = DragDropEffects.Copy; dragOver = true; Invalidate(); }
            else e.Effect = DragDropEffects.None;
        }
        protected override void OnDragOver(DragEventArgs e)
        {
            bool ok = IsExeDrop(e);
            if (ok != dragOver) { dragOver = ok; Invalidate(); }
        }
        protected override void OnDragLeave(EventArgs e) { dragOver = false; Invalidate(); }
        protected override void OnDragDrop(DragEventArgs e)
        {
            dragOver = false; Invalidate();
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length < 1) return;
            string f0 = files[0] ?? "";
            if (!f0.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { var h = InvalidFile; if (h != null) h(f0); return; }
            SetGame(files[0]);
        }
        protected override void OnClick(EventArgs e) { Browse(); base.OnClick(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool oc = gamePath.Length > 0 && MouseIsOverChange(e.Location);
            if (oc != overChange) { overChange = oc; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { overChange = false; Invalidate(); base.OnMouseLeave(e); }
        Rectangle changeRect = Rectangle.Empty;
        bool MouseIsOverChange(Point pt)
        {
            return changeRect.Contains(pt);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = ClientRectangle;

            Ui.FillRound(g, rect, 14, Ui.Surface);
            if (gamePath.Length == 0)
            {
                var bc = dragOver ? Ui.Accent : Ui.BorderC;
                using (var pen = new Pen(bc, 1.6f)) { pen.DashStyle = DashStyle.Dash; using (var p = Ui.RoundPath(Rectangle.Inflate(rect, -1, -1), 13)) g.DrawPath(pen, p); }

                var iconR = new Rectangle(Width / 2 - 19, 15, 38, 38);
                var glowR = new Rectangle(iconR.X - 7, iconR.Y - 7, iconR.Width + 14, iconR.Height + 14);
                using (var b = new SolidBrush(Color.FromArgb(dragOver ? 46 : 24, Ui.Accent.R, Ui.Accent.G, Ui.Accent.B))) using (var p = Ui.RoundPath(glowR, 25)) g.FillPath(b, p);
                using (var lg = new LinearGradientBrush(new RectangleF(iconR.X, iconR.Y, iconR.Width, iconR.Height), Ui.Accent, Ui.Accent2, 45f)) using (var p = Ui.RoundPath(iconR, 19)) g.FillPath(lg, p);
                var tri = new PointF[] { new PointF(iconR.X + 16, iconR.Y + 12), new PointF(iconR.X + 16, iconR.Bottom - 12), new PointF(iconR.Right - 12, iconR.Y + 19) };
                using (var b = new SolidBrush(Color.White)) g.FillPolygon(b, tri);

                var l1 = "Drop the game's .exe here";
                var f1 = Ui.F(11.25f, true);
                var sz1 = g.MeasureString(l1, f1);
                TextRenderer.DrawText(g, l1, f1, new Point(Width / 2 - (int)sz1.Width / 2, 58), Ui.TextC, TextFormatFlags.NoPadding);
                var l2 = "or click to browse  ·  architecture & DRM are detected automatically";
                var f2 = Ui.F(8.5f, false);
                var sz2 = g.MeasureString(l2, f2);
                TextRenderer.DrawText(g, l2, f2, new Point(Width / 2 - (int)sz2.Width / 2, 84), Ui.MutedC, TextFormatFlags.NoPadding);
            }
            else
            {
                Ui.StrokeRound(g, rect, 14, dragOver ? Ui.Accent : Ui.BorderC, 1.4f);
                int pad = 18;
                var iconR = new Rectangle(pad, 15, 36, 36);
                if (fileIcon != null)
                {
                    Ui.FillRound(g, iconR, 10, Ui.Surface2);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    using (var bmp = fileIcon.ToBitmap())
                        g.DrawImage(bmp, new Rectangle(iconR.X + 3, iconR.Y + 3, 30, 30));
                }
                else
                {
                    Ui.FillRound(g, iconR, 18, Ui.Tint(Ui.Surface2, Ui.OkC, 0.22));
                    using (var b = new SolidBrush(Ui.OkC))
                        g.DrawString("\u2713", Ui.F(14, true), b, iconR.X + 9, iconR.Y + 8);
                }

                string name = Path.GetFileName(gamePath);
                TextRenderer.DrawText(g, name, Ui.F(10.5f, true), new Point(pad + 50, 20), Ui.TextC, TextFormatFlags.NoPadding);

                var dirF = Ui.F(8.25f, false);
                string dir = Path.GetDirectoryName(gamePath);
                int dirMaxW = Width - (pad + 50) - 110;
                using (var sfm = CreateGraphicsSafe()) { }
                string shownDir = TruncateForDraw(dir, dirF, dirMaxW);
                TextRenderer.DrawText(g, shownDir, dirF, new Point(pad + 50, 43), Ui.MutedC, TextFormatFlags.NoPadding);

                // CHANGE link top-right
                var cf = Ui.F(8f, true);
                var csz = TextRenderer.MeasureText("CHANGE", cf, Size.Empty, TextFormatFlags.NoPadding);
                changeRect = new Rectangle(Width - pad - csz.Width - 4, 20, csz.Width + 8, 18);
                TextRenderer.DrawText(g, "CHANGE", cf, changeRect, overChange ? Ui.Accent2 : Ui.MutedC,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                // chips row
                int cx = pad + 48; int cy = Height - 38;
                if (!string.IsNullOrEmpty(archChip))
                    Ui.DrawChip(g, ref cx, cy, 24, archChip, Ui.Accent2, Ui.Tint(Ui.Surface2, Ui.Accent2, 0.16), null);
                if (!string.IsNullOrEmpty(sizeChip))
                    Ui.DrawChip(g, ref cx, cy, 24, sizeChip, Ui.MutedC, Ui.Surface2, Ui.BorderC);
                if (!string.IsNullOrEmpty(apiChip))
                {
                    var col = apiState == 1 ? Ui.OkC : Ui.WarnC;
                    Ui.DrawChip(g, ref cx, cy, 24, apiChip, col, Ui.Tint(Ui.Surface2, col, 0.14), null);
                }
            }

            if (!Enabled)
                using (var b = new SolidBrush(Color.FromArgb(170, Ui.Bg.R, Ui.Bg.G, Ui.Bg.B))) g.FillRectangle(b, ClientRectangle);
        }

        Graphics CreateGraphicsSafe() { return null; }
        string TruncateForDraw(string s, Font f, int maxW)
        {
            if (s == null) return "";
            using (var g = CreateGraphics())
            {
                return Ui.TruncMiddle(g, s, f, maxW);
            }
        }
    }

    // ─────────────────────────────────────────────── toggle

    public class Toggle : Control
    {
        bool @checked;
        bool hover = false, press = false;
        public event EventHandler CheckedChanged;
        public bool Checked
        {
            get { return @checked; }
            set { if (@checked != value) { @checked = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); } }
        }
        public Toggle(string label, bool initial)
        {
            Text = label; @checked = initial;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Ui.Surface; // match the card so no black/unpainted area shows behind the pill
            Cursor = Cursors.Hand; Height = 24;
        }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { if (Enabled && !press) { hover = true; Invalidate(); } base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled) { press = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool p = press; press = false; hover = Enabled && !press; Invalidate();
            if (p) Checked = !Checked;
            base.OnMouseUp(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.Surface)) g.FillRectangle(b, ClientRectangle); // no black/unpainted area behind the pill
            int pillH = 18, pillW = 36;
            var pill = new Rectangle(0, Height / 2 - pillH / 2, pillW, pillH);

            Color trackFill, trackBorder;
            if (!Enabled)
            {
                trackFill = @checked ? Ui.Tint(Ui.Accent, Ui.Bg, 0.55) : Ui.Tint(Ui.Surface2, Ui.Bg, 0.3);
                trackBorder = @checked ? Ui.Tint(Ui.Accent, Ui.Bg, 0.6) : Ui.Tint(Ui.BorderC, Ui.Bg, 0.3);
            }
            else if (@checked) { trackFill = Ui.Accent; trackBorder = Ui.Accent; }
            else if (hover) { trackFill = Ui.Tint(Ui.Surface2, Color.White, 0.05); trackBorder = Ui.Tint(Ui.BorderC, Ui.Accent, 0.4); }
            else { trackFill = Ui.Surface2; trackBorder = Ui.BorderC; }

            Ui.FillRound(g, pill, pillH / 2, trackFill);
            Ui.StrokeRound(g, pill, pillH / 2, trackBorder, 1f);
            if (press && Enabled) Ui.FillRound(g, pill, pillH / 2, Color.FromArgb(40, 0, 0, 0));
            int knobD = pillH - 6;
            var knob = new Rectangle(@checked ? pill.Right - knobD - 3 : pill.X + 3, pill.Y + 3, knobD, knobD);
            using (var b = new SolidBrush(Enabled ? Color.White : Ui.FromHex("#6E7688"))) g.FillEllipse(b, knob);

            TextRenderer.DrawText(g, Text, Ui.F(8.75f, false), new Rectangle(pill.Right + 10, 0, Width - pill.Right - 10, Height),
                Enabled ? Ui.TextC : Ui.FromHex("#5A6373"), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ─────────────────────────────────────────────── gradient button

    public class GradientButton : Control
    {
        public enum BtnKind { Primary, Cancel, Success, Secondary }
        public BtnKind Kind = BtnKind.Primary;
        bool hover, press;
        public GradientButton(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { e.SuppressKeyPress = true; e.Handled = true; OnClick(EventArgs.Empty); return; }
            base.OnKeyDown(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseDown(MouseEventArgs e) { press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { press = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = ClientRectangle;
            Color fill1, fill2, txt;
            if (!Enabled) { fill1 = fill2 = Ui.Surface2; txt = Ui.FromHex("#5A6373"); }
            else if (Kind == BtnKind.Cancel) { fill1 = Ui.FromHex("#B23A47"); fill2 = Ui.FromHex("#8E2F3A"); txt = Color.White; }
            else if (Kind == BtnKind.Success) { fill1 = Ui.FromHex("#1F9D66"); fill2 = Ui.FromHex("#157A4F"); txt = Color.White; }
            else if (Kind == BtnKind.Secondary) { fill1 = Ui.Surface2; fill2 = Ui.Tint(Ui.Surface2, Color.Black, 0.25); txt = Ui.TextC; }
            else { fill1 = Ui.Accent; fill2 = Ui.Accent2; txt = Color.White; }

            using (var lg = new LinearGradientBrush(rect, fill1, fill2, 90f)) using (var p = Ui.RoundPath(rect, 12)) g.FillPath(lg, p);
            if (Enabled && (Kind == BtnKind.Primary || Kind == BtnKind.Secondary))
            {
                if (press) Ui.FillRound(g, rect, 12, Color.FromArgb(45, 0, 0, 0));
                else if (hover) Ui.FillRound(g, rect, 12, Kind == BtnKind.Primary ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(22, Ui.Accent.R, Ui.Accent.G, Ui.Accent.B));
            }
            if (Enabled && Kind == BtnKind.Secondary)
                using (var p = new Pen(hover ? Color.FromArgb(160, Ui.Accent.R, Ui.Accent.G, Ui.Accent.B) : Ui.BorderC, 1.2f)) using (var r = Ui.RoundPath(Rectangle.Inflate(rect, -1, -1), 11)) g.DrawPath(p, r);
            if (Focused && Enabled) Ui.FillRound(g, rect, 12, Color.FromArgb(34, 255, 255, 255));
            if (Enabled) using (var p = new Pen(Color.FromArgb(52, 255, 255, 255))) g.DrawLine(p, rect.X + 16, rect.Y + 1, rect.Right - 16, rect.Y + 1);
            var tf = Ui.F(11f, true);
            TextRenderer.DrawText(g, Text.ToUpperInvariant(), tf, rect, txt,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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
                else shown += (target - shown) * 0.18;
                Invalidate();
            };
        }
        public void SetValue(int v) { target = Math.Max(0, Math.Min(100, v)); timer.Start(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = ClientRectangle;
            Ui.FillRound(g, r, r.Height / 2, Ui.Surface2);
            int w = (int)(r.Width * shown / 100.0);
            if (w > r.Height / 2 + 1)
            {
                var fr = new Rectangle(r.X, r.Y, w, r.Height);
                using (var lg = new LinearGradientBrush(fr, Ui.Accent, Ui.Accent2, 0f)) using (var p = Ui.RoundPath(fr, r.Height / 2)) g.FillPath(lg, p);
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
        List<string> actions = new List<string>();
        public event Action<int> ActionClicked;
        readonly List<Rectangle> actionRects = new List<Rectangle>();
        int hoverAction = -1;

        public Banner()
        {
            Visible = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        public void Show(BannerKind kind, string msg, params string[] buttons)
        {
            Kind = kind; message = msg ?? ""; actions = new List<string>(buttons);
            actionRects.Clear(); Visible = true; Invalidate();
        }
        public void HideBanner() { Visible = false; actions.Clear(); }
        protected override void OnMouseLeave(EventArgs e) { hoverAction = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < actionRects.Count; i++) if (actionRects[i].Contains(e.Location)) { h = i; break; }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hoverAction) { hoverAction = h; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            for (int i = 0; i < actionRects.Count; i++)
                if (actionRects[i].Contains(e.Location)) { var h = ActionClicked; if (h != null) h(i); return; }
        }
        protected override void OnVisibleChanged(EventArgs e) { if (Visible) ParentForm_Resize(); base.OnVisibleChanged(e); }
        internal void ParentForm_Resize() { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = ClientRectangle;
            Color bg, bd, fg;
            if (Kind == BannerKind.Success) { bg = Ui.Tint(Ui.Bg, Ui.OkC, 0.09); bd = Ui.FromHex("#1E5C44"); fg = Ui.OkC; }
            else if (Kind == BannerKind.Error) { bg = Ui.Tint(Ui.Bg, Ui.ErrC, 0.09); bd = Ui.FromHex("#6B2B31"); fg = Ui.ErrC; }
            else { bg = Ui.Tint(Ui.Bg, Ui.WarnC, 0.08); bd = Ui.FromHex("#6B5623"); fg = Ui.WarnC; }
            Ui.FillRound(g, rect, 12, bg);
            Ui.StrokeRound(g, rect, 12, bd, 1f);

            string glyph = Kind == BannerKind.Success ? "\u2714" : Kind == BannerKind.Error ? "\u2718" : "!";
            using (var b = new SolidBrush(fg)) g.DrawString(glyph, Ui.F(11, true), b, 16, rect.Height / 2 - 11);

            // lay out action buttons first so the message text can use the remaining width
            var bf = Ui.F(8f, true);
            actionRects.Clear();
            int ax = rect.Right - 14;
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var sz = TextRenderer.MeasureText(actions[i].ToUpperInvariant(), bf, Size.Empty, TextFormatFlags.NoPadding);
                int bw = sz.Width + 24;
                ax -= bw;
                actionRects.Insert(0, new Rectangle(ax, rect.Height / 2 - 14, bw, 28));
                ax -= 8;
            }

            var lines = message.Split('\n');
            int textMaxW = (actions.Count > 0 ? actionRects[actionRects.Count - 1].X : Width - 56) - 56;
            int ty = rect.Height / 2 - (lines.Length * 17) / 2;
            for (int i = 0; i < lines.Length; i++)
            {
                var f = i == 0 ? Ui.F(8.75f, true) : Ui.F(8.25f, false);
                TextRenderer.DrawText(g, lines[i], f, new Rectangle(44, ty + i * 17, Math.Max(80, textMaxW), 18),
                    i == 0 ? Ui.TextC : Ui.MutedC, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            for (int i = 0; i < actions.Count; i++)
            {
                var br = actionRects[i];
                bool hov = i == hoverAction;
                Ui.FillRound(g, br, 14, hov ? fg : Ui.Tint(bg, fg, 0.16));
                Ui.StrokeRound(g, br, 14, fg, 1f);
                TextRenderer.DrawText(g, actions[i].ToUpperInvariant(), bf, br, hov ? Ui.Bg : fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // ─────────────────────────────────────────────── log view

    public class LogView : RichTextBox
    {
        public LogView()
        {
            ReadOnly = true; BorderStyle = System.Windows.Forms.BorderStyle.None;
            BackColor = Ui.Inset; ForeColor = Ui.TextC;
            Font = new Font("Consolas", 8.75f);
            HideSelection = false;
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
                case LogLevel.Dim: c = Ui.FromHex("#67707F"); break;
                default: c = Ui.FromHex("#B9C1CE"); break;
            }
            SelectionStart = TextLength;
            SelectionLength = 0;
            SelectionColor = c;
            AppendText(msg + Environment.NewLine);
            SelectionColor = ForeColor;
            ScrollToCaret();
        }
    }
}
