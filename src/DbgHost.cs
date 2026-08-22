using System;
using System.Drawing;
using System.Windows.Forms;
using Gp;

static class DbgHost
{
    static void Step(string s)
    {
        Console.WriteLine("[step] " + s);
        Console.Out.Flush();
    }

    [STAThread]
    static int Main()
    {
        Step("boot");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Step("app init ok");

        var f = new Form();
        f.StartPosition = FormStartPosition.Manual;
        f.Bounds = new Rectangle(40, 40, 860, 760);
        f.BackColor = Ui.Bg;
        int y = 10;

        try { var tb = new TitleBar(); tb.Dock = DockStyle.None; tb.Bounds = new Rectangle(0, y += 0, 800, 46); f.Controls.Add(tb); Application.DoEvents(); Step("TitleBar ok"); }
        catch (Exception ex) { Step("TitleBar FAIL: " + ex.Message); }

        try { var dz = new DropZone(); dz.Bounds = new Rectangle(20, y += 56, 780, 116); f.Controls.Add(dz); Application.DoEvents(); dz.UpdateAnalysis("X64", "12 MB", "steam_api64.dll found", 1); Application.DoEvents(); Step("DropZone ok"); }
        catch (Exception ex) { Step("DropZone FAIL: " + ex.Message); }

        try { var tg = new Toggle("test toggle", true); tg.Bounds = new Rectangle(20, y += 126, 300, 24); f.Controls.Add(tg); Application.DoEvents(); Step("Toggle ok"); }
        catch (Exception ex) { Step("Toggle FAIL: " + ex.Message); }

        try { var gb = new GradientButton("Patch Game"); gb.Bounds = new Rectangle(20, y += 34, 780, 52); f.Controls.Add(gb); Application.DoEvents(); Step("GradientButton ok"); }
        catch (Exception ex) { Step("GradientButton FAIL: " + ex.Message); }

        try { var pb = new ProgressBarLite(); pb.Bounds = new Rectangle(20, y += 62, 780, 5); f.Controls.Add(pb); pb.SetValue(50); Application.DoEvents(); Step("ProgressBarLite ok"); }
        catch (Exception ex) { Step("ProgressBarLite FAIL: " + ex.Message); }

        try { var bn = new Banner(); bn.Bounds = new Rectangle(20, y += 15, 780, 58); f.Controls.Add(bn); bn.Show(Banner.BannerKind.Success, "all good", "Open folder", "Play game"); Application.DoEvents(); Step("Banner ok"); }
        catch (Exception ex) { Step("Banner FAIL: " + ex.Message); }

        try { var lv = new LogView(); lv.Bounds = new Rectangle(20, y += 68, 780, 200); f.Controls.Add(lv); lv.AppendLine("hello", LogLevel.Info); lv.AppendLine("warn", LogLevel.Warn); Application.DoEvents(); Step("LogView ok"); }
        catch (Exception ex) { Step("LogView FAIL: " + ex.Message); }

        try { var ab = new AppIdBox(); ab.Bounds = new Rectangle(20, y += 210, 250, 38); f.Controls.Add(ab); Application.DoEvents(); Step("AppIdBox ok"); }
        catch (Exception ex) { Step("AppIdBox FAIL: " + ex.Message); }

        try { var sb = new StatusBarCtl(); sb.Dock = DockStyle.Bottom; f.Controls.Add(sb); sb.Set("Ready", Ui.OkC); Application.DoEvents(); Step("StatusBarCtl ok"); }
        catch (Exception ex) { Step("StatusBarCtl FAIL: " + ex.Message); }

        try
        {
            var card = new AppCard();
            card.Bounds = new Rectangle(20, 700, 400, 40);
            f.Controls.Add(card);
            Application.DoEvents();
            Step("AppCard ok");
        }
        catch (Exception ex) { Step("AppCard FAIL: " + ex.Message); }

        try
        {
            Step("building MainForm…");
            var sa = new Gp.StartupArgs();
            sa.Exe = @"D:\Bionis\Goldberg test\steamless\Steamless.CLI.exe";
            var mf = new MainForm(sa);
            Step("MainForm ctor ok");
            mf.StartPosition = FormStartPosition.Manual;
            mf.Bounds = new Rectangle(-3000, -3000, 820, 740); // offscreen
            mf.Show();
            Step("MainForm shown");
            for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
            try
            {
                var fld = typeof(Gp.MainForm).GetField("log", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var lv = (Gp.LogView)fld.GetValue(mf);
                Step("log text length = " + (lv == null ? -1 : lv.TextLength));
            }
            catch (Exception ex2) { Step("reflect fail: " + ex2.Message); }
            using (var bmp = new Bitmap(mf.Width, mf.Height))
            {
                mf.DrawToBitmap(bmp, new Rectangle(0, 0, mf.Width, mf.Height));
                bmp.Save(@"C:\Users\iaski\AppData\Local\Temp\gp_shot.png",
                    System.Drawing.Imaging.ImageFormat.Png);
            }
            Step("bitmap saved");
            mf.Close();
            Step("MAINFORM ALL OK");
        }
        catch (Exception ex) { Step("MainForm FAIL:\n" + ex.ToString()); }

        f.Close();
        Step("ALL OK");
        return 0;
    }
}
