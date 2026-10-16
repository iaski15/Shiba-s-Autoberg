using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Gp
{
    public partial class MainForm
    {
        /// <summary>Game tools → Restore original… for the selected game (zone.GamePath): works out the plan off the
        /// UI thread, shows exactly what it will restore, remove and leave, and only then carries it out.</summary>
        void StartRestore()
        {
            if (!ToolsReady) return;
            string exe = zone.GamePath, hint = appIdBox.Text;
            BeginToolRun("Looking at what can be restored…");
            // Held in patchTask so closing the window waits for it, as it does for a patch.
            patchTask = Task.Run(() => GameRestore.Plan(exe, hint)).ContinueWith(t => UiInvoke(delegate
            {
                if (t.Exception != null)
                {
                    EndToolRun();
                    ShowBanner(Banner.BannerKind.Error, "Could not work out the restore: " + t.Exception.GetBaseException().Message);
                    statusBar.Set("Restore failed", Ui.ErrC);
                    return;
                }
                ConfirmRestore(t.Result);
            }));
        }

        void ConfirmRestore(RestorePlan plan)
        {
            if (closing) { EndToolRun(); return; }
            if (plan.NothingToDo)
            {
                EndToolRun();
                string detail = plan.Items.Count + plan.Notes.Count > 0 ? "\n\n" + string.Join("\n", plan.Describe(24).ToArray()) : "";
                MessageBox.Show(this, "There is nothing Shibaberg can put back for this game." + detail, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                statusBar.Set("Nothing to restore", Ui.MutedC);
                return;
            }
            string question = "Restore " + System.IO.Path.GetFileNameWithoutExtension(plan.Exe) + " to its original files?\n\n"
                + string.Join("\n", plan.Describe(30).ToArray())
                + (plan.KeepBackups ? "" : "\n\nThe backup folder is removed afterwards.");
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                EndToolRun();
                statusBar.Set("Restore cancelled", Ui.MutedC);
                return;
            }
            statusBar.Set("Restoring the original files…", Ui.WarnC);
            patchTask = Task.Run(() =>
            {
                var lines = new List<PatchLogEntry>();
                RestoreResult result;
                try { result = GameRestore.Apply(plan, (l, m) => lines.Add(new PatchLogEntry { Level = l, Message = m })); }
                catch (Exception ex)
                {
                    result = new RestoreResult { Failed = 1 };
                    result.Messages.Add(ex.Message);
                }
                UiInvoke(delegate
                {
                    foreach (var line in lines) Log(line.Level, line.Message);
                    Log(result.Complete ? LogLevel.Ok : LogLevel.Warn, "Restore finished: " + result.Summary);
                    EndToolRun();
                    if (result.Complete)
                    {
                        ShowBanner(Banner.BannerKind.Success, "Restored – " + result.Summary + "\nThe game is back to its original files.");
                        statusBar.Set("Restored – " + result.Summary, Ui.OkC);
                        SetMascot(ShibaMood.Happy, "All back the way it was!");
                    }
                    else
                    {
                        var why = result.Messages.Concat(plan.Notes).Take(3).ToArray();
                        string detail = why.Length > 0 ? "\n" + string.Join("\n", why) : "";
                        ShowBanner(result.Failed > 0 || result.Blocked ? Banner.BannerKind.Error : Banner.BannerKind.Warn, "Restore: " + result.Summary + detail);
                        statusBar.Set("Restore: " + result.Summary, result.Failed > 0 || result.Blocked ? Ui.ErrC : Ui.WarnC);
                    }
                });
            });
        }

        /// <summary>Locks the window like a patch does while a game tool works on the selected game.</summary>
        void BeginToolRun(string status)
        {
            running = true;
            banner.HideBanner();
            ShowBannerLayout(false);
            patchBtn.Enabled = toolsBtn.Enabled = batchBtn.Enabled = cloudBtn.Enabled = zone.Enabled = appIdBox.Enabled = false;
            SetOptionsEnabled(false);
            statusBar.Pulse = true;
            statusBar.Set(status, Ui.Accent);
        }

        void EndToolRun()
        {
            running = false;
            statusBar.Pulse = false;
            patchBtn.Enabled = batchBtn.Enabled = cloudBtn.Enabled = zone.Enabled = !closing;
            toolsBtn.Enabled = ToolsReady;
            SetOptionsEnabled(!closing);
            appIdBox.Enabled = !closing && !tOnlineFix.Checked;
            lastActions = new string[0];
        }
    }
}
