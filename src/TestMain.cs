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
        Check(cli.Arch == expectedAnyCpu, "AnyCPU arch resolution", cli.MachineText);

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
            catch (Exception ex) { noThrow = false; Console.WriteLine("      | threw: " + ex.GetType().Name + ": " + ex.Message); }
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
            catch (Exception ex) { noThrow2 = false; Console.WriteLine("      | threw: " + ex.GetType().Name + ": " + ex.Message); }
            Check(noThrow2, ".NET AnyCPU PE parses without throwing", noThrow2 ? null : "(see above)");
            if (noThrow2)
            {
                Check(dpe.Managed, ".NET PE detected as managed", dpe.MachineText);
                Check(dpe.AnyCpu, "COR flags=0 -> AnyCPU", dpe.MachineText);
                Check(dpe.Arch == expectedAnyCpu, "synthetic AnyCPU arch resolution", dpe.MachineText);
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

            byte[] origDllBytes = File.ReadAllBytes(Path.Combine(root, @"release\regular\x64\steam_api64.dll"));
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
            Check(lastPct >= 100, "progress reached 100", lastPct.ToString());
            Check(logs.Any(l => l.IndexOf("No Steam DRM", StringComparison.OrdinalIgnoreCase) >= 0),
                  "unpack gracefully skipped for non-packed exe");

            var newDll = File.ReadAllBytes(Path.Combine(gameDir, "steam_api.dll"));
            var goldberg86 = File.ReadAllBytes(Path.Combine(root, @"release\regular\x86\steam_api.dll"));
            Check(!newDll.SequenceEqual(origDllBytes), "existing steam_api.dll was replaced");
            Check(newDll.SequenceEqual(goldberg86), "replaced dll matches bundled goldberg x86");

            Check(File.Exists(Path.Combine(gameDir, "goldberg_backup", "steam_api.dll")) &&
                  File.ReadAllBytes(Path.Combine(gameDir, "goldberg_backup", "steam_api.dll")).SequenceEqual(origDllBytes),
                  "original dll backed up intact");

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

        Console.WriteLine("\nRESULT: PASS=" + pass + "  FAIL=" + fail);
        return fail == 0 ? 0 : 1;
    }

    // ---- synthetic PE builders for the PeReader regression tests above ----

    static void W16(byte[] b, int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    static void W32(byte[] b, int off, uint v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }

    /// <summary>Minimal native x64 PE (~1 KB). TimeDateStamp low half is 0xDB25 – the old buggy code read
    /// that as SizeOfOptionalHeader and ran off the end of this small file.</summary>
    static void WriteNativeX64Pe(string path)
    {
        var b = new byte[1024];
        W16(b, 0x00, 0x5A4D);          // "MZ"
        W32(b, 0x3C, 0x80);            // e_lfanew
        int pe = 0x80;
        W32(b, pe, 0x00004550);        // "PE\0\0"
        W16(b, pe + 4, 0x8664);        // Machine = AMD64
        W16(b, pe + 6, 1);             // NumberOfSections
        W32(b, pe + 8, 0xFC7CDB25);    // TimeDateStamp (nasty low half)
        W32(b, pe + 12, 0);            // PointerToSymbolTable
        W16(b, pe + 16, 0xF0);         // SizeOfOptionalHeader (PE32+ standard)
        W16(b, pe + 18, 0x22);         // Characteristics: EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE
        int opt = pe + 24;
        W16(b, opt, 0x20B);            // OptionalHeader Magic = PE32+
        // Data directories (opt+112) stay zero -> no CLR dir -> not managed.
        int sec = opt + 0xF0;          // section table right after the optional header
        for (int i = 0; i < 5; i++) b[sec + i] = (byte)".text"[i];
        W32(b, sec + 8, 0x1000);       // VirtualSize
        W32(b, sec + 12, 0x1000);      // VirtualAddress
        W32(b, sec + 16, 0x200);       // SizeOfRawData
        W32(b, sec + 20, 0x400);       // PointerToRawData
        File.WriteAllBytes(path, b);
    }

    /// <summary>Minimal .NET PE32 (I386/AnyCPU). COR header flags at +8 are 0 -> AnyCPU.</summary>
    static void WriteAnyCpuDotNetPe(string path)
    {
        var b = new byte[2048];
        W16(b, 0x00, 0x5A4D);          // "MZ"
        W32(b, 0x3C, 0x80);            // e_lfanew
        int pe = 0x80;
        W32(b, pe, 0x00004550);        // "PE\0\0"
        W16(b, pe + 4, 0x14C);         // Machine = I386 (typical for AnyCPU .NET)
        W16(b, pe + 6, 1);             // NumberOfSections
        W32(b, pe + 8, 0x5F000000);    // TimeDateStamp
        W32(b, pe + 12, 0);            // PointerToSymbolTable
        W16(b, pe + 16, 0xE0);         // SizeOfOptionalHeader (PE32 standard)
        W16(b, pe + 18, 0x21);         // Characteristics: EXECUTABLE_IMAGE | 32BIT_MACHINE
        int opt = pe + 24;
        W16(b, opt, 0x10B);            // OptionalHeader Magic = PE32
        int clrDir = opt + 96 + 14 * 8; // CLR data directory (DD[14])
        W32(b, clrDir, 0x2008);        // RVA of COR header
        W32(b, clrDir + 4, 72);        // size
        int sec = opt + 0xE0;          // section table right after the optional header
        for (int i = 0; i < 5; i++) b[sec + i] = (byte)".text"[i];
        W32(b, sec + 8, 0x1000);       // VirtualSize
        W32(b, sec + 12, 0x2000);      // VirtualAddress
        W32(b, sec + 16, 0x400);       // SizeOfRawData
        W32(b, sec + 20, 0x400);       // PointerToRawData (VA 0x2000 -> file offset 0x400)
        int cor = 0x408;               // RvaToFile(0x2008) = 0x400 + (0x2008 - 0x2000)
        W32(b, cor, 72);               // cb
        b[cor + 4] = 2;                // MajorRuntimeVersion
        b[cor + 5] = 5;                // MinorRuntimeVersion
        W32(b, cor + 8, 0);            // Flags = 0 -> AnyCPU (no 32BITREQUIRED/PREFERRED)
        File.WriteAllBytes(path, b);
    }
}
