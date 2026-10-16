using System.Windows.Forms;

namespace Gp
{
    public partial class MainForm
    {
        /// <summary>Game tools → Restore original… for the selected game (zone.GamePath).</summary>
        void StartRestore()
        {
            MessageBox.Show(this, "Restore original is coming soon.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
