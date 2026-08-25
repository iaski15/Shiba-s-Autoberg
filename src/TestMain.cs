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
        }
        finally
        {
            try { Directory.Delete(work2, true); } catch { }
        }

        Console.WriteLine("\nRESULT: PASS=" + pass + "  FAIL=" + fail);
        return fail == 0 ? 0 : 1;
    }
}
