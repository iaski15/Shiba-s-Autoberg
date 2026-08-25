using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Gp
{
    public enum LogLevel { Info, Dim, Ok, Warn, Error }

    public enum ExeArch { Unknown, X86, X64 }

    public class PeInfo
    {
        public ushort Machine;
        public bool Managed;
        public bool AnyCpu;
        public ExeArch Arch;
        public string MachineText = "?";
        public long SizeBytes;

        public override string ToString()
        {
            return MachineText + (Managed ? " (.NET)" : "");
        }
    }

    /// <summary>Parses PE headers: architecture + managed/AnyCPU detection.</summary>
    public static class PeReader
    {
        public static PeInfo Analyze(string path)
        {
            var info = new PeInfo();
            var fi = new FileInfo(path);
            info.SizeBytes = fi.Exists ? fi.Length : 0;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                fs.Position = 0x3C;
                int peOff = br.ReadInt32();
                if (peOff <= 0 || peOff + 26 > fs.Length) throw new InvalidDataException("Not a valid PE file.");
                fs.Position = peOff;
                uint sig = br.ReadUInt32();
                if (sig != 0x00004550) throw new InvalidDataException("Not a valid PE file.");

                ushort machine = br.ReadUInt16();          // peOff+4
                ushort numSections = br.ReadUInt16();      // peOff+6
                int optSize = br.ReadUInt16();             // peOff+16 (skip to it)
                fs.Position = peOff + 18;                  // skip past characteristics
                br.ReadUInt16();
                int optOff = peOff + 24;
                fs.Position = optOff;
                ushort magic = br.ReadUInt16();

                // Data directories start offset within optional header
                int ddOff = optOff + (magic == 0x20B ? 112 : 96);
                bool managed = false;
                Dictionary<int, long[]> sections = new Dictionary<int, long[]>();
                if (magic == 0x10B || magic == 0x20B)
                {
                    // read section table for rva mapping
                    int secOff = optOff + optSize;
                    for (int i = 0; i < numSections; i++)
                    {
                        fs.Position = secOff + i * 40 + 8;
                        uint vsize = br.ReadUInt32();
                        uint vaddr = br.ReadUInt32();
                        uint rsize = br.ReadUInt32();
                        uint raddr = br.ReadUInt32();
                        sections[(int)vaddr] = new long[] { vsize, rsize, raddr };
                    }
                    // CLR directory = DD[14]
                    fs.Position = ddOff + 14 * 8;
                    uint clrRva = br.ReadUInt32();
                    uint clrSize = br.ReadUInt32();
                    if (clrRva != 0 && clrSize != 0)
                    {
                        managed = true;
                        int corFile = RvaToFile(clrRva, sections);
                        if (corFile >= 0)
                        {
                            fs.Position = corFile + 16; // Flags
                            uint flags = br.ReadUInt32();
                            const uint FLAG_32BITREQUIRED = 0x2;
                            const uint FLAG_32BITPREFERRED = 0x20000;
                            if ((flags & FLAG_32BITPREFERRED) != 0 || (flags & FLAG_32BITREQUIRED) != 0)
                            { info.AnyCpu = false; info.Arch = ExeArch.X86; }
                            else
                            { info.AnyCpu = true; } // resolved below by OS bitness
                        }
                    }
                }

                info.Managed = managed;
                info.Machine = machine;
                switch (machine)
                {
                    case 0x14c: info.MachineText = "x86"; if (!managed || !info.AnyCpu) info.Arch = ExeArch.X86; break;
                    case 0x8664: info.MachineText = "x64"; info.Arch = ExeArch.X64; break;
                    case 0xAA64: info.MachineText = "ARM64"; info.Arch = ExeArch.X64; break;
                    default: info.MachineText = "0x" + machine.ToString("X4"); break;
                }
                if (info.AnyCpu)
                {
                    info.Arch = Environment.Is64BitOperatingSystem ? ExeArch.X64 : ExeArch.X86;
                    info.MachineText = "AnyCPU (" + (Environment.Is64BitOperatingSystem ? "runs x64" : "runs x86") + ")";
                }
                if (!info.Managed && info.Arch == ExeArch.Unknown && info.Machine != 0)
                    info.Arch = Environment.Is64BitOperatingSystem ? ExeArch.X64 : ExeArch.X86;
            }
            return info;
        }

        static int RvaToFile(uint rva, Dictionary<int, long[]> sections)
        {
            foreach (var kv in sections)
            {
                long va = kv.Key, vsize = kv.Value[0], rsize = kv.Value[1], raddr = kv.Value[2];
                long sz = Math.Max(vsize, rsize);
                if (rva >= va && rva < va + sz) return (int)(raddr + (rva - va));
            }
            return -1;
        }
    }

    public class PatchOptions
    {
        public string GameExe = "";
        public string AppId = "";
        public bool UnpackDrm = true;
        public bool Backup = true;
        public bool WriteAppIdTxt = true;
        public bool CreateSettings = false;
        public bool GenerateInterfaces = true;
        public bool OnlineFix = false;

        /// <summary>AppID actually written when online-fix mode forces Spacewar.</summary>
        public string EffectiveAppId { get { return OnlineFix ? "480" : (AppId ?? "").Trim(); } }
    }

    public class PatchLogEntry
    {
        public DateTime Time;
        public LogLevel Level;
        public string Message;
    }

    public class PatchResult
    {
        public bool Success;
        public string Summary = "";
        public string FinalExe = "";
        public string InstallDir = "";
        public string BackupDir = "";
        public string SettingsDir = "";
        public bool Unpacked;
        public List<string> ReplacedFiles = new List<string>();
        public bool NeedsAdmin;
    }

    /// <summary>Resolves bundled tool paths relative to this app's folder.</summary>
    public static class Tools
    {
        public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string SteamlessCli { get { return Path.Combine(BaseDir, @"steamless\Steamless.CLI.exe"); } }
        public static string SteamlessDir { get { return Path.Combine(BaseDir, "steamless"); } }
        public static string ApiDll86 { get { return Path.Combine(BaseDir, @"release\regular\x86\steam_api.dll"); } }
        public static string ApiDll64 { get { return Path.Combine(BaseDir, @"release\regular\x64\steam_api.dll".Replace("steam_api.dll", "steam_api64.dll")); } }
        public static string GenInterfaces86 { get { return Path.Combine(BaseDir, @"release\tools\generate_interfaces\generate_interfaces_x86.exe"); } }
        public static string GenInterfaces64 { get { return Path.Combine(BaseDir, @"release\tools\generate_interfaces\generate_interfaces_x64.exe"); } }
        public static string SettingsExampleDir { get { return Path.Combine(BaseDir, @"release\steam_settings.EXAMPLE"); } }

        public static List<string> Missing()
        {
            var missing = new List<string>();
            if (!File.Exists(SteamlessCli)) missing.Add("steamless\\Steamless.CLI.exe");
            if (!File.Exists(ApiDll86)) missing.Add("release\\regular\\x86\\steam_api.dll");
            if (!File.Exists(ApiDll64)) missing.Add("release\\regular\\x64\\steam_api64.dll");
            return missing;
        }
    }

    /// <summary>Embedded payload: files baked into the exe at build time (gppay.* resources + gppay.manifest)
    /// are written back beside the exe when missing, making the binary fully self-contained.</summary>
    public static class Payload
    {
        class Entry { public string Res; public string Rel; }
        static List<Entry> entries;

        static void Load()
        {
            entries = new List<Entry>();
            try
            {
                var asm = typeof(Payload).Assembly;
                using (var s = asm.GetManifestResourceStream("gppay.manifest"))
                {
                    if (s == null) return;
                    using (var r = new StreamReader(s))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            line = line.TrimStart('\uFEFF');
                            int bar = line.IndexOf('|');
                            if (bar <= 0) continue;
                            entries.Add(new Entry { Res = line.Substring(0, bar), Rel = line.Substring(bar + 1) });
                        }
                    }
                }
            }
            catch { entries = new List<Entry>(); }
        }

        /// <summary>Number of files embedded at build time (0 when built without payload).</summary>
        public static int Count { get { if (entries == null) Load(); return entries.Count; } }

        /// <summary>Writes every missing or size-mismatched payload file beside the exe.
        /// Returns the relative paths that were restored.</summary>
        public static List<string> ExtractMissing()
        {
            if (entries == null) Load();
            var written = new List<string>();
            var asm = typeof(Payload).Assembly;
            foreach (var e in entries)
            {
                try
                {
                    string dst = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, e.Rel);
                    using (var src = asm.GetManifestResourceStream(e.Res))
                    {
                        if (src == null) continue;
                        if (File.Exists(dst) && new FileInfo(dst).Length == src.Length) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        using (var f = File.Create(dst)) src.CopyTo(f);
                    }
                    written.Add(e.Rel);
                }
                catch { }
            }
            return written;
        }
    }

    /// <summary>Executes the full patch pipeline. UI-agnostic; reports via events.</summary>
    public class PatchRunner
    {
        public event Action<PatchLogEntry> LogLine;
        public event Action<int> ProgressChanged;

        private void Log(LogLevel lvl, string msg)
        {
            var h = LogLine; if (h != null) h(new PatchLogEntry { Time = DateTime.Now, Level = lvl, Message = msg });
        }
        private void Pct(int p)
        {
            var h = ProgressChanged; if (h != null) h(p);
        }

        public Task<PatchResult> RunAsync(PatchOptions o, CancellationToken ct)
        {
            return Task.Run(() => Run(o, ct));
        }

        public PatchResult Run(PatchOptions o, CancellationToken ct)
        {
            var res = new PatchResult();
            try
            {
                ct.ThrowIfCancellationRequested();
                Log(LogLevel.Info, "── Starting patch ──────────────────────────────");

                // ---- validate --------------------------------------------------
                if (string.IsNullOrEmpty(o.GameExe) || !File.Exists(o.GameExe))
                    throw new Exception("Game executable not found:\n" + o.GameExe);
                if (!o.OnlineFix && (string.IsNullOrEmpty(o.AppId) || !Regex.IsMatch(o.AppId, @"^\d{1,10}$")))
                    throw new Exception("Steam AppID must be a numeric ID (find it on steamdb.info).");
                if (o.OnlineFix)
                    Log(LogLevel.Info, "Generic online-fix: the original Steamworks dll stays in place so the game attaches to Steam as Spacewar (AppID 480).");

                var exePath = Path.GetFullPath(o.GameExe);
                var gameDir = Path.GetDirectoryName(exePath);

                // ---- analyze ---------------------------------------------------
                Pct(4);
                Log(LogLevel.Dim, "Analyzing executable…");
                PeInfo pe;
                try { pe = PeReader.Analyze(exePath); }
                catch (Exception ex) { throw new Exception("Could not read the executable as a PE file.\n" + ex.Message); }
                Log(LogLevel.Info, string.Format("Target: {0}  [{1}, {2:N1} MB]",
                    Path.GetFileName(exePath), pe.MachineText, Math.Max(pe.SizeBytes / 1048576.0, 0.01)));
                if (pe.Arch == ExeArch.Unknown)
                    throw new Exception("Unknown CPU architecture – cannot pick a matching steam_api dll.");

                // ---- locate steam api install dir ------------------------------
                Pct(8);
                var foundApi = FindSteamApiFiles(gameDir);
                string installDir = gameDir;
                string preferredName = pe.Arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll";
                string otherName = pe.Arch == ExeArch.X64 ? "steam_api.dll" : "steam_api64.dll";

                                if (foundApi.Count > 0)
                {
                    var best = PickApiTarget(foundApi, gameDir, preferredName);
                    installDir = Path.GetDirectoryName(best);
                    Log(LogLevel.Ok, "Found Steamworks dll(s): " +
                        string.Join(", ", foundApi.Select(f => ShortRel(gameDir, f))));
                    Log(LogLevel.Dim, "Install target folder: " + ShortRel(gameDir, installDir));
                }
                else
                {
                    Log(LogLevel.Warn, "No existing steam_api dll found in the game folder – Goldberg dll will be placed beside the exe.");
                }
                res.InstallDir = installDir;

                // ---- backup dir ----------------------------------------------
                string backupDir = Path.Combine(installDir, "goldberg_backup");
                Func<string, string> backup = (src) =>
                {
                    if (!o.Backup) return null;
                    Directory.CreateDirectory(backupDir);
                    var dst = Path.Combine(backupDir, Path.GetFileName(src));
                    File.Copy(src, dst, true);
                    return dst;
                };
                res.BackupDir = o.Backup ? backupDir : "";

                // ---- unpack DRM (Steamless) ------------------------------------
                string finalExe = exePath;
                if (o.UnpackDrm)
                {
                    Pct(12);
                    finalExe = TryUnpack(exePath, backup, res, ct);
                    Pct(48);
                }
                else Log(LogLevel.Dim, "Steamless auto-unpack disabled – skipping.");
                res.FinalExe = finalExe;
                ct.ThrowIfCancellationRequested();

                // ---- interfaces from ORIGINAL dll -------------------------------
                string interfacesTxt = null;
                if (o.GenerateInterfaces && !o.OnlineFix)
                {
                    Pct(52);
                    interfacesTxt = TryGenerateInterfaces(foundApi, gameDir, preferredName, ct);
                }

                // ---- dlls --------------------------------------------------------
                Pct(62);
                if (o.OnlineFix)
                    PrepareOnlineFixMode(installDir, res);
                else
                    InstallGoldbergDlls(installDir, pe.Arch, foundApi, backup, res);

                // ---- steam_appid.txt ---------------------------------------------
                Pct(82);
                if (o.WriteAppIdTxt || o.OnlineFix)
                {
                    string appId = o.EffectiveAppId;
                    string txt = appId + Environment.NewLine;
                    WriteIfChanged(Path.Combine(installDir, "steam_appid.txt"), txt);
                    res.ReplacedFiles.Add(ShortRel(gameDir, Path.Combine(installDir, "steam_appid.txt")));
                    var besideExe = Path.Combine(Path.GetDirectoryName(finalExe), "steam_appid.txt");
                    if (!string.Equals(besideExe, Path.Combine(installDir, "steam_appid.txt"), StringComparison.OrdinalIgnoreCase))
                    {
                        WriteIfChanged(besideExe, txt);
                        res.ReplacedFiles.Add(ShortRel(gameDir, besideExe));
                    }
                    Log(LogLevel.Ok, "steam_appid.txt → " + appId +
                        (o.OnlineFix ? "  (Spacewar / generic online-fix)" : ""));
                }

                // ---- steam_settings ----------------------------------------------
                Pct(90);
                if (o.CreateSettings)
                {
                    var settingsDir = CopySettingsExample(installDir);
                    res.SettingsDir = settingsDir;
                    if (interfacesTxt != null)
                    {
                        WriteIfChanged(Path.Combine(settingsDir, "steam_interfaces.txt"), interfacesTxt);
                        Log(LogLevel.Ok, "steam_settings\\steam_interfaces.txt written.");
                    }
                }
                else if (interfacesTxt != null)
                {
                    // keep it somewhere useful even without a settings folder
                    var legacy = Path.Combine(installDir, "steam_interfaces.txt");
                    WriteIfChanged(legacy, interfacesTxt);
                    Log(LogLevel.Ok, "steam_interfaces.txt written (move into steam_settings later if you create one).");
                }

                // ---- done ---------------------------------------------------------
                Pct(100);
                res.Success = true;
                res.Summary = o.OnlineFix
                    ? string.Format("Online-fix ready (Spacewar · 480). Start Steam, then launch {0} – it will show up as playing Spacewar.", Path.GetFileName(finalExe))
                    : string.Format("Patched with AppID {0}. Goldberg dll installed to: {1}",
                        o.EffectiveAppId, ShortRel(gameDir, Path.Combine(installDir, preferredName)));
                Log(LogLevel.Ok, res.Summary);
                if (o.OnlineFix)
                    Log(LogLevel.Dim, "Multiplayer traffic is routed through Steam's own servers under Spacewar's AppID.");
                if (res.BackupDir != "") Log(LogLevel.Dim, "Originals backed up in: " + ShortRel(gameDir, res.BackupDir));
                Log(LogLevel.Ok, "✔ Done! Launch the game to test.");
            }
            catch (OperationCanceledException)
            {
                res.Success = false;
                res.Summary = "Cancelled.";
                Log(LogLevel.Warn, "✖ Cancelled by user.");
            }
            catch (UnauthorizedAccessException ex)
            {
                res.Success = false;
                res.NeedsAdmin = true;
                res.Summary = "Access denied – run the patcher as Administrator.";
                Log(LogLevel.Error, "Access denied while writing files: " + ex.Message);
                Log(LogLevel.Error, "Tip: click 'Retry as Admin', or move the game out of a protected folder (Program Files).");
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Summary = ex.Message;
                Log(LogLevel.Error, "✖ " + ex.Message);
            }
            return res;
        }

        // ------------------------------------------------------------------ steps

        private string TryUnpack(string exePath, Func<string, string> backup, PatchResult res, CancellationToken ct)
        {
            if (!File.Exists(Tools.SteamlessCli))
            {
                Log(LogLevel.Warn, "Steamless CLI not found – skipping DRM unpack. (" + Tools.SteamlessCli + ")");
                return exePath;
            }

            Log(LogLevel.Info, "Running Steamless to check/remove SteamStub DRM…");
            var before = DateTime.UtcNow;
            var outputLines = new List<string>();

            var psi = new ProcessStartInfo
            {
                FileName = Tools.SteamlessCli,
                Arguments = "\"" + exePath + "\"",
                WorkingDirectory = Tools.SteamlessDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (outputLines) outputLines.Add(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (outputLines) outputLines.Add(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    // wait with cancellation support (max 10 minutes)
                    var sw = Stopwatch.StartNew();
                    while (!p.HasExited)
                    {
                        if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } throw new OperationCanceledException(ct); }
                        if (sw.Elapsed.TotalMinutes > 10) { try { p.Kill(); } catch { } break; }
                        Thread.Sleep(120);
                    }
                    p.WaitForExit(2000);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log(LogLevel.Warn, "Steamless could not run: " + ex.Message);
                return exePath;
            }

            // find produced file: prefer path mentioned in output, else scan candidates
            string outPath = null;
            lock (outputLines)
            {
                foreach (var line in outputLines)
                {
                    var m = Regex.Match(line, "[A-Za-z]:\\\\[^\"*?<>|]*\\.unpacked\\.exe", RegexOptions.IgnoreCase);
                    if (m.Success) { outPath = m.Value; break; }
                }
            }
            if (outPath == null || !File.Exists(outPath))
            {
                var cand1 = exePath + ".unpacked.exe";
                var nameOnly = Path.GetFileNameWithoutExtension(exePath);
                var cand2 = Path.Combine(Path.GetDirectoryName(exePath), nameOnly + ".unpacked.exe");
                var cands = new[] { cand1, cand2 }.Where(File.Exists)
                    .Concat(SafeFiles(Path.GetDirectoryName(exePath)).Where(f =>
                        f.EndsWith(".unpacked.exe", StringComparison.OrdinalIgnoreCase) &&
                        File.GetLastWriteTimeUtc(f) >= before.ToUniversalTime().AddSeconds(-2)))
                    .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                outPath = cands.FirstOrDefault();
            }

            bool successMsg = false;
            lock (outputLines)
            {
                successMsg = outputLines.Any(l => l.IndexOf("Successfully unpacked", StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var l in outputLines.TakeLastVisible(outputLines.Count))
                {
                    var t = l.TrimEnd();
                    if (t.Length == 0) continue;
                    if (t.StartsWith("[Steamless]", StringComparison.OrdinalIgnoreCase)) t = t.Substring(11).Trim();
                    Log(LogLevel.Dim, "   " + t);
                }
            }

            if (outPath != null && File.Exists(outPath))
            {
                var origBackup = backup(exePath);
                try
                {
                    File.Delete(exePath);
                    File.Move(outPath, exePath);
                }
                catch (IOException ex)
                {
                    throw new Exception("Could not replace the packed exe (is the game still running?).\n" + ex.Message);
                }
                res.Unpacked = true;
                Log(LogLevel.Ok, "DRM removed! Unpacked exe is now: " + Path.GetFileName(exePath));
                if (origBackup != null) Log(LogLevel.Dim, "Original packed exe backed up.");
                return exePath;
            }

            if (successMsg)
                Log(LogLevel.Warn, "Steamless reported success but no output file was found – continuing with original exe.");
            else
                Log(LogLevel.Info, "No Steam DRM detected on this exe – continuing as-is.");
            return exePath;
        }

        private string TryGenerateInterfaces(List<string> foundApi, string gameDir, string preferredName, CancellationToken ct)
        {
            if (foundApi.Count == 0) return null;
            string target = PickApiTarget(foundApi, gameDir, preferredName);
            bool x64 = Path.GetFileName(target).IndexOf("64", StringComparison.Ordinal) >= 0;
            string tool = x64 ? Tools.GenInterfaces64 : Tools.GenInterfaces86;
            if (!File.Exists(tool))
            {
                Log(LogLevel.Dim, "generate_interfaces tool not found – skipping interface dump.");
                return null;
            }
            string tmp = Path.Combine(Path.GetTempPath(), "gp_iface_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmp);
                string dllCopy = Path.Combine(tmp, Path.GetFileName(target));
                File.Copy(target, dllCopy, true);
                var psi = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = "\"" + dllCopy + "\"",
                    WorkingDirectory = tmp,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var sw = Stopwatch.StartNew();
                    while (!p.HasExited)
                    {
                        if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } throw new OperationCanceledException(); }
                        if (sw.Elapsed.TotalSeconds > 60) { try { p.Kill(); } catch { } break; }
                        Thread.Sleep(80);
                    }
                }
                var outFile = Path.Combine(tmp, "steam_interfaces.txt");
                if (File.Exists(outFile) && File.ReadAllLines(outFile).Any(l => l.Trim().Length > 0))
                {
                    Log(LogLevel.Ok, "Generated steam_interfaces.txt from the original dll.");
                    return File.ReadAllText(outFile);
                }
                Log(LogLevel.Dim, "Interface dump produced nothing (dll may not export interfaces) – skipped.");
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log(LogLevel.Dim, "Interface dump failed: " + ex.Message);
                return null;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        /// <summary>Generic online-fix mode: the game's ORIGINAL steam_api dll must stay in place so that,
        /// with steam_appid.txt = 480, the process attaches to the running Steam client as Spacewar and
        /// matchmaking/networking is routed through Valve's servers. If this game was Goldberg-patched
        /// before, the originals are restored from goldberg_backup.</summary>
        private void PrepareOnlineFixMode(string installDir, PatchResult res)
        {
            string backupDir = Path.Combine(installDir, "goldberg_backup");

            // undo a previous Goldberg install if the originals were backed up
            bool restored = false;
            foreach (var n in new[] { "steam_api.dll", "steam_api64.dll" })
            {
                string bak = Path.Combine(backupDir, n);
                if (!File.Exists(bak)) continue;
                string cur = Path.Combine(installDir, n);
                if (!File.Exists(cur) || !FilesEqual(bak, cur))
                {
                    File.Copy(bak, cur, true);
                    Log(LogLevel.Ok, "Restored original " + n + " from goldberg_backup\\");
                    restored = true;
                    res.ReplacedFiles.Add(n);
                }
            }

            // sanity-check the active dll(s): they must not be a bundled Goldberg dll
            bool anyApi = false;
            foreach (var n in new[] { "steam_api.dll", "steam_api64.dll" })
            {
                string cur = Path.Combine(installDir, n);
                if (!File.Exists(cur)) continue;
                anyApi = true;
                if (!restored && LooksLikeBundledGoldberg(cur))
                    throw new Exception("steam_api dll in this game folder is a Goldberg emulator dll and no original backup exists.\n" +
                        "Online-fix mode needs the game's ORIGINAL Steamworks dll so Steam can see the game.\n" +
                        "Restore/reinstall the original steam_api dll first, then run online-fix again.");
                Log(LogLevel.Dim, "Original " + n + " kept in place – required for Steam detection & server routing.");
            }
            if (!anyApi)
                Log(LogLevel.Warn, "No steam_api dll found in the install folder – if this game uses Steamworks, double-check the chosen exe.");
        }

        static bool LooksLikeBundledGoldberg(string dllPath)
        {
            foreach (var src in new[] { Tools.ApiDll86, Tools.ApiDll64 })
            {
                try
                {
                    if (!File.Exists(src)) continue;
                    if (!FilesEqual(dllPath, src)) continue;
                    return true;
                }
                catch { }
            }
            return false;
        }

        static bool FilesEqual(string p1, string p2)
        {
            try
            {
                var f1 = new FileInfo(p1);
                var f2 = new FileInfo(p2);
                if (f1.Length != f2.Length) return false;
                using (var s1 = f1.OpenRead()) using (var s2 = f2.OpenRead())
                {
                    var b1 = new byte[81920];
                    var b2 = new byte[81920];
                    int r1;
                    while ((r1 = s1.Read(b1, 0, b1.Length)) > 0)
                    {
                        int r2 = s2.Read(b2, 0, b2.Length);
                        if (r1 != r2) return false;
                        for (int i = 0; i < r1; i++) if (b1[i] != b2[i]) return false;
                    }
                    return true;
                }
            }
            catch { return false; }
        }

        private void InstallGoldbergDlls(string installDir, ExeArch arch, List<string> foundApi, Func<string, string> backup, PatchResult res)
        {
            string prefName = arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll";
            string otherName = arch == ExeArch.X64 ? "steam_api.dll" : "steam_api64.dll";

            var wanted = new List<string> { prefName };
            if (foundApi.Any(f => string.Equals(Path.GetFileName(f), otherName, StringComparison.OrdinalIgnoreCase)))
                wanted.Add(otherName);

            foreach (var dllName in wanted)
            {
                string src = dllName == "steam_api64.dll" ? Tools.ApiDll64 : Tools.ApiDll86;
                if (!File.Exists(src)) { Log(LogLevel.Error, "Missing bundled emulator dll: " + src); continue; }
                string dst = Path.Combine(installDir, dllName);
                if (File.Exists(dst))
                {
                    backup(dst);
                    Log(LogLevel.Dim, "Backed up original " + dllName);
                }
                File.Copy(src, dst, true);
                res.ReplacedFiles.Add(dllName);
                Log(LogLevel.Ok, "Installed Goldberg → " + dllName + (File.Exists(dst) ? "" : ""));
            }
        }

        private string CopySettingsExample(string installDir)
        {
            string srcRoot = Tools.SettingsExampleDir;
            if (!Directory.Exists(srcRoot)) { Log(LogLevel.Warn, "steam_settings.EXAMPLE folder not found – skipping."); return null; }
            string dstRoot = Path.Combine(installDir, "steam_settings");
            int files = CopyTreeRename(srcRoot, dstRoot);
            Log(LogLevel.Ok, string.Format("steam_settings folder created ({0} files) at: {1}", files, ShortRel(installDir, dstRoot)));
            return dstRoot;
        }

        private static int CopyTreeRename(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            int count = 0;
            foreach (var f in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(f).Replace(".EXAMPLE", "");
                var target = Path.Combine(dst, name);
                if (!File.Exists(target)) { File.Copy(f, target, false); count++; }
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                var name = Path.GetFileName(d).Replace(".EXAMPLE", "");
                count += CopyTreeRename(d, Path.Combine(dst, name));
            }
            return count;
        }

        /// <summary>Ranks candidates: nearest to the exe first; arch-matching name breaks ties.
        /// A dll sitting right next to the exe always wins over deep copies.</summary>
        public static string PickApiTarget(List<string> files, string gameDir, string preferredName)
        {
            return files
                .Select(f => new
                {
                    Path = f,
                    Dist = ShortRel(gameDir, f).Split('\\').Length - 1,
                    ArchMatch = string.Equals(Path.GetFileName(f), preferredName, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
                })
                .OrderBy(x => x.Dist).ThenBy(x => x.ArchMatch).ThenBy(x => x.Path.Length)
                .First().Path;
        }

        // ------------------------------------------------------------------ helpers

        // dirs that never contain a steam_api dll – pruned from deep scans for speed
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "goldberg_backup", "$recycle.bin", "system volume information", "__macosx",
            "_commonredist", "redist", "_redist", "__redist", "directx", "dxsetup", "vcredist",
            "physx", "dotnet", ".git", ".svn", "installers", "__installer",
        };

        /// <summary>Recursively scans the whole game folder (junction-safe, junk-pruned) for steam_api dlls.
        /// Results are ordered nearest-to-exe first.</summary>
        public static List<string> FindSteamApiFiles(string startDir)
        {
            var names = new[] { "steam_api.dll", "steam_api64.dll" };
            var results = new List<string>();
            var stack = new Stack<string>();
            int scanned = 0;
            try
            {
                results.AddRange(names.Select(n => Path.Combine(startDir, n)).Where(File.Exists));
                stack.Push(startDir);
                while (stack.Count > 0 && results.Count < 40 && scanned < 50000)
                {
                    var dir = stack.Pop();
                    scanned++;
                    string[] subdirs;
                    try { subdirs = Directory.GetDirectories(dir); }
                    catch { continue; }
                    foreach (var sd in subdirs)
                    {
                        var ln = Path.GetFileName(sd);
                        if (SkipDirs.Contains(ln) || ln.StartsWith(".")) continue;
                        try
                        {
                            if ((File.GetAttributes(sd) & FileAttributes.ReparsePoint) != 0) continue; // junction/symlink
                        }
                        catch { continue; }
                        results.AddRange(names.Select(n => Path.Combine(sd, n)).Where(File.Exists));
                        stack.Push(sd);
                    }
                }
            }
            catch { }
            return results.OrderBy(r => r.Length).ToList();
        }

        public string FindExistingAppId(params string[] dirs)
        {
            foreach (var d in dirs.Where(d => !string.IsNullOrEmpty(d)))
            {
                if (d == null || !Directory.Exists(d)) continue;
                var f = Path.Combine(d, "steam_appid.txt");
                if (File.Exists(f))
                {
                    try
                    {
                        var line = File.ReadLines(f).FirstOrDefault(l => l.Trim().Length > 0);
                        var digits = Regex.Match(line ?? "", @"\d{1,10}");
                        if (digits.Success) return digits.Value;
                    }
                    catch { }
                }
            }
            return "";
        }

        static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return new string[0]; }
        }

        static void WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content, Encoding.ASCII);
        }

        public static string ShortRel(string root, string fullPath)
        {
            try
            {
                var rp = Path.GetFullPath(root ?? "").TrimEnd('\\') + "\\";
                var fp = Path.GetFullPath(fullPath);
                if (fp.StartsWith(rp, StringComparison.OrdinalIgnoreCase)) return fp.Substring(rp.Length);
            }
            catch { }
            return fullPath;
        }
    }

    internal static class Ext
    {
        public static IEnumerable<T> TakeLastVisible<T>(this IList<T> list, int n) { return list; }
    }

    // --------------------------------------------------------------------- app settings

    public class AppSettings
    {
        public string LastExe = "";
        public string LastAppId = "";
        public bool UnpackDrm = true;
        public bool Backup = true;
        public bool WriteAppIdTxt = true;
        public bool CreateSettings = false;
        public bool GenerateInterfaces = true;
        public bool OnlineFix = false;
        public Dictionary<string, string> AppIdsByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldbergPatcher"); } }
        static string File0 { get { return Path.Combine(Dir, "settings.ini"); } }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (!System.IO.File.Exists(File0)) return s;
                foreach (var raw in System.IO.File.ReadAllLines(File0))
                {
                    var i = raw.IndexOf('=');
                    if (i <= 0) continue;
                    var k = raw.Substring(0, i);
                    var v = raw.Substring(i + 1);
                    switch (k)
                    {
                        case "lastexe": s.LastExe = v; break;
                        case "appid": s.LastAppId = v; break;
                        case "unpack": s.UnpackDrm = v == "1"; break;
                        case "backup": s.Backup = v == "1"; break;
                        case "appidsrc": s.WriteAppIdTxt = v == "1"; break;
                        case "settings": s.CreateSettings = v == "1"; break;
                        case "interfaces": s.GenerateInterfaces = v == "1"; break;
                        case "onlinefix": s.OnlineFix = v == "1"; break;
                        default:
                            if (k.StartsWith("folder:"))
                                s.AppIdsByFolder[k.Substring(7)] = v;
                            break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("lastexe=" + LastExe);
                sb.AppendLine("appid=" + LastAppId);
                sb.AppendLine("unpack=" + (UnpackDrm ? "1" : "0"));
                sb.AppendLine("backup=" + (Backup ? "1" : "0"));
                sb.AppendLine("appidsrc=" + (WriteAppIdTxt ? "1" : "0"));
                sb.AppendLine("settings=" + (CreateSettings ? "1" : "0"));
                sb.AppendLine("interfaces=" + (GenerateInterfaces ? "1" : "0"));
                sb.AppendLine("onlinefix=" + (OnlineFix ? "1" : "0"));
                foreach (var kv in AppIdsByFolder)
                    sb.AppendLine("folder:" + kv.Key + "=" + kv.Value);
                System.IO.File.WriteAllText(File0, sb.ToString());
            }
            catch { }
        }
    }
}
