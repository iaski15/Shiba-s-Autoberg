using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gp;

static partial class TestMain
{
    /// <summary>Playnite list file, game ids, extension install, game names (feature D). Everything runs against
    /// temp folders; nothing here detects, writes to or signals the real Playnite.</summary>
    static void SelfTestPlaynite()
    {
        Console.WriteLine("\n[playnite]");
        string work = Path.Combine(Path.GetTempPath(), "gp_selftest_playnite_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        try
        {
            // ---- game ids: one per exe, whatever the path's case or spelling
            string gameDir = Directory.CreateDirectory(Path.Combine(work, "My Game [Build 123] (OnlineFix)")).FullName;
            string exe = Path.Combine(gameDir, "Game.exe");
            WriteNativeX64Pe(exe);
            string id = PlayniteLink.GameId(exe);
            string prevCwd = Environment.CurrentDirectory;
            string viaRelative;
            try { Environment.CurrentDirectory = gameDir; viaRelative = PlayniteLink.GameId("Game.exe"); }
            finally { Environment.CurrentDirectory = prevCwd; }
            Check(id.Length == 19 && id.StartsWith("sb-") && id.Substring(3).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
                  "game id: sb- plus 16 hex digits", id);
            Check(PlayniteLink.GameId(exe.ToUpperInvariant()) == id && viaRelative == id
                  && PlayniteLink.GameId(Path.Combine(gameDir, ".", "Game.exe")) == id,
                  "game id: the same for any case, a relative path or a \".\" segment", viaRelative);
            Check(PlayniteLink.GameId(Path.Combine(gameDir, "Other.exe")) != id, "game id: another exe gets another id", null);

            // ---- the list: upsert, odd names, forget, junk lines
            string list = Path.Combine(work, "state", "playnite.txt");
            var first = PlayniteLink.Remember(exe, "1250", "Tab\there\r\nand #hash ü", gameDir, list);
            var entries = PlayniteLink.ReadList(list);
            Check(entries.Count == 1 && entries[0].GameId == id && entries[0].Name == "Tab here  and #hash ü" && entries[0].AppId == "1250"
                  && string.Equals(entries[0].Exe, exe, StringComparison.OrdinalIgnoreCase) && string.Equals(entries[0].InstallDir, gameDir, StringComparison.OrdinalIgnoreCase),
                  "list: a game round-trips, tabs and line breaks in its name flattened", entries.Count == 1 ? entries[0].Name : entries.Count.ToString());
            string raw = File.ReadAllText(list);
            Check(raw.StartsWith("#") && raw.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length == 2
                  && raw.Split(new[] { "\r\n" }, StringSplitOptions.None)[1].Split('\t').Length == 6,
                  "list: a header comment, then one six-field tab-separated line per game", raw.Replace("\t", "→").Replace("\r\n", "|"));
            System.Threading.Thread.Sleep(1100);
            PlayniteLink.Remember(exe.ToUpperInvariant(), "1250", "Renamed", gameDir, list);
            string other = Path.Combine(gameDir, "Other.exe");
            WriteNativeX64Pe(other);
            PlayniteLink.Remember(other, "", "", null, list);
            entries = PlayniteLink.ReadList(list);
            Check(entries.Count == 2 && entries[0].Name == "Renamed" && entries[0].AddedUtc == first.AddedUtc.AddTicks(-(first.AddedUtc.Ticks % TimeSpan.TicksPerSecond))
                  && entries[1].Name == "Other",
                  "list: re-adding updates the line in place (keeping when it was added); a nameless game uses the exe name",
                  string.Join(", ", entries.Select(e => e.Name + "@" + e.AddedUtc.ToString("o")).ToArray()));
            File.AppendAllText(list, "garbage line\r\nsb-x\tonly\tthree\r\n\r\n# comment\r\nnot-sb\t1\tName\tC:\\x.exe\tC:\\\t2026-01-01T00:00:00Z\r\n");
            Check(PlayniteLink.ReadList(list).Count == 2, "list: malformed lines and comments are ignored", null);
            Check(PlayniteLink.Forget(other, list) && !PlayniteLink.Forget(other, list) && PlayniteLink.ReadList(list).Single().GameId == id,
                  "list: forget removes exactly that game, once", null);
            Check(!PlayniteLink.Forget(exe, Path.Combine(work, "none.txt")) && !File.Exists(Path.Combine(work, "none.txt")),
                  "list: forgetting with no list creates none", null);
            Check(!Directory.Exists(Path.Combine(work, "state", ".gp-recovery")), "list: no staging folders are left behind", null);

            // ---- detection from an install folder (no registry)
            string pn = Directory.CreateDirectory(Path.Combine(work, "Playnite")).FullName;
            Check(PlayniteLink.Detect(pn) == null && PlayniteLink.Detect(Path.Combine(work, "missing")) == null,
                  "detect: a folder without Playnite.DesktopApp.exe is not Playnite", null);
            File.WriteAllText(Path.Combine(pn, "Playnite.DesktopApp.exe"), "");
            var portable = PlayniteLink.Detect(pn);
            Check(portable != null && portable.Portable && string.Equals(portable.ConfigRoot, pn, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(portable.ExtensionsDir, Path.Combine(pn, "Extensions"), StringComparison.OrdinalIgnoreCase),
                  "detect: no uninstaller means portable, configuration beside the exe", portable == null ? "(null)" : portable.ExtensionsDir);
            File.WriteAllText(Path.Combine(pn, "unins000.exe"), "");
            var installed = PlayniteLink.Detect(pn);
            Check(installed != null && !installed.Portable && installed.ConfigRoot.EndsWith("Playnite", StringComparison.OrdinalIgnoreCase)
                  && !string.Equals(installed.ConfigRoot, pn, StringComparison.OrdinalIgnoreCase),
                  "detect: an installed Playnite keeps its configuration in %APPDATA%\\Playnite", installed == null ? "(null)" : installed.ConfigRoot);
            Check(PlayniteLink.CommandExe("\"C:\\Program Files\\Playnite\\Playnite.DesktopApp.exe\" --uridata \"%1\"") == "C:\\Program Files\\Playnite\\Playnite.DesktopApp.exe"
                  && PlayniteLink.CommandExe("C:\\Playnite\\Playnite.DesktopApp.exe --uridata %1") == "C:\\Playnite\\Playnite.DesktopApp.exe"
                  && PlayniteLink.CommandExe("") == null,
                  "detect: the exe is read from the playnite:// command line, quoted or not", null);

            // ---- installing the extension into a fake portable Playnite
            File.Delete(Path.Combine(pn, "unins000.exe"));
            var fake = PlayniteLink.Detect(pn);
            string src = Directory.CreateDirectory(Path.Combine(work, "payload")).FullName;
            File.WriteAllBytes(Path.Combine(src, PlayniteLink.ExtensionDll), Encoding.ASCII.GetBytes("MZ fake plugin v1"));
            File.WriteAllText(Path.Combine(src, "extension.yaml"), "Id: Shibaberg_Library\r\nVersion: 0.7\r\n");
            bool restart;
            Check(PlayniteLink.EnsureExtension(fake, src, out restart) == PlayniteLink.ExtensionState.Missing && !Directory.Exists(fake.ExtensionsDir),
                  "extension: an incomplete payload installs nothing", null);
            File.WriteAllBytes(Path.Combine(src, "icon.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            var s1 = PlayniteLink.EnsureExtension(fake, src, out restart);
            string dest = Path.Combine(fake.ExtensionsDir, PlayniteLink.ExtensionId);
            Check(s1 == PlayniteLink.ExtensionState.Installed && PlayniteLink.ExtensionFiles.All(f => File.Exists(Path.Combine(dest, f))
                  && File.ReadAllBytes(Path.Combine(dest, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(src, f)))),
                  "extension: copied into Extensions\\Shibaberg_Library", s1.ToString());
            Check(PlayniteLink.EnsureExtension(fake, src, out restart) == PlayniteLink.ExtensionState.UpToDate, "extension: a second run changes nothing", null);
            File.WriteAllText(Path.Combine(src, "extension.yaml"), "Id: Shibaberg_Library\r\nVersion: 0.8\r\n");
            Check(PlayniteLink.EnsureExtension(fake, src, out restart) == PlayniteLink.ExtensionState.Updated
                  && File.ReadAllText(Path.Combine(dest, "extension.yaml")).Contains("0.8"),
                  "extension: a changed file is updated", null);
            if (Path.DirectorySeparatorChar == '\\')
            {
                // Playnite holds its loaded dll open; Windows still lets it be renamed, which is what the update relies on.
                File.WriteAllBytes(Path.Combine(src, PlayniteLink.ExtensionDll), Encoding.ASCII.GetBytes("MZ fake plugin v2"));
                PlayniteLink.ExtensionState s2;
                using (new FileStream(Path.Combine(dest, PlayniteLink.ExtensionDll), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    s2 = PlayniteLink.EnsureExtension(fake, src, out restart);
                Check(s2 == PlayniteLink.ExtensionState.Updated && File.ReadAllText(Path.Combine(dest, PlayniteLink.ExtensionDll)) == "MZ fake plugin v2",
                      "extension: a dll in use is moved aside and replaced", s2.ToString());
                PlayniteLink.EnsureExtension(fake, src, out restart);
                Check(Directory.GetFiles(dest, "*.old-*").Length == 0, "extension: the moved-aside dll is cleaned up on the next run", null);
                File.WriteAllBytes(Path.Combine(src, PlayniteLink.ExtensionDll), Encoding.ASCII.GetBytes("MZ fake plugin v3"));
                using (new FileStream(Path.Combine(dest, PlayniteLink.ExtensionDll), FileMode.Open, FileAccess.Read, FileShare.None))
                    s2 = PlayniteLink.EnsureExtension(fake, src, out restart);
                Check(s2 == PlayniteLink.ExtensionState.Locked, "extension: a dll that cannot even be renamed is reported, not thrown", s2.ToString());
            }

            // ---- names
            Check(GameNames.CleanFolderName("My Game [Build 24911270] (OnlineFix)") == "My Game" && GameNames.CleanFolderName("[DODI] Some Game") == "Some Game"
                  && GameNames.CleanFolderName("Half-Life 2") == "Half-Life 2" && GameNames.CleanFolderName("Game_v1.2-") == "Game_v1.2",
                  "names: release tags in brackets and parentheses are dropped", GameNames.CleanFolderName("My Game [Build 24911270] (OnlineFix)"));
            Check(GameNames.Resolve(exe, "") == "My Game", "names: the game folder's name without tags when nothing better is known", GameNames.Resolve(exe, ""));
            string data = Directory.CreateDirectory(Path.Combine(gameDir, "Game_Data")).FullName;
            File.WriteAllText(Path.Combine(data, "app.info"), "Shiba Studio\nShiba Quest: Deluxe\n");
            Check(GameNames.Resolve(exe, "") == "Shiba Quest: Deluxe", "names: Unity's product name from <exe>_Data\\app.info", GameNames.Resolve(exe, ""));
            string vdf = Path.Combine(work, "appinfo.vdf");
            WriteAppInfo(vdf, 29, new[] { Tuple.Create(1250u, "Killing Floor", (string)null, (string[])null), Tuple.Create(480u, "Spacewar", (string)null, (string[])null) });
            string prevVdf = SteamAppInfo.PathOverride;
            SteamAppInfo.PathOverride = vdf;
            try
            {
                Check(GameNames.Resolve(exe, "1250") == "Killing Floor", "names: Steam's name from the local cache comes first", GameNames.Resolve(exe, "1250"));
                Check(GameNames.Resolve(exe, "480") == "Shiba Quest: Deluxe", "names: never \"Spacewar\" for an online-fix game", GameNames.Resolve(exe, "480"));

                // ---- the whole step after a patch, against the fake Playnite, without signalling anything
                var logs0 = new List<string>();
                PlayniteLink.Register(new PatchOptions { GameExe = exe, AppId = "1250" }, new PatchResult { Success = true, FinalExe = exe },
                    (l, m) => logs0.Add(l + ": " + m), fake, src, list, Path.Combine(work, "state", "icons"), false);
                var registered = PlayniteLink.ReadList(list).SingleOrDefault(e => e.GameId == id);
                Check(registered != null && registered.Name == "Killing Floor" && registered.AppId == "1250"
                      && string.Equals(registered.InstallDir, gameDir, StringComparison.OrdinalIgnoreCase)
                      && logs0.Count == 1 && logs0[0].StartsWith("Ok:") && logs0[0].Contains("Killing Floor"),
                      "register: the game is listed under its name with one OK line in the log", string.Join(" | ", logs0.ToArray()));
            }
            finally { SteamAppInfo.PathOverride = prevVdf; }
            var logs = new List<string>();
            string icons = Path.Combine(work, "state", "icons");
            string icon = Path.Combine(icons, id + ".png");
            Check(!File.Exists(icon) || new FileInfo(icon).Length > 0, "register: the exe icon is saved for Playnite (when Windows can extract one)", icon);
            logs.Clear();
            PlayniteLink.Register(new PatchOptions { GameExe = exe, AppId = "1250" }, new PatchResult { Success = true, FinalExe = exe },
                (l, m) => logs.Add(l + ": " + m), null, src, list, icons, false);
            Check(logs.Count == 1 && logs[0].StartsWith("Dim:"), "register: without Playnite it only notes that", string.Join(" | ", logs.ToArray()));
        }
        catch (Exception ex) { Check(false, "playnite tests ran without throwing", ex.ToString()); }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
