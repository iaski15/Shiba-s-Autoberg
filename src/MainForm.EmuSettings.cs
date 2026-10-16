using System.Windows.Forms;

namespace Gp
{
    public partial class MainForm
    {
        /// <summary>Game tools → Emulator settings… for the selected game (zone.GamePath).</summary>
        void OpenEmuSettings()
        {
            MessageBox.Show(this, "Emulator settings are coming soon.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
