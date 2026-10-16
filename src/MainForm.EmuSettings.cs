namespace Gp
{
    public partial class MainForm
    {
        /// <summary>Game tools → Emulator settings… for the selected game (zone.GamePath).</summary>
        void OpenEmuSettings()
        {
            if (!ToolsReady) return;
            toolsBtn.Enabled = false;
            var f = new EmuSettingsForm(zone.GamePath, appIdBox.Text);
            try { f.ShowDialog(this); } finally { f.Dispose(); toolsBtn.Enabled = ToolsReady; }
        }
    }
}
