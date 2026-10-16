using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Gp
{
    /// <summary>Where Playnite is installed and where it keeps its extensions.</summary>
    public sealed class PlayniteInfo
    {
        public string InstallDir = "";
        /// <summary>A portable install (no uninstaller) keeps its configuration beside the exe.</summary>
        public bool Portable;
        public string ConfigRoot = "";
        public string ExtensionsDir = "";
    }

    /// <summary>Playnite integration: every game Shibaberg patches shows up in Playnite's library with a working
    /// Play button. Shibaberg keeps a list (&lt;state&gt;\playnite.txt) and installs a small library extension
    /// (playnite\ShibabergLibrary.cs, embedded in the payload) that reads it: on every library update, and live
    /// through playnite://shibaberg/import while Playnite is running. Best-effort throughout - nothing here may
    /// ever fail a patch.</summary>
    public static class PlayniteLink
    {
        /// <summary>The extension's folder name under Playnite's Extensions\ (extension.yaml Id).</summary>
        public const string ExtensionId = "Shibaberg_Library";
        public const string ExtensionDll = "ShibabergPlaynite.dll";
        /// <summary>The files of the extension, as the payload holds them (Tools.PlayniteDir).</summary>
        public static readonly string[] ExtensionFiles = { ExtensionDll, "extension.yaml", "icon.png" };

        public static string ListPath { get { return Path.Combine(AppPaths.StateDir, "playnite.txt"); } }
        public static string IconsDir { get { return Path.Combine(AppPaths.StateDir, "playnite_icons"); } }

        // ---------------------------------------------------------------- detection

        /// <summary>The installed Playnite, or null. <paramref name="installDir"/> skips the registry lookup
        /// (HKCU uninstall entry, else the playnite:// protocol handler).</summary>
        public static PlayniteInfo Detect(string installDir = null)
        {
            try
            {
                string dir = installDir ?? RegistryInstallDir();
                if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Playnite.DesktopApp.exe"))) return null;
                var info = new PlayniteInfo { InstallDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) };
                info.Portable = !File.Exists(Path.Combine(info.InstallDir, "unins000.exe"));
                info.ConfigRoot = info.Portable ? info.InstallDir
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite");
                info.ExtensionsDir = Path.Combine(info.ConfigRoot, "Extensions");
                return info;
            }
            catch { return null; }
        }

        static string RegistryInstallDir()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Playnite_is1"))
                {
                    string loc = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc)) return loc;
                }
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\playnite\shell\open\command"))
                {
                    string cmd = k == null ? null : k.GetValue("") as string;
                    string exe = CommandExe(cmd);
                    if (exe != null && File.Exists(exe)) return Path.GetDirectoryName(exe);
                }
            }
            catch { }
            return null;
        }

        /// <summary>The executable of a shell command line: the quoted first token, or everything up to ".exe".</summary>
        internal static string CommandExe(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : null;
            }
            int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? command.Substring(0, exe + 4) : null;
        }

        /// <summary>Whether Playnite is running: its single-instance mutex, or one of its processes.</summary>
        public static bool IsRunning()
        {
            try
            {
                Mutex m;
                if (Mutex.TryOpenExisting("PlayniteInstaceMutex", out m)) { m.Dispose(); return true; }   // sic, Playnite's spelling
            }
            catch (UnauthorizedAccessException) { return true; }   // it exists, we just may not open it
            catch { }
            foreach (var name in new[] { "Playnite.DesktopApp", "Playnite.FullscreenApp" })
            {
                var procs = Process.GetProcessesByName(name);
                bool any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- the game list

        /// <summary>The id the game has in Playnite: stable for one exe path whatever its case or spelling
        /// (Playnite matches GameId ordinally, so the path is canonicalised before hashing).</summary>
        public static string GameId(string exe)
        {
            string canonical = Path.GetFullPath(exe).ToUpperInvariant();
            return "sb-" + SafePersistence.Hash(Encoding.UTF8.GetBytes(canonical)).Substring(0, 16);
        }

        public sealed class Entry
        {
            public string GameId = "";
            public string AppId = "";
            public string Name = "";
            public string Exe = "";
            public string InstallDir = "";
            public DateTime AddedUtc;
        }

        const string Header = "# Shibaberg → Playnite. One game per line: gameId<TAB>appId<TAB>name<TAB>exe<TAB>installDir<TAB>addedUtc";

        static string Field(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        internal static string FormatLine(Entry e)
        {
            return string.Join("\t", new[] { Field(e.GameId), Field(e.AppId), Field(e.Name), Field(e.Exe), Field(e.InstallDir),
                e.AddedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });
        }

        /// <summary>One list line, or null when it is a comment or malformed.</summary>
        internal static Entry ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) return null;
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 6 || !f[0].StartsWith("sb-") || f[2].Trim().Length == 0 || f[3].Trim().Length == 0) return null;
            DateTime added;
            if (!DateTime.TryParse(f[5], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out added))
                added = DateTime.MinValue;
            return new Entry { GameId = f[0], AppId = f[1].Trim(), Name = f[2].Trim(), Exe = f[3].Trim(), InstallDir = f[4].Trim(), AddedUtc = added };
        }

        public static List<Entry> ReadList(string listPath = null)
        {
            var list = new List<Entry>();
            try
            {
                string path = listPath ?? ListPath;
                if (!File.Exists(path)) return list;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var e = ParseLine(line);
                    if (e != null && !list.Any(x => x.GameId == e.GameId)) list.Add(e);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return list;
        }

        /// <summary>Read-modify-write of the list under the path's named mutex (a batch and the GUI can both be
        /// at it), written atomically. Not part of any undo record.</summary>
        static void Update(string listPath, Action<List<Entry>> change)
        {
            string path = Path.GetFullPath(listPath ?? ListPath);
            SafePersistence.Locked(path, () =>
            {
                var list = ReadList(path);
                change(list);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                sb.Append(Header).Append("\r\n");
                foreach (var e in list) sb.Append(FormatLine(e)).Append("\r\n");
                var record = SafePersistence.WriteText(path, sb.ToString());
                Recovery.Discard(new[] { record });
                return true;
            });
        }

        /// <summary>Adds the game, or refreshes its line (keeping when it was first added).</summary>
        public static Entry Remember(string exe, string appId, string name, string installDir, string listPath = null)
        {
            var entry = new Entry
            {
                GameId = GameId(exe),
                AppId = appId ?? "",
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(exe) : name.Trim(),
                Exe = Path.GetFullPath(exe),
                InstallDir = Path.GetFullPath(installDir ?? Path.GetDirectoryName(exe)).TrimEnd(Path.DirectorySeparatorChar),
                AddedUtc = DateTime.UtcNow,
            };
            Update(listPath, list =>
            {
                int at = list.FindIndex(x => x.GameId == entry.GameId);
                if (at < 0) { list.Add(entry); return; }
                if (list[at].AddedUtc != DateTime.MinValue) entry.AddedUtc = list[at].AddedUtc;
                list[at] = entry;
            });
            return entry;
        }

        /// <summary>Takes the game off the list (Restore original). The game stays in Playnite's database until the
        /// user removes it there; it simply stops being reported. True when it was on the list.</summary>
        public static bool Forget(string exe, string listPath = null)
        {
            string id = GameId(exe);
            bool removed = false;
            try
            {
                if (!File.Exists(listPath ?? ListPath)) return false;
                Update(listPath, list => removed = list.RemoveAll(x => x.GameId == id) > 0);
                if (listPath == null) { try { File.Delete(Path.Combine(IconsDir, id + ".png")); } catch { } }
            }
            catch { }
            return removed;
        }

        /// <summary>The game's exe icon as &lt;state&gt;\playnite_icons\&lt;gameId&gt;.png, for the plugin to use.</summary>
        public static string SaveIcon(string exe, string iconsDir = null)
        {
            try
            {
                string dir = iconsDir ?? IconsDir;
                string target = Path.Combine(dir, GameId(exe) + ".png");
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                {
                    if (icon == null) return null;
                    using (var bmp = icon.ToBitmap())
                    {
                        Directory.CreateDirectory(dir);
                        string tmp = target + ".tmp";
                        bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(tmp, target);
                    }
                }
                return target;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- the extension

        public enum ExtensionState { UpToDate, Installed, Updated, Locked, Missing }

        /// <summary>Copies the extension into &lt;Extensions&gt;\Shibaberg_Library\ when it is missing or differs.
        /// A dll Playnite has loaded cannot be overwritten, but it can be renamed: the old one is moved aside
        /// (deleted on a later run) and the new one takes its place, effective from Playnite's next start.</summary>
        /// <param name="restartNeeded">True when the extension was installed or updated while Playnite runs.</param>
        public static ExtensionState EnsureExtension(PlayniteInfo info, string sourceDir, out bool restartNeeded)
        {
            restartNeeded = false;
            if (info == null || string.IsNullOrEmpty(sourceDir)) return ExtensionState.Missing;
            if (ExtensionFiles.Any(f => !File.Exists(Path.Combine(sourceDir, f)))) return ExtensionState.Missing;
            string dest = Path.Combine(info.ExtensionsDir, ExtensionId);
            bool existed = Directory.Exists(dest);
            Directory.CreateDirectory(dest);
            foreach (var stale in Directory.GetFiles(dest, "*.old-*")) { try { File.Delete(stale); } catch { } }

            bool changed = false, locked = false;
            foreach (var name in ExtensionFiles)
            {
                string src = Path.Combine(sourceDir, name), dst = Path.Combine(dest, name);
                if (File.Exists(dst) && SafePersistence.Hash(dst) == SafePersistence.Hash(src)) continue;
                try { CopyVerified(src, dst); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    try
                    {
                        File.Move(dst, dst + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        CopyVerified(src, dst);
                    }
                    catch (Exception) { locked = true; continue; }
                }
                changed = true;
            }
            if (locked) return ExtensionState.Locked;
            if (!changed) return ExtensionState.UpToDate;
            restartNeeded = IsRunning();
            return existed ? ExtensionState.Updated : ExtensionState.Installed;
        }

        static void CopyVerified(string src, string dst)
        {
            string tmp = dst + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                File.Copy(src, tmp, true);
                if (SafePersistence.Hash(tmp) != SafePersistence.Hash(src)) throw new IOException("Copy of " + Path.GetFileName(src) + " failed verification.");
                if (File.Exists(dst)) File.Replace(tmp, dst, null);
                else File.Move(tmp, dst);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        /// <summary>Asks a running Playnite to import new list entries now. Never when it is not running: opening
        /// a playnite:// link starts it.</summary>
        public static bool NotifyRunning()
        {
            if (!IsRunning()) return false;
            try { Process.Start(new ProcessStartInfo("playnite://shibaberg/import") { UseShellExecute = true }); return true; }
            catch { return false; }
        }

        // ---------------------------------------------------------------- after a patch

        /// <summary>Called by PatchRunner.Run after a successful patch when PatchOptions.Playnite is on, and by
        /// Game tools → Add to Playnite. Best-effort: never fails the patch (the call site also catches).</summary>
        public static void Register(PatchOptions o, PatchResult res, Action<LogLevel, string> log)
        {
            Register(o, res, log, Detect(), Tools.PlayniteDir, null, null, true);
        }

        /// <param name="notify">Whether a running Playnite is asked to import now; the self-test passes false, so
        /// it never reaches a real Playnite.</param>
        internal static void Register(PatchOptions o, PatchResult res, Action<LogLevel, string> log, PlayniteInfo info,
            string extensionSource, string listPath, string iconsDir, bool notify)
        {
            if (info == null)
            {
                log(LogLevel.Dim, "Playnite is not installed – nothing added to it.");
                return;
            }
            string exe = !string.IsNullOrEmpty(res.FinalExe) ? res.FinalExe : o.GameExe;
            string appId = o.EffectiveAppId;
            string name = GameNames.Resolve(exe, appId);

            bool restart;
            var state = EnsureExtension(info, extensionSource, out restart);
            Remember(exe, appId, name, PatchRunner.SearchRoot(exe), listPath);
            SaveIcon(exe, iconsDir);

            if (state == ExtensionState.Missing)
                log(LogLevel.Warn, "Playnite: the Shibaberg extension is missing from this build – \"" + name + "\" was put on the list, but Playnite will not see it.");
            else if (state == ExtensionState.Locked)
                log(LogLevel.Warn, "Playnite: could not update the Shibaberg extension in " + info.ExtensionsDir + " – close Playnite and patch again.");
            else if (restart)
                log(LogLevel.Ok, "Added \"" + name + "\" to Playnite – restart Playnite once to load the Shibaberg library.");
            else if (notify && NotifyRunning())
                log(LogLevel.Ok, "Added \"" + name + "\" to Playnite.");
            else
                log(LogLevel.Ok, "\"" + name + "\" will appear in Playnite next time it starts (Shibaberg library).");
        }
    }

    /// <summary>A display name for a patched game.</summary>
    public static class GameNames
    {
        /// <summary>Steam's own name from the local cache (not for 480: online-fix games run as Spacewar), else
        /// Unity's product name from &lt;exe&gt;_Data\app.info, else the game folder's name without release tags.</summary>
        public static string Resolve(string exe, string appId)
        {
            string id = AppIdDetector.Normalize(appId);
            if (id.Length > 0 && id != "480")
            {
                string steam = SteamAppInfo.GetName(id);
                if (!string.IsNullOrWhiteSpace(steam)) return steam.Trim();
            }
            string unity = UnityProductName(exe);
            if (unity != null) return unity;
            string root = PatchRunner.SearchRoot(exe);
            string folder = CleanFolderName(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
            return folder.Length > 0 ? folder : Path.GetFileNameWithoutExtension(exe);
        }

        /// <summary>The product name in &lt;exe name&gt;_Data\app.info (line 1 is the company, line 2 the product).</summary>
        internal static string UnityProductName(string exe)
        {
            try
            {
                string info = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)), Path.GetFileNameWithoutExtension(exe) + "_Data", "app.info");
                if (!File.Exists(info) || new FileInfo(info).Length > 4096) return null;
                var lines = File.ReadAllLines(info);
                string product = lines.Length >= 2 ? lines[1].Trim() : "";
                return product.Length > 0 && product.All(c => c >= 0x20) ? product : null;
            }
            catch { return null; }
        }

        static readonly Regex Tags = new Regex(@"\[[^\]]*\]|\([^)]*\)", RegexOptions.CultureInvariant);

        /// <summary>"Game Name [Build 24911270] (OnlineFix)" → "Game Name".</summary>
        internal static string CleanFolderName(string folder)
        {
            string s = Tags.Replace(folder ?? "", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim().Trim('-', '_', '.', ' ');
            return s;
        }
    }
}
