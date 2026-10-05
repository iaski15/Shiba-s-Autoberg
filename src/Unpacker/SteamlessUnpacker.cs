using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Steamless.API.Events;
using Steamless.API.Model;
using Steamless.API.Services;

namespace SteamlessNative
{
    public enum UnpackErrorCode
    {
        None = 0,
        InvalidInput,
        UnsupportedVariant,
        UnpackFailed,
        OutputValidationFailed
    }

    public sealed class UnpackResult
    {
        public bool Success { get; internal set; }
        public UnpackErrorCode ErrorCode { get; internal set; }
        public string Error { get; internal set; }

        /// <summary>Name of the Steamless unpacker that claimed the file, e.g. "SteamStub Variant 2.1 Unpacker (x86)".
        /// Null when none did.</summary>
        public string Unpacker { get; internal set; }

        /// <summary>The unpacked executable.</summary>
        public byte[] Output { get; internal set; }

        /// <summary>Lower-case hex SHA-256 of the packed input exactly as it was read - lets a caller prove
        /// the file on disk has not changed since, without hashing it twice.</summary>
        public string SourceSha256 { get; internal set; }
    }

    /// <summary>Runs the vendored Steamless unpackers (third_party/steamless) in-process. Steamless does the
    /// unpacking; this class only picks the unpacker, keeps the result in memory, and adds the guards that
    /// decide whether the result may replace the game's exe.</summary>
    public static class SteamlessUnpacker
    {
        static readonly object sharpDisasmLock = new object();
        static Assembly sharpDisasm;

        static SteamlessUnpacker()
        {
            // The 2.x unpackers use SharpDisasm, which upstream ships only as a binary. It is embedded in this
            // exe as a deflated resource and handed to the runtime the first time a 2.x unpacker needs it.
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                if (new AssemblyName(e.Name).Name != "SharpDisasm") return null;
                lock (sharpDisasmLock)
                {
                    if (sharpDisasm != null) return sharpDisasm;
                    using (var res = typeof(SteamlessUnpacker).Assembly.GetManifestResourceStream("SharpDisasm.dll.deflate"))
                    {
                        if (res == null) return null;
                        var ms = new MemoryStream();
                        using (var inflate = new System.IO.Compression.DeflateStream(res, System.IO.Compression.CompressionMode.Decompress))
                            inflate.CopyTo(ms);
                        return sharpDisasm = Assembly.Load(ms.ToArray());
                    }
                }
            };
        }

        /// <summary>Fresh unpacker instances (they keep per-file state, and batch runs unpack in parallel), in
        /// the order Steamless.CLI tries them - sorted by name - limited to the file's bitness. Every upstream
        /// CanProcessFile rejects the other bitness with the same Machine test, so skipping them up front
        /// changes nothing but the number of times the file is read.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]   // must not be JIT-compiled before the resolver above is registered
        static List<SteamlessPlugin> Unpackers(bool is64)
        {
            var all = is64
                ? new SteamlessPlugin[] { new Steamless.Unpacker.Variant30.x64.Main(), new Steamless.Unpacker.Variant31.x64.Main() }
                : new SteamlessPlugin[]
                {
                    new Steamless.Unpacker.Variant10.x86.Main(), new Steamless.Unpacker.Variant20.x86.Main(),
                    new Steamless.Unpacker.Variant21.x86.Main(), new Steamless.Unpacker.Variant30.x86.Main(),
                    new Steamless.Unpacker.Variant31.x86.Main()
                };
            return all.OrderBy(p => p.Name).ToList();
        }

        /// <summary>Whether the file has a ".bind" section - where every SteamStub variant keeps its stub -
        /// reading only the PE headers and section table, never the whole file. Null when the file cannot be
        /// read or is not a PE, so callers can fall back to a full check.</summary>
        public static bool? HasStubSection(string filePath)
        {
            var names = ReadSectionNames(filePath);
            if (names == null) return null;
            foreach (var n in names) if (n == ".bind") return true;
            return false;
        }

        /// <summary>Section names from the PE section table, reading only the headers. Null when the file
        /// cannot be read or is not a PE.</summary>
        public static List<string> ReadSectionNames(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                {
                    var head = new byte[(int)Math.Min(fs.Length, 0x400)];
                    if (ReadFully(fs, head) < 0x40 || PeImage.ReadUInt16(head, 0) != 0x5A4D) return null;
                    int nt = PeImage.ReadInt32(head, 0x3C);
                    if (nt < 0 || (long)nt + 24 > fs.Length) return null;
                    var file = new byte[24];
                    fs.Position = nt;
                    if (ReadFully(fs, file) != 24 || PeImage.ReadUInt32(file, 0) != 0x00004550) return null;
                    int count = PeImage.ReadUInt16(file, 6);
                    long table = (long)nt + 24 + PeImage.ReadUInt16(file, 20);
                    if (count == 0 || count > 96 || table + count * 40L > fs.Length) return null;
                    var sections = new byte[count * 40];
                    fs.Position = table;
                    if (ReadFully(fs, sections) != sections.Length) return null;
                    var names = new List<string>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int len = 0;
                        while (len < 8 && sections[i * 40 + len] != 0) len++;
                        names.Add(Encoding.ASCII.GetString(sections, i * 40, len));
                    }
                    return names;
                }
            }
            catch { return null; }
        }

        static int ReadFully(Stream s, byte[] buffer)
        {
            int total = 0, n;
            while (total < buffer.Length && (n = s.Read(buffer, total, buffer.Length - total)) > 0) total += n;
            return total;
        }

        /// <summary>Unpacks with the first Steamless unpacker that claims the file, exactly as Steamless.CLI
        /// would, but writes nothing: <see cref="UnpackResult.Output"/> holds the rebuilt executable. Unpacker
        /// log lines go to <paramref name="log"/>.</summary>
        public static UnpackResult UnpackToMemory(string sourcePath, Action<string> log = null)
        {
            var result = new UnpackResult();
            byte[] input;
            PeImage packed;
            try
            {
                input = File.ReadAllBytes(sourcePath);
                packed = new PeImage(input);
            }
            catch (Exception ex)
            {
                result.ErrorCode = UnpackErrorCode.InvalidInput;
                result.Error = "Input is not a readable PE file: " + ex.Message;
                return result;
            }
            result.SourceSha256 = Sha256Hex(input);

            var logService = new LoggingService();
            logService.AddLogMessage += (sender, e) =>
            {
                // The output never reaches disk under that name, so do not claim it did.
                if (log != null && e.Message.IndexOf("File Saved As:", StringComparison.Ordinal) < 0)
                    log(e.Message.Trim());
            };

            foreach (var unpacker in Unpackers(packed.Is64))
            {
                unpacker.Initialize(logService);
                bool claimed = unpacker.CanProcessFile(sourcePath);
                // Upstream probes and unpacks by re-reading and copying the whole file each time; on a
                // multi-hundred-MB exe that is gigabytes of large-object garbage the GC would otherwise let
                // pile up before the next copy. Measured on a 248 MB exe: peak working set 3.3 GB -> 1.5 GB.
                // ponytail: upstream still holds ~4 copies while unpacking; a Pe32File/Pe64File-from-bytes
                // patch to the fork would cut that further if large exes ever hit memory limits.
                GC.Collect();
                if (!claimed) continue;

                result.Unpacker = unpacker.Name;
                var sink = new MemoryStream(input.Length);   // the output is about the input's size: no regrowth
                // Same defaults Steamless.CLI runs with; only the output destination differs.
                var options = new SteamlessOptions { OutputStreamFactory = path => sink };
                bool ok;
                try { ok = unpacker.ProcessFile(sourcePath, options); }
                catch (Exception ex) { ok = false; if (log != null) log(unpacker.Name + " crashed: " + ex.Message); }
                if (!ok)
                {
                    // Steamless.CLI moves on to the next unpacker that claims the file; so do we.
                    result.ErrorCode = UnpackErrorCode.UnpackFailed;
                    result.Error = unpacker.Name + " failed to unpack the file.";
                    continue;
                }

                GC.Collect();
                byte[] output = sink.ToArray();   // valid after the unpacker disposed the stream
                sink = null;
                try
                {
                    RelocateCertificate(packed, output);
                    ValidateOutput(output);
                }
                catch (InvalidDataException ex)
                {
                    result.ErrorCode = UnpackErrorCode.OutputValidationFailed;
                    result.Error = ex.Message;
                    return result;
                }
                result.Output = output;
                result.Success = true;
                result.ErrorCode = UnpackErrorCode.None;
                result.Error = null;
                return result;
            }

            if (result.Unpacker == null)
            {
                result.ErrorCode = UnpackErrorCode.UnsupportedVariant;
                result.Error = "No Steamless unpacker recognised the SteamStub variant.";
            }
            return result;
        }

        /// <summary>The certificate table (data directory 4) is addressed by FILE offset, normally into the
        /// overlay. Dropping .bind moves the overlay, and Steamless leaves the pointer aimed at the old offset.
        /// Shift it to where the certificate bytes actually landed - and only if they are verifiably there.
        /// (The signature no longer verifies either way; this keeps the file self-consistent.)</summary>
        static void RelocateCertificate(PeImage packed, byte[] output)
        {
            int delta = output.Length - packed.Data.Length;
            int dirCount = (int)PeImage.ReadUInt32(packed.Data, packed.OptionalOffset + (packed.Is64 ? 108 : 92));
            int certField = packed.OptionalOffset + (packed.Is64 ? 112 : 96) + 4 * 8;
            if (delta == 0 || dirCount <= 4 || certField + 8 > output.Length) return;
            uint certOffset = PeImage.ReadUInt32(output, certField);
            uint certSize = PeImage.ReadUInt32(output, certField + 4);
            long moved = (long)certOffset + delta;
            if (certSize == 0 || certOffset < packed.OverlayStart || (long)certOffset + certSize > packed.Data.Length
                || moved < 0 || moved + certSize > output.Length)
                return;
            for (int i = 0; i < certSize; i++)
                if (output[moved + i] != packed.Data[certOffset + i]) return;
            PeImage.WriteUInt32(output, certField, (uint)moved);
        }

        /// <summary>Cheap semantic checks on the rebuilt image. Parsing as a PE is not enough: a section-rebuild
        /// mistake still parses, then crashes at launch. The entry point must land in code, and no stub
        /// section may survive.</summary>
        static void ValidateOutput(byte[] output)
        {
            PeImage rebuilt;
            try { rebuilt = new PeImage(output); }
            catch (Exception ex) { throw new InvalidDataException("Unpacked output is not a valid PE: " + ex.Message); }
            if (rebuilt.Sections.Any(s => s.Name.Equals(".bind", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Unpacked output still contains the .bind stub section.");
            var entry = rebuilt.Sections.FirstOrDefault(s =>
                rebuilt.EntryPoint >= s.VirtualAddress && rebuilt.EntryPoint < s.VirtualAddress + (ulong)(s.VirtualSize != 0 ? s.VirtualSize : s.SizeOfRawData));
            if (entry == null)
                throw new InvalidDataException("Restored entry point 0x" + rebuilt.EntryPoint.ToString("X") + " is outside every section.");
            const uint Code = 0x00000020, Execute = 0x20000000;
            if ((entry.Characteristics & (Code | Execute)) == 0)
                throw new InvalidDataException("Restored entry point lands in non-executable section " + entry.Name + ".");
        }

        static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        sealed class PeSection
        {
            public string Name;
            public uint VirtualSize, VirtualAddress, SizeOfRawData, PointerToRawData, Characteristics;
        }

        /// <summary>Just enough of a PE reader for the guards above.</summary>
        sealed class PeImage
        {
            public readonly byte[] Data;
            public readonly int OptionalOffset;
            public readonly bool Is64;
            public readonly uint EntryPoint;
            public readonly List<PeSection> Sections = new List<PeSection>();

            /// <summary>Where section data ends and the overlay begins: the furthest raw end of any section. The
            /// section table need not be sorted by file offset, so the last entry is not necessarily it.</summary>
            public long OverlayStart
            {
                get { return Sections.Where(s => s.SizeOfRawData > 0).Select(s => (long)s.PointerToRawData + s.SizeOfRawData).DefaultIfEmpty(0).Max(); }
            }

            public PeImage(byte[] data)
            {
                Data = data;
                if (Data.Length < 0x40 || ReadUInt16(Data, 0) != 0x5A4D)
                    throw new InvalidDataException("Missing MZ signature.");
                int nt = ReadInt32(Data, 0x3C);
                if (nt < 0 || nt + 24 > Data.Length || ReadUInt32(Data, nt) != 0x00004550)
                    throw new InvalidDataException("Missing PE signature.");
                ushort machine = ReadUInt16(Data, nt + 4);
                int count = ReadUInt16(Data, nt + 6);
                int optionalSize = ReadUInt16(Data, nt + 20);
                OptionalOffset = nt + 24;
                Is64 = machine == 0x8664;
                if ((!Is64 && machine != 0x014C) || optionalSize < (Is64 ? 0x70 : 0x60) || OptionalOffset + optionalSize > Data.Length
                    || ReadUInt16(Data, OptionalOffset) != (Is64 ? (ushort)0x20B : (ushort)0x10B))
                    throw new InvalidDataException("Unsupported PE format.");
                EntryPoint = ReadUInt32(Data, OptionalOffset + 16);
                int table = OptionalOffset + optionalSize;
                if (count == 0 || count > 96 || table + count * 40 > Data.Length)
                    throw new InvalidDataException("Invalid section table.");
                for (int i = 0; i < count; i++)
                {
                    int o = table + i * 40, len = 0;
                    while (len < 8 && Data[o + len] != 0) len++;
                    var s = new PeSection
                    {
                        Name = Encoding.ASCII.GetString(Data, o, len),
                        VirtualSize = ReadUInt32(Data, o + 8),
                        VirtualAddress = ReadUInt32(Data, o + 12),
                        SizeOfRawData = ReadUInt32(Data, o + 16),
                        PointerToRawData = ReadUInt32(Data, o + 20),
                        Characteristics = ReadUInt32(Data, o + 36)
                    };
                    if (s.SizeOfRawData > 0 && (ulong)s.PointerToRawData + s.SizeOfRawData > (ulong)Data.Length)
                        throw new InvalidDataException("Section data is outside the file.");
                    Sections.Add(s);
                }
            }

            public static ushort ReadUInt16(byte[] data, int offset) { return (ushort)(data[offset] | (data[offset + 1] << 8)); }
            public static uint ReadUInt32(byte[] data, int offset) { return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24)); }
            public static int ReadInt32(byte[] data, int offset) { return unchecked((int)ReadUInt32(data, offset)); }
            public static void WriteUInt32(byte[] data, int offset, uint value)
            {
                data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8);
                data[offset + 2] = (byte)(value >> 16); data[offset + 3] = (byte)(value >> 24);
            }
        }
    }
}
