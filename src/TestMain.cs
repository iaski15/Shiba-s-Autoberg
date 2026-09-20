using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Gp;

static class TestMain
{
    static int pass = 0, fail = 0;

    static void Check(bool cond, string name, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
        else { fail++; Console.WriteLine("  FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "   -> " + detail)); }
    }

    static int Main()
    {
        // The pipeline tests below run the real PatchRunner, which persists an undo journal. Redirect it
        // to a throwaway file for the whole run: otherwise the self-test clobbers a real pending undo and
        // leaves a journal pointing at temp folders that are deleted before it returns.
        string undoDir = Path.Combine(Path.GetTempPath(), "gp_selftest_undo_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Recovery.JournalPathOverride = Path.Combine(undoDir, "journal.txt");
        Recovery.StateRootOverride = undoDir;

        Console.WriteLine("== Goldberg Patcher self-test ==\n[PE analysis]");
        var root = AppDomain.CurrentDomain.BaseDirectory;

        var p64 = PeReader.Analyze(Path.Combine(root, @"release\regular\x64\steam_api64.dll"));
        Check(p64.Machine == 0x8664, "x64 dll machine=AMD64", "0x" + p64.Machine.ToString("X"));
        Check(p64.Arch == ExeArch.X64, "x64 dll arch resolved", p64.MachineText);

        var p86 = PeReader.Analyze(Path.Combine(root, @"release\regular\x86\steam_api.dll"));
        Check(p86.Machine == 0x14c, "x86 dll machine=I386", "0x" + p86.Machine.ToString("X"));
        Check(p86.Arch == ExeArch.X86, "x86 dll arch resolved", p86.MachineText);

        var cli = PeReader.Analyze(Path.Combine(root, @"steamless\Steamless.CLI.exe"));
        Check(cli.Managed, "Steamless CLI detected as .NET", cli.MachineText);
        var expectedAnyCpu = Environment.Is64BitOperatingSystem ? ExeArch.X64 : ExeArch.X86;
        Check(cli.Arch == ExeArch.X86 && !cli.AnyCpu, "Steamless CLI 32-bit execution flags", cli.MachineText);
        var selfPe = PeReader.Analyze(typeof(TestMain).Assembly.Location);
        Check(selfPe.AnyCpu && selfPe.Arch == expectedAnyCpu, "compiler-produced AnyCPU resolution", selfPe.MachineText);

        // ---- synthetic PEs: regression tests for the PeReader offset bugs (bug 1) ----
        Console.WriteLine("\n[PE analysis: synthetic]");
        string synDir = Path.Combine(Path.GetTempPath(), "gp_selftest_pe_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(synDir);
        try
        {
            // (a) Minimal native x64 PE with a nasty TimeDateStamp low half. The old code read that low
            //     half as SizeOfOptionalHeader and ran off the end of this small file
            //     (EndOfStreamException -> "Could not read the executable as a PE file").
            var nativePath = Path.Combine(synDir, "native_x64.exe");
            WriteNativeX64Pe(nativePath);
            PeInfo npe = null; bool noThrow = true;
            try { npe = PeReader.Analyze(nativePath); }
            catch (Exception ex) { noThrow = false; Console.WriteLine("      | threw: " + ex.GetType().Name + ": " + ex.Message); DumpPe(nativePath); }
            Check(noThrow, "small native x64 PE parses without throwing", noThrow ? null : "(see above)");
            if (noThrow)
            {
                Check(npe.Machine == 0x8664, "native x64 machine=AMD64", "0x" + npe.Machine.ToString("X"));
                Check(npe.Arch == ExeArch.X64, "native x64 arch resolved", npe.MachineText);
                Check(!npe.Managed && !npe.AnyCpu, "native x64 not managed/AnyCPU", npe.MachineText);
            }

            // (b) Minimal .NET PE32 with COR header flags=0 at +8 -> AnyCPU. This is the case that used to
            //     pass only by luck: the section table was read from random bytes and the flags were read
            //     from the wrong offset inside the COR header.
            var dotnetPath = Path.Combine(synDir, "anycpu_dotnet.exe");
            WriteAnyCpuDotNetPe(dotnetPath);
            PeInfo dpe = null; bool noThrow2 = true;
            try { dpe = PeReader.Analyze(dotnetPath); }
            catch (Exception ex) { noThrow2 = false; Console.WriteLine("      | threw: " + ex.GetType().Name + ": " + ex.Message); DumpPe(dotnetPath); }
            Check(noThrow2, ".NET AnyCPU PE parses without throwing", noThrow2 ? null : "(see above)");
            if (noThrow2)
            {
                Check(dpe.Managed, ".NET PE detected as managed", dpe.MachineText);
                Check(dpe.AnyCpu, "COR ILONLY flag -> AnyCPU", dpe.MachineText);
                Check(dpe.Arch == expectedAnyCpu, "synthetic AnyCPU arch resolution", dpe.MachineText);

            var x86NetPath = Path.Combine(synDir, "x86_dotnet.exe");
            WriteAnyCpuDotNetPe(x86NetPath, 0x3);
            PeInfo xpe = null; bool noThrow3 = true;
            try { xpe = PeReader.Analyze(x86NetPath); }
            catch (Exception ex) { noThrow3 = false; Console.WriteLine("      | threw: " + ex.GetType().Name + ": " + ex.Message); }
            Check(noThrow3, ".NET x86-flagged PE parses without throwing", noThrow3 ? null : "(see above)");
            if (noThrow3)
            {
                Check(xpe.Managed && !xpe.AnyCpu && xpe.Arch == ExeArch.X86, "COR 32BITREQUIRED -> forced x86", xpe.MachineText);
            }

            var preferredPath = Path.Combine(synDir, "preferred32.exe");
            WriteAnyCpuDotNetPe(preferredPath, 0x20001);
            var preferred = PeReader.Analyze(preferredPath);
            Check(preferred.Managed && !preferred.AnyCpu && preferred.Arch == ExeArch.X86,
                  "COR 32BITPREFERRED executable resolves x86");
            var preferredDll = File.ReadAllBytes(preferredPath);
            W16(preferredDll, 0x80 + 22, 0x2102);
            File.WriteAllBytes(preferredPath, preferredDll);
            preferred = PeReader.Analyze(preferredPath);
            Check(preferred.Managed && preferred.AnyCpu && preferred.Arch == expectedAnyCpu,
                  "COR 32BITPREFERRED ignored for DLL");

            var armPath = Path.Combine(synDir, "arm64_native.exe");
            WriteNativeX64Pe(armPath);
            {
                var bytes = File.ReadAllBytes(armPath);
                var peOff = BitConverter.ToInt32(bytes, 0x3C);
                W16(bytes, peOff + 4, 0xAA64);
                File.WriteAllBytes(armPath, bytes);
            }
            PeInfo ape = null; bool noThrow4 = true;
            try { ape = PeReader.Analyze(armPath); }
            catch (Exception ex) { noThrow4 = false; Console.WriteLine("      | threw: " + ex.GetType().Name + ": " + ex.Message); }
            Check(noThrow4, "ARM64 PE parses without throwing", noThrow4 ? null : "(see above)");
            if (noThrow4)
            {
                Check(ape.Arch == ExeArch.Unknown && !ape.Managed, "ARM64 is explicitly unsupported (not x64)", ape.MachineText);
            }

            var truncPath = Path.Combine(synDir, "truncated.exe");
            var fullBytes = File.ReadAllBytes(nativePath);
            File.WriteAllBytes(truncPath, fullBytes.Take(fullBytes.Length / 2).ToArray());
            bool truncRejected = false;
            try { PeReader.Analyze(truncPath); }
            catch { truncRejected = true; }
            Check(truncRejected, "truncated PE rejected", null);

            var noPePath = Path.Combine(synDir, "notpe.exe");
            File.WriteAllBytes(noPePath, new byte[] { 0x4D, 0x5A, 0x00, 0x00 });
            bool noPeRejected = false;
            try { PeReader.Analyze(noPePath); }
            catch { noPeRejected = true; }
            Check(noPeRejected, "non-PE file rejected", null);
            }
        }
        finally
        {
            try { Directory.Delete(synDir, true); } catch { }
        }

        Console.WriteLine("\n[integration: fake game]");
        string work = Path.Combine(Path.GetTempPath(), "gp_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        try
        {
            var gameDir = Path.Combine(work, "MyGame");
            Directory.CreateDirectory(gameDir);
            var dummyExe = Path.Combine(gameDir, "MyGame.exe");
            File.Copy(Path.Combine(root, @"steamless\Steamless.CLI.exe"), dummyExe);
            var exeHash = SafePersistence.Hash(dummyExe);
            var stalePaths = new[] { dummyExe + ".unpacked.exe", Path.Combine(gameDir, "MyGame.unpacked.exe"), Path.Combine(gameDir, "unrelated.unpacked.exe") };
            foreach (var stale in stalePaths)
            {
                WriteNativeX64Pe(stale);
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(1));
            }

            byte[] origDllBytes = File.ReadAllBytes(Path.Combine(root, @"release\regular\x64\steam_api64.dll"));
            origDllBytes[origDllBytes.Length - 1] ^= 1;
            File.WriteAllBytes(Path.Combine(gameDir, "steam_api.dll"), origDllBytes);

            // nested copy deep in the tree SHOULD be picked up by the full-folder scan
            var deepDir = gameDir;
            for (int i = 0; i < 5; i++) deepDir = Directory.CreateDirectory(Path.Combine(deepDir, "deep" + i)).FullName;
            File.WriteAllBytes(Path.Combine(deepDir, "steam_api64.dll"), new byte[] { 1 });

            var opts = new PatchOptions
            {
                GameExe = dummyExe,
                AppId = "1250",
                UnpackDrm = true,
                Backup = true,
                WriteAppIdTxt = true,
                CreateSettings = true,
                GenerateInterfaces = true,
            };
            var runner = new PatchRunner();
            var logs = new List<string>();
            runner.LogLine += e => { logs.Add(e.Message); Console.WriteLine("      | " + e.Message.Replace("\n", " / ")); };
            int lastPct = -1; runner.ProgressChanged += p => lastPct = p;

            var res = runner.Run(opts, CancellationToken.None);

            Check(res.Success, "pipeline succeeded", res.Summary);
            Check(!res.Unpacked, "dummy exe not claimed as unpacked");
            Check(File.Exists(dummyExe), "exe still present after unpack attempt");
            Check(SafePersistence.Hash(dummyExe) == exeHash && stalePaths.All(File.Exists),
                  "stale explicit and scanned outputs cannot replace updated exe");
            Check(lastPct >= 100, "progress reached 100", lastPct.ToString());
            Check(logs.Any(l => l.IndexOf("No Steam DRM", StringComparison.OrdinalIgnoreCase) >= 0),
                  "unpack gracefully skipped for non-packed exe");

            var newDll = File.ReadAllBytes(Path.Combine(gameDir, "steam_api.dll"));
            var goldberg86 = File.ReadAllBytes(Path.Combine(root, @"release\regular\x86\steam_api.dll"));
            Check(!newDll.SequenceEqual(origDllBytes), "existing steam_api.dll was replaced");
            Check(newDll.SequenceEqual(goldberg86), "replaced dll matches bundled goldberg x86");

            var bakDir = Path.Combine(gameDir, "goldberg_backup");
            var bakCandidates = Directory.GetFiles(Path.Combine(bakDir, "sources"), "steam_api.dll", SearchOption.AllDirectories);
            Check(bakCandidates.Length == 1 &&
                  File.ReadAllBytes(bakCandidates[0]).SequenceEqual(origDllBytes),
                  "original dll backed up intact");
            if (bakCandidates.Length == 1)
            {
                var manifest = File.ReadAllLines(bakCandidates[0] + ".source.txt");
                Check(manifest.Length == 2 &&
                      string.Equals(manifest[0], Path.Combine(gameDir, "steam_api.dll"), StringComparison.OrdinalIgnoreCase) &&
                      manifest[1] == SafePersistence.Hash(bakCandidates[0]),
                      "backup manifest preserves source path + hash");
            }

            var res1b = new PatchRunner().Run(opts, CancellationToken.None);
            Check(res1b.Success, "re-patch succeeded", res1b.Summary);
            var bakAfter = Directory.GetFiles(Path.Combine(bakDir, "sources"), "steam_api.dll", SearchOption.AllDirectories);
            Check(bakAfter.Length == 1 && bakAfter[0] == bakCandidates[0] &&
                  File.ReadAllBytes(bakAfter[0]).SequenceEqual(origDllBytes),
                  "second run keeps the first-run original (not the Goldberg dll)");

            Check(File.Exists(Path.Combine(gameDir, "steam_appid.txt")) &&
                  File.ReadAllText(Path.Combine(gameDir, "steam_appid.txt")).Trim() == "1250",
                  "steam_appid.txt written with AppID");

            var settingsIni = Path.Combine(gameDir, "steam_settings", "configs.main.ini");
            Check(File.Exists(settingsIni), "steam_settings copied & '.EXAMPLE' stripped", settingsIni);
            var iface = Path.Combine(gameDir, "steam_settings", "steam_interfaces.txt");
            Check(File.Exists(iface) && File.ReadAllLines(iface).Any(l => l.Trim().Length > 0),
                  "steam_interfaces.txt generated into steam_settings");

            // ---- appid prefill helper ----
            Console.WriteLine("\n[helpers]");
            var r2 = new PatchRunner();
            var found = r2.FindExistingAppId(gameDir, work);
            Check(found == "1250", "FindExistingAppId reads steam_appid.txt", found ?? "(null)");

            var near = PatchRunner.FindSteamApiFiles(gameDir);
            Check(near.Any(f => Path.GetDirectoryName(f).Equals(gameDir, StringComparison.OrdinalIgnoreCase)),
                  "nearest steam_api dll found at exe level");
            Check(near.Any(f => f.StartsWith(deepDir, StringComparison.OrdinalIgnoreCase)),
                  "full-folder scan finds dlls deep in the tree");
            Check(near.IndexOf(near.First(f => Path.GetDirectoryName(f).Equals(gameDir, StringComparison.OrdinalIgnoreCase)))
                  < near.IndexOf(near.First(f => f.StartsWith(deepDir, StringComparison.OrdinalIgnoreCase))),
                  "nearest match ranked first");

            // ---- batch appid detection (offline sources only) ----
            Console.WriteLine("\n[batch appid detection]");
            var detCached = AppIdDetector.Detect(dummyExe, "777", false);
            Check(detCached.AppId == "777" && detCached.Source == "saved",
                  "cached id takes precedence over files in the tree", detCached.AppId + " / " + detCached.Source);

            var detFile = AppIdDetector.Detect(dummyExe, "", false);
            Check(detFile.AppId == "1250" && detFile.Source == "steam_appid.txt",
                  "steam_appid.txt in install tree detected", detFile.AppId + " / " + detFile.Source);

            var emptyDir = Directory.CreateDirectory(Path.Combine(work, "NoApp")).FullName;
            File.Copy(dummyExe, Path.Combine(emptyDir, "NoApp.exe"));
            var detNone = AppIdDetector.Detect(Path.Combine(emptyDir, "NoApp.exe"), "", false);
            Check(!detNone.Found && detNone.AppId == "" && detNone.Source == "",
                  "no local source -> not found (offline)", detNone.AppId + " / " + detNone.Source);

            var detBad = AppIdDetector.Detect(dummyExe, " 840291 ", false);
            Check(detBad.AppId == "840291" && detBad.Source == "saved", "cached id trimmed before use", detBad.AppId);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }

        // ---- generic online-fix (Spacewar) ----
        Console.WriteLine("\n[integration: generic online-fix]");
        string work2 = Path.Combine(Path.GetTempPath(), "gp_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        try
        {
            var gameDir2 = Directory.CreateDirectory(Path.Combine(work2, "OFGame")).FullName;
            var exe2 = Path.Combine(gameDir2, "OFGame.exe");
            File.Copy(Path.Combine(root, @"steamless\Steamless.CLI.exe"), exe2);

            var opts2 = new PatchOptions
            {
                GameExe = exe2,
                AppId = "",
                UnpackDrm = false,
                Backup = false,
                WriteAppIdTxt = true,
                CreateSettings = false,
                GenerateInterfaces = false,
                OnlineFix = true,
            };
            var runner2 = new PatchRunner();
            var res2 = runner2.Run(opts2, CancellationToken.None);

            Check(res2.Success, "online-fix pipeline succeeded", res2.Summary);
            Check(File.Exists(Path.Combine(gameDir2, "steam_appid.txt")) &&
                  File.ReadAllText(Path.Combine(gameDir2, "steam_appid.txt")).Trim() == "480",
                  "online-fix forces steam_appid.txt to 480",
                  File.Exists(Path.Combine(gameDir2, "steam_appid.txt"))
                      ? File.ReadAllText(Path.Combine(gameDir2, "steam_appid.txt")).Trim() : "(missing)");
            Check(Directory.Exists(Path.Combine(gameDir2, "steam_settings")) &&
                  Directory.GetFiles(Path.Combine(gameDir2, "steam_settings")).Length > 0,
                  "online-fix creates the steam_settings scaffold even with CreateSettings off");


            // previously Goldberg-patched game (live dll IS a bundled Goldberg build):
            // original must be restored from goldberg_backup – required for online-fix to work
            var gameDir3 = Directory.CreateDirectory(Path.Combine(work2, "OFGame2")).FullName;
            var exe3 = Path.Combine(gameDir3, "OFGame2.exe");
            File.Copy(exe2, exe3);
            var origBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42 };
            Directory.CreateDirectory(Path.Combine(gameDir3, "goldberg_backup"));
            File.WriteAllBytes(Path.Combine(gameDir3, "goldberg_backup", "steam_api64.dll"), origBytes);
            File.Copy(Path.Combine(root, @"release\regular\x64\steam_api64.dll"), Path.Combine(gameDir3, "steam_api64.dll"));

            var opts3 = new PatchOptions
            {
                GameExe = exe3,
                AppId = "",
                UnpackDrm = false,
                Backup = false,
                WriteAppIdTxt = false,
                CreateSettings = false,
                GenerateInterfaces = false,
                OnlineFix = true,
            };
            var runner3 = new PatchRunner();
            var res3 = runner3.Run(opts3, CancellationToken.None);

            Check(res3.Success, "online-fix re-patch succeeded", res3.Summary);
            Check(File.ReadAllBytes(Path.Combine(gameDir3, "steam_api64.dll")).SequenceEqual(origBytes),
                  "Goldberg emulator dll replaced by original from goldberg_backup");
            Check(File.Exists(Path.Combine(gameDir3, "steam_appid.txt")) &&
                  File.ReadAllText(Path.Combine(gameDir3, "steam_appid.txt")).Trim() == "480",
                  "online-fix writes steam_appid.txt=480 even with appid writing toggled off");

            // live dll that is NOT a known Goldberg build must never be touched,
            // even when goldberg_backup holds something different (e.g. after a game update)
            var gameDir4 = Directory.CreateDirectory(Path.Combine(work2, "OFGame3")).FullName;
            var exe4 = Path.Combine(gameDir4, "OFGame3.exe");
            File.Copy(exe2, exe4);
            var foreignBytes = new byte[] { 1, 2, 3 };
            Directory.CreateDirectory(Path.Combine(gameDir4, "goldberg_backup"));
            File.WriteAllBytes(Path.Combine(gameDir4, "goldberg_backup", "steam_api64.dll"), origBytes);
            File.WriteAllBytes(Path.Combine(gameDir4, "steam_api64.dll"), foreignBytes);

            var opts4 = new PatchOptions
            {
                GameExe = exe4,
                AppId = "",
                UnpackDrm = false,
                Backup = false,
                WriteAppIdTxt = true,
                CreateSettings = false,
                GenerateInterfaces = false,
                OnlineFix = true,
            };
            var runner4 = new PatchRunner();
            var res4 = runner4.Run(opts4, CancellationToken.None);

            Check(res4.Success, "online-fix on untouched original dll succeeded", res4.Summary);
            Check(File.ReadAllBytes(Path.Combine(gameDir4, "steam_api64.dll")).SequenceEqual(foreignBytes),
                  "non-Goldberg live dll left completely alone (only steam_appid.txt written)");
        }
        finally
        {
            try { Directory.Delete(work2, true); } catch { }
        }

        // ---- steam appid lookup (offline units) ----
        Console.WriteLine("\n[steam appid lookup]");
        var cands = SteamLookup.CandidateTitles(@"C:\Games\Half-Life 2\hl2.exe");
        Check(cands.IndexOf("Half-Life 2") >= 0, "candidate titles from install folder", string.Join("|", cands));

        var steamCands = SteamLookup.CandidateTitles(@"C:\Program Files (x86)\Steam\steamapps\common\Portal 2\portal2.exe");
        Check(steamCands.Count > 0 && steamCands[0] == "Portal 2",
              "steamapps\\common layout yields game folder first", string.Join("|", steamCands));

        var sample = "{\"total\":3,\"items\":[{\"type\":\"app\",\"name\":\"Half-Life 2\",\"id\":220},{\"type\":\"app\",\"name\":\"Half-Life\",\"id\":70},{\"appid\":\"770\",\"name\":\"Counter-Strike\"}]}";
        var items = SteamLookup.ParseItems(sample);
        Check(items.Count == 3 && items[0].AppId == "220" && items[0].GameName == "Half-Life 2",
              "storesearch json parsed (id+name)");
        Check(items[2].AppId == "770", "legacy 'appid' field still supported", items[2].AppId);

        var exact = SteamLookup.BestMatch(items, "half life 2");
        Check(exact != null && exact.AppId == "220",
              "normalised exact match wins over ranked-first", exact == null ? "(null)" : exact.AppId);

        var partial = SteamLookup.BestMatch(
            new List<SteamMatch> { new SteamMatch { AppId = "630", GameName = "Portal" }, new SteamMatch { AppId = "999", GameName = "Something Else" } },
            "portal 1");
        Check(partial != null && partial.AppId == "630", "long-enough containment matches (folder 'portal 1' -> Portal)", partial == null ? "(null)" : partial.AppId);

        var none = SteamLookup.BestMatch(items, "Zork");
        Check(none == null, "unrelated title yields no match", none == null ? "" : none.AppId);
        var shorty = SteamLookup.BestMatch(items, "HL2");
        Check(shorty == null, "too-short title rejected", shorty == null ? "" : shorty.AppId);

        Console.WriteLine("\n[json parser edge cases]");
        var bracket = SteamLookup.ParseItems("{\"items\":[{\"id\":570,\"name\":\"Dota 2 [OFFLINE]\"},{\"id\":730,\"name\":\"CS [x] [y]\"}]}");
        Check(bracket.Count == 2 && bracket[0].GameName == "Dota 2 [OFFLINE]" && bracket[1].AppId == "730",
              "brackets in names survive real JSON parse", string.Join("|", bracket.Select(m => m.AppId + ":" + m.GameName)));

        var nested = SteamLookup.ParseItems("{\"items\":[{\"id\":1,\"name\":\"A\",\"meta\":{\"list\":[1,2]}},{\"id\":2,\"name\":\"B\"}]}");
        Check(nested.Count == 2 && nested[0].AppId == "1" && nested[1].AppId == "2", "nested arrays parse via serializer", nested.Count.ToString());

        var uni = SteamLookup.ParseItems("{\"items\":[{\"id\":440,\"name\":\"Team Fortress \\u00B2\"}]}");
        Check(uni.Count == 1 && uni[0].GameName == "Team Fortress \u00B2", "unicode escape decoded", uni.Count == 1 ? uni[0].GameName : "(none)");

        Check(SteamLookup.ParseItems("{\"items\":[{\"id\":10}]}").Count == 0, "missing name skipped", null);
        Check(SteamLookup.ParseItems("{\"items\":[{\"name\":\"No Id Here\"}]}").Count == 0, "missing id skipped", null);
        Check(SteamLookup.ParseItems("{not json").Count == 0, "malformed json yields empty list", null);
        Check(SteamLookup.ParseItems(null).Count == 0, "null json yields empty list", null);
        Check(SteamLookup.ParseItems(new string('x', (1 << 20) + 1)).Count == 0, "oversized response rejected", null);
        Check(SteamLookup.ParseItems("{\"items\":null}").Count == 0, "null items yields empty list", null);
        Check(SteamLookup.ParseItems("{\"items\":[{\"id\":0,\"name\":\"Zero\"}]}").Count == 0, "zero appid rejected", null);

        Console.WriteLine("\n[appid normalization]");
        string norm;
        Check(AppIdDetector.TryNormalize(" 1250 ", out norm) && norm == "1250", "whitespace trimmed", null);
        Check(AppIdDetector.TryNormalize("0000000125", out norm) && norm == "125", "leading zeros collapsed", null);
        Check(AppIdDetector.TryNormalize("１２３", out norm) == false, "fullwidth digits rejected", null);
        Check(AppIdDetector.TryNormalize("12345678901", out norm) == false, "11-digit rejected", null);
        Check(AppIdDetector.TryNormalize("2147483648", out norm) && norm == "2147483648", "above int range accepted", null);
        Check(AppIdDetector.TryNormalize("4294967295", out norm) && norm == "4294967295", "uint max accepted", null);
        Check(AppIdDetector.TryNormalize("4294967296", out norm) == false, "above uint range rejected", null);
        Check(AppIdDetector.TryNormalize("0", out norm) == false, "zero rejected", null);
        Check(AppIdDetector.TryNormalize("12a3", out norm) == false, "embedded text rejected", null);
        Check(AppIdDetector.TryNormalize("", out norm) == false, "empty rejected", null);
        Check(AppIdDetector.IsValid(null) == false, "null rejected", null);
        Check(AppIdDetector.Normalize(" 480 ") == "480", "Normalize trims", null);
        Check(AppIdDetector.Normalize("abc") == "", "Normalize returns empty on invalid", null);

        Console.WriteLine("\n[safe persistence + recovery]");
        string fpDir = Path.Combine(Path.GetTempPath(), "gp_selftest_fp_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(fpDir);
        try
        {
            var target = Path.Combine(fpDir, "config.ini");
            File.WriteAllText(target, "v1");
            var journal = new List<FileWriteRecord>();
            SafePersistence.WriteText(target, "v2", journal);
            Check(journal.Count == 1 && journal[0].Completed && File.ReadAllText(target) == "v2", "staged write completes with journal", journal.Count.ToString());
            Check(File.Exists(journal[0].RecoveryPath) && File.ReadAllText(journal[0].RecoveryPath) == "v1", "previous content kept as recovery copy", journal[0].RecoveryPath);
            var jr = File.ReadAllLines(journal[0].JournalPath);
            Check(jr.Length >= 7 && jr[0].StartsWith("destination=") && jr[jr.Length - 1] == "state=completed", "journal records destination and completion", string.Join(";", jr));

            SafePersistence.WriteText(target, "v3");
            Check(File.ReadAllText(journal[0].RecoveryPath) == "v1", "second write never overwrites the first recovery copy", null);

            var first = SafePersistence.WriteText(Path.Combine(fpDir, "a", "one.txt"), "A");
            SafePersistence.Copy(first.Destination, Path.Combine(fpDir, "b", "two.txt"));
            Check(File.ReadAllText(Path.Combine(fpDir, "b", "two.txt")) == "A", "hash-verified copy", null);

            bool threw = false;
            try { SafePersistence.Write(target, s => s.WriteByte(1), staged => { throw new InvalidOperationException("validator rejected staged data"); }); }
            catch (IOException) { threw = true; }
            Check(threw && File.ReadAllText(target) == "v3",
                  "failed validate leaves destination intact", null);
            var recoveryFiles = Directory.GetFiles(fpDir, "*.staged-*", SearchOption.AllDirectories);
            Check(recoveryFiles.Length > 0, "failed write left a staging artifact for recovery", null);

            string missing = Path.Combine(fpDir, "no-such-source.bin");
            bool missingReported = false;
            try { SafePersistence.Copy(missing, Path.Combine(fpDir, "dest-for-missing.bin")); }
            catch (FileNotFoundException) { missingReported = true; }
            catch (DirectoryNotFoundException) { missingReported = true; }
            Check(missingReported && !File.Exists(Path.Combine(fpDir, "dest-for-missing.bin")),
                  "missing source reports a clear error", null);
        }
        finally
        {
            try { Directory.Delete(fpDir, true); } catch { }
        }

        Console.WriteLine("\n[rollback]");
        string rbDir = Path.Combine(Path.GetTempPath(), "gp_selftest_rb_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(rbDir);
        try
        {
            var keep = Path.Combine(rbDir, "keep.txt");
            File.WriteAllText(keep, "original");
            var w1 = new List<FileWriteRecord>();
            SafePersistence.WriteText(keep, "patched", w1);
            Check(File.ReadAllText(keep) == "patched", "patch replaced the file", null);
            var r1 = Recovery.RollbackWrites(w1, null);
            Check(r1.Restored == 1 && r1.Failed == 0, "rollback restores a replaced file", r1.Summary);
            Check(File.ReadAllText(keep) == "original", "restored bytes match the original", File.ReadAllText(keep));

            var created = Path.Combine(rbDir, "created.txt");
            var w2 = new List<FileWriteRecord>();
            SafePersistence.WriteText(created, "new file", w2);
            Check(w2[0].PreviousHash == "absent", "a created file records an absent previous hash", w2[0].PreviousHash);
            var r2 = Recovery.RollbackWrites(w2, null);
            Check(r2.Deleted == 1 && !File.Exists(created), "rollback deletes a file the patch created", r2.Summary);

            var edited = Path.Combine(rbDir, "edited.txt");
            File.WriteAllText(edited, "v1");
            var w3 = new List<FileWriteRecord>();
            SafePersistence.WriteText(edited, "v2", w3);
            File.WriteAllText(edited, "user edited this after patching");
            var r3 = Recovery.RollbackWrites(w3, null);
            Check(r3.Skipped == 1 && r3.Restored == 0 && File.ReadAllText(edited) == "user edited this after patching",
                  "rollback never clobbers a file edited after the patch", r3.Summary);

            var lost = Path.Combine(rbDir, "lost.txt");
            File.WriteAllText(lost, "before");
            var w4 = new List<FileWriteRecord>();
            SafePersistence.WriteText(lost, "after", w4);
            File.Delete(w4[0].RecoveryPath);
            var r4 = Recovery.RollbackWrites(w4, null);
            Check(r4.Failed == 1 && File.ReadAllText(lost) == "after",
                  "a missing recovery copy fails cleanly and leaves the file alone", r4.Summary);

            var twice = Path.Combine(rbDir, "twice.txt");
            File.WriteAllText(twice, "one");
            var w5 = new List<FileWriteRecord>();
            SafePersistence.WriteText(twice, "two", w5);
            SafePersistence.WriteText(twice, "three", w5);
            Recovery.RollbackWrites(w5, null);
            Check(File.ReadAllText(twice) == "one", "rollback replays writes newest-first", File.ReadAllText(twice));

            var exe = Path.Combine(rbDir, "game.exe");
            File.WriteAllText(exe, "packed");
            var verified = Path.Combine(rbDir, "verified-original.exe");
            File.Copy(exe, verified);
            var w6 = new List<FileWriteRecord>();
            SafePersistence.WriteText(exe, "unpacked", w6, verified);
            Check(w6[0].ExternalRecovery && w6[0].RecoveryPath == Path.GetFullPath(verified),
                  "an existing verified backup is reused instead of copied again", w6[0].RecoveryPath);
            Check(!File.Exists(Path.Combine(w6[0].Area, "game.exe.previous")),
                  "no second copy of the exe is left in the staging area", null);
            var r6 = Recovery.RollbackWrites(w6, null);
            Check(r6.Restored == 1 && File.ReadAllText(exe) == "packed",
                  "rollback restores from the external backup", r6.Summary);
            Check(File.ReadAllText(verified) == "packed", "rollback leaves the verified backup intact", null);

            var journalled = Path.Combine(rbDir, "journalled.txt");
            File.WriteAllText(journalled, "pre");
            var w7 = new List<FileWriteRecord>();
            SafePersistence.WriteText(journalled, "post", w7);
            Recovery.SaveJournal(w7, true);
            var loaded = Recovery.LoadJournal(Recovery.JournalPath);
            Check(loaded.Count == 1 && loaded[0].Completed && loaded[0].Destination == Path.GetFullPath(journalled),
                  "the consolidated journal round-trips through disk", loaded.Count.ToString());
            Check(loaded[0].PreviousHash == w7[0].PreviousHash && loaded[0].StagedHash == w7[0].StagedHash,
                  "the journal keeps both hashes the undo needs", loaded[0].PreviousHash);
            Check(Recovery.HasLastPatch(), "a finished patch reports as undoable", null);
            var r7 = Recovery.RollbackLastPatch(null);
            Check(r7.Restored == 1 && File.ReadAllText(journalled) == "pre",
                  "undo works from the persisted journal after a restart", r7.Summary);
            Recovery.ClearLastPatch();
            Check(!Recovery.HasLastPatch(), "clearing the journal removes the undo offer", null);

            var gcTarget = Path.Combine(rbDir, "gc.txt");
            File.WriteAllText(gcTarget, "keep me");
            var w8 = new List<FileWriteRecord>();
            SafePersistence.WriteText(gcTarget, "patched", w8);
            Recovery.CollectStaging(w8);
            Check(File.Exists(w8[0].RecoveryPath) && File.ReadAllText(w8[0].RecoveryPath) == "keep me",
                  "collection keeps the recovery copy so undo still works", null);
            Check(!File.Exists(w8[0].JournalPath), "collection drops the superseded per-write journal", null);

            // A patch that only creates files leaves no recovery copies behind, so the .gp-recovery
            // folder itself must go – otherwise every patch strands one empty folder per directory.
            // Use a directory of its own: sibling areas from the tests above keep their .previous copies
            // and would legitimately keep the shared root alive.
            string gcIsolated = Path.Combine(rbDir, "isolated");
            Directory.CreateDirectory(gcIsolated);
            var brandNew = Path.Combine(gcIsolated, "brand-new.txt");
            var w9 = new List<FileWriteRecord>();
            SafePersistence.WriteText(brandNew, "created by the patch", w9);
            string gcRoot = Path.GetDirectoryName(w9[0].Area);
            Check(Directory.Exists(gcRoot), "a write creates its .gp-recovery root", gcRoot);
            Recovery.CollectStaging(w9);
            Check(!Directory.Exists(gcRoot),
                  "collection removes an empty .gp-recovery root, not just its contents", gcRoot);

            // Preserving an original is itself a pair of writes that belong to no run journal, so
            // nothing else would collect their staging areas – they must not litter the backup folder.
            string backupRoot = Path.Combine(rbDir, "backup-root");
            Directory.CreateDirectory(backupRoot);
            var originalFile = Path.Combine(rbDir, "original-dll.bin");
            File.WriteAllBytes(originalFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            string preserved = OriginalBackups.Preserve(backupRoot, originalFile);
            Check(preserved != null && File.Exists(preserved), "preserve writes a verified backup", preserved);
            var stray = Directory.GetDirectories(backupRoot, ".gp-recovery", SearchOption.AllDirectories);
            Check(stray.Length == 0, "preserve strands no .gp-recovery litter in the backup root",
                  stray.Length == 0 ? null : stray[0]);

            // The startup sweep clears roots orphaned by a crash – the one case collection cannot reach,
            // because a crash means no journal was ever written to describe them.
            string sweepRoot = Path.Combine(rbDir, "sweep-root");
            Directory.CreateDirectory(sweepRoot);
            string staleRoot = Path.Combine(sweepRoot, ".gp-recovery");
            string staleArea = Path.Combine(staleRoot, "stale");
            Directory.CreateDirectory(staleArea);
            string staleFile = Path.Combine(staleArea, "x.previous");
            File.WriteAllText(staleFile, "old");
            File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-30));
            Directory.SetLastWriteTimeUtc(staleArea, DateTime.UtcNow.AddDays(-30));
            Directory.SetLastWriteTimeUtc(staleRoot, DateTime.UtcNow.AddDays(-30));

            Recovery.SweepStale(new[] { sweepRoot }, 7);
            Check(!Directory.Exists(staleRoot), "sweep removes an abandoned .gp-recovery root", staleRoot);

            // A root written to recently is left alone...
            string freshRoot = Path.Combine(sweepRoot, ".gp-recovery");
            Directory.CreateDirectory(freshRoot);
            string freshFile = Path.Combine(freshRoot, "y.previous");
            File.WriteAllText(freshFile, "recent");
            Recovery.SweepStale(new[] { sweepRoot }, 7);
            Check(Directory.Exists(freshRoot), "sweep leaves a recently written .gp-recovery root alone", freshRoot);

            // ...and so is one the undo journal still points at, however old it is: deleting it would
            // silently break "Undo last patch" for the very patch it describes.
            File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow.AddDays(-30));
            Directory.SetLastWriteTimeUtc(freshRoot, DateTime.UtcNow.AddDays(-30));
            Recovery.SaveJournal(new[]
            {
                new FileWriteRecord
                {
                    Destination = Path.Combine(sweepRoot, "kept.txt"),
                    JournalPath = Path.Combine(freshRoot, "area", "kept.txt.journal.txt"),
                    RecoveryPath = Path.Combine(freshRoot, "area", "kept.txt.previous"),
                    Completed = true,
                    PreviousHash = "aa",
                    StagedHash = "bb",
                },
            }, true);
            Recovery.SweepStale(new[] { sweepRoot }, 7);
            Check(Directory.Exists(freshRoot), "sweep keeps a root the undo journal still references", freshRoot);
            Recovery.ClearLastPatch();

            var empty = Recovery.Rollback(new List<RecoveryEntry>(), null);
            Check(empty.Restored == 0 && empty.Failed == 0 && empty.Summary == "Nothing to undo.",
                  "rolling back nothing is a no-op, not an error", empty.Summary);
        }
        finally
        {
            // Clear the journal between sections, but leave the process-wide override in place –
            // Main owns it and restores it before returning.
            try { if (File.Exists(Recovery.JournalPath)) File.Delete(Recovery.JournalPath); } catch { }
            try { Directory.Delete(rbDir, true); } catch { }
        }

        ReviewRegressions();
        Console.WriteLine("\nRESULT: PASS=" + pass + "  FAIL=" + fail);

        Recovery.JournalPathOverride = null;
        Recovery.StateRootOverride = null;
        try { Directory.Delete(undoDir, true); } catch { }
        return fail == 0 ? 0 : 1;
    }

    static void ReviewRegressions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gp_review_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "a")).FullName, "same.dll");
            string b = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, "same.dll");
            File.WriteAllText(a, "original A");
            File.WriteAllText(b, "original B");
            string backups = Path.Combine(dir, "backups");
            string ba = OriginalBackups.Preserve(backups, a);
            string bb = OriginalBackups.Preserve(backups, b);
            Check(ba != bb && File.ReadAllText(ba) == "original A" && File.ReadAllText(bb) == "original B",
                  "same-named originals have distinct verified backups");
            File.WriteAllText(a, "updated A");
            OriginalBackups.Restore(backups, a);
            Check(File.ReadAllText(a) == "original A", "verified hashed original restores");
            File.WriteAllText(a, "destination unchanged");
            string manifest = File.ReadAllText(ba + ".source.txt");
            foreach (string bad in new[] { "malformed", Path.GetFullPath(b) + "\r\n" + SafePersistence.Hash(ba) + "\r\n", Path.GetFullPath(a) + "\r\nwrong-hash\r\n" })
            {
                File.WriteAllText(ba + ".source.txt", bad);
                var writes = new List<FileWriteRecord>();
                bool rejected = false;
                try { OriginalBackups.Restore(backups, a, writes); }
                catch (IOException) { rejected = true; }
                Check(rejected && writes.Count == 0 && File.ReadAllText(a) == "destination unchanged",
                      "corrupt source manifest rejected before destination mutation");
            }
            File.WriteAllText(ba + ".source.txt", manifest);
            File.WriteAllText(ba, "corrupted backup bytes");
            bool corruptRejected = false;
            try { OriginalBackups.Restore(backups, a); }
            catch (IOException) { corruptRejected = true; }
            Check(corruptRejected && File.ReadAllText(a) == "destination unchanged", "corrupt backup content rejected");

            string live = Path.Combine(dir, "steam_api.dll");
            File.Copy(Tools.ApiDll86, live);
            string legacyRoot = Path.Combine(dir, "goldberg_backup");
            Check(OriginalBackups.Preserve(legacyRoot, live) == null && !File.Exists(OriginalBackups.Location(legacyRoot, live)),
                  "bundled replacement is never promoted as original");
            Directory.CreateDirectory(legacyRoot);
            string legacy = Path.Combine(legacyRoot, "steam_api.dll");
            File.WriteAllText(legacy, "legacy original");
            string migrated = OriginalBackups.Preserve(legacyRoot, live);
            Check(File.ReadAllText(migrated) == "legacy original" && File.ReadAllText(legacy) == "legacy original",
                  "legacy original migrated before preserving patched live DLL");
            File.Copy(Tools.ApiDll64, migrated, true);
            File.WriteAllText(migrated + ".source.txt", Path.GetFullPath(live) + "\r\n" + SafePersistence.Hash(migrated) + "\r\n");
            Check(OriginalBackups.Preserve(legacyRoot, live) == legacy, "bundled hashed backup cannot shadow legacy original");
            OriginalBackups.Restore(legacyRoot, live);
            Check(File.ReadAllText(live) == "legacy original", "restore falls back from bundled hashed backup");
            File.Copy(Tools.ApiDll86, live, true);
            File.WriteAllText(migrated + ".source.txt", "corrupt");
            OriginalBackups.Restore(legacyRoot, live);
            Check(File.ReadAllText(live) == "legacy original", "restore falls back from corrupt hashed manifest");
            File.Copy(Tools.ApiDll86, legacy, true);
            bool replacementRejected = false;
            try { OriginalBackups.Restore(legacyRoot, live); }
            catch (IOException) { replacementRejected = true; }
            Check(replacementRejected && File.ReadAllText(live) == "legacy original", "bundled legacy backup rejected without mutation");

            string input = Path.Combine(dir, "input.exe");
            WriteAnyCpuDotNetPe(input);
            string output = input + ".unpacked.exe";
            WriteNativeX64Pe(output);
            var invocation = new InvocationOutputs(input);
            File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddDays(1));
            Check(!invocation.IsCurrent(output), "timestamp-only stale output rejected");
            var changed = File.ReadAllBytes(output);
            changed[changed.Length - 1] ^= 1;
            File.WriteAllBytes(output, changed);
            Check(invocation.IsCurrent(output), "rewritten output fingerprint accepted");
            string oldInputHash = SafePersistence.Hash(input);
            File.WriteAllText(input, "concurrent update");
            bool updateRejected = false;
            try { invocation.CopyAndDelete(output, null, oldInputHash); }
            catch (IOException) { updateRejected = true; }
            Check(updateRejected && File.ReadAllText(input) == "concurrent update" && File.Exists(output),
                  "concurrent executable update preserved and output retained");
            WriteAnyCpuDotNetPe(input);
            var copied = new List<FileWriteRecord>();
            invocation.CopyAndDelete(output, copied, SafePersistence.Hash(input));
            Check(copied.Count == 1 && copied[0].Completed && !File.Exists(output) && File.ReadAllBytes(input).SequenceEqual(changed),
                  "confirmed output copy deletes produced output");
            var next = new InvocationOutputs(input);
            Check(!next.IsCurrent(output), "next invocation cannot reuse consumed output");
            File.WriteAllText(output, "not a PE");
            string intactHash = SafePersistence.Hash(input);
            bool invalidRejected = false;
            try { next.CopyAndDelete(output, null, intactHash); }
            catch (IOException) { invalidRejected = true; }
            Check(invalidRejected && SafePersistence.Hash(input) == intactHash && File.Exists(output),
                  "output validator failure retains original and failed output");

            var denied = new UnauthorizedAccessException("injected access denial");
            bool deniedPreserved = false;
            try { SafePersistence.Write(input, s => s.WriteByte(1), staged => { throw denied; }); }
            catch (UnauthorizedAccessException ex)
            {
                deniedPreserved = ex.InnerException == denied && ex.Message.Contains(input) && ex.Message.Contains("Recovery/staging records:");
            }
            Check(deniedPreserved && SafePersistence.Hash(input) == intactHash, "access denial preserves type and recovery context");

            string batchDir = Directory.CreateDirectory(Path.Combine(dir, "batch")).FullName;
            string batchExe = Path.Combine(batchDir, "game.exe");
            WriteNativeX64Pe(batchExe);
            using (var cts = new CancellationTokenSource())
            {
                var batch = new BatchPatcher();
                batch.GamePercent += (i, p) => { if (p == 90) cts.Cancel(); };
                var results = batch.RunAsync(new List<BatchInput> {
                    new BatchInput { Exe = batchExe, AppId = "1250" },
                    new BatchInput { Exe = batchExe, AppId = "1250" }
                }, new BatchPrefs { UnpackDrm = false, Backup = false, CreateSettings = true }, cts.Token).GetAwaiter().GetResult();
                Check(results.Count == 1 && results[0].Cancelled && !results[0].Success && results[0].Summary.Contains("Partial changes remain"),
                      "batch cancellation uses flag with partial-change suffix and stops subsequent items");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- synthetic PE builders ----

    static void W16(byte[] b, int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    static void W32(byte[] b, int off, uint v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }

    static void WriteNativeX64Pe(string path)
    {
        var b = new byte[1536];
        W16(b, 0x00, 0x5A4D);          // DOS "MZ"
        W32(b, 0x3C, 0x80);            // e_lfanew
        int pe = 0x80;
        W32(b, pe, 0x00004550);        // "PE\0\0"
        W16(b, pe + 4, 0x8664);        // Machine = AMD64
        W16(b, pe + 6, 1);             // NumberOfSections
        W32(b, pe + 8, 0xFC7CDB25);    // TimeDateStamp (nasty low half)
        W32(b, pe + 12, 0);            // PointerToSymbolTable
        W32(b, pe + 16, 0);            // NumberOfSymbols
        W16(b, pe + 20, 0xF0);         // SizeOfOptionalHeader (PE32+ standard)
        W16(b, pe + 22, 0x22);         // Characteristics: EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE
        int opt = pe + 24;
        W16(b, opt, 0x20B);            // OptionalHeader Magic = PE32+
        W32(b, opt + 60, 0x400);       // SizeOfHeaders (section table must end inside it)
        // Data directories (opt+112) stay zero -> no CLR dir -> not managed.
        int sec = opt + 0xF0;          // section table right after the optional header
        for (int i = 0; i < 5; i++) b[sec + i] = (byte)".text"[i];
        W32(b, sec + 8, 0x1000);       // VirtualSize
        W32(b, sec + 12, 0x1000);      // VirtualAddress
        W32(b, sec + 16, 0x200);       // SizeOfRawData
        W32(b, sec + 20, 0x400);       // PointerToRawData
        File.WriteAllBytes(path, b);
    }

    /// <summary>Minimal .NET PE32 (I386), written per spec so PeReader can't agree with a shared
    /// mistake. AnyCPU = COMIMAGE_FLAGS_ILONLY (0x1) with no 32BITREQUIRED/PREFERRED bits.</summary>
    static void WriteAnyCpuDotNetPe(string path, uint corFlags = 1, ushort machine = 0x14C, bool includeClr = true)
    {
        var b = new byte[4096];
        W16(b, 0x00, 0x5A4D);          // DOS "MZ"
        W32(b, 0x3C, 0x80);            // e_lfanew
        int pe = 0x80;
        W32(b, pe, 0x00004550);        // "PE\0\0"
        W16(b, pe + 4, machine);       // Machine
        W16(b, pe + 6, 1);             // NumberOfSections
        W32(b, pe + 8, 0x5F000000);    // TimeDateStamp
        W32(b, pe + 12, 0);            // PointerToSymbolTable
        W32(b, pe + 16, 0);            // NumberOfSymbols
        W16(b, pe + 20, 0xE0);         // SizeOfOptionalHeader (PE32 standard)
        W16(b, pe + 22, 0x0102);       // Characteristics: EXECUTABLE_IMAGE | 32BIT_MACHINE
        int opt = pe + 24;
        W16(b, opt, 0x10B);            // OptionalHeader Magic = PE32
        W32(b, opt + 60, 0x600);       // SizeOfHeaders covers the section table
        W32(b, opt + 92, 16);          // NumberOfRvaAndSizes = 16
        int clrDir = opt + 96 + 14 * 8; // CLR data directory (DD[14])
        if (includeClr)
        {
            W32(b, clrDir, 0x2008);    // RVA of COR header
            W32(b, clrDir + 4, 72);    // size
        }
        int sec = opt + 0xE0;          // section table right after the optional header
        for (int i = 0; i < 5; i++) b[sec + i] = (byte)".text"[i];
        W32(b, sec + 8, 0x1000);       // VirtualSize
        W32(b, sec + 12, 0x2000);      // VirtualAddress
        W32(b, sec + 16, 0x400);       // SizeOfRawData
        W32(b, sec + 20, 0x400);       // PointerToRawData (VA 0x2000 -> file offset 0x400)
        if (includeClr)
        {
            int cor = 0x408;           // RvaToFile(0x2008) = 0x400 + (0x2008 - 0x2000)
            W32(b, cor, 72);           // cb
            b[cor + 4] = 2;            // MajorRuntimeVersion
            b[cor + 5] = 5;            // MinorRuntimeVersion
            W32(b, cor + 8, 0x2060);   // MetaData RVA (inside .text)
            W32(b, cor + 12, 0x80);    // MetaData Size
            W32(b, cor + 16, corFlags); // COM Flags @+16 per ECMA-335 (0x1 = ILONLY -> AnyCPU; 2 = 32BITREQUIRED; 0x20000 = PREFERRED)
        }
        File.WriteAllBytes(path, b);
    }

    static void DumpPe(string path)
    {
        var b = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(b, 0x3C);
        int optSize = BitConverter.ToUInt16(b, pe + 20);
        int magic = BitConverter.ToUInt16(b, pe + 24);
        int dd = magic == 0x20B ? 112 : 96;
        Console.WriteLine("      | peOff=0x" + pe.ToString("X") + " machine=0x" + BitConverter.ToUInt16(b, pe + 4).ToString("X4")
            + " sections=" + BitConverter.ToUInt16(b, pe + 6)
            + " optSize=0x" + optSize.ToString("X") + " char=0x" + BitConverter.ToUInt16(b, pe + 22).ToString("X4")
            + " magic=0x" + magic.ToString("X4")
            + " sizeHdr=0x" + BitConverter.ToUInt32(b, pe + 24 + 60).ToString("X")
            + " clrRva=0x" + BitConverter.ToUInt32(b, pe + 24 + dd + 112).ToString("X"));
    }
}
