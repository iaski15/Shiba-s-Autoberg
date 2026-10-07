using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Xml;
using System.Xml.Linq;
using Shibaless;

namespace Gp
{
    public enum LogLevel { Info, Dim, Ok, Warn, Error }

    public enum ExeArch { Unknown, X86, X64 }

    /// <summary>Display scale relative to 96 DPI, for the hand-positioned paint geometry that WinForms'
    /// own auto-scaling cannot reach. Lives here rather than in Ui.cs so the self-test - which compiles
    /// Core.cs and TestMain.cs only - can cover it.</summary>
    public static class Dpi
    {
        public static float Scale = 1f;

        public static void Initialize()
        {
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            }
            catch { Scale = 1f; }
            if (Scale < 0.5f || Scale > 4f) Scale = 1f;   // nonsense DPI: fall back rather than distort
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static float S(float px) { return px * Scale; }
        public static System.Drawing.PointF S(System.Drawing.PointF p)
        {
            return new System.Drawing.PointF(p.X * Scale, p.Y * Scale);
        }
        public static System.Drawing.Point S(System.Drawing.Point p)
        {
            return new System.Drawing.Point((int)Math.Round(p.X * Scale), (int)Math.Round(p.Y * Scale));
        }
    }

    /// <summary>The release version, read from the assembly attribute that build.ps1 injects rather than
    /// hardcoded in the paint method that displays it - the two drifted apart the first time the literal
    /// was forgotten. Lives here, not in Ui.cs, so the self-test can assert it.</summary>
    public static class BuildInfo
    {
        /// <summary>The product name shown to users. Internal names (the %APPDATA%\GoldbergPatcher state folder,
        /// the payload folder, mutex names) deliberately keep the old name: renaming them would orphan existing
        /// settings, AppID caches and undo records.</summary>
        public const string AppName = "Shibaberg";

        static string version;

        public static string Version
        {
            get
            {
                if (version != null) return version;
                try
                {
                    var attrs = typeof(BuildInfo).Assembly
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                    if (attrs.Length > 0)
                        version = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                }
                catch { }
                if (string.IsNullOrEmpty(version)) version = "0.0";   // unbuilt/unknown, never blank
                return version;
            }
        }
    }

    /// <summary>Single source of truth for where the app keeps its own state. The
    /// %APPDATA%\GoldbergPatcher path used to be duplicated across Core, Batch and MainForm.</summary>
    public static class AppPaths
    {
        public static string StateDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldbergPatcher"); }
        }

        /// <summary>Undo records for the most recent patch run, kept outside the game folder so that
        /// "Undo last patch" still works after the app is restarted.</summary>
        public static string LastPatchDir { get { return Path.Combine(StateDir, "last-patch"); } }

        public static string LastPatchJournal { get { return Path.Combine(LastPatchDir, "journal.txt"); } }
    }

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
        static void RequireRange(long offset, long size, long length)
        {
            if (offset < 0 || size < 0 || offset > length || size > length - offset)
                throw new InvalidDataException("PE range is outside the file or header.");
        }

        /// <summary>Everything the header walk produces, so <see cref="Analyze"/> and
        /// <see cref="ImportedDlls"/> share one bounds-checked parse instead of two that can drift.</summary>
        sealed class Layout
        {
            public long Length;
            public ushort Machine;
            public ushort Characteristics;
            public ushort Magic;
            public int Count;
            public int OptSize;
            public long Opt;
            public int Dd;
            public uint Headers;
            public uint Directories;
            public readonly List<long[]> Sections = new List<long[]>();
        }

        static Layout ReadLayout(FileStream fs, BinaryReader br)
        {
            var L = new Layout { Length = fs.Length };
            RequireRange(0, 64, fs.Length);
            if (br.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Missing DOS signature.");
            fs.Position = 0x3c;
            long pe = br.ReadUInt32();
            if (pe < 64) throw new InvalidDataException("Invalid PE header offset.");
            RequireRange(pe, 24, fs.Length);
            fs.Position = pe;
            if (br.ReadUInt32() != 0x4550) throw new InvalidDataException("Missing PE signature.");
            L.Machine = br.ReadUInt16();
            L.Count = br.ReadUInt16();
            fs.Position = pe + 20;
            L.OptSize = br.ReadUInt16();
            L.Characteristics = br.ReadUInt16();
            L.Opt = pe + 24;
            RequireRange(L.Opt, L.OptSize, fs.Length);
            RequireRange(0, 2, L.OptSize);
            fs.Position = L.Opt;
            L.Magic = br.ReadUInt16();
            if (L.Magic != 0x10b && L.Magic != 0x20b) throw new InvalidDataException("Unsupported PE optional header.");
            if ((L.Machine == 0x14c && L.Magic != 0x10b) || (L.Machine == 0x8664 && L.Magic != 0x20b))
                throw new InvalidDataException("PE machine and optional header disagree.");
            L.Dd = L.Magic == 0x20b ? 112 : 96;
            RequireRange(0, L.Dd, L.OptSize);
            fs.Position = L.Opt + 60;
            L.Headers = br.ReadUInt32();
            RequireRange(0, L.Headers, fs.Length);
            fs.Position = L.Opt + L.Dd - 4;
            L.Directories = br.ReadUInt32();
            RequireRange(L.Dd, (long)L.Directories * 8, L.OptSize);
            long table = L.Opt + L.OptSize;
            RequireRange(table, (long)L.Count * 40, fs.Length);
            if (L.Count == 0 || table + (long)L.Count * 40 > L.Headers)
                throw new InvalidDataException("Invalid PE section table.");
            for (int i = 0; i < L.Count; i++)
            {
                fs.Position = table + i * 40L + 8;
                long virtualSize = br.ReadUInt32();
                long va = br.ReadUInt32();
                long rawSize = br.ReadUInt32();
                long raw = br.ReadUInt32();
                RequireRange(raw, rawSize, fs.Length);
                if (va + Math.Max(virtualSize, rawSize) > 0x100000000L)
                    throw new InvalidDataException("PE section RVA overflows.");
                L.Sections.Add(new[] { va, rawSize, raw });
            }
            return L;
        }

        /// <summary>Names of the DLLs in the executable's import directory (data directory index 1), in
        /// file order. This is what the loader will actually ask for — which is not always derivable from
        /// the CPU architecture, since wrapper layers and renamed redistributables exist where a 64-bit
        /// Steamworks library is imported under the 32-bit name.</summary>
        public static List<string> ImportedDlls(string path)
        {
            var names = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var L = ReadLayout(fs, br);
                if (L.Directories <= 1) return names;
                fs.Position = L.Opt + L.Dd + 8;                 // index 1: the import directory
                uint rva = br.ReadUInt32();
                uint size = br.ReadUInt32();
                if (rva == 0 || size == 0) return names;

                long start = MapRva(rva, size, L.Headers, L.Sections);
                RequireRange(start, size, fs.Length);
                // 20 bytes per IMAGE_IMPORT_DESCRIPTOR; the array ends at the first all-zero entry.
                int entries = (int)Math.Min(size / 20, 4096);
                for (int i = 0; i < entries; i++)
                {
                    fs.Position = start + i * 20L;
                    br.ReadUInt32();        // OriginalFirstThunk
                    br.ReadUInt32();        // TimeDateStamp
                    br.ReadUInt32();        // ForwarderChain
                    uint nameRva = br.ReadUInt32();
                    br.ReadUInt32();        // FirstThunk
                    if (nameRva == 0) break;
                    try
                    {
                        long at = MapRva(nameRva, 1, L.Headers, L.Sections);
                        RequireRange(at, 1, fs.Length);
                        var sb = new StringBuilder(64);
                        long limit = Math.Min(at + 260, fs.Length);
                        fs.Position = at;               // the descriptor walk moved the position; seek back
                        for (long p = at; p < limit; p++)
                        {
                            int ch = br.ReadByte();
                            if (ch == 0) break;
                            sb.Append((char)ch);
                        }
                        string name = sb.ToString().Trim();
                        if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                            names.Add(name);
                    }
                    catch { /* a malformed name RVA must not discard the other imports */ }
                }
            }
            return names;
        }

        public static PeInfo Analyze(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var L = ReadLayout(fs, br);
                var info = new PeInfo { SizeBytes = L.Length, Machine = L.Machine };
                uint flags = 0;
                if (L.Directories > 14)
                {
                    fs.Position = L.Opt + L.Dd + 14 * 8;
                    uint rva = br.ReadUInt32();
                    uint size = br.ReadUInt32();
                    if (rva != 0 || size != 0)
                    {
                        if (rva == 0 || size < 72) throw new InvalidDataException("Invalid CLR directory.");
                        long cor = MapRva(rva, size, L.Headers, L.Sections);
                        RequireRange(cor, size, fs.Length);
                        fs.Position = cor;
                        uint cb = br.ReadUInt32();
                        if (cb < 72 || cb > size) throw new InvalidDataException("Invalid CLR header size.");
                        fs.Position = cor + 16;
                        flags = br.ReadUInt32();
                        info.Managed = true;
                    }
                }
                switch (info.Machine)
                {
                    case 0x14c: info.Arch = ExeArch.X86; info.MachineText = "x86"; break;
                    case 0x8664: info.Arch = ExeArch.X64; info.MachineText = "x64"; break;
                    case 0xaa64: info.MachineText = "ARM64 (unsupported)"; break;
                    default: info.MachineText = "Unsupported machine 0x" + info.Machine.ToString("X4"); break;
                }
                bool prefer32 = (flags & 0x20000) != 0 && (L.Characteristics & 0x2000) == 0;
                info.AnyCpu = info.Managed && info.Machine == 0x14c && (flags & 1) != 0 && (flags & 2) == 0 && !prefer32;
                if (info.AnyCpu)
                {
                    info.Arch = Environment.Is64BitOperatingSystem ? ExeArch.X64 : ExeArch.X86;
                    info.MachineText = "AnyCPU (" + (info.Arch == ExeArch.X64 ? "runs x64" : "runs x86") + ")";
                }
                return info;
            }
        }

        static long MapRva(uint rva, uint size, uint headers, List<long[]> sections)
        {
            long end = (long)rva + size;
            if (end > 0x100000000L) throw new InvalidDataException("CLR RVA overflows.");
            if (end <= headers) return rva;
            long mapped = -1;
            foreach (var s in sections)
            {
                if (rva < s[0] || end > s[0] + s[1]) continue;
                if (mapped >= 0) throw new InvalidDataException("Ambiguous CLR RVA mapping.");
                mapped = s[2] + (rva - s[0]);
            }
            if (mapped < 0) throw new InvalidDataException("CLR directory is not backed by file data.");
            return mapped;
        }
    }

    public sealed class FileWriteRecord
    {
        public string Destination;
        public string StagedPath;
        public string RecoveryPath;
        public string JournalPath;
        public bool Completed;

        /// <summary>Hash of the content that was in place before this write, or "absent" when the
        /// destination did not exist. This is what rollback restores.</summary>
        public string PreviousHash = "";

        /// <summary>Hash of the content this write installed.</summary>
        public string StagedHash = "";

        /// <summary>True when <see cref="RecoveryPath"/> is a pre-existing verified backup (the
        /// goldberg_backup copy) rather than a copy taken inside the staging area. Such a path lives
        /// outside the staging area and must never be garbage-collected as part of it.</summary>
        public bool ExternalRecovery;

        /// <summary>The per-write staging directory this record owns, derived from the journal path.</summary>
        public string Area
        {
            get { return JournalPath.Length == 0 ? "" : Path.GetDirectoryName(JournalPath); }
        }
    }

    /// <summary>Write-through stream that hashes every byte on its way to the inner stream, so the hash of
    /// staged content falls out of the copy itself instead of costing a second full read of the file.</summary>
    internal sealed class HashingStream : Stream
    {
        readonly Stream inner;
        readonly SHA256 sha = SHA256.Create();
        string hex;

        internal HashingStream(Stream inner) { this.inner = inner; }

        /// <summary>Hex SHA-256 of everything written so far. Finalises the hash: write nothing after reading it.</summary>
        internal string Hex
        {
            get
            {
                if (hex == null) { sha.TransformFinalBlock(new byte[0], 0, 0); hex = SafePersistence.ToHex(sha.Hash); }
                return hex;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (hex != null) throw new InvalidOperationException("Hash already finalised.");
            sha.TransformBlock(buffer, offset, count, null, 0);
            inner.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value) { Write(new[] { value }, 0, 1); }
        public override void Flush() { inner.Flush(); }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return inner.Length; } }
        public override long Position { get { return inner.Position; } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) sha.Dispose(); base.Dispose(disposing); }
    }

    public static class SafePersistence
    {
        const int IoBuffer = 1 << 20;

        public static string Hash(string path)
        {
            // A large sequential buffer: the default 4 KB FileStream buffer turns a multi-hundred-MB exe
            // into tens of thousands of tiny reads.
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, IoBuffer, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(input));
        }

        public static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(data));
        }

        static readonly char[] HexDigits = "0123456789abcdef".ToCharArray();

        internal static string ToHex(byte[] bytes)
        {
            var c = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++) { c[2 * i] = HexDigits[bytes[i] >> 4]; c[2 * i + 1] = HexDigits[bytes[i] & 15]; }
            return new string(c);
        }

        internal static string PathKey(string path)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
        }

        [ThreadStatic] static HashSet<string> heldKeys;

        internal static T Locked<T>(string path, Func<T> action)
        {
            string key = PathKey(path);
            // Windows mutexes are recursive for the owning thread, so a nested Locked() on the same path
            // happens to work today - but only by accident, and it silently becomes a 30-second stall
            // followed by "Another instance is writing" the moment an inner call moves to another thread.
            // Tracking the held keys explicitly makes re-entry intentional instead of incidental.
            if (heldKeys != null && heldKeys.Contains(key)) return action();

            using (var mutex = new Mutex(false, @"Local\GoldbergPatcher-" + key))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Another instance is writing " + path + ". Retry after it finishes.");
                    if (heldKeys == null) heldKeys = new HashSet<string>(StringComparer.Ordinal);
                    heldKeys.Add(key);
                    try { return action(); }
                    finally { heldKeys.Remove(key); }
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        static void FlushJournal(string path, string text, FileMode mode)
        {
            using (var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <param name="externalRecovery">Path to an already-verified copy of the current destination
        /// content (the goldberg_backup original). When supplied, no second copy is taken inside the
        /// staging area – the caller has already paid for one – and rollback restores from there.</param>
        /// <param name="expectedStagedHash">When set, the bytes written must hash to exactly this, or the write
        /// is abandoned before anything is replaced. Checked against the hash taken while writing, so it costs
        /// no extra read.</param>
        /// <param name="knownPreviousHash">Hash of what rollback will restore - the external recovery file, or
        /// the destination's current content - when the caller has already established it. Saves a full read
        /// of a file that can be hundreds of megabytes.</param>
        public static FileWriteRecord Write(string path, Action<Stream> write, Action<string> validate = null,
            List<FileWriteRecord> journal = null, Action<string> checkpoint = null, string externalRecovery = null,
            string expectedStagedHash = null, string knownPreviousHash = null)
        {
            path = Path.GetFullPath(path);
            return Locked(path, () =>
            {
                string parent = Path.GetDirectoryName(path);
                // 12 hex digits, not 32: these areas nest inside goldberg_backup under Unreal's already deep
                // Engine\Binaries\ThirdParty\Steamworks\...\Win64 folders, and the full GUID pushed staging
                // paths past the 260-character MAX_PATH limit.
                string area = Path.Combine(parent, ".gp-recovery", Guid.NewGuid().ToString("N").Substring(0, 12));
                Directory.CreateDirectory(area);
                string name = Path.GetFileName(path);
                bool external = !string.IsNullOrEmpty(externalRecovery);
                var record = new FileWriteRecord
                {
                    Destination = path,
                    StagedPath = Path.Combine(area, name + ".staged-" + Guid.NewGuid().ToString("N").Substring(0, 8)),
                    RecoveryPath = external
                        ? Path.GetFullPath(externalRecovery)
                        : (File.Exists(path) ? Path.Combine(area, name + ".previous") : ""),
                    JournalPath = Path.Combine(area, name + ".journal.txt"),
                    ExternalRecovery = external
                };
                if (journal != null) journal.Add(record);
                try
                {
                    using (var stream = new FileStream(record.StagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, IoBuffer))
                    using (var hashing = new HashingStream(stream))
                    {
                        write(hashing);
                        hashing.Flush();
                        stream.Flush(true);
                        record.StagedHash = hashing.Hex;
                    }
                    if (expectedStagedHash != null && record.StagedHash != expectedStagedHash)
                        throw new InvalidDataException("Staged copy hash mismatch: " + path);
                    if (validate != null) validate(record.StagedPath);
                    if (checkpoint != null) checkpoint("staged");
                    // For a local recovery copy, File.Replace will move the destination's current
                    // content there, so the destination is what we must hash. For an external recovery
                    // the copy already exists and is what rollback will restore from.
                    string oldHash = record.RecoveryPath.Length == 0 && !external ? "absent"
                        : knownPreviousHash ?? (external ? Hash(record.RecoveryPath) : Hash(path));
                    record.PreviousHash = oldHash;
                    var j = new StringBuilder();
                    j.AppendLine("destination=" + path);
                    j.AppendLine("staged=" + record.StagedPath);
                    j.AppendLine("recovery=" + record.RecoveryPath);
                    j.AppendLine("previous-sha256=" + oldHash);
                    j.AppendLine("staged-sha256=" + record.StagedHash);
                    j.AppendLine("area=" + area);
                    j.AppendLine("state=prepared");
                    FlushJournal(record.JournalPath, j.ToString(), FileMode.CreateNew);
                    if (checkpoint != null) checkpoint("prepared");
                    // Order matters: an external recovery source must never be handed to File.Replace as
                    // its backup argument, or the verified original would be overwritten by whatever the
                    // destination happened to contain.
                    if (external && File.Exists(path))
                        // null backup: the destination's previous content is deliberately discarded, it is
                        // already preserved in the verified external backup.
                        File.Replace(record.StagedPath, path, null);
                    else if (!external && record.RecoveryPath.Length != 0 && File.Exists(path))
                        File.Replace(record.StagedPath, path, record.RecoveryPath);
                    else
                        File.Move(record.StagedPath, path);
                    record.Completed = true;
                    FlushJournal(record.JournalPath, "state=completed" + Environment.NewLine, FileMode.Append);
                    return record;
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new UnauthorizedAccessException("Access denied while writing " + path + ". Recovery/staging records: " + area + ". " + ex.Message, ex);
                }
                catch (Exception ex)
                {
                    throw new IOException("Write failed for " + path + ". Recovery/staging records: " + area + ". " + ex.Message, ex);
                }
            });
        }

        /// <param name="knownSourceHash">The source's hash when the caller already holds it. The copy is
        /// still checked against it byte for byte (hash-on-write), so a source that changed in between is
        /// caught exactly as before - only the separate up-front read is skipped.</param>
        public static FileWriteRecord Copy(string source, string destination, List<FileWriteRecord> journal = null,
            Action<string> validate = null, string externalRecovery = null, string knownSourceHash = null,
            string knownPreviousHash = null)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Source file for staged copy not found: " + source, source);
            string expected = knownSourceHash ?? Hash(source);
            return Write(destination, output =>
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, IoBuffer, FileOptions.SequentialScan))
                    input.CopyTo(output, IoBuffer);
            }, validate, journal, null, externalRecovery, expected, knownPreviousHash);
        }

        public static FileWriteRecord WriteText(string path, string text, List<FileWriteRecord> journal = null,
            string externalRecovery = null)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            return Write(path, stream => stream.Write(bytes, 0, bytes.Length), null, journal, null, externalRecovery);
        }
    }

    public static class OriginalBackups
    {
        /// <summary>Where the backup of <paramref name="source"/> is kept. The folder is the first 16 hex digits
        /// of the path hash - ample to keep the handful of files in one backup root apart, and 48 characters
        /// shorter than the full hash, which put staging paths under Unreal games past MAX_PATH.</summary>
        public static string Location(string root, string source)
        {
            return Path.Combine(root, "sources", SafePersistence.PathKey(source).Substring(0, 16), Path.GetFileName(source));
        }

        /// <summary>The full-hash location used by releases up to 0.5. Still read, so a backup taken by an
        /// older version is found instead of a fresh "original" being taken from the already patched file.</summary>
        static string LegacyLocation(string root, string source)
        {
            return Path.Combine(root, "sources", SafePersistence.PathKey(source), Path.GetFileName(source));
        }

        /// <summary>The backup of <paramref name="source"/> as it exists on disk - current or legacy layout -
        /// or the current location when neither exists.</summary>
        public static string Find(string root, string source)
        {
            string current = Location(root, source);
            if (File.Exists(current)) return current;
            string legacy = LegacyLocation(root, source);
            return File.Exists(legacy) ? legacy : current;
        }

        static string VerifiedHash(string backup, string source)
        {
            string manifest = backup + ".source.txt";
            if (!File.Exists(manifest)) throw new IOException("Backup has no source verification record: " + backup);
            var lines = File.ReadAllLines(manifest);
            if (lines.Length != 2 || !string.Equals(lines[0], Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
                || lines[1] != SafePersistence.Hash(backup))
                throw new IOException("Backup verification failed; preserve and inspect " + backup);
            return lines[1];
        }

        static bool Eligible(string path)
        {
            return File.Exists(path) && new FileInfo(path).Length > 0 && !PatchRunner.LooksLikeBundledGoldberg(path);
        }

        /// <summary>Moves a backup that failed verification aside so a fresh one can be taken from the live
        /// file. Returns the new path, or null when it could not be moved - in which case the caller still
        /// fails safe rather than overwriting something it cannot vouch for.</summary>
        static string Quarantine(string backup, Action<LogLevel, string> log)
        {
            try
            {
                string target = backup + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(backup, target);
                string manifest = backup + ".source.txt";
                if (File.Exists(manifest)) File.Move(manifest, target + ".source.txt");
                if (log != null)
                    log(LogLevel.Warn, "Existing backup failed verification and was set aside as "
                        + Path.GetFileName(target) + " – a fresh copy will be taken from the current file.");
                return target;
            }
            catch { return null; }
        }

        static string Existing(string root, string source, out string hash, Action<LogLevel, string> log)
        {
            string destination = Find(root, source);
            IOException failure = null;
            hash = null;
            if (File.Exists(destination))
            {
                try
                {
                    hash = VerifiedHash(destination, source);
                    if (Eligible(destination)) return destination;
                    failure = new IOException("Backup is not an eligible original: " + destination);
                }
                catch (IOException ex)
                {
                    // A backup that no longer verifies used to abort the whole patch with "preserve and
                    // inspect ...", leaving the user to delete files by hand before they could patch again.
                    // Set the bad copy aside and re-preserve instead; the guard in Eligible still refuses to
                    // promote a bundled Goldberg dll, so this cannot turn a patched file into "the original".
                    if (Quarantine(destination, log) != null) failure = null;
                    else failure = ex;
                }
            }
            string legacy = Path.Combine(root, Path.GetFileName(source));
            if (string.Equals(Path.GetFullPath(root), Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source)), "goldberg_backup"), StringComparison.OrdinalIgnoreCase)
                && Eligible(legacy))
            {
                hash = SafePersistence.Hash(legacy);
                return legacy;
            }
            if (failure != null) throw failure;
            hash = null;
            return null;
        }

        public static string Preserve(string root, string source, Action<LogLevel, string> log = null)
        {
            string ignored;
            return Preserve(root, source, log, null, out ignored);
        }

        /// <param name="knownSourceHash">Hash of <paramref name="source"/> when the caller already has it.</param>
        /// <param name="backupHash">Hash of the backup that is returned, so callers can hand it on instead
        /// of reading the backup again.</param>
        public static string Preserve(string root, string source, Action<LogLevel, string> log, string knownSourceHash, out string backupHash)
        {
            string destination = Location(root, source);
            string result = null, resultHash = null;
            SafePersistence.Locked(destination, () =>
            {
                string hash;
                string existing = Existing(root, source, out hash, log);
                if (existing != null && File.Exists(destination)) { result = existing; resultHash = hash; return true; }
                string content = existing ?? source;
                if (!Eligible(content)) return true;
                if (hash == null && existing == null) hash = knownSourceHash;
                // These two writes are the backup itself, so they need no undo record of their own –
                // and they are not part of any run's journal, so nothing else would ever collect their
                // staging areas. Drop them here or goldberg_backup accumulates .gp-recovery litter.
                // Copy verifies the bytes it wrote against 'hash' (or hashes the source first when no hash
                // is known); either way a source that changes mid-copy is rejected.
                FileWriteRecord backupWrite;
                try
                {
                    backupWrite = SafePersistence.Copy(content, destination, null, staged =>
                    {
                        if (!Eligible(staged)) throw new IOException("Original changed while preserving: " + content);
                    }, null, hash);
                }
                catch (IOException ex) when (ex.InnerException is InvalidDataException)
                {
                    throw new IOException("Original changed while preserving: " + content, ex);
                }
                hash = backupWrite.StagedHash;
                var manifestWrite = SafePersistence.WriteText(destination + ".source.txt", Path.GetFullPath(source) + "\r\n" + hash + "\r\n");
                Recovery.Discard(new[] { backupWrite, manifestWrite });
                result = destination; resultHash = hash;
                return true;
            });
            backupHash = resultHash;
            return result;
        }

        public static void Restore(string root, string source, List<FileWriteRecord> journal = null, Action<LogLevel, string> log = null)
        {
            SafePersistence.Locked(Location(root, source), () =>
            {
                string hash;
                string backup = Existing(root, source, out hash, log);
                if (backup == null) throw new IOException("No verified or eligible legacy original backup exists for " + source);
                FileWriteRecord write;
                try
                {
                    write = SafePersistence.Copy(backup, source, journal, staged =>
                    {
                        if (!Eligible(staged)) throw new IOException("Original backup changed while restoring: " + backup);
                    }, null, hash);
                }
                catch (IOException ex) when (ex.InnerException is InvalidDataException)
                {
                    throw new IOException("Original backup changed while restoring: " + backup, ex);
                }
                // When no run journal was supplied nothing will collect this staging area.
                if (journal == null) Recovery.Discard(new[] { write });
                return true;
            });
        }
    }

    /// <summary>One restorable step of a patch run, parsed from a journal. When
    /// <see cref="PreviousHash"/> is "absent" the destination did not exist before the patch, so
    /// undoing it means deleting the file.</summary>
    public sealed class RecoveryEntry
    {
        public string Destination = "";
        public string RecoveryPath = "";
        public string PreviousHash = "";
        public string StagedHash = "";
        public string Area = "";
        public bool Completed;
    }

    public sealed class RecoveryReport
    {
        public int Restored;
        public int Deleted;
        public int Skipped;
        public int Failed;
        public readonly List<string> Messages = new List<string>();

        public bool ChangedAnything { get { return Restored > 0 || Deleted > 0; } }

        public string Summary
        {
            get
            {
                if (Restored == 0 && Deleted == 0 && Skipped == 0 && Failed == 0) return "Nothing to undo.";
                var parts = new List<string>();
                if (Restored > 0) parts.Add(Restored + " restored");
                if (Deleted > 0) parts.Add(Deleted + " removed");
                if (Skipped > 0) parts.Add(Skipped + " left alone");
                if (Failed > 0) parts.Add(Failed + " failed");
                return string.Join(", ", parts) + ".";
            }
        }
    }

    /// <summary>Replays the journal that <see cref="SafePersistence"/> writes, in reverse, so a patch
    /// that failed or was cancelled part-way can be undone instead of leaving the game half-patched.
    /// The journal is mirrored into %APPDATA% so the undo survives a restart.</summary>
    public static class Recovery
    {
        // Both hooks are assigned only by TestMain.cs, which is compiled into _selftest.exe and not into
        // the app - so the app's own compilation sees them as never assigned. They are not dead: the test
        // host depends on them, and they are what keeps a self-test run out of real application state.
#pragma warning disable 0649
        /// <summary>Test hook: redirects the undo journal away from the real application state
        /// directory so a self-test run cannot disturb a pending undo.</summary>
        internal static string JournalPathOverride;

        /// <summary>Test hook: keeps the startup sweep out of the real application state directory.</summary>
        internal static string StateRootOverride;
#pragma warning restore 0649

        static string StateRoot { get { return StateRootOverride ?? AppPaths.StateDir; } }

        public static string JournalPath
        {
            get { return JournalPathOverride ?? AppPaths.LastPatchJournal; }
        }

        public static bool HasLastPatch()
        {
            try { return LoadJournal(JournalPath).Any(e => e.Completed); }
            catch { return false; }
        }

        public static List<RecoveryEntry> EntriesFrom(IEnumerable<FileWriteRecord> writes)
        {
            var list = new List<RecoveryEntry>();
            if (writes == null) return list;
            foreach (var w in writes)
                list.Add(new RecoveryEntry
                {
                    Destination = w.Destination,
                    RecoveryPath = w.RecoveryPath,
                    PreviousHash = w.PreviousHash,
                    StagedHash = w.StagedHash,
                    Area = w.Area,
                    Completed = w.Completed,
                });
            return list;
        }

        // ------------------------------------------------------------------ journal file

        public static List<RecoveryEntry> LoadJournal(string path)
        {
            var list = new List<RecoveryEntry>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
                RecoveryEntry cur = null;
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.TrimEnd();
                    if (line.Length == 0) continue;
                    if (line == "--")
                    {
                        if (cur != null) list.Add(cur);
                        cur = null;
                        continue;
                    }
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i);
                    string v = line.Substring(i + 1);
                    if (k == "destination") { cur = new RecoveryEntry(); cur.Destination = v; continue; }
                    if (cur == null) continue;
                    switch (k)
                    {
                        case "recovery": cur.RecoveryPath = v; break;
                        case "previous-sha256": cur.PreviousHash = v; break;
                        case "staged-sha256": cur.StagedHash = v; break;
                        case "area": cur.Area = v; break;
                        case "state": cur.Completed = v == "completed"; break;
                    }
                }
                if (cur != null) list.Add(cur);
            }
            catch { }
            return list;
        }

        /// <summary>Records the finished run so "Undo last patch" survives a restart. The previous
        /// run's staging areas are pruned first, which bounds the litter to one run's worth.</summary>
        public static void SaveJournal(IEnumerable<FileWriteRecord> writes, bool success)
        {
            try
            {
                var entries = EntriesFrom(writes);
                PruneAreas(LoadJournal(JournalPath));
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));

                var sb = new StringBuilder();
                sb.AppendLine("patch=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.AppendLine("success=" + (success ? "1" : "0"));
                sb.AppendLine("count=" + entries.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var e in entries)
                {
                    sb.AppendLine("destination=" + e.Destination);
                    sb.AppendLine("recovery=" + e.RecoveryPath);
                    sb.AppendLine("previous-sha256=" + e.PreviousHash);
                    sb.AppendLine("staged-sha256=" + e.StagedHash);
                    sb.AppendLine("area=" + e.Area);
                    sb.AppendLine("state=" + (e.Completed ? "completed" : "prepared"));
                    sb.AppendLine("--");
                }

                string tmp = JournalPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                File.Copy(tmp, JournalPath, true);
                File.Delete(tmp);
            }
            catch { /* the undo journal is a convenience; never fail a patch over it */ }
        }

        public static void ClearLastPatch()
        {
            try { if (File.Exists(JournalPath)) File.Delete(JournalPath); } catch { }
        }

        /// <summary>Forgets the last patch once it has been fully undone: drops the journal AND the
        /// .gp-recovery areas it pointed at. <see cref="ClearLastPatch"/> alone left those areas (and
        /// their empty .gp-recovery roots) in every touched game folder, where nothing would ever collect
        /// them, because the next run only prunes what the journal still lists.</summary>
        public static void DiscardLastPatch()
        {
            var entries = LoadJournal(JournalPath);
            ClearLastPatch();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (e.Area.Length == 0 || !seen.Add(e.Area)) continue;
                try { if (Directory.Exists(e.Area)) Directory.Delete(e.Area, true); } catch { }
                RemoveEmptyRecoveryRoot(e.Area);
            }
        }

        // ------------------------------------------------------------------ rollback

        public static RecoveryReport RollbackWrites(IEnumerable<FileWriteRecord> writes, Action<LogLevel, string> log)
        {
            return Rollback(EntriesFrom(writes), log);
        }

        public static RecoveryReport RollbackLastPatch(Action<LogLevel, string> log)
        {
            return Rollback(LoadJournal(JournalPath), log);
        }

        /// <summary>Undoes the given records newest-first. A destination that no longer matches what
        /// the patch wrote is left alone rather than clobbered, so a file the user edited after
        /// patching is never silently overwritten.</summary>
        public static RecoveryReport Rollback(IEnumerable<RecoveryEntry> entries, Action<LogLevel, string> log)
        {
            var report = new RecoveryReport();
            var list = new List<RecoveryEntry>(entries ?? new RecoveryEntry[0]);
            list.Reverse();
            foreach (var e in list)
            {
                if (!e.Completed) { report.Skipped++; continue; }
                try
                {
                    bool exists = e.Destination.Length != 0 && File.Exists(e.Destination);

                    if (exists)
                    {
                        string current = SafePersistence.Hash(e.Destination);
                        if (current == e.PreviousHash) { report.Skipped++; continue; }   // already back
                        if (e.StagedHash.Length != 0 && current != e.StagedHash)
                        {
                            report.Skipped++;
                            report.Messages.Add(Path.GetFileName(e.Destination) + " changed after the patch – left it alone.");
                            Say(log, LogLevel.Warn, "Not undoing " + Path.GetFileName(e.Destination) + " – it was modified after the patch.");
                            continue;
                        }
                    }
                    else if (e.PreviousHash == "absent") { report.Skipped++; continue; }

                    if (e.PreviousHash == "absent")
                    {
                        File.Delete(e.Destination);
                        report.Deleted++;
                        Say(log, LogLevel.Ok, "Removed " + Path.GetFileName(e.Destination));
                        continue;
                    }

                    if (!File.Exists(e.RecoveryPath))
                    {
                        report.Failed++;
                        report.Messages.Add("Cannot restore " + Path.GetFileName(e.Destination) + " – its recovery copy is missing.");
                        continue;
                    }
                    if (SafePersistence.Hash(e.RecoveryPath) != e.PreviousHash)
                    {
                        report.Failed++;
                        report.Messages.Add("Cannot restore " + Path.GetFileName(e.Destination) + " – the recovery copy no longer matches its recorded hash.");
                        continue;
                    }

                    RestoreFrom(e);
                    report.Restored++;
                    Say(log, LogLevel.Ok, "Restored " + Path.GetFileName(e.Destination));
                }
                catch (Exception ex)
                {
                    report.Failed++;
                    report.Messages.Add("Could not undo " + Path.GetFileName(e.Destination) + ": " + ex.Message);
                    Say(log, LogLevel.Warn, "Could not undo " + Path.GetFileName(e.Destination) + ": " + ex.Message);
                }
            }
            return report;
        }

        static void Say(Action<LogLevel, string> log, LogLevel level, string message)
        {
            if (log != null) log(level, message);
        }

        /// <summary>Copies the recovery content over the destination through a temp file in the same
        /// directory, so the swap stays on one volume and is verified before it replaces anything.</summary>
        static void RestoreFrom(RecoveryEntry e)
        {
            string dir = Path.GetDirectoryName(e.Destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = e.Destination + ".undo-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                File.Copy(e.RecoveryPath, tmp, true);
                if (SafePersistence.Hash(tmp) != e.PreviousHash)
                    throw new IOException("Undo copy failed hash verification.");
                if (File.Exists(e.Destination)) File.Replace(tmp, e.Destination, null);
                else File.Move(tmp, e.Destination);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ------------------------------------------------------------------ garbage collection

        /// <summary>Deletes the scratch half of a finished run: the per-write journals are superseded
        /// by the consolidated undo journal, and any leftover staging file is dead. The recovery copies
        /// stay, because "Undo last patch" needs them.</summary>
        public static void CollectStaging(IEnumerable<FileWriteRecord> writes)
        {
            foreach (var area in AreasOf(writes))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(area, "*.journal.txt")) TryDelete(f);
                    foreach (var f in Directory.GetFiles(area, "*.staged-*")) TryDelete(f);
                    if (Directory.GetFileSystemEntries(area).Length == 0) Directory.Delete(area);
                    RemoveEmptyRecoveryRoot(area);
                }
                catch { }
            }
        }

        /// <summary>Removes a run's staging areas outright. Used once a rollback has already put
        /// everything back and there is nothing left to undo.</summary>
        public static void Discard(IEnumerable<FileWriteRecord> writes)
        {
            foreach (var area in AreasOf(writes))
            {
                try
                {
                    if (Directory.Exists(area)) Directory.Delete(area, true);
                    RemoveEmptyRecoveryRoot(area);
                }
                catch { }
            }
        }

        /// <summary>Drops the .gp-recovery folder itself once its last per-write area is gone. Without
        /// this, every patch leaves one empty .gp-recovery behind per directory it touched – visible to
        /// Steam's "verify integrity of game files" and to antivirus heuristics.</summary>
        static void RemoveEmptyRecoveryRoot(string area)
        {
            try
            {
                string root = Path.GetDirectoryName(area);
                if (string.IsNullOrEmpty(root)) return;
                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
                    Directory.Delete(root);
            }
            catch { }
        }

        /// <summary>Deletes .gp-recovery roots abandoned by a run that died before it could record or
        /// collect them – the one case <see cref="CollectStaging"/> cannot reach, because the crash means
        /// no journal was ever written.
        ///
        /// Granularity is the whole root, not the individual areas inside it: coarser, but it can never
        /// delete part of a set the undo journal still needs. Deliberately bounded too – it only looks at
        /// the application's own state directory plus the roots the caller names, never recurses into the
        /// game tree, and skips any root the current undo journal refers to or that was written to within
        /// <paramref name="olderThanDays"/>, so a concurrent run is never disturbed.</summary>
        public static void SweepStale(IEnumerable<string> extraRoots, int olderThanDays)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in LoadJournal(JournalPath))
            {
                string root = string.IsNullOrEmpty(e.Area) ? "" : Path.GetDirectoryName(e.Area);
                if (!string.IsNullOrEmpty(root)) keep.Add(root);
            }

            // The application's own state directory never holds undo data – the journal lives in
            // <state>\last-patch, not in .gp-recovery – so anything found there is orphaned by
            // definition and only needs a short grace period to avoid disturbing a second instance
            // that happens to be saving its settings right now.
            SweepRoot(StateRoot, keep, DateTime.UtcNow.AddHours(-1));

            // Game folders can hold the recovery copies of the current undo, so only old roots go.
            DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, olderThanDays));
            if (extraRoots != null)
                foreach (var root in extraRoots) SweepRoot(root, keep, cutoff);
        }

        static void SweepRoot(string root, HashSet<string> keep, DateTime cutoff)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            string[] candidates;
            try { candidates = Directory.GetDirectories(root, ".gp-recovery"); }
            catch { return; }
            foreach (var dir in candidates)
            {
                if (keep.Contains(dir)) continue;
                try
                {
                    if (NewestWriteUtc(dir) > cutoff) continue;
                    Directory.Delete(dir, true);
                }
                catch { }
            }
        }

        static DateTime NewestWriteUtc(string dir)
        {
            DateTime newest = DateTime.MinValue;
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    DateTime t = File.GetLastWriteTimeUtc(file);
                    if (t > newest) newest = t;
                }
                if (newest == DateTime.MinValue) newest = Directory.GetLastWriteTimeUtc(dir);
            }
            catch { }
            return newest;
        }

        static IEnumerable<string> AreasOf(IEnumerable<FileWriteRecord> writes)
        {
            var result = new List<string>();
            if (writes == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in writes)
            {
                string area = w.Area;
                if (area.Length != 0 && seen.Add(area)) result.Add(area);
            }
            return result;
        }

        static void PruneAreas(IEnumerable<RecoveryEntry> previous)
        {
            if (previous == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in previous)
            {
                if (!e.Completed || e.Area.Length == 0 || !seen.Add(e.Area)) continue;
                try { if (Directory.Exists(e.Area)) Directory.Delete(e.Area, true); } catch { }
            }
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
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
        /// <summary>Install the overlay build and achievements.json so unlocks pop up in game.</summary>
        public bool Achievements = false;

        /// <summary>AppID actually written when online-fix mode forces Spacewar.</summary>
        public string EffectiveAppId { get { return OnlineFix ? "480" : AppIdDetector.Normalize(AppId); } }
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
        public bool Cancelled;
        public bool PartialChanges;

        /// <summary>True when a failed or cancelled run had its already-applied writes undone, so the
        /// game is back to its previous state.</summary>
        public bool RolledBack;

        public List<FileWriteRecord> Writes = new List<FileWriteRecord>();
        public SettingsOutcome Settings = new SettingsOutcome();

        /// <summary>Post-patch install checks (see <see cref="InstallCheck"/>); empty when the run failed.</summary>
        public List<InstallCheckItem> Checks = new List<InstallCheckItem>();

        public InstallCheckItem FirstFailedCheck
        {
            get { return Checks.FirstOrDefault(c => c.Status == CheckStatus.Fail); }
        }
    }

    public enum SettingsStatus { NotRequested, Copied, AlreadyPresent, Missing, Failed }

    public sealed class SettingsOutcome
    {
        public SettingsStatus Status;
        public string Directory = "";
        public string Error = "";
        public int FilesCopied;
        internal List<KeyValuePair<string, string>> Files = new List<KeyValuePair<string, string>>();
    }

    public static class SettingsScaffold
    {
        public static SettingsOutcome Plan(string source, string installDir)
        {
            var outcome = new SettingsOutcome();
            if (!Directory.Exists(source))
            {
                outcome.Status = SettingsStatus.Missing;
                outcome.Error = "Optional settings scaffold missing: " + source + ". Generated interfaces will be kept beside the installed dll.";
                return outcome;
            }
            try
            {
                outcome.Directory = Path.Combine(installDir, "steam_settings");
                outcome.Status = Directory.Exists(outcome.Directory) ? SettingsStatus.AlreadyPresent : SettingsStatus.Copied;
                Collect(source, outcome.Directory, outcome.Files);
            }
            catch (Exception ex)
            {
                outcome.Status = SettingsStatus.Failed;
                outcome.Error = "Cannot prepare settings scaffold: " + ex.Message;
            }
            return outcome;
        }

        static void Collect(string source, string destination, List<KeyValuePair<string, string>> files)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Settings scaffold contains a reparse point: " + source);
            foreach (string file in Directory.GetFiles(source).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                using (File.OpenRead(file)) { }
                files.Add(new KeyValuePair<string, string>(file, Path.Combine(destination, Path.GetFileName(file).Replace(".EXAMPLE", ""))));
            }
            foreach (string dir in Directory.GetDirectories(source).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                Collect(dir, Path.Combine(destination, Path.GetFileName(dir).Replace(".EXAMPLE", "")), files);
        }

        public static void Apply(SettingsOutcome outcome, List<FileWriteRecord> journal, CancellationToken ct = default(CancellationToken))
        {
            if (outcome.Status == SettingsStatus.Missing || outcome.Status == SettingsStatus.NotRequested) return;
            if (outcome.Status == SettingsStatus.Failed) throw new IOException(outcome.Error);
            try
            {
                foreach (var file in outcome.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    SafePersistence.Locked(file.Value, () =>
                    {
                        if (!File.Exists(file.Value))
                        {
                            SafePersistence.Copy(file.Key, file.Value, journal);
                            outcome.FilesCopied++;
                        }
                        return true;
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome.Status = SettingsStatus.Failed;
                outcome.Error = ex.Message;
                throw;
            }
        }
    }

    /// <summary>Turns the achievement schema the Steam client caches for every game it has run
    /// (Steam\appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin, binary KeyValues) into the emulator's
    /// steam_settings\achievements.json + stats.json. No web API, no login. The schema only names the icons;
    /// they come from Steam's public image CDN, best-effort.</summary>
    public static class AchievementSchema
    {
        public sealed class Result
        {
            public string AchievementsJson = "";
            public string StatsJson = "";
            public int Achievements;
            public int Stats;
            public List<string> Icons = new List<string>();
        }

        /// <summary>The cached schema for this AppID, or null when Steam never ran the game on this PC.</summary>
        public static string LocalSchemaPath(string appId)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string steam = k == null ? null : k.GetValue("SteamPath") as string;
                    if (string.IsNullOrEmpty(steam)) return null;
                    string p = Path.Combine(steam.Replace('/', '\\'), "appcache", "stats", "UserGameStatsSchema_" + appId + ".bin");
                    return File.Exists(p) ? p : null;
                }
            }
            catch { return null; }
        }

        public static Result Build(byte[] schema)
        {
            var res = new Result();
            var root = ParseKv(schema);
            // root = { "<appid>": { "stats": { "<n>": { type, name, default, bits: { "<n>": achievement } } } } }
            var app = root.Count > 0 ? root[0].Value as Kv : null;
            var stats = Get(app, "stats") as Kv;
            if (stats == null) throw new InvalidDataException("Schema has no stats section.");

            var achJson = new StringBuilder("[");
            var statJson = new StringBuilder("[");
            foreach (var stat in stats)
            {
                var s = stat.Value as Kv;
                if (s == null) continue;
                var bits = Get(s, "bits") as Kv;
                if (bits != null)
                {
                    foreach (var bit in bits)
                    {
                        var b = bit.Value as Kv;
                        string name = Get(b, "name") as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        var display = Get(b, "display") as Kv;
                        var entry = new Kv();
                        entry.Add(Pair("name", name));
                        entry.Add(Pair("displayName", Localized(Get(display, "name"))));
                        entry.Add(Pair("description", Localized(Get(display, "desc"))));
                        entry.Add(Pair("hidden", (Get(display, "hidden") as string) == "1" ? "1" : "0"));
                        foreach (var key in new[] { "icon", "icon_gray" })
                        {
                            string icon = Get(display, key) as string;
                            // the file name ends up in a path: accept plain names only
                            if (string.IsNullOrEmpty(icon) || !Regex.IsMatch(icon, @"^[A-Za-z0-9_\-]+\.(jpg|jpeg|png)$")) continue;
                            entry.Add(Pair(key, "images/" + icon));
                            if (!res.Icons.Contains(icon)) res.Icons.Add(icon);
                        }
                        var progress = Get(b, "progress") as Kv;
                        if (progress != null) entry.Add(Pair("progress", progress));
                        if (res.Achievements++ > 0) achJson.Append(',');
                        WriteJson(achJson, entry, 1);
                    }
                    continue;
                }
                string type;
                switch (Get(s, "type") as string)
                {
                    case "1": case "INT": type = "int"; break;
                    case "2": case "FLOAT": type = "float"; break;
                    case "3": case "AVGRATE": type = "avgrate"; break;
                    default: continue;
                }
                string statName = Get(s, "name") as string;
                if (string.IsNullOrEmpty(statName)) continue;
                var st = new Kv { Pair("name", statName), Pair("type", type), Pair("default", (Get(s, "default") as string) ?? "0"), Pair("global", "0") };
                if (res.Stats++ > 0) statJson.Append(',');
                WriteJson(statJson, st, 1);
            }
            res.AchievementsJson = achJson.Append("\n]\n").ToString();
            res.StatsJson = statJson.Append("\n]\n").ToString();
            return res;
        }

        public static string IconUrl(string appId, string icon)
        {
            return "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/" + appId + "/" + icon;
        }

        // ---- binary KeyValues ------------------------------------------------------------------------
        // Ordered list rather than a dictionary: the achievement order is the order the overlay lists them in.
        internal sealed class Kv : List<KeyValuePair<string, object>> { }

        static KeyValuePair<string, object> Pair(string k, object v) { return new KeyValuePair<string, object>(k, v); }

        static object Get(Kv kv, string key)
        {
            if (kv == null) return null;
            foreach (var p in kv) if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        /// <summary>{"english": "...", "german": "..."} without Valve's "token" entry; the emulator picks the
        /// game's language from it. A plain string stays a string.</summary>
        static object Localized(object v)
        {
            var kv = v as Kv;
            if (kv == null) return v as string ?? "";
            var o = new Kv();
            o.AddRange(kv.Where(p => p.Value is string && !string.Equals(p.Key, "token", StringComparison.OrdinalIgnoreCase)));
            return o;
        }

        // Valve binary KeyValues: type byte, NUL-terminated UTF-8 key, value. 0 = nested section, 8 = end.
        internal static Kv ParseKv(byte[] b)
        {
            int i = 0;
            return ReadSection(b, ref i);
        }

        static Kv ReadSection(byte[] b, ref int i)
        {
            var kv = new Kv();
            while (i < b.Length)
            {
                byte t = b[i++];
                if (t == 8 || t == 11) break;
                string key = ReadZ(b, ref i);
                object v;
                switch (t)
                {
                    case 0: v = ReadSection(b, ref i); break;
                    case 1: v = ReadZ(b, ref i); break;
                    case 2: case 4: case 6: Need(b, i, 4); v = BitConverter.ToInt32(b, i).ToString(CultureInfo.InvariantCulture); i += 4; break;
                    case 3: Need(b, i, 4); v = BitConverter.ToSingle(b, i).ToString("R", CultureInfo.InvariantCulture); i += 4; break;
                    case 7: Need(b, i, 8); v = BitConverter.ToUInt64(b, i).ToString(CultureInfo.InvariantCulture); i += 8; break;
                    case 10: Need(b, i, 8); v = BitConverter.ToInt64(b, i).ToString(CultureInfo.InvariantCulture); i += 8; break;
                    default: throw new InvalidDataException("Unknown KeyValues type " + t + " at offset " + (i - 1) + ".");
                }
                kv.Add(Pair(key, v));
            }
            return kv;
        }

        static void Need(byte[] b, int i, int n)
        {
            if (i + n > b.Length) throw new InvalidDataException("Truncated KeyValues.");
        }

        static string ReadZ(byte[] b, ref int i)
        {
            int start = i;
            while (i < b.Length && b[i] != 0) i++;
            if (i >= b.Length) throw new InvalidDataException("Truncated KeyValues.");
            return Encoding.UTF8.GetString(b, start, i++ - start);
        }

        static void WriteJson(StringBuilder sb, object v, int depth)
        {
            var kv = v as Kv;
            if (kv == null) { sb.Append('"').Append(JsonEscape((string)v)).Append('"'); return; }
            string pad = "\n" + new string(' ', depth * 2);
            sb.Append(pad).Append('{');
            for (int n = 0; n < kv.Count; n++)
            {
                sb.Append(n > 0 ? "," : "").Append(pad).Append("  \"").Append(JsonEscape(kv[n].Key)).Append("\": ");
                if (kv[n].Value is Kv) WriteJson(sb, kv[n].Value, depth + 1);
                else WriteJson(sb, kv[n].Value, 0);
            }
            sb.Append(pad).Append('}');
        }

        static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>Resolves bundled tool paths relative to this app's folder.</summary>
    public static class Tools
    {
        /// <summary>Root of the extracted payload. Everything below hangs off this, so pointing it at
        /// %LOCALAPPDATA% is what lets the app run from a read-only application directory.</summary>
        public static string BaseDir { get { return Payload.Root; } }
        public static string ApiDll86 { get { return Path.Combine(BaseDir, @"shibaberg\bin\x86\steam_api.dll"); } }
        public static string ApiDll64 { get { return Path.Combine(BaseDir, @"shibaberg\bin\x64\steam_api64.dll"); } }
        // The experimental build: same emulator plus the in-game overlay with the shiba achievement toast.
        public static string OverlayDll86 { get { return Path.Combine(BaseDir, @"shibaberg\bin\overlay\x86\steam_api.dll"); } }
        public static string OverlayDll64 { get { return Path.Combine(BaseDir, @"shibaberg\bin\overlay\x64\steam_api64.dll"); } }
        public static string SettingsExampleDir { get { return Path.Combine(BaseDir, @"shibaberg\post_build\steam_settings.EXAMPLE"); } }

        public static List<string> Missing()
        {
            var missing = new List<string>();
            if (!File.Exists(ApiDll86)) missing.Add("shibaberg\\bin\\x86\\steam_api.dll");
            if (!File.Exists(ApiDll64)) missing.Add("shibaberg\\bin\\x64\\steam_api64.dll");
            return missing;
        }
    }

    /// <summary>Embedded payload: files baked into the exe at build time (gppay.* resources + gppay.manifest)
    /// are written back beside the exe when missing or corrupt, making the binary fully self-contained.
    /// Manifest lines are `resource|relativePath|sha256`; existing files whose size matches are
    /// hash-verified so a same-size corrupted file is repaired instead of kept.</summary>
    public static class Payload
    {
        class Entry
        {
            public string Res;
            public string Rel;
            public string Hash;

            /// <summary>Size of the file as embedded *before* deflating, or -1 for a legacy manifest
            /// that predates compression and carries no length.</summary>
            public long Length = -1;

            public bool Deflated;
        }
        static List<Entry> entries;

        /// <summary>Per-file failures from the last ExtractMissing() call (empty when everything worked).</summary>
        public static List<string> LastErrors { get; private set; }

        /// <summary>True when the last pass could not use the fast path and had to hash files.</summary>
        public static bool LastPassHashed { get; private set; }

        /// <summary>Cache of a verified payload: records the manifest it was built from plus each file's
        /// size and write time. Next to the payload it describes, so it travels with the install.</summary>
        static string StampPath { get { return Path.Combine(Root, ".payload-ok"); } }

        /// <summary>Identity of the embedded payload: the manifest's own hash. Extraction is scoped by it,
        /// so a file dropped from the manifest in a newer build cannot linger from an older one.</summary>
        public static string BuildId
        {
            get
            {
                string h = ManifestHash();
                return h.Length >= 16 ? h.Substring(0, 16) : "unbuilt";
            }
        }

        static string root;

        /// <summary>Where the extracted payload lives. %LOCALAPPDATA% by preference, not the application
        /// directory: a binary advertised as "one file is all you need" cannot demand a writable
        /// application directory, and Program Files, a read-only share or an archive-mount path would
        /// otherwise make the app refuse to start at all. Deliberately Local rather than Roaming - this is
        /// tens of megabytes of cache and has no business being copied around by a roaming profile. Falls
        /// back to the application directory only if that location cannot be created.</summary>
        public static string Root
        {
            get
            {
                if (root != null) return root;
                // A binary with no embedded payload - the self-test host is built without one - has nothing
                // to extract, so it keeps looking beside itself where its tool files actually live.
                if (Count == 0) { root = AppDomain.CurrentDomain.BaseDirectory; return root; }
                try
                {
                    string preferred = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GoldbergPatcher", "payload", BuildId);
                    Directory.CreateDirectory(preferred);
                    root = preferred;
                }
                catch
                {
                    root = AppDomain.CurrentDomain.BaseDirectory;
                }
                return root;
            }
        }

        sealed class Stamp { public long Length; public long Ticks; }

        static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

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
                            var parts = line.Split('|');
                            if (parts.Length < 2 || parts[0].Length == 0) continue;
                            var e = new Entry { Res = parts[0], Rel = parts[1] };
                            if (parts.Length >= 3 && parts[2].Length > 0) e.Hash = parts[2]; // tolerate legacy hash-less manifests
                            if (parts.Length >= 4)
                            {
                                long len;
                                if (long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out len) && len >= 0)
                                    e.Length = len;
                            }
                            e.Deflated = parts.Length >= 5 && string.Equals(parts[4], "deflate", StringComparison.OrdinalIgnoreCase);
                            entries.Add(e);
                        }
                    }
                }
            }
            catch { entries = new List<Entry>(); }
        }

        /// <summary>Hash of the manifest resource itself: any change to the embedded payload changes it,
        /// which is what invalidates a stale <see cref="StampPath"/>.</summary>
        static string ManifestHash()
        {
            try
            {
                using (var s = typeof(Payload).Assembly.GetManifestResourceStream("gppay.manifest"))
                {
                    if (s == null) return "";
                    using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(s));
                }
            }
            catch { return ""; }
        }

        static Dictionary<string, Stamp> ReadStamp(string manifestHash)
        {
            var map = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
            if (manifestHash.Length == 0) return map;
            try
            {
                if (!File.Exists(StampPath)) return map;
                var lines = File.ReadAllLines(StampPath);
                if (lines.Length == 0 || lines[0] != "manifest=" + manifestHash) return map;
                for (int i = 1; i < lines.Length; i++)
                {
                    var p = lines[i].Split('|');
                    if (p.Length < 3) continue;
                    long len, ticks;
                    if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out len)) continue;
                    if (!long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    map[p[0]] = new Stamp { Length = len, Ticks = ticks };
                }
            }
            catch { map.Clear(); }
            return map;
        }

        static void WriteStamp(string manifestHash)
        {
            if (manifestHash.Length == 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("manifest=" + manifestHash);
                foreach (var e in entries)
                {
                    var fi = new FileInfo(Path.Combine(Root, e.Rel));
                    if (!fi.Exists) continue;
                    sb.AppendLine(e.Rel + "|" + fi.Length.ToString(CultureInfo.InvariantCulture)
                        + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                }
                string tmp = StampPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                File.Copy(tmp, StampPath, true);
                File.Delete(tmp);
            }
            catch { /* a cache, not a requirement: failing to write it just means hashing next launch */ }
        }

        /// <summary>Number of files embedded at build time (0 when built without payload).</summary>
        public static int Count { get { if (entries == null) Load(); return entries.Count; } }

        /// <summary>Writes every missing, size-mismatched or hash-corrupt payload file beside the exe.
        /// Returns the relative paths that were restored; failures land in LastErrors.</summary>
        public static List<string> ExtractMissing()
        {
            return ExtractMissing(false);
        }

        /// <param name="forceVerify">Ignore the stamp and hash every file, even ones that look unchanged.</param>
        public static List<string> ExtractMissing(bool forceVerify)
        {
            return SafePersistence.Locked(Path.Combine(Tools.BaseDir, "payload-extraction"), () => ExtractCore(forceVerify));
        }

        static List<string> ExtractCore(bool forceVerify)
        {
            if (entries == null) Load();
            var written = new List<string>();
            LastErrors = new List<string>();
            LastPassHashed = false;
            var asm = typeof(Payload).Assembly;
            string manifestHash = ManifestHash();
            var stamp = forceVerify ? new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase) : ReadStamp(manifestHash);
            bool stampUsable = stamp.Count > 0;

            foreach (var e in entries)
            {
                try
                {
                    string dst = Path.Combine(Root, e.Rel);
                    using (var src = asm.GetManifestResourceStream(e.Res))
                    {
                        if (src == null) { LastErrors.Add(e.Rel + ": embedded resource missing"); continue; }
                        long expected = e.Length >= 0 ? e.Length : src.Length;   // legacy manifest: resource is raw

                        // Fast path. A file whose size and write time are both exactly what the stamp
                        // recorded cannot have been rewritten since it was verified, so skip the hash.
                        Stamp known;
                        if (stampUsable && stamp.TryGetValue(e.Rel, out known))
                        {
                            var fi = new FileInfo(dst);
                            if (fi.Exists && fi.Length == known.Length && fi.Length == expected
                                && fi.LastWriteTimeUtc.Ticks == known.Ticks)
                                continue;
                        }

                        bool intact = File.Exists(dst) && new FileInfo(dst).Length == expected;
                        if (intact && e.Hash != null)
                        {
                            LastPassHashed = true;
                            intact = Sha256Matches(dst, e.Hash);
                        }
                        if (intact) continue;

                        // Repair. Inflate when the build deflated the resource, then verify the result
                        // before it is allowed to replace anything.
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        var tempPath = dst + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        try
                        {
                            using (var f = File.Create(tempPath))
                            {
                                if (e.Deflated)
                                {
                                    using (var inflate = new DeflateStream(src, CompressionMode.Decompress))
                                        inflate.CopyTo(f);
                                }
                                else src.CopyTo(f);
                            }
                            long got = new FileInfo(tempPath).Length;
                            if (e.Length >= 0 && got != e.Length)
                                throw new InvalidDataException("Restored size " + got + " does not match the manifest (" + e.Length + ").");
                            if (e.Hash != null && !Sha256Matches(tempPath, e.Hash))
                                throw new InvalidDataException("Restored payload failed its hash check.");
                            File.Copy(tempPath, dst, true);
                        }
                        finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
                    }
                    written.Add(e.Rel);
                }
                catch (Exception ex)
                {
                    LastErrors.Add(e.Rel + ": " + ex.Message);
                }
            }

            // Refresh the stamp when it was unusable (first run, or a new build) or when something was
            // restored. A clean pass against a valid stamp needs no rewrite.
            if (LastErrors.Count == 0 && (!stampUsable || written.Count > 0)) WriteStamp(manifestHash);
            if (LastErrors.Count == 0) PruneOldBuilds();
            return written;
        }

        /// <summary>Removes payload folders left behind by earlier builds. Extraction is scoped by the
        /// manifest hash, so any build that changes the payload creates a fresh ~22 MB folder and nothing
        /// else would ever delete the previous one.
        ///
        /// Only runs after a clean pass, only ever touches siblings of the current folder, and refuses to
        /// act unless the parent is literally named "payload" - the fallback path points Root at the
        /// application directory, and guessing there would mean deleting the user's own folders.</summary>
        static void PruneOldBuilds()
        {
            try
            {
                string current = Root;
                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
                if (!string.Equals(Path.GetFileName(parent), "payload", StringComparison.OrdinalIgnoreCase)) return;

                DateTime cutoff = DateTime.UtcNow.AddHours(-1);   // leave a second instance starting up alone
                foreach (var dir in Directory.GetDirectories(parent))
                {
                    if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static bool Sha256Matches(string path, string expectedHex)
        {
            try
            {
                using (var s = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return string.Equals(ToHex(sha.ComputeHash(s)), expectedHex, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; } // unreadable → treat as corrupt so the caller rewrites it
        }
    }

    /// <summary>Executes the full patch pipeline. UI-agnostic; reports via events.</summary>
    /// <summary>Preserves <paramref name="source"/> in goldberg_backup. <paramref name="knownSourceHash"/>
    /// spares a re-read when the caller already hashed the source; <paramref name="backupHash"/> returns the
    /// backup's hash for the same reason. Returns null when backups are off or the file is not eligible.</summary>
    public delegate string BackupTaker(string source, string knownSourceHash, out string backupHash);

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
                if (!o.OnlineFix && !AppIdDetector.IsValid(o.AppId))
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
                foreach (var target in new[] { exePath, UnrealCompanion(exePath) }.Where(t => t != null))
                    foreach (var protection in ProtectionScan.Detect(target))
                        Log(LogLevel.Warn, protection + " detected on " + Path.GetFileName(target)
                            + " – the emulator replaces Steam's API but cannot remove this protection; the game may refuse to start offline.");

                // ---- locate steam api install dir ------------------------------
                Pct(8);
                string searchRoot = SearchRoot(exePath);
                if (!string.Equals(searchRoot, gameDir, StringComparison.OrdinalIgnoreCase))
                    Log(LogLevel.Dim, "Unreal project layout – searching for Steamworks from the game root: " + searchRoot);
                var foundApi = FindSteamApiFiles(searchRoot, ct);
                string installDir = gameDir;
                // Prefer the name the loader will actually ask for; the architecture only breaks ties and
                // covers executables that load the library dynamically.
                string importedApi = ImportedSteamApiName(exePath);
                string preferredName = importedApi ?? (pe.Arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll");
                if (importedApi != null)
                    Log(LogLevel.Dim, "Imports " + importedApi + " – that is the name the emulator will be installed under.");

                if (foundApi.Count > 0)
                {
                    var best = PickApiTarget(foundApi, searchRoot, preferredName);
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
                var settingsPlan = (o.CreateSettings || o.OnlineFix)
                    ? SettingsScaffold.Plan(Tools.SettingsExampleDir, installDir) : new SettingsOutcome();
                res.Settings = settingsPlan;
                if (settingsPlan.Status == SettingsStatus.Failed) throw new IOException(settingsPlan.Error);
                if (settingsPlan.Status == SettingsStatus.Missing) Log(LogLevel.Warn, settingsPlan.Error);

                // ---- backup dir ----------------------------------------------
                string backupDir = Path.Combine(installDir, "goldberg_backup");
                bool anyBackup = false;
                BackupTaker backup = (string src, string known, out string backupHash) =>
                {
                    backupHash = null;
                    if (!o.Backup) return null;
                    var dst = OriginalBackups.Preserve(backupDir, src, Log, known, out backupHash);
                    if (dst != null) anyBackup = true;
                    return dst;
                };
                res.BackupDir = o.Backup ? backupDir : "";

                // ---- unpack DRM (Shibaless) ------------------------------------
                string finalExe = exePath;
                if (o.UnpackDrm)
                {
                    Pct(12);
                    finalExe = TryUnpack(exePath, backup, res, ct);
                    string companion = UnrealCompanion(exePath);
                    if (companion != null)
                    {
                        Pct(30);
                        Log(LogLevel.Info, "Unreal launcher – also checking the game exe it starts: " + ShortRel(gameDir, companion));
                        TryUnpack(companion, backup, res, ct);
                    }
                    Pct(48);
                }
                else Log(LogLevel.Dim, "Shibaless auto-unpack disabled – skipping.");
                res.FinalExe = finalExe;
                ct.ThrowIfCancellationRequested();

                // ---- re-analyze after unpack ------------------------------------
                // Shibaless rewrote the exe in place. The packed file's machine type was used above to pick the
                // install target, but the dll that actually gets loaded must match what will run. Unpackers preserve
                // architecture in practice; if the new file disagrees we trust it and warn.
                if (res.Unpacked)
                {
                    try
                    {
                        var pe2 = PeReader.Analyze(finalExe);
                        if (pe2.Arch != ExeArch.Unknown && pe2.Arch != pe.Arch)
                            Log(LogLevel.Warn, "Unpacked exe reports a different architecture (" + pe2.MachineText
                                + ") than the packed one – using it for dll selection.");
                        if (pe2.Arch != ExeArch.Unknown)
                        {
                            pe = pe2;
                            // The import table does not change when Shibaless rewrites the exe, so the
                            // imported name stands. Only the architecture-derived fallback is recomputed.
                            if (importedApi == null)
                                preferredName = pe.Arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll";
                        }
                    }
                    catch { /* keep the pre-unpack analysis */ }
                }

                // ---- interfaces from ORIGINAL dll -------------------------------
                string interfacesTxt = null;
                if (o.GenerateInterfaces && !o.OnlineFix)
                {
                    Pct(52);
                    interfacesTxt = TryGenerateInterfaces(foundApi, searchRoot, preferredName, ct);
                }

                // ---- dlls --------------------------------------------------------
                Pct(62);
                if (o.OnlineFix)
                    PrepareOnlineFixMode(installDir, res);
                else
                    InstallGoldbergDlls(installDir, pe.Arch, foundApi, backup, res, o.Achievements);

                // ---- steam_appid.txt ---------------------------------------------
                Pct(82);
                if (o.WriteAppIdTxt || o.OnlineFix)
                {
                    string appId = o.EffectiveAppId;
                    string txt = appId + Environment.NewLine;
                    WriteIfChanged(Path.Combine(installDir, "steam_appid.txt"), txt, res);
                    res.ReplacedFiles.Add(ShortRel(gameDir, Path.Combine(installDir, "steam_appid.txt")));
                    var besideExe = Path.Combine(Path.GetDirectoryName(finalExe), "steam_appid.txt");
                    if (!string.Equals(besideExe, Path.Combine(installDir, "steam_appid.txt"), StringComparison.OrdinalIgnoreCase))
                    {
                        WriteIfChanged(besideExe, txt, res);
                        res.ReplacedFiles.Add(ShortRel(gameDir, besideExe));
                    }
                    Log(LogLevel.Ok, "steam_appid.txt → " + appId +
                        (o.OnlineFix ? "  (Spacewar / generic online-fix)" : ""));
                }

                // ---- steam_settings ----------------------------------------------
                Pct(90);
                if (o.CreateSettings || o.OnlineFix)   // online-fix always ships the scaffold
                {
                    var outcome = settingsPlan;
                    SettingsScaffold.Apply(outcome, res.Writes, ct);
                    res.SettingsDir = outcome.Directory;
                    if (interfacesTxt != null && outcome.Status != SettingsStatus.Failed && outcome.Directory.Length > 0)
                    {
                        WriteIfChanged(Path.Combine(outcome.Directory, "steam_interfaces.txt"), interfacesTxt, res);
                        Log(LogLevel.Ok, "steam_settings\\steam_interfaces.txt written.");
                    }
                    else if (interfacesTxt != null && outcome.Status == SettingsStatus.Missing)
                    {
                        var legacy = Path.Combine(installDir, "steam_interfaces.txt");
                        WriteIfChanged(legacy, interfacesTxt, res);
                        Log(LogLevel.Warn, "steam_settings.EXAMPLE missing – steam_interfaces.txt written to the install folder instead.");
                    }
                }
                else if (interfacesTxt != null)
                {
                    // keep it somewhere useful even without a settings folder
                    var legacy = Path.Combine(installDir, "steam_interfaces.txt");
                    WriteIfChanged(legacy, interfacesTxt, res);
                    Log(LogLevel.Ok, "steam_interfaces.txt written (move into steam_settings later if you create one).");
                }

                // ---- achievements ------------------------------------------------------
                if (o.Achievements && !o.OnlineFix)
                {
                    Pct(93);
                    InstallAchievements(installDir, o.EffectiveAppId, res, ct);
                }

                // ---- verify the install as a whole ---------------------------------
                Pct(96);
                Log(LogLevel.Dim, "Checking the finished install…");
                res.Checks = InstallCheck.Run(finalExe, o.EffectiveAppId, o.OnlineFix, ct);
                foreach (var c in res.Checks)
                    Log(c.Status == CheckStatus.Pass ? LogLevel.Ok : c.Status == CheckStatus.Warn ? LogLevel.Warn : LogLevel.Error,
                        (c.Status == CheckStatus.Pass ? "✔ " : c.Status == CheckStatus.Warn ? "! " : "✖ ") + c.Title + (c.Detail.Length > 0 ? " – " + c.Detail : ""));

                // ---- done ---------------------------------------------------------
                Pct(100);
                res.Success = true;
                res.Summary = o.OnlineFix
                    ? string.Format("Online-fix ready (Spacewar · 480). Start Steam, then launch {0} – it will show up as playing Spacewar.", Path.GetFileName(finalExe))
                    : string.Format("Patched with AppID {0}. Goldberg dll installed to: {1}",
                        o.EffectiveAppId, ShortRel(gameDir, Path.Combine(installDir, preferredName)));
                var failedCheck = res.FirstFailedCheck;
                if (failedCheck != null)
                    res.Summary += " – but the install check failed: " + failedCheck.Title + (failedCheck.Detail.Length > 0 ? " (" + failedCheck.Detail + ")" : "") + ".";
                Log(failedCheck == null ? LogLevel.Ok : LogLevel.Warn, res.Summary);
                if (o.OnlineFix)
                    Log(LogLevel.Dim, "Multiplayer traffic is routed through Steam's own servers under Spacewar's AppID.");
                if (anyBackup && res.BackupDir != "") Log(LogLevel.Dim, "Originals backed up in: " + ShortRel(gameDir, res.BackupDir));
                Log(failedCheck == null ? LogLevel.Ok : LogLevel.Warn, failedCheck == null
                    ? "✔ Done! Launch the game to test."
                    : "Done, with a failed check – fix the item above or the game may not start offline.");
            }
            catch (OperationCanceledException)
            {
                res.Success = false;
                res.Cancelled = true;
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
            res.PartialChanges = !res.Success && res.Writes.Any(w => w.Completed);

            // ---- undo a half-applied patch -------------------------------------
            // A run that dies between two writes can leave a game that no longer launches. The journal
            // already holds everything needed to put it back, so use it instead of telling the user to
            // sort the recovery folders out by hand.
            if (res.PartialChanges && o.Backup)
            {
                Log(LogLevel.Warn, "Patch did not finish – undoing the "
                    + res.Writes.Count(w => w.Completed) + " change(s) that were already applied…");
                var undo = Recovery.RollbackWrites(res.Writes, Log);
                if (undo.Failed == 0)
                {
                    res.PartialChanges = false;
                    res.RolledBack = true;
                    res.Summary += " Already-applied changes were rolled back: " + undo.Summary;
                    Log(LogLevel.Ok, "Undo complete – " + undo.Summary + " The game is back to its previous state.");
                    Recovery.Discard(res.Writes);
                    // Deliberately no ClearLastPatch here: the journal on disk still describes the
                    // previous *successful* patch, whose recovery copies this run never touched.
                }
                else
                {
                    res.Summary += " Undo incomplete: " + undo.Summary;
                    Log(LogLevel.Error, "Undo incomplete – " + undo.Summary
                        + " The files that could not be reverted are listed below.");
                    foreach (var m in undo.Messages) Log(LogLevel.Warn, "   " + m);
                    Recovery.SaveJournal(res.Writes, false);   // keep the record so undo can be retried
                }
            }
            else if (!res.Success && res.Writes.Count != 0)
            {
                res.Summary += res.PartialChanges
                    ? " Partial changes remain (backups are off, so nothing was rolled back)."
                    : " No destination writes completed.";
            }

            // ---- keep an undo record for the last good patch --------------------
            if (res.Success && res.Writes.Count != 0)
            {
                Recovery.SaveJournal(res.Writes, true);
                Recovery.CollectStaging(res.Writes);
                Log(LogLevel.Dim, "Undo record saved – 'Undo last patch' can revert this run.");
            }
            return res;
        }

        // ------------------------------------------------------------------ steps

        private string TryUnpack(string exePath, BackupTaker backup, PatchResult res, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Every SteamStub variant lives in a .bind section. Reading just the headers settles the common
            // DRM-free case without loading the whole exe or hashing it.
            bool? hasStub = ShibalessUnpacker.HasStubSection(exePath);
            if (hasStub == false)
            {
                Log(LogLevel.Info, "No SteamStub section (.bind) – no Steam DRM to remove.");
                return exePath;
            }

            var before = new FileInfo(exePath);
            long stampLength = before.Length; DateTime stampTime = before.LastWriteTimeUtc;
            UnpackResult native;
            Log(LogLevel.Info, "SteamStub section found – unpacking with Shibaless…");
            try { native = ShibalessUnpacker.UnpackToMemory(exePath, line => Log(LogLevel.Dim, "   " + line)); }
            catch (Exception ex) { native = null; Log(LogLevel.Warn, "Shibaless unpack crashed: " + ex.Message); }

            if (native != null && native.Success)
            {
                string backupHash;
                string nativeBackup = backup(exePath, native.SourceSha256, out backupHash);
                byte[] output = native.Output;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    SafePersistence.Write(exePath, stream => stream.Write(output, 0, output.Length), staged =>
                    {
                        // Prove the exe on disk is still the one that was unpacked. Size and timestamp settle
                        // it for free; only if either moved is the file hashed.
                        var now = new FileInfo(exePath);
                        if ((now.Length != stampLength || now.LastWriteTimeUtc != stampTime)
                            && SafePersistence.Hash(exePath) != native.SourceSha256)
                            throw new IOException("Executable changed during processing: " + exePath);
                        if (PeReader.Analyze(staged).Arch == ExeArch.Unknown)
                            throw new InvalidDataException("Unsupported unpacked executable architecture.");
                    }, res.Writes, null, nativeBackup, null, nativeBackup != null ? backupHash : native.SourceSha256);
                }
                catch (IOException ex)
                {
                    throw new Exception("Could not replace the packed exe (is the game still running?).\n" + ex.Message);
                }
                res.Unpacked = true;
                Log(LogLevel.Ok, "DRM removed! (" + native.Unpacker + ")");
                if (nativeBackup != null) Log(LogLevel.Dim, "Original packed exe backed up.");
                return exePath;
            }

            // Nothing replaced the exe; the post-patch check reports the .bind that is still there.
            if (native != null && native.ErrorCode == UnpackErrorCode.UnsupportedVariant)
                Log(LogLevel.Warn, "The exe has a .bind section, but no Shibaless unpacker recognised the SteamStub variant – continuing with the original exe.");
            else if (native != null)
                Log(LogLevel.Warn, "Shibaless could not unpack the exe (" + native.Error + ") – keeping the original exe untouched.");
            return exePath;
        }

        private string TryGenerateInterfaces(List<string> foundApi, string gameDir, string preferredName, CancellationToken ct)
        {
            if (foundApi.Count == 0) return null;
            string target = PickApiTarget(foundApi, gameDir, preferredName);

            // On a re-patch the live dll is the emulator installed last time. Dumping that would replace a
            // correct steam_interfaces.txt with every interface the emulator knows about, so read the
            // preserved original instead - or skip, leaving any earlier dump in place.
            if (InstallCheck.IsEmulatorDll(target))
            {
                string original = null;
                try
                {
                    string candidate = OriginalBackups.Find(
                        Path.Combine(Path.GetDirectoryName(target), "goldberg_backup"), target);
                    if (File.Exists(candidate) && !InstallCheck.IsEmulatorDll(candidate)) original = candidate;
                }
                catch { }
                if (original == null)
                {
                    Log(LogLevel.Dim, Path.GetFileName(target) + " is already the Goldberg emulator and no original backup was found – keeping the existing interface list.");
                    return null;
                }
                Log(LogLevel.Dim, Path.GetFileName(target) + " is already patched – reading interfaces from the backed-up original.");
                target = original;
            }

            ct.ThrowIfCancellationRequested();
            List<string> lines;
            try { lines = InterfaceScanner.Scan(target); }
            catch (Exception ex)
            {
                Log(LogLevel.Dim, "Interface dump failed: " + ex.Message);
                return null;
            }
            if (lines.Count == 0)
            {
                Log(LogLevel.Warn, "No Steam interface versions found in " + Path.GetFileName(target) + " – it may not be a Steamworks library.");
                return null;
            }
            Log(LogLevel.Ok, "Generated steam_interfaces.txt from the original dll (" + lines.Count + " interfaces).");
            return string.Join("\r\n", lines.ToArray()) + "\r\n";
        }

        /// <summary>Generic online-fix mode: the game's ORIGINAL steam_api dll must stay in place so that,
        /// with steam_appid.txt = 480, the process attaches to the running Steam client as Spacewar and
        /// matchmaking/networking is routed through Valve's servers.
        /// The live dll is NEVER modified unless it is provably one of our bundled Goldberg emulator
        /// builds (byte-identical) – in that case the original from goldberg_backup must be restored,
        /// because the emulator cannot attach to a real Steam client. Any other dll (original or an
        /// updated Steamworks version) is left exactly as-is; only steam_appid.txt is written.</summary>
        private void PrepareOnlineFixMode(string installDir, PatchResult res)
        {
            string backupDir = Path.Combine(installDir, "goldberg_backup");

            bool anyApi = false;
            foreach (var n in new[] { "steam_api.dll", "steam_api64.dll" })
            {
                string cur = Path.Combine(installDir, n);
                if (!File.Exists(cur)) continue;
                anyApi = true;

                if (LooksLikeBundledGoldberg(cur))
                {
                    // live dll is a Goldberg emulator build – online-fix cannot work with it in place
                    OriginalBackups.Restore(backupDir, cur, res.Writes, Log);
                    Log(LogLevel.Ok, "Detected Goldberg emulator dll – restored original " + n + " from goldberg_backup\\ (required for online-fix).");
                    res.ReplacedFiles.Add(n);
                }
                else
                {
                    // not one of our builds: leave it alone, whatever it is
                    string bak = Path.Combine(backupDir, n);
                    if (File.Exists(bak) && !FilesEqual(bak, cur))
                        Log(LogLevel.Warn, "goldberg_backup\\" + n + " differs from the live dll and the live dll is not a known Goldberg build – leaving it in place.");
                    Log(LogLevel.Dim, "Original " + n + " kept in place – required for Steam detection & server routing.");
                }
            }
            if (!anyApi)
                Log(LogLevel.Warn, "No steam_api dll found in the install folder – if this game uses Steamworks, double-check the chosen exe.");
        }

        internal static bool LooksLikeBundledGoldberg(string dllPath)
        {
            foreach (var src in new[] { Tools.ApiDll86, Tools.ApiDll64, Tools.OverlayDll86, Tools.OverlayDll64 })
            {
                try
                {
                    if (!File.Exists(src)) continue;
                    if (!FilesEqual(dllPath, src)) continue;
                    return true;
                }
                catch { }
            }
            // Dlls earlier patcher releases installed, so games patched by them are still recognised after
            // the bundled build changes. Only add hashes of dlls we actually shipped.
            try
            {
                long len = new FileInfo(dllPath).Length;
                if (len == 9189288 || len == 11429288)
                    return PreviousBundledDlls.Contains(SafePersistence.Hash(dllPath));
            }
            catch { }
            return false;
        }

        static readonly HashSet<string> PreviousBundledDlls = new HashSet<string>
        {
            // gbe_fork release-2026_07_19, emu-win-release.7z regular\ (shipped before Shibaberg)
            "8e804d38cde295b1f08a6c41ecc8978fde69e53e2a6e22652017371a49cba1be", // x86\steam_api.dll
            "8b1bd0bea955aaeccd6de92d2a8c6208757dde038fd3c074113f0c2609e04de1", // x64\steam_api64.dll
        };

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
                    while (true)
                    {
                        // Stream.Read may legally return fewer bytes than asked for, so fill each buffer
                        // completely before comparing; comparing raw read counts could report two identical
                        // files as different.
                        int r1 = ReadFull(s1, b1);
                        int r2 = ReadFull(s2, b2);
                        if (r1 != r2) return false;
                        if (r1 == 0) return true;
                        for (int i = 0; i < r1; i++) if (b1[i] != b2[i]) return false;
                    }
                }
            }
            catch { return false; }
        }

        static int ReadFull(Stream s, byte[] buffer)
        {
            int total = 0, n;
            while (total < buffer.Length && (n = s.Read(buffer, total, buffer.Length - total)) > 0) total += n;
            return total;
        }

        private void InstallGoldbergDlls(string installDir, ExeArch arch, List<string> foundApi, BackupTaker backup, PatchResult res, bool overlay)
        {
            // The *name* comes from the import table; the *architecture* comes from the PE header. Deriving
            // the name from the architecture is the worst failure mode in the app: a correctly-architected
            // dll written under a name the loader never asks for, reported as success, game still broken.
            string imported = ImportedSteamApiName(res.FinalExe);
            string prefName = imported ?? (arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll");
            string otherName = prefName == "steam_api64.dll" ? "steam_api.dll" : "steam_api64.dll";
            if (imported == null)
                Log(LogLevel.Warn, "This executable does not import a Steamworks dll directly, so " + prefName
                    + " is a guess from its architecture. If the game still fails to start, check which name it loads at runtime.");

            // The second name is only replaced when it already sits in the install folder itself. Matching it
            // anywhere in the tree used to create a brand-new, wrongly named emulator dll beside the first
            // one whenever some unrelated subfolder (a 32-bit tool, a redistributable) shipped the other name.
            var wanted = new List<KeyValuePair<string, ExeArch>> { new KeyValuePair<string, ExeArch>(prefName, arch) };
            string otherPath = Path.Combine(installDir, otherName);
            if (File.Exists(otherPath))
            {
                // Games that ship both libraries side by side (a 32- and a 64-bit binary sharing one folder)
                // need each replaced by the build of its OWN architecture; overwriting the 32-bit library
                // with the 64-bit emulator breaks the other binary. Unreadable or already-patched files fall
                // back to the target's architecture.
                ExeArch otherArch = arch;
                try
                {
                    string probe = otherPath;
                    if (LooksLikeBundledGoldberg(probe))
                    {
                        string original = OriginalBackups.Find(Path.Combine(installDir, "goldberg_backup"), probe);
                        // No original: the emulator dll's own header still names its architecture.
                        probe = File.Exists(original) ? original : otherPath;
                    }
                    if (probe != null)
                    {
                        var probed = PeReader.Analyze(probe).Arch;
                        if (probed != ExeArch.Unknown) otherArch = probed;
                    }
                }
                catch { }
                wanted.Add(new KeyValuePair<string, ExeArch>(otherName, otherArch));
            }

            int installed = 0;
            foreach (var entry in wanted)
            {
                string dllName = entry.Key;
                ExeArch dllArch = entry.Value;
                // Chosen by architecture, never by the destination name: a 64-bit game can legitimately
                // import steam_api.dll, and installing the 32-bit library there would be silent breakage.
                string src = overlay
                    ? (dllArch == ExeArch.X64 ? Tools.OverlayDll64 : Tools.OverlayDll86)
                    : (dllArch == ExeArch.X64 ? Tools.ApiDll64 : Tools.ApiDll86);
                if (!File.Exists(src)) { Log(LogLevel.Error, "Missing bundled emulator dll: " + src); continue; }
                string dst = Path.Combine(installDir, dllName);
                if (File.Exists(dst))
                {
                    string ignored;
                    if (backup(dst, null, out ignored) != null) Log(LogLevel.Dim, "Preserved original backup for " + dllName);
                }
                SafePersistence.Copy(src, dst, res.Writes, staged =>
                {
                    var pe = PeReader.Analyze(staged);
                    if (pe.Arch == ExeArch.Unknown)
                        throw new InvalidDataException("Unsupported emulator dll architecture.");
                    if (pe.Arch != dllArch)
                        throw new InvalidDataException("Emulator dll is " + pe.MachineText
                            + " but " + dllName + " needs " + (dllArch == ExeArch.X64 ? "x64" : "x86") + ".");
                });
                res.ReplacedFiles.Add(dllName);
                installed++;
                Log(LogLevel.Ok, "Installed Goldberg → " + dllName + "  (" + (dllArch == ExeArch.X64 ? "x64" : "x86") + (overlay ? ", with in-game overlay" : "") + ")");
            }

            if (installed == 0)
                throw new Exception("No steam_api dll could be installed – the bundled emulator dll(s) are missing from this patcher's folder.\n"
                    + "The self-contained restore may have failed; check the log above and re-run, or reinstall the patcher.");

            // Confirm the name the loader will ask for now resolves on disk. Nothing else in the pipeline
            // checks this, and a mismatch stays invisible until the game is launched.
            if (imported != null && !File.Exists(Path.Combine(installDir, imported)))
                throw new Exception("The emulator was installed, but the executable imports " + imported
                    + " and no such file exists in " + installDir + " – the game would still fail to start.");
        }

        /// <summary>Turns the overlay on and gives the emulator the game's achievement list, from the schema the
        /// local Steam client cached. Existing achievements.json / stats.json are kept: they may be hand-made.</summary>
        private void InstallAchievements(string installDir, string appId, PatchResult res, CancellationToken ct)
        {
            string dir = Path.Combine(installDir, "steam_settings");
            string ini = Path.Combine(dir, "configs.overlay.ini");
            // Flip the switch in an existing config so the user's other overlay settings survive.
            string text = File.Exists(ini) ? File.ReadAllText(ini) : "";
            var sw = new Regex(@"(?m)^([ \t]*enable_experimental_overlay[ \t]*=)[^\r\n]*");
            text = sw.IsMatch(text) ? sw.Replace(text, "${1}1") : "[overlay::general]\r\nenable_experimental_overlay=1\r\n" + text;
            WriteIfChanged(ini, text, res);
            Log(LogLevel.Ok, "In-game overlay enabled (steam_settings\\configs.overlay.ini).");

            // The overlay's Cloud saves panel runs this exe (--cloud-*) for Google sign-in and syncing.
            var entry = System.Reflection.Assembly.GetEntryAssembly();
            if (entry != null && entry.Location.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                WriteIfChanged(Path.Combine(dir, "shibaberg_cloud.txt"), entry.Location + "\r\n", res);

            string achPath = Path.Combine(dir, "achievements.json");
            if (File.Exists(achPath))
            {
                Log(LogLevel.Dim, "steam_settings\\achievements.json already exists – kept as is.");
                return;
            }
            string schemaPath = AchievementSchema.LocalSchemaPath(appId);
            if (schemaPath == null)
            {
                Log(LogLevel.Warn, "No achievement data for AppID " + appId + " on this PC – Steam only caches it for games it has launched. "
                    + "Launch the game once from Steam, then patch again; until then nothing will pop up.");
                return;
            }
            AchievementSchema.Result schema;
            try { schema = AchievementSchema.Build(File.ReadAllBytes(schemaPath)); }
            catch (Exception ex)
            {
                Log(LogLevel.Warn, "Could not read Steam's achievement cache (" + ex.Message + ") – no achievements installed.");
                return;
            }
            if (schema.Achievements == 0)
            {
                Log(LogLevel.Dim, "This game has no achievements.");
                return;
            }
            WriteIfChanged(achPath, schema.AchievementsJson, res);
            string statsPath = Path.Combine(dir, "stats.json");
            if (schema.Stats > 0 && !File.Exists(statsPath)) WriteIfChanged(statsPath, schema.StatsJson, res);
            Log(LogLevel.Ok, string.Format("{0} achievements and {1} stats from Steam's local cache → steam_settings.", schema.Achievements, schema.Stats));

            // Icons: only the overlay's achievement list (Shift+Tab) shows them, so a failed download costs nothing.
            string imgDir = Path.Combine(dir, "images");
            string tmp = Path.Combine(Path.GetTempPath(), "shibaberg_icons_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var todo = schema.Icons.Where(n => !File.Exists(Path.Combine(imgDir, n))).ToList();
                var got = new System.Collections.Concurrent.ConcurrentBag<string>();
                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, icon =>
                {
                    try
                    {
                        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                        var req = (HttpWebRequest)WebRequest.Create(AchievementSchema.IconUrl(appId, icon));
                        req.Timeout = req.ReadWriteTimeout = 8000;
                        req.UserAgent = "Shibaberg/" + BuildInfo.Version;
                        using (ct.Register(() => req.Abort()))
                        using (var resp = req.GetResponse())
                        using (var f = File.Create(Path.Combine(tmp, icon)))
                            resp.GetResponseStream().CopyTo(f);
                        got.Add(icon);
                    }
                    catch { }
                });
                foreach (var icon in got)
                    SafePersistence.Copy(Path.Combine(tmp, icon), Path.Combine(imgDir, icon), res.Writes);
                if (todo.Count > 0)
                    Log(got.Count == todo.Count ? LogLevel.Ok : LogLevel.Warn,
                        string.Format("Achievement icons: {0}/{1} downloaded{2}.", got.Count, todo.Count,
                            got.Count == todo.Count ? "" : " (offline? the popups work without them)"));
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        /// <summary>True for the two names Steamworks libraries are shipped under.</summary>
        public static bool IsSteamApiName(string name)
        {
            return string.Equals(name, "steam_api.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "steam_api64.dll", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The Steamworks name the loader will actually ask for, taken from the target's import
        /// table. Null when the executable imports neither name — it may load the library dynamically, in
        /// which case only the architecture can guide the choice.</summary>
        public static string ImportedSteamApiName(string exePath)
        {
            try
            {
                foreach (var name in PeReader.ImportedDlls(exePath))
                    if (IsSteamApiName(name)) return name;
            }
            catch { }
            return null;
        }

        /// <summary>Ranks candidates: nearest to the exe first; arch-matching name breaks ties.
        /// A dll sitting right next to the exe always wins over deep copies.</summary>
        /// <summary>Where to look for the game's Steamworks libraries. Normally the exe's own folder, but an
        /// Unreal game's real exe sits in &lt;Project&gt;\Binaries\Win64 while the library lives under the game
        /// root's Engine\Binaries\ThirdParty - searching down from the exe never finds it, and the emulator
        /// would be dropped beside an exe that never loads it. Climbs out of Binaries\Win64|Win32 to the
        /// folder holding Engine\, and only that far.</summary>
        public static string SearchRoot(string exePath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            try
            {
                var platform = new DirectoryInfo(dir);
                if (!IsBinariesPlatform(platform)) return dir;
                var project = platform.Parent.Parent;          // <Project>
                var gameRoot = project == null ? null : project.Parent;
                if (gameRoot != null && Directory.Exists(Path.Combine(gameRoot.FullName, "Engine"))) return gameRoot.FullName;
                return project != null ? project.FullName : dir;
            }
            catch { return dir; }
        }

        static bool IsBinariesPlatform(DirectoryInfo d)
        {
            return d != null && d.Parent != null
                && (string.Equals(d.Name, "Win64", StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name, "Win32", StringComparison.OrdinalIgnoreCase))
                && string.Equals(d.Parent.Name, "Binaries", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>For an Unreal launcher exe in the game root, the -Shipping exe it starts: that is the binary
        /// SteamStub is usually applied to, so unpacking only the launcher can leave the DRM in place. Looks
        /// exactly one project folder deep (&lt;root&gt;\&lt;Project&gt;\Binaries\Win64|Win32). Null when there is
        /// none, or more than one and none matches the launcher's name.</summary>
        public static string UnrealCompanion(string exePath)
        {
            try
            {
                string full = Path.GetFullPath(exePath);
                string dir = Path.GetDirectoryName(full);
                if (IsBinariesPlatform(new DirectoryInfo(dir))) return null;           // already the real exe
                var hits = new List<string>();
                foreach (var project in Directory.GetDirectories(dir))
                    foreach (var platform in new[] { "Win64", "Win32" })
                    {
                        string bin = Path.Combine(project, "Binaries", platform);
                        if (Directory.Exists(bin)) hits.AddRange(Directory.GetFiles(bin, "*-Shipping.exe"));
                    }
                if (hits.Count == 1) return hits[0];
                string stem = Path.GetFileNameWithoutExtension(full);
                var named = hits.Where(h => Path.GetFileName(h).StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase)).ToList();
                return named.Count == 1 ? named[0] : null;
            }
            catch { return null; }
        }

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
        internal static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "goldberg_backup", "$recycle.bin", "system volume information", "__macosx",
            "_commonredist", "redist", "_redist", "__redist", "directx", "dxsetup", "vcredist",
            "physx", "dotnet", ".git", ".svn", "installers", "__installer",
        };

        /// <summary>Recursively scans the whole game folder (junction-safe, junk-pruned) for steam_api dlls.
        /// Results are ordered nearest-to-exe first.</summary>
        public static List<string> FindSteamApiFiles(string startDir, CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
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
                    ct.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException) { throw; }
            catch { }
            ct.ThrowIfCancellationRequested();
            // No ordering here: PickApiTarget recomputes depth properly, and sorting by string length was a
            // proxy that could disagree with it for equal-length paths at different depths.
            return results;
        }

        /// <summary>Reads a steam_appid.txt from the first of the given directories that has one. Static
        /// because it uses no instance state - two call sites used to construct a throwaway PatchRunner
        /// just to reach it.</summary>
        public static string FindExistingAppId(params string[] dirs)
        {
            foreach (var d in dirs.Where(d => !string.IsNullOrEmpty(d)))
            {
                if (d == null || !Directory.Exists(d)) continue;
                var f = Path.Combine(d, "steam_appid.txt");
                if (File.Exists(f))
                {
                    try
                    {
                        if (new FileInfo(f).Length > 128) continue;
                        var id = AppIdDetector.Normalize(File.ReadAllText(f));
                        if (id.Length != 0) return id;
                    }
                    catch { }
                }
            }
            return "";
        }

        static void WriteIfChanged(string path, string content, PatchResult res)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            SafePersistence.WriteText(path, content, res.Writes);
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

    // --------------------------------------------------------------------- batch patching

    /// <summary>Result of resolving one game's Steam AppID from local sources (and optionally the online store).</summary>
    /// <summary>Managed replacement for GSE's generate_interfaces tool: lists the Steam interface version
    /// strings an ORIGINAL steam_api dll was built against, which is what steam_interfaces.txt tells the
    /// emulator. Same patterns, same order and the same SteamClient017 rule as the upstream tool
    /// (tools/generate_interfaces/generate_interfaces.cpp in gbe_fork), so no external process, no
    /// architecture-matched helper exe and no temp copy of the dll are needed.</summary>
    public static class InterfaceScanner
    {
        static readonly string[] Patterns =
        {
            @"STEAMAPPS_INTERFACE_VERSION\d+", @"SteamApps\d+", @"STEAMAPPLIST_INTERFACE_VERSION\d+",
            @"STEAMAPPTICKET_INTERFACE_VERSION\d+", @"SteamClient\d+", @"STEAMCONTROLLER_INTERFACE_VERSION",
            @"SteamController\d+", @"SteamFriends\d+", @"SteamGameServerStats\d+", @"SteamGameCoordinator\d+",
            @"SteamGameServer\d+", @"STEAMHTMLSURFACE_INTERFACE_VERSION_\d+", @"STEAMHTTP_INTERFACE_VERSION\d+",
            @"SteamInput\d+", @"STEAMINVENTORY_INTERFACE_V\d+", @"SteamMatchMakingServers\d+",
            @"SteamMatchMaking\d+", @"SteamMatchGameSearch\d+", @"SteamParties\d+",
            @"STEAMMUSIC_INTERFACE_VERSION\d+", @"STEAMMUSICREMOTE_INTERFACE_VERSION\d+",
            @"SteamNetworkingMessages\d+", @"SteamNetworkingSockets\d+", @"SteamNetworkingUtils\d+",
            @"SteamNetworking\d+", @"STEAMPARENTALSETTINGS_INTERFACE_VERSION\d+",
            @"STEAMREMOTEPLAY_INTERFACE_VERSION\d+", @"STEAMREMOTESTORAGE_INTERFACE_VERSION\d+",
            @"STEAMSCREENSHOTS_INTERFACE_VERSION\d+", @"STEAMTIMELINE_INTERFACE_V\d+",
            @"STEAMUGC_INTERFACE_VERSION\d+", @"SteamUser\d+", @"STEAMUSERSTATS_INTERFACE_VERSION\d+",
            @"SteamUtils\d+", @"STEAMVIDEO_INTERFACE_V\d+", @"STEAMUNIFIEDMESSAGES_INTERFACE_VERSION\d+",
            @"SteamMasterServerUpdater\d+",
        };

        static readonly System.Text.RegularExpressions.Regex[] Compiled =
            Patterns.Select(x => new System.Text.RegularExpressions.Regex(x, System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToArray();

        /// <summary>The lines of steam_interfaces.txt for <paramref name="dllPath"/>, in upstream order.</summary>
        public static List<string> Scan(string dllPath)
        {
            // Latin-1 maps every byte to one char, so offsets and ASCII matches are exact.
            string text = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(dllPath));
            var lines = new List<string>();
            for (int i = 0; i < Compiled.Length; i++)
            {
                var matches = Compiled[i].Matches(text).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
                if (Patterns[i] == @"SteamClient\d+" && matches.Count > 1 && matches.Contains("SteamClient017"))
                    matches = new List<string> { "SteamClient017" };
                lines.AddRange(matches);
            }
            return lines;
        }

        public static string Generate(string dllPath)
        {
            var lines = Scan(dllPath);
            return lines.Count == 0 ? null : string.Join("\r\n", lines.ToArray()) + "\r\n";
        }
    }

    /// <summary>Pre-flight: protections the emulator cannot get past, recognised from the section table
    /// alone (a header read, no full-file scan). Only protectors with fixed, documented section names are
    /// listed; Denuvo and Arxan have no reliable structural marker and are deliberately not guessed at - a
    /// false "Denuvo detected" would stop people patching games that would have worked.</summary>
    public static class ProtectionScan
    {
        static readonly KeyValuePair<string, string>[] Markers =
        {
            new KeyValuePair<string, string>(".vmp", "VMProtect"),
            new KeyValuePair<string, string>(".themida", "Themida"),
            new KeyValuePair<string, string>(".winlice", "WinLicense"),
            new KeyValuePair<string, string>(".enigma", "Enigma Protector"),
        };

        public static List<string> Detect(string exePath)
        {
            var found = new List<string>();
            var names = Shibaless.ShibalessUnpacker.ReadSectionNames(exePath);
            if (names == null) return found;
            foreach (var marker in Markers)
                if (names.Any(n => n.StartsWith(marker.Key, StringComparison.OrdinalIgnoreCase)) && !found.Contains(marker.Value))
                    found.Add(marker.Value);
            return found;
        }
    }

    public enum CheckStatus { Pass, Warn, Fail }

    public sealed class InstallCheckItem
    {
        public CheckStatus Status;
        public string Title = "";
        public string Detail = "";
        public override string ToString() { return Status.ToString().ToUpperInvariant() + "  " + Title + (Detail.Length > 0 ? " – " + Detail : ""); }
    }

    /// <summary>Checks a finished install as a whole: every individual write is verified when it happens,
    /// but nothing else asks the question that matters - will the game actually load the emulator? This
    /// catches the "patched successfully, still talks to real Steam" class of failure.</summary>
    public static class InstallCheck
    {
        /// <summary>True for a Goldberg/GSE steam_api build of any version. GSE reads its config from a
        /// "steam_settings" folder and never loads steamclient; Valve's library does the opposite. Comparing
        /// against the bundled dll byte-for-byte would miss an install made by any other patcher release.</summary>
        public static bool IsEmulatorDll(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch { return false; }
            return IndexOf(data, Encoding.ASCII.GetBytes("steam_settings")) >= 0
                && IndexOf(data, Encoding.ASCII.GetBytes("steamclient")) < 0;
        }

        internal static int IndexOf(byte[] haystack, byte[] needle)
        {
            byte first = needle[0];
            int last = haystack.Length - needle.Length;
            for (int i = Array.IndexOf(haystack, first); i >= 0 && i <= last; i = Array.IndexOf(haystack, first, i + 1))
            {
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>Whether an existing install looks like an online-fix one: every steam_appid.txt says 480
        /// AND no Steamworks library in the game is the emulator. AppID 480 alone is not enough - a normal patch
        /// can use 480 too. Used by --check when the mode is not given, so an online-fix game is not reported as
        /// broken for still loading Valve's library.</summary>
        public static bool LooksLikeOnlineFix(string exePath)
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                var libs = PatchRunner.FindSteamApiFiles(PatchRunner.SearchRoot(exePath));
                if (libs.Count == 0 || libs.Any(IsEmulatorDll)) return false;
                var values = new[] { exeDir }.Concat(libs.Select(Path.GetDirectoryName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "steam_appid.txt")).Where(File.Exists)
                    .Select(f => AppIdDetector.Normalize(File.ReadAllText(f))).ToList();
                return values.Count > 0 && values.All(v => v == "480");
            }
            catch { return false; }
        }

        /// <param name="expectedAppId">The AppID the install should carry, or null to only check that all
        /// copies agree. Online-fix installs are expected to carry 480.</param>
        public static List<InstallCheckItem> Run(string exePath, string expectedAppId, bool onlineFix, CancellationToken ct = default(CancellationToken))
        {
            var items = new List<InstallCheckItem>();
            Action<CheckStatus, string, string> add = (st, t, d) => items.Add(new InstallCheckItem { Status = st, Title = t, Detail = d ?? "" });

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                add(CheckStatus.Fail, "Game executable exists", exePath);
                return items;
            }
            exePath = Path.GetFullPath(exePath);
            string exeDir = Path.GetDirectoryName(exePath);
            PeInfo pe;
            try { pe = PeReader.Analyze(exePath); }
            catch (Exception ex) { add(CheckStatus.Fail, "Executable is readable", ex.Message); return items; }

            // 1. SteamStub left in place makes the game hand itself to real Steam before any dll loads. For an
            //    Unreal launcher the exe that matters is the -Shipping one it starts.
            var stubTargets = new List<string> { exePath };
            string companion = PatchRunner.UnrealCompanion(exePath);
            if (companion != null) stubTargets.Add(companion);
            foreach (var target in stubTargets)
            {
                string label = target == exePath ? "SteamStub DRM removed" : "SteamStub DRM removed from " + Path.GetFileName(target);
                bool? stub = Shibaless.ShibalessUnpacker.HasStubSection(target);
                if (stub == true)
                    add(onlineFix ? CheckStatus.Pass : CheckStatus.Fail, label,
                        onlineFix ? "still present, which online-fix mode tolerates" : "still has its .bind stub – it will try to start through Steam");
                else
                    add(CheckStatus.Pass, label, stub == null ? "could not read the section table" : "");
            }

            // 2. Which library will the loader actually pick up?
            string searchRoot = PatchRunner.SearchRoot(exePath);
            var found = PatchRunner.FindSteamApiFiles(searchRoot, ct);
            string imported = PatchRunner.ImportedSteamApiName(exePath);
            string wantedKind = onlineFix ? "Valve's original" : "the emulator";
            if (imported != null)
            {
                // A static import is resolved from the exe's own folder first; for a Steam game nothing
                // later on the search path supplies it.
                string resolved = Path.Combine(exeDir, imported);
                if (!File.Exists(resolved))
                    add(CheckStatus.Fail, "Imported " + imported + " is beside the exe", "the loader will not find it and the game will not start");
                else
                {
                    bool emu = IsEmulatorDll(resolved);
                    add(emu != onlineFix ? CheckStatus.Pass : CheckStatus.Fail, "The game loads " + wantedKind,
                        imported + " beside the exe is " + (emu ? "the emulator" : "Valve's original"));
                    ExeArch dllArch = ExeArch.Unknown;
                    try { dllArch = PeReader.Analyze(resolved).Arch; } catch { }
                    add(dllArch == pe.Arch ? CheckStatus.Pass : CheckStatus.Fail, "Library architecture matches the exe",
                        imported + " is " + dllArch + ", the exe is " + pe.Arch);
                }
            }
            else if (found.Count == 0)
            {
                add(CheckStatus.Warn, "A Steamworks library is present", "none found and the exe imports none – is this the game's main exe?");
            }
            else
            {
                // Loaded at runtime by path (typical for Unreal). Judge the copy the patcher targets - the same
                // pick it installs into - and only warn about other copies: games and their tools can carry
                // extra libraries the game itself never loads.
                string preferred = pe.Arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll";
                string target = PatchRunner.PickApiTarget(found, searchRoot, preferred);
                bool targetEmu = IsEmulatorDll(target);
                add(targetEmu != onlineFix ? CheckStatus.Pass : CheckStatus.Fail, "The game loads " + wantedKind,
                    PatchRunner.ShortRel(exeDir, target) + " is " + (targetEmu ? "the emulator" : "Valve's original"));
                var others = found.Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase) && IsEmulatorDll(f) == onlineFix).ToList();
                if (others.Count > 0)
                    add(CheckStatus.Warn, "Other Steamworks libraries in the game folder",
                        string.Join(", ", others.Select(f => PatchRunner.ShortRel(exeDir, f)).ToArray())
                        + (onlineFix ? " – the emulator" : " – still Valve's original; fine unless the game loads one of these"));
                foreach (var f in found.Where(f => string.Equals(Path.GetFileName(f), preferred, StringComparison.OrdinalIgnoreCase)))
                {
                    ExeArch a = ExeArch.Unknown;
                    try { a = PeReader.Analyze(f).Arch; } catch { }
                    if (a != pe.Arch)
                        add(CheckStatus.Fail, "Library architecture matches the exe", PatchRunner.ShortRel(exeDir, f) + " is " + a + ", the exe is " + pe.Arch);
                }
            }

            // 3. steam_appid.txt: beside the exe and beside each library, all saying the same thing.
            var appIdFiles = new[] { exeDir }.Concat(found.Select(Path.GetDirectoryName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "steam_appid.txt")).Where(File.Exists).ToList();
            string expected = onlineFix ? "480" : (expectedAppId ?? "").Trim();
            if (appIdFiles.Count == 0)
                add(CheckStatus.Warn, "steam_appid.txt present", "none beside the exe or the libraries");
            else
            {
                var values = appIdFiles.Select(f => { try { return AppIdDetector.Normalize(File.ReadAllText(f)); } catch { return ""; } }).ToList();
                var distinct = values.Distinct().ToList();
                if (distinct.Count > 1)
                    add(CheckStatus.Warn, "steam_appid.txt copies agree", string.Join(", ", appIdFiles.Select((f, i) => PatchRunner.ShortRel(exeDir, f) + "=" + values[i]).ToArray()));
                else if (expected.Length > 0 && distinct[0] != expected)
                    add(CheckStatus.Fail, "steam_appid.txt carries AppID " + expected, "found " + (distinct[0].Length == 0 ? "an invalid value" : distinct[0]));
                else
                    add(distinct[0].Length > 0 ? CheckStatus.Pass : CheckStatus.Fail, "steam_appid.txt is valid",
                        distinct[0].Length > 0 ? "AppID " + distinct[0] + " in " + appIdFiles.Count + " place(s)" : "not a numeric AppID");
            }
            return items;
        }
    }

    public class AppIdDetection
    {
        public string AppId = "";   // "" when nothing was found
        public string Source = "";  // "saved" | "steam_appid.txt" | "Steam Store (<game name>)"
        public bool Found { get { return !string.IsNullOrEmpty(AppId); } }
    }

    /// <summary>Resolves a game's Steam AppID the same way the single-game view does:
    /// previously saved id → steam_appid.txt anywhere in the install tree → Steam Store autocomplete.</summary>
    public static class AppIdDetector
    {
        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = "";
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.Length > 10) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            uint number;
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number == 0) return false;
            normalized = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        public static bool IsValid(string value)
        {
            string normalized;
            return TryNormalize(value, out normalized);
        }

        public static string Normalize(string value)
        {
            string normalized;
            return TryNormalize(value, out normalized) ? normalized : "";
        }

        /// <summary>Memoised local lookups, keyed by the exe's folder. A batch detects an AppID for every row
        /// and the UI re-detects on every selection, so the same folder was being scanned repeatedly. The
        /// batch engine clears this once per run so a steam_appid.txt written mid-run is not missed.</summary>
        static readonly Dictionary<string, AppIdDetection> localCache = new Dictionary<string, AppIdDetection>(StringComparer.OrdinalIgnoreCase);

        public static void ClearLocalCache()
        {
            lock (localCache) localCache.Clear();
        }

        static void AddDir(List<string> dirs, HashSet<string> seen, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !seen.Add(dir)) return;
            dirs.Add(dir);
        }

        /// <summary>Directories plausibly holding a steam_appid.txt: the exe's own folder first - that is the
        /// one Steam actually reads - then its parents, then a shallow descent. Bounded by both depth and a
        /// hard cap on purpose: this used to run the full game-tree scan that FindSteamApiFiles performs, up
        /// to 50,000 directories, once per batch row.</summary>
        static List<string> AppIdCandidateDirs(string exeDir)
        {
            var dirs = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDir(dirs, seen, exeDir);

            string up = exeDir;
            for (int i = 0; i < 2 && !string.IsNullOrEmpty(up); i++)
            {
                up = Path.GetDirectoryName(up);
                AddDir(dirs, seen, up);
            }

            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(exeDir, 0));
            while (queue.Count > 0 && dirs.Count < 400)
            {
                var item = queue.Dequeue();
                if (item.Value >= 3) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(item.Key); }
                catch { continue; }
                foreach (var sub in subs)
                {
                    if (PatchRunner.SkipDirs.Contains(Path.GetFileName(sub))) continue;
                    AddDir(dirs, seen, sub);
                    queue.Enqueue(new KeyValuePair<string, int>(sub, item.Value + 1));
                }
            }
            return dirs;
        }

        static AppIdDetection LookupLocal(string dir)
        {
            var found = new AppIdDetection();
            try
            {
                var id = PatchRunner.FindExistingAppId(AppIdCandidateDirs(dir).ToArray());
                if (!string.IsNullOrEmpty(id)) { found.AppId = id; found.Source = "steam_appid.txt"; }
            }
            catch { }
            return found;
        }

        public static AppIdDetection Detect(string exePath, string cachedId, bool allowOnline, CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            var d = new AppIdDetection();
            if (string.IsNullOrEmpty(exePath)) return d;

            if (IsValid(cachedId))
            { d.AppId = Normalize(cachedId); d.Source = "saved"; return d; }

            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (dir != null)
                {
                    AppIdDetection local;
                    lock (localCache)
                    {
                        if (!localCache.TryGetValue(dir, out local))
                        {
                            local = LookupLocal(dir);
                            localCache[dir] = local;
                        }
                    }
                    if (!string.IsNullOrEmpty(local.AppId)) { d.AppId = local.AppId; d.Source = local.Source; return d; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            ct.ThrowIfCancellationRequested();
            if (allowOnline)
            {
                var m = SteamLookup.FindBestForExe(exePath, ct);
                if (m != null && IsValid(m.AppId))
                { d.AppId = Normalize(m.AppId); d.Source = "Steam Store (" + m.GameName + ")"; }
            }
            return d;
        }
    }

    /// <summary>One game queued for a batch run. AppId may be empty (the engine skips it unless online-fix is on).</summary>
    public class BatchInput
    {
        public string Exe = "";
        public string AppId = "";
    }

    public class BatchItemOutcome
    {
        public string Exe = "";
        public bool Success;
        public bool Skipped;      // never patched (no AppID)
        public bool Cancelled;    // user cancelled before / during this game
        public string AppIdUsed = "";
        public string Summary = "";
    }

    /// <summary>Options shared by every game in a batch – mirrors the single-game option toggles.</summary>
    public class BatchPrefs
    {
        public bool UnpackDrm = true;
        public bool Backup = true;
        public bool WriteAppIdTxt = true;
        public bool CreateSettings = false;
        public bool OnlineFix = false;   // force Spacewar 480 for every game, AppIDs are ignored
        public bool Achievements = false;
    }

    /// <summary>Patches several games sequentially. AppIDs must be resolved by the caller (see AppIdDetector).</summary>
    public class BatchPatcher
    {
        public event Action<PatchLogEntry> LogLine;       // all log output, worker thread
        public event Action<int, int> GameStarted;        // (1-based index, total) before a game starts
        public event Action<int, int> GamePercent;        // (1-based index, 0-100 sub-progress)
        public event Action<BatchItemOutcome> ItemCompleted;

        private void Log(LogLevel lvl, string msg)
        {
            var h = LogLine; if (h != null) h(new PatchLogEntry { Time = DateTime.Now, Level = lvl, Message = msg });
        }

        public Task<List<BatchItemOutcome>> RunAsync(List<BatchInput> items, BatchPrefs prefs, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                // Detection results are memoised per folder; drop them once per run so a steam_appid.txt
                // written between runs is picked up while repeated lookups within a run stay cheap.
                AppIdDetector.ClearLocalCache();
                // Bound the online lookups for the whole run: two candidates per game, so a batch cannot
                // stall indefinitely on the network. Single-game detection keeps the unlimited default.
                SteamLookup.ResetRequestBudget(Math.Max(10, (items == null ? 0 : items.Count) * 2));
                var results = new List<BatchItemOutcome>();
                int n = (items == null ? 0 : items.Count);

                for (int i = 0; i < n; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    string exe = items[i].Exe;
                    var o = new BatchItemOutcome { Exe = exe };
                    var started = GameStarted; if (started != null) started(i + 1, n);

                    try
                    {
                        bool ofix = prefs.OnlineFix;
                        string appId = (items[i].AppId ?? "").Trim();
                        Log(LogLevel.Info, string.Format("── [{0}/{1}] {2}{3}", i + 1, n, Path.GetFileName(exe),
                            ofix ? "   (online-fix · Spacewar 480)" : "   AppID " + (appId.Length > 0 ? appId : "?")));

                        if (!ofix && appId.Length == 0)
                        {
                            o.Skipped = true;
                            o.Summary = "No AppID available – skipped.";
                            Log(LogLevel.Warn, Path.GetFileName(exe) + ": no AppID detected or entered – skipping this game.");
                            var done1 = ItemCompleted; if (done1 != null) done1(o);
                            results.Add(o);
                            continue;
                        }

                        var opts = new PatchOptions
                        {
                            GameExe = exe,
                            AppId = ofix ? "480" : appId,
                            UnpackDrm = prefs.UnpackDrm,
                            Backup = prefs.Backup,
                            WriteAppIdTxt = prefs.WriteAppIdTxt,
                            CreateSettings = prefs.CreateSettings,
                            Achievements = prefs.Achievements,
                            OnlineFix = ofix,
                        };
                        o.AppIdUsed = opts.EffectiveAppId;

                        var runner = new PatchRunner();
                        runner.LogLine += e => Log(e.Level, "   " + e.Message);
                        runner.ProgressChanged += p => { var gp = GamePercent; if (gp != null) gp(i + 1, p); };

                        var res = runner.Run(opts, ct);
                        o.Success = res.Success;
                        o.Summary = res.Summary;
                        o.Cancelled = res.Cancelled;
                    }
                    catch (OperationCanceledException)
                    {
                        o.Cancelled = true;
                        o.Summary = "Cancelled.";
                        Log(LogLevel.Warn, Path.GetFileName(exe) + ": cancelled.");
                    }
                    catch (Exception ex)
                    {
                        o.Summary = ex.Message;
                        Log(LogLevel.Error, Path.GetFileName(exe) + ": " + ex.Message);
                    }

                    results.Add(o);
                    var done = ItemCompleted; if (done != null) done(o);
                    if (o.Cancelled) break;
                }

                int ok = 0, bad = 0, skip = 0;
                foreach (var r in results) { if (r.Success) ok++; else if (r.Skipped || r.Cancelled) skip++; else bad++; }
                Log(LogLevel.Info, "── Batch finished: " + ok + " patched · " + bad + " failed · " + skip + " skipped ──────────────");
                return results;
            });
        }
    }

    // --------------------------------------------------------------------- steam store lookup

    public class SteamMatch
    {
        public string AppId = "";
        public string GameName = "";
    }

    /// <summary>Guesses game-title candidates from an install path and searches the key-less Steam Store autocomplete API.</summary>
    public static class SteamLookup
    {
        const string UrlFmt = "https://store.steampowered.com/api/storesearch/?term={0}&f=apps&cc=us&l=en";

        /// <summary>Folder names up the tree (innermost first) that could be a game title.</summary>
        public static List<string> CandidateTitles(string exePath)
        {
            var list = new List<string>();
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(exePath ?? ""));
                var parts = (dir ?? "").Replace('/', '\\').Split('\\');
                // Deepest component first, which is the exe's own folder - the likeliest title by far - and
                // capped at two: the third candidate was almost never the answer but always cost a request.
                for (int i = parts.Length - 1; i >= 0 && list.Count < 2; i--)
                {
                    var p = (parts[i] ?? "").Trim();
                    if (p.Length < 4) continue;
                    if (IsContainer(p.ToLowerInvariant())) continue;
                    bool dup = false;
                    foreach (var q in list) if (q.Equals(p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) list.Add(p);
                }
            }
            catch { }
            return list;
        }

        static bool IsContainer(string lp)
        {
            switch (lp)
            {
                case "games": case "game": case "apps": case "app": case "steam": case "common":
                case "steamapps": case "program files": case "program files (x86)": case "my games":
                case "software": case "public": case "users": case "documents": case "downloads":
                    return true;
            }
            return false;
        }

        /// <summary>Searches the Steam Store autocomplete for a title. Returns null on no confident match or network failure.</summary>
        public static SteamMatch Search(string title, CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(title)) return null;
            if (!TakeRequestBudget()) return null;   // budget spent: fall back to local detection
            string json = HttpGet(UrlFmt.Replace("{0}", Uri.EscapeDataString(title.Trim())), ct);
            if (json == null) return null;
            return BestMatch(ParseItems(json), title);
        }

        /// <summary>Picks the best match for a title: normalised exact match wins outright, else a long-enough containment.</summary>
        public static SteamMatch BestMatch(List<SteamMatch> items, string title)
        {
            if (items == null || items.Count == 0) return null;
            string n2 = Norm(title);
            if (n2.Length < 3) return null;

            SteamMatch best = null; int bestScore = 0;
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.AppId)) continue;
                string n1 = Norm(it.GameName);
                if (n1.Length == 0) continue;
                if (n1 == n2) return it; // exact: take immediately, even if ranked later
                int score = Math.Min(n1.Length, n2.Length) >= 5 && (n1.Contains(n2) || n2.Contains(n1)) ? 2 : 0;
                if (score > bestScore) { bestScore = score; best = it; }
            }
            return best;
        }

        /// <summary>Normalises a name for comparison: lowercase alphanumerics only ("Half-Life 2" == "halflife2").</summary>
        public static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant()) if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            return sb.ToString();
        }

        public const int MaxResponseLength = 1024 * 1024;

        /// <summary>Pulls (appid, name) pairs out of a store search response. Uses the JSON reader that ships
        /// with .NET Framework (JSON surfaced as typed XML), which is what removed the System.Web.Extensions
        /// dependency. Every element carries its JSON type, so ids sent as numbers or as strings both work.</summary>
        public static List<SteamMatch> ParseItems(string json)
        {
            var list = new List<SteamMatch>();
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseLength) return list;
            try
            {
                var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 32, MaxStringContentLength = MaxResponseLength };
                XElement root;
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), quotas))
                    root = XElement.Load(reader);
                var items = root.Element("items");
                if (items == null || (string)items.Attribute("type") != "array") return list;
                foreach (var item in items.Elements("item"))
                {
                    if ((string)item.Attribute("type") != "object") continue;
                    var name = item.Element("name");
                    if (name == null || (string)name.Attribute("type") != "string" || string.IsNullOrWhiteSpace(name.Value)) continue;
                    var id = item.Element("id") ?? item.Element("appid");
                    string idType = id == null ? null : (string)id.Attribute("type");
                    if (idType != "string" && idType != "number") continue;
                    string normalized = AppIdDetector.Normalize(id.Value);
                    if (normalized.Length != 0)
                        list.Add(new SteamMatch { AppId = normalized, GameName = name.Value });
                }
            }
            catch (XmlException) { list.Clear(); }
            catch (ArgumentException) { list.Clear(); }
            catch (InvalidOperationException) { list.Clear(); }
            return list;
        }

        public static SteamMatch FindBestForExe(string exePath, CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            foreach (string title in CandidateTitles(exePath))
            {
                ct.ThrowIfCancellationRequested();
                var match = Search(title, ct);
                if (match != null) return match;
            }
            return null;
        }

        public static Task<SteamMatch> FindBestForExeAsync(string exePath, CancellationToken ct = default(CancellationToken))
        {
            return Task.Run(() => FindBestForExe(exePath, ct), ct);
        }

        /// <summary>Requests left for online lookups in the current run. A 50-game batch used to be able to
        /// issue unbounded sequential lookups, each with its own timeout; once this is spent the batch falls
        /// back to local detection rather than stalling on the network.</summary>
        static int requestBudget = int.MaxValue;

        public static void ResetRequestBudget(int requests)
        {
            Interlocked.Exchange(ref requestBudget, Math.Max(0, requests));
        }

        static bool TakeRequestBudget()
        {
            while (true)
            {
                int current = Volatile.Read(ref requestBudget);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref requestBudget, current - 1, current) == current) return true;
            }
        }

        static string HttpGet(string url, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                // 4s rather than 8s: this runs while a batch is stalled on the network, and a slow answer
                // is worth less than the delay costs.
                req.Timeout = 4000;
                req.ReadWriteTimeout = 4000;
                req.UserAgent = "Shibaberg/" + BuildInfo.Version;   // was a stale hardcoded "0.3"
                using (ct.Register(() => req.Abort()))
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    if (resp.ContentLength > MaxResponseLength) return null;
                    var text = new StringBuilder();
                    var buffer = new char[4096];
                    int read;
                    while ((read = sr.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (text.Length + read > MaxResponseLength) return null;
                        text.Append(buffer, 0, read);
                    }
                    ct.ThrowIfCancellationRequested();
                    return text.ToString();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { ct.ThrowIfCancellationRequested(); return null; }
        }
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
        public bool OnlineFix = false;
        public bool LookupAppId = true;
        public bool Achievements = false;
        public Dictionary<string, string> AppIdsByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Cloud saves: save folders the user added per AppID, beyond the detected ones.</summary>
        public Dictionary<string, List<string>> SaveDirs = new Dictionary<string, List<string>>();

        static string Dir { get { return AppPaths.StateDir; } }
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
                        case "onlinefix": s.OnlineFix = v == "1"; break;
                        case "lookup": s.LookupAppId = v == "1"; break;
                        case "achievements": s.Achievements = v == "1"; break;
                        default:
                            if (k.StartsWith("folder:"))
                                s.AppIdsByFolder[UnescKey(k.Substring(7))] = v;
                            else if (k.StartsWith("savedirs:"))
                                s.SaveDirs[k.Substring(9)] = v.Split('|').Where(x => x.Length > 0).ToList();
                            break;
                    }
                }
            }
            catch { }
            return s;
        }

        public bool Save(out string error)
        {
            error = "";
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
                sb.AppendLine("onlinefix=" + (OnlineFix ? "1" : "0"));
                sb.AppendLine("lookup=" + (LookupAppId ? "1" : "0"));
                sb.AppendLine("achievements=" + (Achievements ? "1" : "0"));
                foreach (var kv in AppIdsByFolder)
                    sb.AppendLine("folder:" + EscKey(kv.Key) + "=" + kv.Value);
                foreach (var kv in SaveDirs)
                    if (kv.Value.Count > 0) sb.AppendLine("savedirs:" + kv.Key + "=" + string.Join("|", kv.Value));
                var record = SafePersistence.WriteText(File0, sb.ToString());
                // settings.ini is tiny and fully regenerable, so it needs no undo record. Without this
                // the staging area (and a copy of the previous settings) leaks on every save – which
                // happens on every game selection and every batch item.
                Recovery.Discard(new[] { record });
                return true;
            }
            catch (Exception ex)
            {
                error = "Settings could not be saved: " + ex.Message;
                return false;
            }
        }

        public void Save()
        {
            string error;
            Save(out error);
        }

        // Keys are written as `folder:<path>=<id>` and parsed by splitting on the FIRST '='.
        // A game path containing '=' (or '%') would corrupt that split – percent-escape key chars.
        static string EscKey(string s)
        {
            return (s ?? "").Replace("%", "%25").Replace("\\", "%5C").Replace("=", "%3D");
        }
        static string UnescKey(string s)
        {
            return (s ?? "").Replace("%3D", "=").Replace("%5C", "\\").Replace("%25", "%");
        }
    }
}
