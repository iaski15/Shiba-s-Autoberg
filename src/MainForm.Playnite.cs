using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Gp
{
    public partial class MainForm
    {
        /// <summary>Game tools → Add to Playnite for the selected game (zone.GamePath): the same step a patch
        /// runs when the Playnite option is on, for a game that is already patched.</summary>
        void AddSelectedToPlaynite()
        {
            if (!ToolsReady) return;
            if (!playniteFound)
            {
                MessageBox.Show(this, "Playnite isn't installed on this PC (playnite.link).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string exe = zone.GamePath, hint = appIdBox.Text;
            toolsBtn.Enabled = false;   // also keeps a second click out until this finishes
            statusBar.Set("Adding to Playnite…", Ui.Accent);
            Task.Run(() =>
            {
                var lines = new List<PatchLogEntry>();
                var target = EmuSettings.Locate(exe);
                bool patched = target.IsEmulator || target.OnlineFix;
                if (patched)
                {
                    string id = target.OnlineFix ? "480" : (target.AppId.Length > 0 ? target.AppId : AppIdDetector.Normalize(hint));
                    var opts = new PatchOptions { GameExe = exe, AppId = id, OnlineFix = target.OnlineFix };
                    try { PlayniteLink.Register(opts, new PatchResult { Success = true, FinalExe = exe }, (l, m) => lines.Add(new PatchLogEntry { Level = l, Message = m })); }
                    catch (System.Exception ex) { lines.Add(new PatchLogEntry { Level = LogLevel.Warn, Message = "Playnite: " + ex.Message }); }
                }
                UiInvoke(delegate
                {
                    toolsBtn.Enabled = ToolsReady;
                    foreach (var line in lines) Log(line.Level, line.Message);
                    if (!patched)
                    {
                        statusBar.Set("Not added to Playnite", Ui.WarnC);
                        MessageBox.Show(this, "Patch this game first – Playnite would start it through Steam as it is now.",
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else statusBar.Set(lines.Exists(l => l.Level == LogLevel.Warn) ? "Playnite: see the log" : "Added to Playnite", lines.Exists(l => l.Level == LogLevel.Warn) ? Ui.WarnC : Ui.OkC);
                });
            });
        }
    }
}
