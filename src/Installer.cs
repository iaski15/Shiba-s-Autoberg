using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Gp
{
    /// <summary>Shibaberg installs itself: the same exe, named Shibaberg-Setup-*.exe (build.ps1 -Package), copies
    /// itself to %LOCALAPPDATA%\Programs\Shibaberg, adds a Start menu shortcut and an "Installed apps" entry.
    /// Per user, no admin. The fixed location also keeps the path cloud saves rely on stable.</summary>
    public static class Installer
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Shibaberg";

        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Shibaberg"); } }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "Shibaberg.exe"); } }
        static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Shibaberg.lnk"); } }
        static string Self { get { return Application.ExecutablePath; } }

        public static bool IsSetupExe { get { return Path.GetFileName(Self).StartsWith("Shibaberg-Setup", StringComparison.OrdinalIgnoreCase); } }

        /// <summary>Copies this exe into place, creates the shortcut and the uninstall entry. Throws on failure.</summary>
        public static void Install()
        {
            Directory.CreateDirectory(InstallDir);
            if (!string.Equals(Path.GetFullPath(Self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(Self, InstalledExe, true); }
                catch (IOException) { throw new IOException("Shibaberg is running – close it and run the setup again."); }
            }

            // WScript.Shell is on every Windows; no interop assembly needed.
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                dynamic lnk = shell.CreateShortcut(Shortcut);
                lnk.TargetPath = InstalledExe;
                lnk.WorkingDirectory = InstallDir;
                lnk.IconLocation = InstalledExe + ",0";
                lnk.Description = "Patch Steam games to run offline, with achievements and cloud saves";
                lnk.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Shibaberg");
                k.SetValue("DisplayVersion", BuildInfo.Version);
                k.SetValue("Publisher", "iaski15");
                k.SetValue("DisplayIcon", InstalledExe + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                k.SetValue("URLInfoAbout", "https://github.com/iaski15/Shiba-s-Autoberg");
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        /// <summary>Removes the shortcut, the uninstall entry and the program folder. Settings, logs, the Google
        /// sign-in and save backups in %APPDATA%\GoldbergPatcher stay (patched games keep working without it).</summary>
        public static void Uninstall()
        {
            if (MessageBox.Show("Uninstall Shibaberg?\n\nPatched games keep working. Your settings and cloud-save sign-in stay in "
                    + AppPaths.StateDir + " – delete that folder too if you want everything gone.",
                    "Shibaberg", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try { File.Delete(Shortcut); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            // This exe is running from the folder: let cmd delete it once we've exited.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }

        /// <summary>The setup window: one button, then it launches the installed app.</summary>
        public static void RunSetup()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var f = new SetupForm()) Application.Run(f);
        }

        sealed class SetupForm : Form
        {
            readonly FlatButton installBtn;
            string status = "";

            public SetupForm()
            {
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                AutoScaleDimensions = new SizeF(96f, 96f);
                AutoScaleMode = AutoScaleMode.Dpi;
                ClientSize = new Size(460, 250);
                BackColor = Ui.Bg;
                DoubleBuffered = true;
                Text = "Shibaberg " + BuildInfo.Version + " setup";
                try { using (var bmp = Shiba.Render(64, ShibaMood.Happy)) Icon = Icon.FromHandle(bmp.GetHicon()); } catch { }

                bool update = File.Exists(InstalledExe);
                installBtn = new FlatButton(update ? "Update" : "Install") { Bounds = new Rectangle(460 - 24 - 150, 250 - 24 - 44, 150, 44) };
                installBtn.Click += delegate { DoInstall(); };
                Controls.Add(installBtn);
                AcceptButton = installBtn;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Shiba.Draw(g, new RectangleF(Ui.S(24), Ui.S(24), Ui.S(72), Ui.S(72)), ShibaMood.Happy);
                TextRenderer.DrawText(g, "Install Shibaberg", Ui.F(14f, true), new Point(Ui.S(112), Ui.S(30)), Ui.TextC, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "Version " + BuildInfo.Version + "  ·  no admin rights needed", Ui.F(9f, false), new Point(Ui.S(112), Ui.S(66)), Ui.MutedC, TextFormatFlags.NoPadding);
                var body = new Rectangle(Ui.S(24), Ui.S(112), ClientSize.Width - Ui.S(48), Ui.S(60));
                TextRenderer.DrawText(g, "Installs to " + InstallDir + " and adds Shibaberg to the Start menu. Remove it any time from Settings › Apps.",
                    Ui.F(9f, false), body, Ui.TextC, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                if (status.Length > 0)
                    TextRenderer.DrawText(g, status, Ui.F(9f, false), new Rectangle(Ui.S(24), installBtn.Top, installBtn.Left - Ui.S(36), installBtn.Height),
                        Ui.ErrC, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            void DoInstall()
            {
                installBtn.Enabled = false;
                try
                {
                    Install();
                    Process.Start(new ProcessStartInfo(InstalledExe) { WorkingDirectory = InstallDir });
                    Close();
                }
                catch (Exception ex)
                {
                    status = ex.Message;
                    installBtn.Enabled = true;
                    Invalidate();
                }
            }
        }
    }
}
