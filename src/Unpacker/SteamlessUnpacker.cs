using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SteamlessNative
{
    public enum UnpackErrorCode
    {
        None = 0,
        InvalidInput,
        UnsupportedVariant,
        InvalidHeader,
        InvalidPayload,
        InvalidSteamDrmp,
        InvalidCodeSection,
        CodeDecryptionFailed,
        InvalidDestination,
        OutputValidationFailed,
        IoError,
        UnexpectedError
    }

    public sealed class VariantInfo
    {
        public string Name { get; internal set; }
        public bool IsX64 { get; internal set; }
        public int HeaderSize { get; internal set; }
        public uint Flags { get; internal set; }
        public uint SteamAppId { get; internal set; }
        public uint OriginalEntryPoint { get; internal set; }
        public uint BindSectionVirtualSize { get; internal set; }
        public uint BindSectionRawSize { get { return BindSectionVirtualSize; } }
        public bool UsesTlsOep { get; internal set; }
    }

    public sealed class UnpackOptions
    {
        public bool KeepBindSection { get; set; }
        public bool ZeroDosStubData { get; set; }
        public bool RecalculateChecksum { get; set; }
        public string DumpPayloadPath { get; set; }
        public string DumpSteamDrmpPath { get; set; }

        public UnpackOptions()
        {
            ZeroDosStubData = true;
        }
    }

    public sealed class UnpackResult
    {
        public bool Success { get; internal set; }
        public UnpackErrorCode ErrorCode { get; internal set; }
        public string Error { get; internal set; }
        public string DestinationPath { get; internal set; }
        public VariantInfo Variant { get; internal set; }
        public byte[] Payload { get; internal set; }
        public byte[] SteamDrmp { get; internal set; }

        /// <summary>The unpacked executable, set by <see cref="SteamlessUnpacker.UnpackToMemory"/>.</summary>
        public byte[] Output { get; internal set; }

        /// <summary>Lower-case hex SHA-256 of the packed input exactly as it was read - lets a caller prove
        /// the file on disk has not changed since, without hashing it twice.</summary>
        public string SourceSha256 { get; internal set; }

        internal UnpackResult()
        {
            Payload = new byte[0];
            SteamDrmp = new byte[0];
        }
    }

    public static class SteamlessUnpacker
    {
        public static VariantInfo Detect(string filePath)
        {
            PeImage image;
            VariantInfo info;
            if (!PeImage.TryLoad(filePath, out image) || !TryGetHeader(image, out info))
                return null;
            return info;
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

        public static UnpackResult Unpack(string sourcePath, string destinationPath)
        {
            return Unpack(sourcePath, destinationPath, new UnpackOptions());
        }

        /// <summary>Unpacks without writing the result anywhere: <see cref="UnpackResult.Output"/> holds the
        /// rebuilt executable. Lets the caller stream it straight into its own staged write instead of
        /// round-tripping through a temporary file.</summary>
        public static UnpackResult UnpackToMemory(string sourcePath, UnpackOptions options = null)
        {
            var result = new UnpackResult();
            options = options ?? new UnpackOptions();
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                result.ErrorCode = UnpackErrorCode.InvalidInput;
                result.Error = "Input path is empty.";
                return result;
            }
            try
            {
                if (PathsConflict(sourcePath, null, options.DumpPayloadPath, options.DumpSteamDrmpPath))
                {
                    result.ErrorCode = UnpackErrorCode.InvalidDestination;
                    result.Error = "Input and dump paths must be distinct.";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.ErrorCode = UnpackErrorCode.InvalidDestination;
                result.Error = "Dump path is invalid: " + ex.Message;
                return result;
            }
            UnpackCore(sourcePath, options, result);
            return result;
        }

        public static UnpackResult Unpack(string sourcePath, string destinationPath, UnpackOptions options)
        {
            var result = new UnpackResult();
            options = options ?? new UnpackOptions();

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                result.ErrorCode = UnpackErrorCode.InvalidInput;
                result.Error = "Input path is empty.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                result.ErrorCode = UnpackErrorCode.InvalidDestination;
                result.Error = "Destination path is empty.";
                return result;
            }
            try
            {
                if (PathsConflict(sourcePath, destinationPath, options.DumpPayloadPath, options.DumpSteamDrmpPath))
                {
                    result.ErrorCode = UnpackErrorCode.InvalidDestination;
                    result.Error = "Input, output, and dump paths must be distinct.";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.ErrorCode = UnpackErrorCode.InvalidDestination;
                result.Error = "Destination path is invalid: " + ex.Message;
                return result;
            }

            if (!UnpackCore(sourcePath, options, result)) return result;
            try
            {
                AtomicWrite(destinationPath, result.Output);
                result.DestinationPath = Path.GetFullPath(destinationPath);
                result.Output = null;   // the caller asked for a file; do not also pin the whole image in memory
                return result;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                result.Success = false;
                result.Output = null;
                result.ErrorCode = UnpackErrorCode.IoError;
                result.Error = "Output could not be written: " + ex.Message;
                return result;
            }
        }

        static bool UnpackCore(string sourcePath, UnpackOptions options, UnpackResult result)
        {
            PeImage image;
            if (!PeImage.TryLoad(sourcePath, out image))
            {
                result.ErrorCode = UnpackErrorCode.InvalidInput;
                result.Error = "Input is not a valid supported PE file.";
                return false;
            }

            VariantInfo info;
            if (!TryGetHeader(image, out info))
            {
                result.ErrorCode = UnpackErrorCode.UnsupportedVariant;
                result.Error = "SteamStub Variant 3.1 was not detected.";
                return false;
            }

            PeHeader header;
            if (!TryReadHeader(image, out header))
            {
                result.ErrorCode = UnpackErrorCode.InvalidHeader;
                result.Error = "SteamStub header could not be decoded.";
                return false;
            }

            result.Variant = info;
            byte[] payload;
            if (!TryReadPayload(image, header, out payload))
            {
                result.ErrorCode = UnpackErrorCode.InvalidPayload;
                result.Error = "Payload data is outside the input file.";
                return false;
            }
            result.Payload = payload;
            if (payload.Length > 0 && !string.IsNullOrEmpty(options.DumpPayloadPath))
            {
                try { AtomicWrite(options.DumpPayloadPath, payload); }
                catch (Exception ex)
                {
                    result.ErrorCode = UnpackErrorCode.IoError;
                    result.Error = "Payload dump could not be written: " + ex.Message;
                    return false;
                }
            }

            byte[] drmp;
            if (!TryReadSteamDrmp(image, header, out drmp))
            {
                result.ErrorCode = UnpackErrorCode.InvalidSteamDrmp;
                result.Error = "SteamDRMP data is outside the input file.";
                return false;
            }
            result.SteamDrmp = drmp;
            if (drmp.Length > 0 && !string.IsNullOrEmpty(options.DumpSteamDrmpPath))
            {
                try { AtomicWrite(options.DumpSteamDrmpPath, drmp); }
                catch (Exception ex)
                {
                    result.ErrorCode = UnpackErrorCode.IoError;
                    result.Error = "SteamDRMP dump could not be written: " + ex.Message;
                    return false;
                }
            }

            byte[] codeData = null;
            int codeIndex = -1;
            if ((header.Flags & 0x04u) == 0)
            {
                PeSection codeSection;
                if (!image.TryFindOwner(header.CodeSectionVirtualAddress, out codeSection))
                {
                    result.ErrorCode = UnpackErrorCode.InvalidCodeSection;
                    result.Error = "Encrypted code section was not found.";
                    return false;
                }
                codeIndex = image.IndexOf(codeSection);
                int codeLength = image.Is64 ? (int)codeSection.SizeOfRawData : (int)header.CodeSectionRawSize;
                if (codeLength <= 0 || codeLength > codeSection.SizeOfRawData)
                {
                    result.ErrorCode = UnpackErrorCode.InvalidCodeSection;
                    result.Error = "Encrypted code section size is invalid.";
                    return false;
                }
                byte[] combined = new byte[header.CodeSectionStolenData.Length + codeLength];
                Buffer.BlockCopy(header.CodeSectionStolenData, 0, combined, 0, header.CodeSectionStolenData.Length);
                Buffer.BlockCopy(image.Data, (int)codeSection.PointerToRawData, combined, header.CodeSectionStolenData.Length, codeLength);
                try
                {
                    // The header stores the IV encrypted: it has to be AES-ECB-decrypted with the same key before
                    // the CBC pass. Using it raw only corrupts the first 16-byte block - which is exactly the
                    // start of the code section, so the result parsed fine and crashed in its first function.
                    // Found by the --corpus differential run against Steamless on real games.
                    byte[] iv;
                    using (AesManaged ecb = new AesManaged())
                    {
                        ecb.Key = header.AesKey;
                        ecb.Mode = CipherMode.ECB;
                        ecb.Padding = PaddingMode.None;
                        using (ICryptoTransform d = ecb.CreateDecryptor())
                            iv = d.TransformFinalBlock(header.AesIv, 0, 16);
                    }
                    using (AesManaged aes = new AesManaged())
                    {
                        aes.Key = header.AesKey;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.None;
                        using (ICryptoTransform decryptor = aes.CreateDecryptor())
                            codeData = decryptor.TransformFinalBlock(combined, 0, combined.Length);
                    }
                }
                catch
                {
                    result.ErrorCode = UnpackErrorCode.CodeDecryptionFailed;
                    result.Error = "Code section decryption failed.";
                    return false;
                }
            }

            try
            {
                byte[] output = BuildUnpacked(image, header, options, codeIndex, codeData);
                if (options.RecalculateChecksum)
                    UpdateChecksum(output, image.OptionalOffset + 64);
                ValidateOutput(output, options);
                result.Output = output;
                result.SourceSha256 = Sha256Hex(image.Data);
                result.Success = true;
                result.ErrorCode = UnpackErrorCode.None;
                result.Error = null;
                return true;
            }
            catch (InvalidDataException ex)
            {
                result.ErrorCode = UnpackErrorCode.OutputValidationFailed;
                result.Error = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                result.ErrorCode = UnpackErrorCode.UnexpectedError;
                result.Error = ex.Message;
                return false;
            }
        }

        private static bool TryGetHeader(PeImage image, out VariantInfo info)
        {
            info = null;
            PeSection bind;
            if (!image.TryGetSection(".bind", out bind))
                return false;

            byte[] data = image.GetSectionData(bind);
            int limit = Math.Min(data.Length, 0x3000);
            int offset;
            int headerSize;
            if (image.Is64)
            {
                if (FindPattern(data, limit, new byte[] { 0xE8, 0, 0, 0, 0, 0x50, 0x53, 0x51, 0x52, 0x56, 0x57, 0x55, 0x41, 0x50 }, null) < 0)
                    return false;
                offset = FindPattern(data, limit, new byte[] { 0x48, 0x8D, 0x91, 0, 0, 0, 0, 0x48 }, new bool[] { true, true, true, false, false, false, false, true });
                if (offset < 0)
                    offset = FindPattern(data, limit, new byte[] { 0x48, 0x8D, 0x91, 0, 0, 0, 0, 0x41 }, new bool[] { true, true, true, false, false, false, false, true });
                if (offset < 0)
                {
                    offset = FindPattern(data, limit, new byte[] { 0x48, 0xC7, 0x84, 0x24, 0, 0, 0, 0, 0, 0, 0, 0, 0x48 }, new bool[] { true, true, true, true, false, false, false, false, false, false, false, false, true });
                    if (offset >= 0)   // a match at offset 0 is still a match
                        offset += 5;
                }
                if (offset < 0 || offset + 7 > limit)
                    return false;
                int rawHeaderSize = PeImage.ReadInt32(data, offset + 3);
                if (rawHeaderSize == int.MinValue)
                    return false;
                headerSize = Math.Abs(rawHeaderSize);
            }
            else
            {
                if (FindPattern(data, limit, new byte[] { 0xE8, 0, 0, 0, 0, 0x50, 0x53, 0x51, 0x52, 0x56, 0x57, 0x55, 0x8B, 0x44, 0x24, 0x1C, 0x2D, 0x05, 0, 0, 0, 0x8B, 0xCC, 0x83, 0xE4, 0xF0, 0x51, 0x51, 0x51, 0x50 }, null) < 0)
                    return false;
                int[] sizeOffsets = new int[] { 0x10, 0x16, 0x10 };
                byte[][] patterns = new byte[][]
                {
                    new byte[] { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0, 0, 0, 0, 0x53, 0, 0, 0, 0, 0, 0x68 },
                    new byte[] { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0, 0, 0, 0, 0x53, 0, 0, 0, 0, 0, 0x8D, 0x83 },
                    new byte[] { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0, 0, 0, 0, 0x56, 0, 0, 0, 0, 0, 0, 0, 0, 0x8D }
                };
                bool[][] masks = new bool[][]
                {
                    new bool[] { true, true, true, true, true, false, false, false, false, true, false, false, false, false, false, true },
                    new bool[] { true, true, true, true, true, false, false, false, false, true, false, false, false, false, false, true, true },
                    new bool[] { true, true, true, true, true, false, false, false, false, true, false, false, false, false, false, false, false, false, true }
                };
                offset = -1;
                headerSize = 0;
                for (int i = 0; i < patterns.Length; i++)
                {
                    int candidate = FindPattern(data, limit, patterns[i], masks[i]);
                    if (candidate < 0)
                        continue;
                    if ((long)candidate + sizeOffsets[i] + 4 > limit)
                        continue;
                    offset = candidate;
                    headerSize = PeImage.ReadInt32(data, candidate + sizeOffsets[i]);
                    break;
                }
                if (offset < 0 || headerSize != 0xF0)
                    return false;
            }
            if (headerSize != 0xF0)
                return false;

            PeHeader header;
            if (!TryReadHeader(image, out header))
                return false;

            info = new VariantInfo
            {
                Name = image.Is64 ? "SteamStub Variant 3.1 (x64)" : "SteamStub Variant 3.1 (x86)",
                IsX64 = image.Is64,
                HeaderSize = headerSize,
                Flags = header.Flags,
                SteamAppId = header.SteamAppId,
                OriginalEntryPoint = header.OriginalEntryPoint,
                BindSectionVirtualSize = header.BindSectionVirtualSize,
                UsesTlsOep = header.UsesTlsOep
            };
            return true;
        }

        private static bool TryReadHeader(PeImage image, out PeHeader header)
        {
            header = null;
            int entryOffset = image.RvaToOffset(image.EntryPoint);
            PeHeader candidate;
            if (TryReadHeaderAt(image, entryOffset, false, 0, out candidate))
            {
                header = candidate;
                return true;
            }

            foreach (ulong callback in image.TlsCallbacks)
            {
                ulong rva = callback - image.ImageBase;
                int offset = image.RvaToOffset(rva);
                if (TryReadHeaderAt(image, offset, true, rva, out candidate))
                {
                    header = candidate;
                    return true;
                }
            }
            return false;
        }

        private static bool TryReadHeaderAt(PeImage image, int offset, bool usesTls, ulong tlsOepRva, out PeHeader header)
        {
            header = null;
            if (offset < 0xF0 || offset > image.Data.Length)
                return false;
            byte[] raw = new byte[0xF0];
            Buffer.BlockCopy(image.Data, offset - 0xF0, raw, 0, raw.Length);
            uint rollingKey = SteamXor(raw, raw.Length, 0);
            if (PeImage.ReadUInt32(raw, 4) != 0xC0DEC0DF)
                return false;

            header = PeHeader.Parse(raw, rollingKey, usesTls, tlsOepRva);
            return true;
        }

        private static bool TryReadPayload(PeImage image, PeHeader header, out byte[] payload)
        {
            payload = new byte[0];
            ulong baseRva = header.UsesTlsOep ? header.TlsOepRva : image.EntryPoint;
            if (baseRva < header.BindSectionOffset)
                return false;
            ulong payloadRva = baseRva - header.BindSectionOffset;
            int offset = image.RvaToOffset(payloadRva);
            ulong size = ((ulong)header.PayloadSize + 15u) & ~15ul;
            if (size == 0)
                return true;
            if (offset < 0 || size > int.MaxValue || offset + (long)size > image.Data.Length)
                return false;
            payload = new byte[(int)size];
            Buffer.BlockCopy(image.Data, offset, payload, 0, payload.Length);
            SteamXor(payload, payload.Length, header.RollingXorKey);
            return true;
        }

        private static bool TryReadSteamDrmp(PeImage image, PeHeader header, out byte[] drmp)
        {
            drmp = new byte[0];
            if (header.DrmpDllSize == 0)
                return true;
            ulong baseRva = header.UsesTlsOep ? header.TlsOepRva : image.EntryPoint;
            if (baseRva < header.BindSectionOffset)
                return false;
            ulong drmpRva = baseRva - header.BindSectionOffset + header.DrmpDllOffset;
            int offset = image.RvaToOffset(drmpRva);
            if (offset < 0 || offset + (long)header.DrmpDllSize > image.Data.Length)
                return false;
            byte[] encrypted = new byte[header.DrmpDllSize];
            Buffer.BlockCopy(image.Data, offset, encrypted, 0, encrypted.Length);
            drmp = DecryptSteamDrmp(encrypted, header.EncryptionKeys);
            return true;
        }

        private static uint SteamXor(byte[] data, int size, uint key)
        {
            int offset = 0;
            if (key == 0)
            {
                offset = 4;
                key = PeImage.ReadUInt32(data, 0);
            }
            for (int i = offset; i + 4 <= size; i += 4)
            {
                uint value = PeImage.ReadUInt32(data, i);
                PeImage.WriteUInt32(data, i, value ^ key);
                key = value;
            }
            return key;
        }

        private static byte[] DecryptSteamDrmp(byte[] data, uint[] keys)
        {
            byte[] result = (byte[])data.Clone();
            uint v1 = 0x55555555u;
            uint v2 = 0x55555555u;
            unchecked
            {
                for (int i = 0; i + 8 <= result.Length; i += 8)
                {
                    uint d1 = PeImage.ReadUInt32(result, i);
                    uint d2 = PeImage.ReadUInt32(result, i + 4);
                    uint a = d1;
                    uint b = d2;
                    uint sum = unchecked(0x9E3779B9u * 32u);
                    for (int round = 0; round < 32; round++)
                    {
                        uint left = unchecked(((a << 4) ^ (a >> 5)) + a);
                        uint right = unchecked(sum + keys[(sum >> 11) & 3u]);
                        b = unchecked(b - (left ^ right));
                        sum = unchecked(sum - 0x9E3779B9u);
                        left = unchecked(((b << 4) ^ (b >> 5)) + b);
                        right = unchecked(sum + keys[sum & 3u]);
                        a = unchecked(a - (left ^ right));
                    }
                    PeImage.WriteUInt32(result, i, a ^ v1);
                    PeImage.WriteUInt32(result, i + 4, b ^ v2);
                    v1 = d1;
                    v2 = d2;
                }
            }
            return result;
        }

        private static int FindPattern(byte[] data, int limit, byte[] pattern, bool[] mask)
        {
            if (limit > data.Length)
                limit = data.Length;
            for (int i = 0; i + pattern.Length <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (mask == null || mask[j])
                    {
                        if (data[i + j] != pattern[j])
                        {
                            match = false;
                            break;
                        }
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        private static byte[] BuildUnpacked(PeImage image, PeHeader header, UnpackOptions options, int codeIndex, byte[] codeData)
        {
            List<PeSection> sections = new List<PeSection>();
            for (int i = 0; i < image.Sections.Count; i++)
            {
                if (!options.KeepBindSection && image.Sections[i].Name.Equals(".bind", StringComparison.OrdinalIgnoreCase))
                    continue;
                sections.Add(image.Sections[i]);
            }
            if (sections.Count == 0)
                throw new InvalidDataException("No output sections remain.");

            int firstRaw = int.MaxValue;
            int maxEnd = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                PeSection section = sections[i];
                if (section.PointerToRawData > 0)
                    firstRaw = Math.Min(firstRaw, (int)section.PointerToRawData);
                int end = checked((int)section.PointerToRawData + (int)section.SizeOfRawData);
                maxEnd = Math.Max(maxEnd, end);
                if (codeIndex >= 0 && image.Sections[codeIndex] == section && codeData != null)
                {
                    int codeEnd = image.Is64 ? codeData.Length : (int)section.SizeOfRawData;
                    maxEnd = Math.Max(maxEnd, checked((int)section.PointerToRawData + codeEnd));
                }
            }
            if (firstRaw == int.MaxValue)
                firstRaw = image.SectionTableOffset;

            int originalOverlayStart = image.LastSectionEnd;
            int overlayLength = Math.Max(0, image.Data.Length - originalOverlayStart);
            int overlayStart = maxEnd;
            byte[] output = new byte[checked(overlayStart + overlayLength)];
            Buffer.BlockCopy(image.Data, 0, output, 0, Math.Min(firstRaw, image.Data.Length));
            int headerEnd = checked(image.SectionTableOffset + sections.Count * 40);
            if (headerEnd < firstRaw)
                Array.Clear(output, headerEnd, firstRaw - headerEnd);
            if (options.ZeroDosStubData && image.NtOffset > 0x40)
                Array.Clear(output, 0x40, image.NtOffset - 0x40);

            PeImage.WriteUInt16(output, checked(image.NtOffset + 6), (ushort)sections.Count);
            PeImage.WriteUInt32(output, checked(image.OptionalOffset + 16), header.OriginalEntryPoint);
            PeImage.WriteUInt32(output, checked(image.OptionalOffset + 64), 0);
            uint sizeOfImage = image.Align((uint)(sections[sections.Count - 1].VirtualAddress + sections[sections.Count - 1].VirtualSize), image.SectionAlignment);
            PeImage.WriteUInt32(output, checked(image.OptionalOffset + 56), sizeOfImage);

            for (int i = 0; i < sections.Count; i++)
            {
                PeSection section = sections[i];
                Buffer.BlockCopy(image.Data, section.HeaderOffset, output, checked(image.SectionTableOffset + i * 40), 40);
                int sourceIndex = image.IndexOf(section);
                if (codeIndex >= 0 && sourceIndex == codeIndex && codeData != null)
                {
                    if (image.Is64)
                        Buffer.BlockCopy(codeData, 0, output, (int)section.PointerToRawData, codeData.Length);
                    else
                    {
                        byte[] sectionData = new byte[section.SizeOfRawData];
                        Buffer.BlockCopy(codeData, 0, sectionData, 0, Math.Min(codeData.Length, sectionData.Length));
                        Buffer.BlockCopy(sectionData, 0, output, (int)section.PointerToRawData, sectionData.Length);
                    }
                }
                else if (section.SizeOfRawData > 0)
                    Buffer.BlockCopy(image.Data, (int)section.PointerToRawData, output, (int)section.PointerToRawData, (int)section.SizeOfRawData);
            }

            if (overlayLength > 0)
                Buffer.BlockCopy(image.Data, originalOverlayStart, output, overlayStart, overlayLength);

            // The certificate table (data directory 4) is addressed by FILE offset, normally into the overlay.
            // Dropping .bind moves the overlay, so shift the pointer with it rather than leave it aimed at the
            // wrong bytes. (The signature no longer verifies either way; this keeps the file self-consistent.)
            int dirBase = image.OptionalOffset + (image.Is64 ? 112 : 96);
            int dirCount = (int)PeImage.ReadUInt32(output, image.OptionalOffset + (image.Is64 ? 108 : 92));
            if (dirCount > 4 && overlayStart != originalOverlayStart)
            {
                uint certOffset = PeImage.ReadUInt32(output, dirBase + 4 * 8);
                uint certSize = PeImage.ReadUInt32(output, dirBase + 4 * 8 + 4);
                if (certSize != 0 && certOffset >= (uint)originalOverlayStart)
                    PeImage.WriteUInt32(output, dirBase + 4 * 8, (uint)(certOffset - originalOverlayStart + overlayStart));
            }
            return output;
        }

        /// <summary>Cheap semantic checks on the rebuilt image. Parsing as a PE is not enough: a section-rebuild
        /// mistake still parses, then crashes at launch. The entry point must land in code, and no stub
        /// section may survive (unless asked to keep it).</summary>
        private static void ValidateOutput(byte[] output, UnpackOptions options)
        {
            PeImage rebuilt;
            try { rebuilt = new PeImage(output); }
            catch (Exception ex) { throw new InvalidDataException("Unpacked output is not a valid PE: " + ex.Message); }
            PeSection bind;
            if (!options.KeepBindSection && rebuilt.TryGetSection(".bind", out bind))
                throw new InvalidDataException("Unpacked output still contains the .bind stub section.");
            PeSection entry;
            if (!rebuilt.TryFindOwner(rebuilt.EntryPoint, out entry))
                throw new InvalidDataException("Restored entry point 0x" + rebuilt.EntryPoint.ToString("X") + " is outside every section.");
            const uint Code = 0x00000020, Execute = 0x20000000;
            if ((entry.Characteristics & (Code | Execute)) == 0)
                throw new InvalidDataException("Restored entry point lands in non-executable section " + entry.Name + ".");
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathsConflict(string sourcePath, string destinationPath, string payloadPath, string steamDrmpPath)
        {
            bool hasDestination = !string.IsNullOrEmpty(destinationPath);
            if (hasDestination && PathsEqual(sourcePath, destinationPath))
                return true;
            if (!string.IsNullOrEmpty(payloadPath)
                && (PathsEqual(sourcePath, payloadPath) || (hasDestination && PathsEqual(destinationPath, payloadPath))))
                return true;
            if (!string.IsNullOrEmpty(steamDrmpPath)
                && (PathsEqual(sourcePath, steamDrmpPath) || (hasDestination && PathsEqual(destinationPath, steamDrmpPath))))
                return true;
            return !string.IsNullOrEmpty(payloadPath) && !string.IsNullOrEmpty(steamDrmpPath)
                && PathsEqual(payloadPath, steamDrmpPath);
        }

        private static void AtomicWrite(string path, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Output path is empty.", "path");
            string fullPath = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent))
                throw new IOException("Output path has no parent directory: " + fullPath);
            Directory.CreateDirectory(parent);
            string temporaryPath = Path.Combine(parent, "." + Path.GetFileName(fullPath) + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(temporaryPath, data);
                if (File.Exists(fullPath))
                    File.Replace(temporaryPath, fullPath, null);
                else
                    File.Move(temporaryPath, fullPath);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }

        private static void UpdateChecksum(byte[] data, int checksumOffset)
        {
            PeImage.WriteUInt32(data, checksumOffset, CalculateChecksum(data, checksumOffset));
        }

        private static uint CalculateChecksum(byte[] data, int checksumOffset)
        {
            ulong sum = 0;
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                if (i == checksumOffset || i == checksumOffset + 2)
                    continue;
                sum += BitConverter.ToUInt16(data, i);
                sum = (sum & 0xffffu) + (sum >> 16);
            }
            if ((data.Length & 1) != 0)
            {
                sum += data[data.Length - 1];
                sum = (sum & 0xffffu) + (sum >> 16);
            }
            sum = (sum & 0xffffu) + (sum >> 16);
            sum += (uint)data.Length;
            return (uint)(sum & 0xffffu);
        }

        private sealed class PeHeader
        {
            public uint RollingXorKey;
            public uint Signature;
            public ulong ImageBase;
            public ulong AddressOfEntryPoint;
            public uint BindSectionOffset;
            public uint OriginalEntryPoint;
            public uint PayloadSize;
            public uint DrmpDllOffset;
            public uint DrmpDllSize;
            public uint SteamAppId;
            public uint Flags;
            public uint BindSectionVirtualSize;
            public ulong CodeSectionVirtualAddress;
            public ulong CodeSectionRawSize;
            public byte[] AesKey;
            public byte[] AesIv;
            public byte[] CodeSectionStolenData;
            public uint[] EncryptionKeys;
            public bool UsesTlsOep;
            public ulong TlsOepRva;

            public static PeHeader Parse(byte[] data, uint rollingKey, bool usesTls, ulong tlsOepRva)
            {
                return new PeHeader
                {
                    RollingXorKey = rollingKey,
                    Signature = PeImage.ReadUInt32(data, 4),
                    ImageBase = PeImage.ReadUInt64(data, 8),
                    AddressOfEntryPoint = PeImage.ReadUInt64(data, 16),
                    BindSectionOffset = PeImage.ReadUInt32(data, 24),
                    OriginalEntryPoint = PeImage.ReadUInt32(data, 32),
                    PayloadSize = PeImage.ReadUInt32(data, 44),
                    DrmpDllOffset = PeImage.ReadUInt32(data, 48),
                    DrmpDllSize = PeImage.ReadUInt32(data, 52),
                    SteamAppId = PeImage.ReadUInt32(data, 56),
                    Flags = PeImage.ReadUInt32(data, 60),
                    BindSectionVirtualSize = PeImage.ReadUInt32(data, 64),
                    CodeSectionVirtualAddress = PeImage.ReadUInt64(data, 72),
                    CodeSectionRawSize = PeImage.ReadUInt64(data, 80),
                    AesKey = PeImage.ReadBytes(data, 88, 32),
                    AesIv = PeImage.ReadBytes(data, 120, 16),
                    CodeSectionStolenData = PeImage.ReadBytes(data, 136, 16),
                    EncryptionKeys = new uint[] { PeImage.ReadUInt32(data, 152), PeImage.ReadUInt32(data, 156), PeImage.ReadUInt32(data, 160), PeImage.ReadUInt32(data, 164) },
                    UsesTlsOep = usesTls,
                    TlsOepRva = tlsOepRva
                };
            }
        }

        private sealed class PeSection
        {
            public string Name;
            public uint VirtualSize;
            public uint VirtualAddress;
            public uint SizeOfRawData;
            public uint PointerToRawData;
            public uint Characteristics;
            public int HeaderOffset;

            public byte[] GetData(byte[] fileData)
            {
                byte[] data = new byte[SizeOfRawData];
                if (SizeOfRawData > 0)
                    Buffer.BlockCopy(fileData, (int)PointerToRawData, data, 0, (int)SizeOfRawData);
                return data;
            }
        }

        private sealed class PeImage
        {
            public byte[] Data;
            public int NtOffset;
            public int OptionalOffset;
            public int SectionTableOffset;
            public ushort Machine;
            public ushort NumberOfSections;
            public bool Is64;
            public uint EntryPoint;
            public ulong ImageBase;
            public uint SectionAlignment;
            public uint FileAlignment;
            public List<PeSection> Sections = new List<PeSection>();
            public List<ulong> TlsCallbacks = new List<ulong>();

            /// <summary>Where section data ends and the overlay begins: the furthest raw end of any section. The
            /// section table need not be sorted by file offset, so the last entry is not necessarily it.</summary>
            public int LastSectionEnd
            {
                get
                {
                    long end = 0;
                    foreach (PeSection section in Sections)
                        if (section.SizeOfRawData > 0)
                            end = Math.Max(end, (long)section.PointerToRawData + section.SizeOfRawData);
                    return checked((int)end);
                }
            }

            public static bool TryLoad(string path, out PeImage image)
            {
                image = null;
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;
                try
                {
                    image = new PeImage(File.ReadAllBytes(path));
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public PeImage(byte[] data)
            {
                Data = data;
                if (Data.Length < 0x40 || ReadUInt16(Data, 0) != 0x5A4D)
                    throw new InvalidDataException("Missing MZ signature.");
                NtOffset = ReadInt32(Data, 0x3C);
                if (NtOffset < 0 || NtOffset + 24 > Data.Length || ReadUInt32(Data, NtOffset) != 0x00004550)
                    throw new InvalidDataException("Missing PE signature.");
                Machine = ReadUInt16(Data, NtOffset + 4);
                NumberOfSections = ReadUInt16(Data, NtOffset + 6);
                int optionalSize = ReadUInt16(Data, NtOffset + 20);
                OptionalOffset = NtOffset + 24;
                Is64 = Machine == 0x8664;
                if ((!Is64 && Machine != 0x014C) || optionalSize < (Is64 ? 0x70 : 0x60) || OptionalOffset + optionalSize > Data.Length || ReadUInt16(Data, OptionalOffset) != (Is64 ? (ushort)0x20B : (ushort)0x10B))
                    throw new InvalidDataException("Unsupported PE format.");
                EntryPoint = ReadUInt32(Data, OptionalOffset + 16);
                ImageBase = Is64 ? ReadUInt64(Data, OptionalOffset + 24) : ReadUInt32(Data, OptionalOffset + 28);
                SectionAlignment = ReadUInt32(Data, OptionalOffset + 32);
                FileAlignment = ReadUInt32(Data, OptionalOffset + 36);
                SectionTableOffset = OptionalOffset + optionalSize;
                if (NumberOfSections == 0 || NumberOfSections > 96 || SectionTableOffset + NumberOfSections * 40 > Data.Length)
                    throw new InvalidDataException("Invalid section table.");
                for (int i = 0; i < NumberOfSections; i++)
                {
                    int offset = SectionTableOffset + i * 40;
                    int nameLength = 0;
                    while (nameLength < 8 && Data[offset + nameLength] != 0)
                        nameLength++;
                    string name = Encoding.ASCII.GetString(Data, offset, nameLength);
                    PeSection section = new PeSection
                    {
                        Name = name,
                        VirtualSize = ReadUInt32(Data, offset + 8),
                        VirtualAddress = ReadUInt32(Data, offset + 12),
                        SizeOfRawData = ReadUInt32(Data, offset + 16),
                        PointerToRawData = ReadUInt32(Data, offset + 20),
                        Characteristics = ReadUInt32(Data, offset + 36),
                        HeaderOffset = offset
                    };
                    if (section.SizeOfRawData > 0 && (ulong)section.PointerToRawData + section.SizeOfRawData > (ulong)Data.Length)
                        throw new InvalidDataException("Section data is outside the file.");
                    Sections.Add(section);
                }
                ReadTlsCallbacks();
            }

            public int IndexOf(PeSection section)
            {
                return Sections.IndexOf(section);
            }

            public bool TryGetSection(string name, out PeSection section)
            {
                foreach (PeSection candidate in Sections)
                {
                    if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        section = candidate;
                        return true;
                    }
                }
                section = null;
                return false;
            }

            public byte[] GetSectionData(PeSection section)
            {
                return section.GetData(Data);
            }

            public int RvaToOffset(ulong rva)
            {
                if (rva > int.MaxValue)
                    return -1;
                foreach (PeSection section in Sections)
                {
                    uint size = section.VirtualSize != 0 ? section.VirtualSize : section.SizeOfRawData;
                    ulong start = section.VirtualAddress;
                    ulong end = start + size;
                    if (rva >= start && rva < end)
                    {
                        ulong delta = rva - start;
                        if (delta >= section.SizeOfRawData && section.SizeOfRawData != 0)
                            return -1;
                        long offset = (long)section.PointerToRawData + (long)delta;
                        return offset >= 0 && offset < Data.Length ? (int)offset : -1;
                    }
                }
                if (rva < (ulong)Data.Length)
                    return (int)rva;
                return -1;
            }

            public bool TryFindOwner(ulong rva, out PeSection section)
            {
                foreach (PeSection candidate in Sections)
                {
                    uint size = candidate.VirtualSize != 0 ? candidate.VirtualSize : candidate.SizeOfRawData;
                    if (rva >= candidate.VirtualAddress && rva < candidate.VirtualAddress + (ulong)size)
                    {
                        section = candidate;
                        return true;
                    }
                }
                section = null;
                return false;
            }

            public uint Align(uint value, uint alignment)
            {
                if (alignment == 0)
                    return value;
                return checked((value + alignment - 1) / alignment * alignment);
            }

            private void ReadTlsCallbacks()
            {
                try
                {
                    int directoryOffset = OptionalOffset + (Is64 ? 184 : 168);
                    uint tlsRva = ReadUInt32(Data, directoryOffset);
                    if (tlsRva == 0)
                        return;
                    int tlsOffset = RvaToOffset(tlsRva);
                    int callbackStructSize = Is64 ? 40 : 24;
                    if (tlsOffset < 0 || tlsOffset + callbackStructSize > Data.Length)
                        return;
                    ulong callbacksVa = Is64 ? ReadUInt64(Data, tlsOffset + 24) : ReadUInt32(Data, tlsOffset + 12);
                    if (callbacksVa < ImageBase)
                        return;
                    int callbacksOffset = RvaToOffset(callbacksVa - ImageBase);
                    int entrySize = Is64 ? 8 : 4;
                    if (callbacksOffset < 0)
                        return;
                    for (int i = 0; i < 1024 && callbacksOffset + (i + 1) * entrySize <= Data.Length; i++)
                    {
                        ulong callback = Is64 ? ReadUInt64(Data, callbacksOffset + i * entrySize) : ReadUInt32(Data, callbacksOffset + i * entrySize);
                        if (callback == 0)
                            break;
                        TlsCallbacks.Add(callback);
                    }
                }
                catch
                {
                }
            }

            public static ushort ReadUInt16(byte[] data, int offset)
            {
                return (ushort)(data[offset] | (data[offset + 1] << 8));
            }

            public static uint ReadUInt32(byte[] data, int offset)
            {
                return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            }

            public static int ReadInt32(byte[] data, int offset)
            {
                return unchecked((int)ReadUInt32(data, offset));
            }

            public static ulong ReadUInt64(byte[] data, int offset)
            {
                return (ulong)ReadUInt32(data, offset) | ((ulong)ReadUInt32(data, offset + 4) << 32);
            }

            public static byte[] ReadBytes(byte[] data, int offset, int size)
            {
                byte[] result = new byte[size];
                Buffer.BlockCopy(data, offset, result, 0, size);
                return result;
            }

            public static void WriteUInt16(byte[] data, int offset, ushort value)
            {
                data[offset] = (byte)value;
                data[offset + 1] = (byte)(value >> 8);
            }

            public static void WriteUInt32(byte[] data, int offset, uint value)
            {
                data[offset] = (byte)value;
                data[offset + 1] = (byte)(value >> 8);
                data[offset + 2] = (byte)(value >> 16);
                data[offset + 3] = (byte)(value >> 24);
            }
        }
    }
}
