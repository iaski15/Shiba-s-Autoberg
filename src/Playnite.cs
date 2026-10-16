using System;

namespace Gp
{
    /// <summary>Playnite integration: the patched game shows up in Playnite's library. Scaffold placeholder.</summary>
    static class PlayniteLink
    {
        /// <summary>Called by PatchRunner.Run after a successful patch when PatchOptions.Playnite is on.
        /// Best-effort: never fails the patch (the call site also catches).</summary>
        public static void Register(PatchOptions o, PatchResult res, Action<LogLevel, string> log)
        {
        }
    }
}
