using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Web.Script.Serialization;

namespace Gp
{
    public enum LogLevel { Info, Dim, Ok, Warn, Error }

    public enum ExeArch { Unknown, X86, X64 }

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

        public static PeInfo Analyze(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var info = new PeInfo { SizeBytes = fs.Length };
                RequireRange(0, 64, fs.Length);
                if (br.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Missing DOS signature.");
                fs.Position = 0x3c;
                long pe = br.ReadUInt32();
                if (pe < 64) throw new InvalidDataException("Invalid PE header offset.");
                RequireRange(pe, 24, fs.Length);
                fs.Position = pe;
                if (br.ReadUInt32() != 0x4550) throw new InvalidDataException("Missing PE signature.");
                info.Machine = br.ReadUInt16();
                int count = br.ReadUInt16();
                fs.Position = pe + 20;
                int optSize = br.ReadUInt16();
                ushort characteristics = br.ReadUInt16();
                long opt = pe + 24;
                RequireRange(opt, optSize, fs.Length);
                RequireRange(0, 2, optSize);
                fs.Position = opt;
                ushort magic = br.ReadUInt16();
                if (magic != 0x10b && magic != 0x20b) throw new InvalidDataException("Unsupported PE optional header.");
                if ((info.Machine == 0x14c && magic != 0x10b) || (info.Machine == 0x8664 && magic != 0x20b))
                    throw new InvalidDataException("PE machine and optional header disagree.");
                int dd = magic == 0x20b ? 112 : 96;
                RequireRange(0, dd, optSize);
                fs.Position = opt + 60;
                uint headers = br.ReadUInt32();
                RequireRange(0, headers, fs.Length);
                fs.Position = opt + dd - 4;
                uint directories = br.ReadUInt32();
                RequireRange(dd, (long)directories * 8, optSize);
                long table = opt + optSize;
                RequireRange(table, (long)count * 40, fs.Length);
                if (count == 0 || table + (long)count * 40 > headers)
                    throw new InvalidDataException("Invalid PE section table.");
                var sections = new List<long[]>();
                for (int i = 0; i < count; i++)
                {
                    fs.Position = table + i * 40L + 8;
                    long virtualSize = br.ReadUInt32();
                    long va = br.ReadUInt32();
                    long rawSize = br.ReadUInt32();
                    long raw = br.ReadUInt32();
                    RequireRange(raw, rawSize, fs.Length);
                    if (va + Math.Max(virtualSize, rawSize) > 0x100000000L)
                        throw new InvalidDataException("PE section RVA overflows.");
                    sections.Add(new[] { va, rawSize, raw });
                }
                uint flags = 0;
                if (directories > 14)
                {
                    fs.Position = opt + dd + 14 * 8;
                    uint rva = br.ReadUInt32();
                    uint size = br.ReadUInt32();
                    if (rva != 0 || size != 0)
                    {
                        if (rva == 0 || size < 72) throw new InvalidDataException("Invalid CLR directory.");
                        long cor = MapRva(rva, size, headers, sections);
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
                bool prefer32 = (flags & 0x20000) != 0 && (characteristics & 0x2000) == 0;
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

    public static class SafePersistence
    {
        public static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        internal static string PathKey(string path)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
        }

        internal static T Locked<T>(string path, Func<T> action)
        {
            using (var mutex = new Mutex(false, @"Local\GoldbergPatcher-" + PathKey(path)))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Another instance is writing " + path + ". Retry after it finishes.");
                    return action();
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
        public static FileWriteRecord Write(string path, Action<Stream> write, Action<string> validate = null,
            List<FileWriteRecord> journal = null, Action<string> checkpoint = null, string externalRecovery = null)
        {
            path = Path.GetFullPath(path);
            return Locked(path, () =>
            {
                string parent = Path.GetDirectoryName(path);
                string area = Path.Combine(parent, ".gp-recovery", Guid.NewGuid().ToString("N"));
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
                    using (var stream = new FileStream(record.StagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        write(stream);
                        stream.Flush(true);
                    }
                    if (validate != null) validate(record.StagedPath);
                    if (checkpoint != null) checkpoint("staged");
                    // For a local recovery copy, File.Replace will move the destination's current
                    // content there, so the destination is what we must hash. For an external recovery
                    // the copy already exists and is what rollback will restore from.
                    string oldHash = external
                        ? Hash(record.RecoveryPath)
                        : (record.RecoveryPath.Length == 0 ? "absent" : Hash(path));
                    record.PreviousHash = oldHash;
                    record.StagedHash = Hash(record.StagedPath);
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

        public static FileWriteRecord Copy(string source, string destination, List<FileWriteRecord> journal = null,
            Action<string> validate = null, string externalRecovery = null)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Source file for staged copy not found: " + source, source);
            string expected = Hash(source);
            return Write(destination, output =>
            {
                using (var input = File.OpenRead(source)) input.CopyTo(output);
            }, staged =>
            {
                if (Hash(staged) != expected) throw new InvalidDataException("Staged copy hash mismatch: " + source);
                if (validate != null) validate(staged);
            }, journal, null, externalRecovery);
        }

        public static FileWriteRecord WriteText(string path, string text, List<FileWriteRecord> journal = null,
            string externalRecovery = null)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            return Write(path, stream => stream.Write(bytes, 0, bytes.Length), null, journal, null, externalRecovery);
        }
    }

    internal sealed class InvocationOutputs
    {
        readonly string directory;
        readonly string input;
        readonly Dictionary<string, string> before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal InvocationOutputs(string inputPath)
        {
            input = Path.GetFullPath(inputPath);
            directory = Path.GetDirectoryName(input);
            foreach (string path in Directory.GetFiles(directory))
                if (IsCandidate(path)) before.Add(Path.GetFullPath(path), SafePersistence.Hash(path));
        }

        bool IsCandidate(string path)
        {
            return !string.Equals(path, input, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".unpacked.exe", StringComparison.OrdinalIgnoreCase);
        }

        internal bool IsCurrent(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path = Path.GetFullPath(path);
            if (!IsCandidate(path) || !File.Exists(path)) return false;
            string hash;
            return !before.TryGetValue(path, out hash) || hash != SafePersistence.Hash(path);
        }

        internal void CopyAndDelete(string path, List<FileWriteRecord> journal, string expectedInputHash,
            string externalRecovery = null)
        {
            if (!IsCurrent(path)) throw new IOException("No current invocation output: " + path);
            string expected = SafePersistence.Hash(path);
            SafePersistence.Copy(path, input, journal, staged =>
            {
                if (SafePersistence.Hash(staged) != expected || SafePersistence.Hash(input) != expectedInputHash)
                    throw new IOException("Executable or output changed during processing: " + input);
                if (PeReader.Analyze(staged).Arch == ExeArch.Unknown)
                    throw new InvalidDataException("Unsupported unpacked executable architecture.");
            }, externalRecovery);
            if (SafePersistence.Hash(path) == expected) File.Delete(path);
        }
    }

    public static class OriginalBackups
    {
        public static string Location(string root, string source)
        {
            return Path.Combine(root, "sources", SafePersistence.PathKey(source), Path.GetFileName(source));
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

        static string Existing(string root, string source, out string hash)
        {
            string destination = Location(root, source);
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
                catch (IOException ex) { failure = ex; }
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

        public static string Preserve(string root, string source)
        {
            string destination = Location(root, source);
            return SafePersistence.Locked(destination, () =>
            {
                string hash;
                string existing = Existing(root, source, out hash);
                if (existing != null && File.Exists(destination)) return existing;
                string content = existing ?? source;
                if (!Eligible(content)) return null;
                hash = hash ?? SafePersistence.Hash(content);
                // These two writes are the backup itself, so they need no undo record of their own –
                // and they are not part of any run's journal, so nothing else would ever collect their
                // staging areas. Drop them here or goldberg_backup accumulates .gp-recovery litter.
                var backupWrite = SafePersistence.Copy(content, destination, null, staged =>
                {
                    if (SafePersistence.Hash(staged) != hash || !Eligible(staged))
                        throw new IOException("Original changed while preserving: " + content);
                });
                var manifestWrite = SafePersistence.WriteText(destination + ".source.txt", Path.GetFullPath(source) + "\r\n" + hash + "\r\n");
                Recovery.Discard(new[] { backupWrite, manifestWrite });
                return destination;
            });
        }

        public static void Restore(string root, string source, List<FileWriteRecord> journal = null)
        {
            SafePersistence.Locked(Location(root, source), () =>
            {
                string hash;
                string backup = Existing(root, source, out hash);
                if (backup == null) throw new IOException("No verified or eligible legacy original backup exists for " + source);
                var write = SafePersistence.Copy(backup, source, journal, staged =>
                {
                    if (SafePersistence.Hash(staged) != hash || !Eligible(staged))
                        throw new IOException("Original backup changed while restoring: " + backup);
                });
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
        /// <summary>Test hook: redirects the undo journal away from the real application state
        /// directory so a self-test run cannot disturb a pending undo.</summary>
        internal static string JournalPathOverride;

        /// <summary>Test hook: keeps the startup sweep out of the real application state directory.</summary>
        internal static string StateRootOverride;

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

    /// <summary>Resolves bundled tool paths relative to this app's folder.</summary>
    public static class Tools
    {
        public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string SteamlessCli { get { return Path.Combine(BaseDir, @"steamless\Steamless.CLI.exe"); } }
        public static string SteamlessDir { get { return Path.Combine(BaseDir, "steamless"); } }
        public static string ApiDll86 { get { return Path.Combine(BaseDir, @"release\regular\x86\steam_api.dll"); } }
        public static string ApiDll64 { get { return Path.Combine(BaseDir, @"release\regular\x64\steam_api64.dll"); } }
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
        static string StampPath { get { return Path.Combine(Tools.BaseDir, ".payload-ok"); } }

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
                    var fi = new FileInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, e.Rel));
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
                    string dst = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, e.Rel);
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
            return written;
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

                // ---- locate steam api install dir ------------------------------
                Pct(8);
                var foundApi = FindSteamApiFiles(gameDir, ct);
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
                var settingsPlan = (o.CreateSettings || o.OnlineFix)
                    ? SettingsScaffold.Plan(Tools.SettingsExampleDir, installDir) : new SettingsOutcome();
                res.Settings = settingsPlan;
                if (settingsPlan.Status == SettingsStatus.Failed) throw new IOException(settingsPlan.Error);
                if (settingsPlan.Status == SettingsStatus.Missing) Log(LogLevel.Warn, settingsPlan.Error);

                // ---- backup dir ----------------------------------------------
                string backupDir = Path.Combine(installDir, "goldberg_backup");
                bool anyBackup = false;
                Func<string, string> backup = (src) =>
                {
                    if (!o.Backup) return null;
                    var dst = OriginalBackups.Preserve(backupDir, src);
                    if (dst != null) anyBackup = true;
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

                // ---- re-analyze after unpack ------------------------------------
                // Steamless rewrote the exe in place. The packed file's machine type was used above to pick the
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
                            preferredName = pe.Arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll";
                            otherName = pe.Arch == ExeArch.X64 ? "steam_api.dll" : "steam_api64.dll";
                        }
                    }
                    catch { /* keep the pre-unpack analysis */ }
                }

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
                if (anyBackup && res.BackupDir != "") Log(LogLevel.Dim, "Originals backed up in: " + ShortRel(gameDir, res.BackupDir));
                Log(LogLevel.Ok, "✔ Done! Launch the game to test.");
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

        private string TryUnpack(string exePath, Func<string, string> backup, PatchResult res, CancellationToken ct)
        {
            if (!File.Exists(Tools.SteamlessCli))
            {
                Log(LogLevel.Warn, "Steamless CLI not found – skipping DRM unpack. (" + Tools.SteamlessCli + ")");
                return exePath;
            }

            Log(LogLevel.Info, "Running Steamless to check/remove SteamStub DRM…");
            var outputs = new InvocationOutputs(exePath);
            string inputHash = SafePersistence.Hash(exePath);
            var outputLines = new List<string>();
            bool timedOut = false;

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
                        if (sw.Elapsed.TotalMinutes > 10) { timedOut = true; Log(LogLevel.Warn, "Steamless took over 10 minutes – killing it."); try { p.Kill(); } catch { } break; }
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
                    if (m.Success && outputs.IsCurrent(m.Value)) { outPath = m.Value; break; }
                }
            }
            if (outPath == null || !File.Exists(outPath))
            {
                var cand1 = exePath + ".unpacked.exe";
                var nameOnly = Path.GetFileNameWithoutExtension(exePath);
                var cand2 = Path.Combine(Path.GetDirectoryName(exePath), nameOnly + ".unpacked.exe");
                var cands = new[] { cand1, cand2 }
                    .Concat(SafeFiles(Path.GetDirectoryName(exePath)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Where(outputs.IsCurrent)
                    .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                outPath = cands.FirstOrDefault();
            }

            bool successMsg = false;
            lock (outputLines)
            {
                successMsg = outputLines.Any(l => l.IndexOf("Successfully unpacked", StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var l in outputLines.TakeLastVisible(50)) // echo only the tail – Steamless can be verbose
                {
                    var t = l.TrimEnd();
                    if (t.Length == 0) continue;
                    if (t.StartsWith("[Steamless]", StringComparison.OrdinalIgnoreCase)) t = t.Substring(11).Trim();
                    Log(LogLevel.Dim, "   " + t);
                }
            }

            if (outPath != null && File.Exists(outPath))
            {
                // Never replace the original with a file we can't verify as a real PE. On timeout in
                // particular, Steamless was killed mid-write and the .unpacked.exe may be half-written.
                bool validPe;
                try { PeReader.Analyze(outPath); validPe = true; }
                catch { validPe = false; }
                if (!validPe)
                {
                    Log(LogLevel.Error, "Steamless output \"" + Path.GetFileName(outPath) + "\" is not a valid PE"
                        + (timedOut ? " – the unpack was killed after 10 minutes and the file may be incomplete." : ".")
                        + "\nKeeping the original exe untouched; delete the .unpacked.exe manually if you're sure it's junk.");
                    return exePath;
                }

                var origBackup = backup(exePath);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // origBackup already holds a hash-verified copy of the packed exe, so the write
                    // reuses it as the recovery source instead of storing a second copy of a file
                    // that can be hundreds of megabytes.
                    outputs.CopyAndDelete(outPath, res.Writes, inputHash, origBackup);
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
                    OriginalBackups.Restore(backupDir, cur, res.Writes);
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

            int installed = 0;
            foreach (var dllName in wanted)
            {
                string src = dllName == "steam_api64.dll" ? Tools.ApiDll64 : Tools.ApiDll86;
                if (!File.Exists(src)) { Log(LogLevel.Error, "Missing bundled emulator dll: " + src); continue; }
                string dst = Path.Combine(installDir, dllName);
                if (File.Exists(dst))
                {
                    if (backup(dst) != null) Log(LogLevel.Dim, "Preserved original backup for " + dllName);
                }
                SafePersistence.Copy(src, dst, res.Writes, staged =>
                {
                    if (PeReader.Analyze(staged).Arch == ExeArch.Unknown)
                        throw new InvalidDataException("Unsupported emulator dll architecture.");
                });
                res.ReplacedFiles.Add(dllName);
                installed++;
                Log(LogLevel.Ok, "Installed Goldberg → " + dllName);
            }

            if (installed == 0)
                throw new Exception("No steam_api dll could be installed – the bundled emulator dll(s) are missing from this patcher's folder.\n"
                    + "The self-contained restore may have failed; check the log above and re-run, or reinstall the patcher.");
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
                        if (new FileInfo(f).Length > 128) continue;
                        var id = AppIdDetector.Normalize(File.ReadAllText(f));
                        if (id.Length != 0) return id;
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
                    // The exe's own folder first – that's the steam_appid.txt Steam actually reads;
                    // a stale copy in some deep subfolder must not beat it.
                    var dirs = new List<string> { dir };
                    foreach (var a in PatchRunner.FindSteamApiFiles(dir, ct))
                    {
                        var ad = Path.GetDirectoryName(a);
                        bool seen = false;
                        foreach (var q in dirs) if (string.Equals(q, ad, StringComparison.OrdinalIgnoreCase)) { seen = true; break; }
                        if (!seen) dirs.Add(ad);
                    }
                    var id = new PatchRunner().FindExistingAppId(dirs.ToArray());
                    if (!string.IsNullOrEmpty(id)) { d.AppId = id; d.Source = "steam_appid.txt"; return d; }
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
                            GenerateInterfaces = true,
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
                for (int i = parts.Length - 1; i >= 0 && list.Count < 3; i--)
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

        public sealed class StoreResponse
        {
            public StoreItem[] items { get; set; }
        }

        public sealed class StoreItem
        {
            public object id { get; set; }
            public object appid { get; set; }
            public object name { get; set; }
        }

        public static List<SteamMatch> ParseItems(string json)
        {
            var list = new List<SteamMatch>();
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseLength) return list;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxResponseLength, RecursionLimit = 32 };
                var response = serializer.Deserialize<StoreResponse>(json);
                if (response == null || response.items == null) return list;
                foreach (var item in response.items)
                {
                    if (item == null || !(item.name is string) || string.IsNullOrWhiteSpace((string)item.name)) continue;
                    object id = item.id ?? item.appid;
                    if (!(id is string) && !(id is int) && !(id is long)) continue;
                    string normalized = AppIdDetector.Normalize(Convert.ToString(id, CultureInfo.InvariantCulture));
                    if (normalized.Length != 0)
                        list.Add(new SteamMatch { AppId = normalized, GameName = (string)item.name });
                }
            }
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

        static string HttpGet(string url, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "GoldbergPatcher/0.3";
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

    internal static class Ext
    {
        /// <summary>Returns the last n items of a list (all of it when n >= count).</summary>
        public static IEnumerable<T> TakeLastVisible<T>(this IList<T> list, int n)
        {
            if (list == null || n <= 0) yield break;
            for (int i = Math.Max(0, list.Count - n); i < list.Count; i++) yield return list[i];
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
        public bool GenerateInterfaces = true;
        public bool OnlineFix = false;
        public bool LookupAppId = true;
        public Dictionary<string, string> AppIdsByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                        case "interfaces": s.GenerateInterfaces = v == "1"; break;
                        case "onlinefix": s.OnlineFix = v == "1"; break;
                        case "lookup": s.LookupAppId = v == "1"; break;
                        default:
                            if (k.StartsWith("folder:"))
                                s.AppIdsByFolder[UnescKey(k.Substring(7))] = v;
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
                sb.AppendLine("interfaces=" + (GenerateInterfaces ? "1" : "0"));
                sb.AppendLine("onlinefix=" + (OnlineFix ? "1" : "0"));
                sb.AppendLine("lookup=" + (LookupAppId ? "1" : "0"));
                foreach (var kv in AppIdsByFolder)
                    sb.AppendLine("folder:" + EscKey(kv.Key) + "=" + kv.Value);
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
