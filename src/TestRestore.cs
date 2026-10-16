using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Gp;

static partial class TestMain
{
    /// <summary>A stand-in emulator dll: a valid x64 PE carrying the "steam_settings" marker (InstallCheck.IsEmulatorDll).</summary>
    static byte[] FakeEmulatorDll(string scratch, string tag)
    {
        WriteNativeX64Pe(scratch);
        var bytes = File.ReadAllBytes(scratch).Concat(Encoding.ASCII.GetBytes("\0steam_settings\0" + tag + "\0")).ToArray();
        File.Delete(scratch);
        return bytes;
    }

    /// <summary>A stand-in Valve library: a valid x64 PE with interface strings and no emulator marker.</summary>
    static byte[] FakeValveDll(string scratch, string tag)
    {
        WriteNativeX64Pe(scratch);
        var bytes = File.ReadAllBytes(scratch).Concat(Encoding.ASCII.GetBytes("\0SteamUser021\0SteamClient017\0" + tag + "\0")).ToArray();
        File.Delete(scratch);
        return bytes;
    }

    /// <summary>Patches a fixture the way PatchRunner does, without needing the bundled emulator: originals into
    /// shibaberg_backup, then staged writes with the backup as external recovery, then the ledger.</summary>
    static List<FileWriteRecord> SimulatePatch(string root, string exe, string dll, byte[] emulator, byte[] unpackedExe, string appId)
    {
        var writes = new List<FileWriteRecord>();
        string installDir = Path.GetDirectoryName(dll);
        string backupDir = OriginalBackups.Dir(installDir);
        string hash;
        if (unpackedExe != null)
        {
            string exeBackup = OriginalBackups.Preserve(backupDir, exe, null, null, out hash);
            SafePersistence.Write(exe, s => s.Write(unpackedExe, 0, unpackedExe.Length), null, writes, null, exeBackup, null, hash);
        }
        if (File.Exists(dll))
        {
            string dllBackup = OriginalBackups.Preserve(backupDir, dll, null, null, out hash);
            SafePersistence.Write(dll, s => s.Write(emulator, 0, emulator.Length), null, writes, null, dllBackup, null, hash);
        }
        else SafePersistence.Write(dll, s => s.Write(emulator, 0, emulator.Length), null, writes);
        foreach (var dir in new[] { installDir, Path.GetDirectoryName(exe) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string txt = Path.Combine(dir, "steam_appid.txt");
            if (!File.Exists(txt) || File.ReadAllText(txt) != appId + "\r\n") SafePersistence.WriteText(txt, appId + "\r\n", writes);
        }
        BackupLedger.Record(backupDir, root, writes, null);
        Recovery.CollectStaging(writes);
        return writes;
    }

    /// <summary>Restore original + backup ledger (feature B).</summary>
    static void SelfTestRestore()
    {
        Console.WriteLine("\n[restore original]");
        string work = Path.Combine(Path.GetTempPath(), "gp_selftest_restore_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        try
        {
            string scratch = Path.Combine(work, "scratch.bin");

            // ---- Unity-like: Game.exe (SteamStub) + Game_Data\Plugins\x86_64\steam_api64.dll, a user save
            string unity = Directory.CreateDirectory(Path.Combine(work, "unity")).FullName;
            string uExe = Path.Combine(unity, "Game.exe");
            WriteSteamStub31(uExe, true);
            byte[] packed = File.ReadAllBytes(uExe);
            WriteNativeX64Pe(scratch);
            byte[] unpacked = File.ReadAllBytes(scratch);
            string plugins = Directory.CreateDirectory(Path.Combine(unity, "Game_Data", "Plugins", "x86_64")).FullName;
            string uDll = Path.Combine(plugins, "steam_api64.dll");
            byte[] valve = FakeValveDll(scratch, "v1");
            File.WriteAllBytes(uDll, valve);
            string save = Path.Combine(unity, "Game_Data", "save.dat");
            File.WriteAllText(save, "player progress");
            byte[] emu = FakeEmulatorDll(scratch, "emu");
            SimulatePatch(unity, uExe, uDll, emu, unpacked, "1250");

            var ledger = BackupLedger.Load(OriginalBackups.Existing(plugins));
            Check(ledger != null && ledger.Find(uExe) != null && !ledger.Find(uExe).Created && ledger.Find(uExe).Original == SafePersistence.Hash(packed)
                  && ledger.Find(Path.Combine(plugins, "steam_appid.txt")).Created
                  && ledger.Find(uDll).Written == SafePersistence.Hash(emu),
                  "ledger: replaced files keep their original hash, created files are marked, the written hash is kept", null);

            var plan = GameRestore.Plan(uExe, "");
            Check(plan.HasLedger && plan.Of(RestoreAction.Restore).Select(i => Path.GetFileName(i.Path)).OrderBy(n => n).SequenceEqual(new[] { "Game.exe", "steam_api64.dll" })
                  && plan.Of(RestoreAction.Delete).Count() == 2 && plan.Of(RestoreAction.Delete).All(i => Path.GetFileName(i.Path) == "steam_appid.txt")
                  && !plan.Of(RestoreAction.Leave).Any(),
                  "plan (Unity): exe and dll come back, both steam_appid.txt go, nothing else is touched", string.Join(" | ", plan.Describe().ToArray()));
            var res = GameRestore.Apply(plan, null);
            Check(res.Complete && File.ReadAllBytes(uExe).SequenceEqual(packed) && File.ReadAllBytes(uDll).SequenceEqual(valve),
                  "restore (Unity): the original exe and dll are back, byte for byte", res.Summary);
            Check(!File.Exists(Path.Combine(unity, "steam_appid.txt")) && !File.Exists(Path.Combine(plugins, "steam_appid.txt"))
                  && File.ReadAllText(save) == "player progress",
                  "restore (Unity): created files are gone, the user's files stay", null);
            Check(OriginalBackups.Existing(plugins) == null && !Directory.Exists(Path.Combine(plugins, ".gp-recovery")) && !Directory.Exists(Path.Combine(unity, ".gp-recovery")),
                  "restore (Unity): the backup folder and staging areas are removed", null);
            Check(GameRestore.Plan(uExe, "").NothingToDo, "restore (Unity): a second plan finds nothing to do", null);

            // ---- a game that shipped its own steam_appid.txt: the patch overwrites it, restore brings it back
            string shipped = Directory.CreateDirectory(Path.Combine(work, "shipped")).FullName;
            string sExe = Path.Combine(shipped, "Game.exe");
            WriteNativeX64Pe(sExe);
            string sDll = Path.Combine(shipped, "steam_api64.dll");
            File.WriteAllBytes(sDll, FakeValveDll(scratch, "v1"));
            File.WriteAllText(Path.Combine(shipped, "steam_appid.txt"), "480");
            SimulatePatch(shipped, sExe, sDll, emu, null, "1250");
            var sPlan = GameRestore.Plan(sExe, "");
            var sRes = GameRestore.Apply(sPlan, null);
            Check(sRes.Complete && File.ReadAllText(Path.Combine(shipped, "steam_appid.txt")) == "480",
                  "restore: a steam_appid.txt the game shipped is put back, not deleted", string.Join(" | ", sPlan.Describe().ToArray()));

            // ---- an install by an old version: flat goldberg_backup, no ledger, activated samples
            string old = Directory.CreateDirectory(Path.Combine(work, "old")).FullName;
            string oExe = Path.Combine(old, "Game.exe");
            WriteNativeX64Pe(oExe);
            string oDll = Path.Combine(old, "steam_api64.dll");
            byte[] oValve = FakeValveDll(scratch, "old");
            Directory.CreateDirectory(Path.Combine(old, OriginalBackups.LegacyFolderName));
            File.WriteAllBytes(Path.Combine(old, OriginalBackups.LegacyFolderName, "steam_api64.dll"), oValve);
            File.WriteAllBytes(oDll, emu);
            File.WriteAllText(Path.Combine(old, "steam_appid.txt"), "1250\r\n");
            string oSs = Directory.CreateDirectory(Path.Combine(old, "steam_settings")).FullName;
            string examples = ExampleTree();
            bool haveExamples = Directory.Exists(examples);
            if (haveExamples) File.Copy(Path.Combine(examples, "configs.app.EXAMPLE.ini"), Path.Combine(oSs, "configs.app.ini"));
            File.WriteAllText(Path.Combine(oSs, "steam_interfaces.txt"), "SteamUser021\r\n");
            File.WriteAllText(Path.Combine(oSs, "mine.txt"), "my own notes");
            var oPlan = GameRestore.Plan(oExe, "", haveExamples ? examples : null);
            var deletes = oPlan.Of(RestoreAction.Delete).Select(i => Path.GetFileName(i.Path)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var expectDeletes = (haveExamples ? new[] { "configs.app.ini" } : new string[0]).Concat(new[] { "steam_appid.txt", "steam_interfaces.txt" }).ToList();
            Check(!oPlan.HasLedger && oPlan.Of(RestoreAction.Restore).Single().Path == Path.GetFullPath(oDll) && deletes.SequenceEqual(expectDeletes)
                  && oPlan.Of(RestoreAction.Leave).Single().Path.EndsWith("mine.txt"),
                  "plan (old install): the flat backup is used, samples and generated files go, the user's file stays", string.Join(" | ", oPlan.Describe().ToArray()));
            var oRes = GameRestore.Apply(oPlan, null);
            Check(oRes.Complete && oRes.Restored == 1 && oRes.Left == 1 && oRes.NeedAttention == 0 && File.ReadAllBytes(oDll).SequenceEqual(oValve) && File.Exists(Path.Combine(oSs, "mine.txt"))
                  && !File.Exists(Path.Combine(old, "steam_appid.txt")) && !Directory.Exists(Path.Combine(old, OriginalBackups.LegacyFolderName)),
                  "restore (old install): the original is back and goldberg_backup is removed", oRes.Summary);

            // ---- a backup that is itself the emulator is never "restored"; Steam's verify is suggested
            string emuOnly = Directory.CreateDirectory(Path.Combine(work, "emuonly")).FullName;
            string eExe = Path.Combine(emuOnly, "Game.exe");
            WriteNativeX64Pe(eExe);
            string eDll = Path.Combine(emuOnly, "steam_api64.dll");
            File.WriteAllBytes(eDll, emu);
            string eBackup = OriginalBackups.Location(Path.Combine(emuOnly, OriginalBackups.FolderName), eDll);
            Directory.CreateDirectory(Path.GetDirectoryName(eBackup));
            File.WriteAllBytes(eBackup, FakeEmulatorDll(scratch, "older emu"));
            File.WriteAllText(eBackup + ".source.txt", Path.GetFullPath(eDll) + "\r\n" + SafePersistence.Hash(eBackup) + "\r\n");
            File.WriteAllText(Path.Combine(emuOnly, "steam_appid.txt"), "1250");
            var ePlan = GameRestore.Plan(eExe, "");
            Check(!ePlan.Of(RestoreAction.Restore).Any() && ePlan.Of(RestoreAction.Leave).Any(i => i.Path == Path.GetFullPath(eDll))
                  && ePlan.Notes.Any(n => n.Contains("steam://validate/1250")),
                  "plan: an emulator-only backup is rejected and Steam's verify is suggested", string.Join(" | ", ePlan.Describe().ToArray()));

            // ---- a game update after the patch: the newer dll is not overwritten, and a re-patch backs it up
            string upd = Directory.CreateDirectory(Path.Combine(work, "updated")).FullName;
            string pExe = Path.Combine(upd, "Game.exe");
            WriteNativeX64Pe(pExe);
            string pDll = Path.Combine(upd, "steam_api64.dll");
            File.WriteAllBytes(pDll, FakeValveDll(scratch, "v1"));
            SimulatePatch(upd, pExe, pDll, emu, null, "1250");
            byte[] v2 = FakeValveDll(scratch, "v2 after a Steam update");
            File.WriteAllBytes(pDll, v2);
            var uPlan = GameRestore.Plan(pExe, "");
            Check(uPlan.KeepBackups && uPlan.Of(RestoreAction.Leave).Any(i => i.Path == Path.GetFullPath(pDll) && i.Reason.StartsWith("changed")),
                  "plan: a library the game updated after the patch is left alone and the backups kept", string.Join(" | ", uPlan.Describe().ToArray()));
            var preserveLog = new List<string>();
            string bDir = OriginalBackups.Existing(upd);
            string reTaken = OriginalBackups.Preserve(bDir, pDll, (l, m) => preserveLog.Add(m));
            Check(reTaken != null && File.ReadAllBytes(reTaken).SequenceEqual(v2) && preserveLog.Any(m => m.Contains("game update")),
                  "backup: a re-patch after a game update keeps the new original, not the outdated one", string.Join(" | ", preserveLog.ToArray()));
            File.WriteAllBytes(pDll, emu);
            string again = OriginalBackups.Preserve(bDir, pDll, (l, m) => preserveLog.Add(m));
            Check(again == reTaken && File.ReadAllBytes(again).SequenceEqual(v2), "backup: the emulator in place never replaces the kept original", null);

            // ---- a failing restore puts back what it already restored and deletes nothing
            string fail = Directory.CreateDirectory(Path.Combine(work, "fail")).FullName;
            string fExe = Path.Combine(fail, "Game.exe");
            WriteSteamStub31(fExe, true);
            string fDll = Path.Combine(fail, "steam_api64.dll");
            File.WriteAllBytes(fDll, FakeValveDll(scratch, "f"));
            SimulatePatch(fail, fExe, fDll, emu, unpacked, "1250");
            var fPlan = GameRestore.Plan(fExe, "");
            var lastRestore = fPlan.Of(RestoreAction.Restore).Last();
            File.AppendAllText(lastRestore.Source, "tampered");   // the copy no longer matches the planned hash
            var fRes = GameRestore.Apply(fPlan, null);
            var firstRestore = fPlan.Of(RestoreAction.Restore).First();
            Check(fRes.Failed == 1 && fRes.Restored == 0 && File.Exists(Path.Combine(fail, "steam_appid.txt")) && OriginalBackups.Existing(fail) != null
                  && File.ReadAllBytes(firstRestore.Path).SequenceEqual(firstRestore.Path.EndsWith(".exe") ? unpacked : emu),
                  "restore: a failure undoes the files already put back and deletes nothing", fRes.Summary + " | " + string.Join(" | ", fRes.Messages.ToArray()));

            // ---- the real pipeline, where the bundled emulator dlls exist (the repo after tools\build-shibaberg.ps1)
            if (File.Exists(Tools.ApiDll64) && File.Exists(Tools.ApiDll86))
            {
                string real = Directory.CreateDirectory(Path.Combine(work, "real")).FullName;
                string rExe = Path.Combine(real, "Game.exe");
                WriteSteamStub31(rExe, true);
                byte[] rPacked = File.ReadAllBytes(rExe);
                string rDll = Path.Combine(real, "steam_api64.dll");
                byte[] rValve = FakeValveDll(scratch, "real");
                File.WriteAllBytes(rDll, rValve);
                File.WriteAllText(Path.Combine(real, "notes.txt"), "keep");
                var run = new PatchRunner().Run(new PatchOptions { GameExe = rExe, AppId = "1250", CreateSettings = true }, CancellationToken.None);
                Check(run.Success && ShibalessUnpacker_HasNoStub(rExe) && InstallCheck.IsEmulatorDll(rDll), "pipeline: the fixture patched (unpacked, emulator in place)", run.Summary);
                var rPlan = GameRestore.Plan(rExe, "");
                var rRes = GameRestore.Apply(rPlan, null);
                Check(rRes.Complete && File.ReadAllBytes(rExe).SequenceEqual(rPacked) && File.ReadAllBytes(rDll).SequenceEqual(rValve)
                      && !Directory.Exists(Path.Combine(real, "steam_settings")) && !File.Exists(Path.Combine(real, "steam_appid.txt"))
                      && File.ReadAllText(Path.Combine(real, "notes.txt")) == "keep" && OriginalBackups.Existing(real) == null,
                      "pipeline: Restore original undoes a real patch completely (exe, dll, steam_settings, steam_appid.txt)",
                      rRes.Summary + " | " + string.Join(" | ", rPlan.Describe().ToArray()));

                // Unreal: launcher + Proj\Binaries\Win64\Proj-Win64-Shipping.exe + Engine\...\steam_api64.dll
                string ue = Directory.CreateDirectory(Path.Combine(work, "ue")).FullName;
                string launcher = Path.Combine(ue, "Proj.exe");
                WriteNativeX64Pe(launcher);
                string ship = Path.Combine(Directory.CreateDirectory(Path.Combine(ue, "Proj", "Binaries", "Win64")).FullName, "Proj-Win64-Shipping.exe");
                WriteSteamStub31(ship, true);
                byte[] shipPacked = File.ReadAllBytes(ship);
                string sdk = Directory.CreateDirectory(Path.Combine(ue, "Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv157", "Win64")).FullName;
                string ueDll = Path.Combine(sdk, "steam_api64.dll");
                File.WriteAllBytes(ueDll, rValve);
                var ueRun = new PatchRunner().Run(new PatchOptions { GameExe = launcher, AppId = "1250" }, CancellationToken.None);
                var uePlan = GameRestore.Plan(launcher, "");
                var ueRes = GameRestore.Apply(uePlan, null);
                Check(ueRun.Success && ueRes.Complete && File.ReadAllBytes(ship).SequenceEqual(shipPacked) && File.ReadAllBytes(ueDll).SequenceEqual(rValve)
                      && !File.Exists(Path.Combine(Path.GetDirectoryName(ship), "steam_appid.txt")) && !File.Exists(Path.Combine(ue, "steam_appid.txt")),
                      "pipeline (Unreal): the -Shipping exe, the Engine library and every steam_appid.txt are restored",
                      ueRun.Summary + " | " + ueRes.Summary + " | " + string.Join(" | ", uePlan.Describe().ToArray()));
            }
            else Console.WriteLine("      | (the bundled emulator dlls are not beside the self-test - the PatchRunner round trips are skipped)");
        }
        catch (Exception ex) { Check(false, "restore tests ran without throwing", ex.ToString()); }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    static bool ShibalessUnpacker_HasNoStub(string exe) { return Shibaless.ShibalessUnpacker.HasStubSection(exe) == false; }
}
