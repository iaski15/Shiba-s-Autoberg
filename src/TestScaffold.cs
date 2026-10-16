using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Gp;

static partial class TestMain
{
    /// <summary>The upstream steam_settings.EXAMPLE tree beside the self-test (the repo root when run from there).</summary>
    static string ExampleTree()
    {
        string tools = Tools.SettingsExampleDir;
        if (Directory.Exists(tools)) return tools;
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shibaberg", "post_build", "steam_settings.EXAMPLE");
    }

    /// <summary>steam_settings example handling and activated-example cleanup (feature A).</summary>
    static void SelfTestScaffold()
    {
        Console.WriteLine("\n[steam_settings samples]");
        string examples = ExampleTree();
        if (!Directory.Exists(examples))
        {
            Check(false, "steam_settings.EXAMPLE tree is present for the scaffold tests", examples);
            return;
        }
        string work = Path.Combine(Path.GetTempPath(), "gp_selftest_scaffold_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        try
        {
            // ---- the scaffold keeps ".EXAMPLE" in every name, so nothing it copies is read by the emulator
            string install = Directory.CreateDirectory(Path.Combine(work, "install")).FullName;
            var plan = SettingsScaffold.Plan(examples, install);
            var names = plan.Files.Select(f => f.Value.Substring(plan.Directory.Length + 1)).ToList();
            Check(plan.Status == SettingsStatus.Copied && names.Count > 20
                  && names.All(n => n.IndexOf("EXAMPLE", StringComparison.OrdinalIgnoreCase) >= 0),
                  "scaffold: every copied sample keeps its EXAMPLE name", string.Join(", ", names.Where(n => n.IndexOf("EXAMPLE", StringComparison.OrdinalIgnoreCase) < 0).ToArray()));
            SettingsScaffold.Apply(plan, new List<FileWriteRecord>());
            string ss = Path.Combine(install, "steam_settings");
            Check(File.Exists(Path.Combine(ss, "configs.user.EXAMPLE.ini")) && File.Exists(Path.Combine(ss, "steam_appid.EXAMPLE.txt"))
                  && !File.Exists(Path.Combine(ss, "steam_appid.txt")) && !File.Exists(Path.Combine(ss, "configs.app.ini"))
                  && !File.Exists(Path.Combine(ss, "configs.user.ini")) && !Directory.Exists(Path.Combine(ss, "controller")),
                  "scaffold: no live steam_appid.txt, configs.*.ini or controller\\ is created", null);
            Check(SettingsScaffold.FindActivatedExamples(ss, examples).Count == 0,
                  "scaffold: a fresh scaffold has no activated samples", null);

            // ---- an install damaged by an older release: samples under their stripped names
            string old = Directory.CreateDirectory(Path.Combine(work, "old")).FullName;
            string oldSs = Directory.CreateDirectory(Path.Combine(old, "steam_settings")).FullName;
            File.WriteAllText(Path.Combine(oldSs, "steam_appid.txt"), "1337\r\n");
            File.Copy(Path.Combine(examples, "configs.app.EXAMPLE.ini"), Path.Combine(oldSs, "configs.app.ini"));
            File.Copy(Path.Combine(examples, "configs.user.EXAMPLE.ini"), Path.Combine(oldSs, "configs.user.ini"));
            File.WriteAllText(Path.Combine(oldSs, "configs.main.ini"), File.ReadAllText(Path.Combine(examples, "configs.main.EXAMPLE.ini")) + "\r\n# mine\r\n");
            File.WriteAllText(Path.Combine(oldSs, "achievements.json"), "[]");
            string ctl = Directory.CreateDirectory(Path.Combine(oldSs, "controller")).FullName;
            File.Copy(Path.Combine(examples, "controller.EXAMPLE", "InGameControls.txt"), Path.Combine(ctl, "InGameControls.txt"));
            File.WriteAllText(Path.Combine(oldSs, "steam_interfaces.txt"), "SteamUser021\r\n");

            var activated = SettingsScaffold.FindActivatedExamples(oldSs, examples).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Check(activated.SequenceEqual(new[] { "configs.app.ini", "configs.user.ini", "InGameControls.txt", "steam_appid.txt" }, StringComparer.OrdinalIgnoreCase),
                  "activated samples: byte-identical copies and the 1337 steam_appid.txt are found, edited and own files are not",
                  string.Join(", ", activated.ToArray()));

            // configs.user.ini moved the saves next to the dll; without migration it has to stay
            string localSaves = SettingsScaffold.ExampleLocalSaveDir(oldSs);
            string localApp = Directory.CreateDirectory(Path.Combine(localSaves, "1337", "remote")).FullName;
            File.WriteAllText(Path.Combine(localApp, "slot1.sav"), "progress");
            Check(string.Equals(localSaves, Path.Combine(old, "path", "relative", "to", "dll"), StringComparison.OrdinalIgnoreCase),
                  "activated samples: the sample local_save_path resolves beside the dll", localSaves);

            var keep = SettingsScaffold.CleanUpActivatedExamples(oldSs, "1250", false, examples);
            Check(File.Exists(Path.Combine(oldSs, "configs.user.ini")) && keep.Kept.Any(k => k.EndsWith("configs.user.ini", StringComparison.OrdinalIgnoreCase))
                  && !File.Exists(Path.Combine(oldSs, "steam_appid.txt")) && !File.Exists(Path.Combine(oldSs, "configs.app.ini"))
                  && !Directory.Exists(ctl),
                  "cleanup: samples deleted, the emptied controller\\ removed, configs.user.ini kept while saves sit beside the dll",
                  string.Join(" | ", keep.Messages.ToArray()));
            Check(File.Exists(Path.Combine(oldSs, "configs.main.ini")) && File.Exists(Path.Combine(oldSs, "achievements.json"))
                  && File.Exists(Path.Combine(oldSs, "steam_interfaces.txt")),
                  "cleanup: edited and generated files are left alone", null);

            // migration copies (never moves) into the global save root, only when the target is new
            string globalRoot = Path.Combine(work, "GSE Saves");
            Directory.CreateDirectory(Path.Combine(globalRoot, "1250"));
            var blocked = SettingsScaffold.CleanUpActivatedExamples(oldSs, "1250", true, examples, globalRoot);
            Check(File.Exists(Path.Combine(oldSs, "configs.user.ini")) && blocked.MigratedTo.Length == 0,
                  "cleanup: an existing global save folder is never overwritten", string.Join(" | ", blocked.Messages.ToArray()));
            Directory.Delete(Path.Combine(globalRoot, "1250"));
            var moved = SettingsScaffold.CleanUpActivatedExamples(oldSs, "1250", true, examples, globalRoot);
            string copied = Path.Combine(globalRoot, "1250", "remote", "slot1.sav");
            Check(!File.Exists(Path.Combine(oldSs, "configs.user.ini")) && File.Exists(copied) && File.ReadAllText(copied) == "progress"
                  && File.Exists(Path.Combine(localApp, "slot1.sav")),
                  "cleanup: with migration the saves are copied to GSE Saves\\<appid> and configs.user.ini goes", string.Join(" | ", moved.Messages.ToArray()));

            // ---- install check: steam_settings\steam_appid.txt overrides the root copy in the emulator
            string ic = Directory.CreateDirectory(Path.Combine(work, "ic")).FullName;
            string icExe = Path.Combine(ic, "Game.exe");
            WritePeWithImport(icExe, "steam_api64.dll");
            string emuTmp = Path.Combine(ic, "emu.tmp");
            WriteNativeX64Pe(emuTmp);
            File.WriteAllBytes(Path.Combine(ic, "steam_api64.dll"), File.ReadAllBytes(emuTmp).Concat(Encoding.ASCII.GetBytes("\0steam_settings\0")).ToArray());
            File.Delete(emuTmp);
            File.WriteAllText(Path.Combine(ic, "steam_appid.txt"), "1250\r\n");
            Func<List<InstallCheckItem>> icRun = () => InstallCheck.Run(icExe, "1250", false);
            Check(!icRun().Any(i => i.Title.Contains("steam_settings")), "install check: no steam_settings\\steam_appid.txt, no complaint", null);
            Directory.CreateDirectory(Path.Combine(ic, "steam_settings"));
            File.WriteAllText(Path.Combine(ic, "steam_settings", "steam_appid.txt"), "1337");
            var icBad = icRun();
            Check(icBad.Any(i => i.Status == CheckStatus.Fail && i.Title.Contains("steam_settings")),
                  "install check: a steam_settings\\steam_appid.txt with another AppID fails", string.Join(" | ", icBad.Select(i => i.ToString()).ToArray()));
            File.WriteAllText(Path.Combine(ic, "steam_settings", "steam_appid.txt"), "1250\n");
            Check(!icRun().Any(i => i.Status == CheckStatus.Fail && i.Title.Contains("steam_settings")),
                  "install check: a matching steam_settings\\steam_appid.txt passes", null);
            Check(!InstallCheck.Run(icExe, "480", true).Any(i => i.Title.Contains("steam_settings")),
                  "install check: online-fix ignores steam_settings (Valve's library never reads it)", null);
        }
        catch (Exception ex) { Check(false, "steam_settings sample tests ran without throwing", ex.ToString()); }
        finally { try { Directory.Delete(work, true); } catch { } }

        Console.WriteLine("\n[settings pruning]");
        string temp = Path.Combine(Path.GetTempPath(), "gp_selftest_prune_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(temp);
        try
        {
            Check(!AppSettings.KeepFolder(temp, false) && AppSettings.KeepFolder(temp, true),
                  "pruning: a temp-folder game is dropped, unless the state folder is redirected (test runs)", temp);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Check(AppSettings.KeepFolder(home, false), "pruning: an existing folder is kept", home);
            string gone = Path.Combine(home, "gp_selftest_gone_" + Guid.NewGuid().ToString("N"));
            Check(!AppSettings.KeepFolder(gone, false), "pruning: a missing folder on a present drive is dropped", gone);
            if (Path.DirectorySeparatorChar == '\\')
            {
                char missing = "QRSTUVWXYZ".FirstOrDefault(c => !Directory.Exists(c + ":\\"));
                if (missing != '\0')
                    Check(AppSettings.KeepFolder(missing + ":\\Games\\Some Game", false), "pruning: a folder on an unplugged drive is kept", missing + ":");
            }
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }
}
