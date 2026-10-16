using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Gp
{
    /// <summary>Reads the Steam client's product-info cache, Steam\appcache\appinfo.vdf: every app this PC has seen,
    /// with its name, DLC list and languages. Offline, no login. Formats v27/v28 (inline keys) and v29 (keys are
    /// indices into a string table at the end of the file). The file is indexed once per process and change
    /// (length + write time); only the records asked for are parsed.
    ///
    /// Layout: magic, universe, [v29: int64 string-table offset], then records - appid, size (bytes that follow),
    /// info state, last updated, PICS token (8), text SHA-1 (20), change number, [v28+: binary SHA-1 (20)],
    /// binary KeyValues - until an appid of 0.</summary>
    public static class SteamAppInfo
    {
        const uint V27 = 0x07564427, V28 = 0x07564428, V29 = 0x07564429;

        public sealed class App
        {
            public uint AppId;
            public string Name = "";
            /// <summary>extended\listofdlc, in Steam's order.</summary>
            public readonly List<uint> Dlc = new List<uint>();
            /// <summary>common\supported_languages: Steam API language codes ("english", "schinese", ...).</summary>
            public readonly List<string> Languages = new List<string>();
        }

#pragma warning disable 0649
        /// <summary>Test hook: read this file instead of the Steam client's.</summary>
        internal static string PathOverride;
#pragma warning restore 0649

        /// <summary>The Steam client's appinfo.vdf, or null when Steam is not installed.</summary>
        public static string FilePath
        {
            get
            {
                if (PathOverride != null) return PathOverride;
                string steam = SteamClient.InstallDir;
                return steam == null ? null : Path.Combine(steam, "appcache", "appinfo.vdf");
            }
        }

        sealed class Index
        {
            public string Path = "";
            public long Length;
            public DateTime WriteUtc;
            public string[] Strings;
            public bool HasBinarySha;
            /// <summary>appid → { offset of its KeyValues, their length }.</summary>
            public readonly Dictionary<uint, long[]> Records = new Dictionary<uint, long[]>();
        }

        static readonly object gate = new object();
        static Index cached;

        static FileStream Open(string path)
        {
            // Steam keeps the file open and rewrites it; share everything so neither side is blocked.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }

        static Index GetIndex(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            lock (gate)
            {
                if (cached != null && string.Equals(cached.Path, fi.FullName, StringComparison.OrdinalIgnoreCase)
                    && cached.Length == fi.Length && cached.WriteUtc == fi.LastWriteTimeUtc)
                    return cached;
                var index = Build(fi);
                cached = index;
                return index;
            }
        }

        static Index Build(FileInfo fi)
        {
            var index = new Index { Path = fi.FullName, Length = fi.Length, WriteUtc = fi.LastWriteTimeUtc };
            using (var fs = Open(fi.FullName))
            using (var br = new BinaryReader(fs))
            {
                uint magic = br.ReadUInt32();
                br.ReadUInt32();                                   // universe
                if (magic != V27 && magic != V28 && magic != V29)
                    throw new InvalidDataException("Unsupported appinfo.vdf version 0x" + magic.ToString("X8", CultureInfo.InvariantCulture) + ".");
                index.HasBinarySha = magic != V27;
                long end = fs.Length;
                if (magic == V29)
                {
                    long table = br.ReadInt64();
                    if (table < 16 || table > fs.Length) throw new InvalidDataException("appinfo.vdf string table offset is outside the file.");
                    end = table;
                    fs.Position = table;
                    uint count = br.ReadUInt32();
                    if (count > 10000000) throw new InvalidDataException("appinfo.vdf string table is implausibly large.");
                    var strings = new string[count];
                    var bytes = new List<byte>(64);
                    for (uint n = 0; n < count; n++)
                    {
                        bytes.Clear();
                        int b;
                        while ((b = fs.ReadByte()) > 0) bytes.Add((byte)b);
                        if (b < 0) throw new InvalidDataException("appinfo.vdf string table is truncated.");
                        strings[n] = Encoding.UTF8.GetString(bytes.ToArray());
                    }
                    index.Strings = strings;
                    fs.Position = 16;
                }
                int header = index.HasBinarySha ? 60 : 40;   // what follows the size field before the KeyValues
                while (fs.Position + 8 <= end)
                {
                    uint appId = br.ReadUInt32();
                    if (appId == 0) break;
                    long size = br.ReadUInt32();
                    long start = fs.Position;
                    if (size < header || start + size > end) throw new InvalidDataException("appinfo.vdf record for " + appId + " runs past the end.");
                    index.Records[appId] = new[] { start + header, size - header };
                    fs.Position = start + size;
                }
            }
            return index;
        }

        static AchievementSchema.Kv ReadRecord(Index index, uint appId)
        {
            long[] at;
            if (!index.Records.TryGetValue(appId, out at) || at[1] > int.MaxValue) return null;
            var data = new byte[at[1]];
            using (var fs = Open(index.Path))
            {
                fs.Position = at[0];
                int got = 0, n;
                while (got < data.Length && (n = fs.Read(data, got, data.Length - got)) > 0) got += n;
                if (got != data.Length) return null;   // the file shrank under us; the next call re-indexes
            }
            var root = AchievementSchema.ParseKv(data, 0, index.Strings);
            return AchievementSchema.Get(root, "appinfo") as AchievementSchema.Kv ?? root;
        }

        /// <summary>The app's cached info, or null when this PC's Steam has never seen it, Steam is not
        /// installed, or the file cannot be read.</summary>
        public static App TryGetApp(uint appId)
        {
            return TryGetApp(appId, FilePath);
        }

        public static App TryGetApp(string appId)
        {
            string id = AppIdDetector.Normalize(appId);
            return id.Length == 0 ? null : TryGetApp(uint.Parse(id, CultureInfo.InvariantCulture));
        }

        internal static App TryGetApp(uint appId, string vdfPath)
        {
            try
            {
                if (string.IsNullOrEmpty(vdfPath)) return null;
                var index = GetIndex(vdfPath);
                if (index == null) return null;
                var info = ReadRecord(index, appId);
                if (info == null) return null;
                var app = new App { AppId = appId };
                var common = AchievementSchema.Get(info, "common") as AchievementSchema.Kv;
                app.Name = ((AchievementSchema.Get(common, "name") as string) ?? "").Trim();
                var langs = AchievementSchema.Get(common, "supported_languages") as AchievementSchema.Kv;
                if (langs != null)
                    foreach (var l in langs)
                    {
                        var flags = l.Value as AchievementSchema.Kv;
                        string supported = flags == null ? null : AchievementSchema.Get(flags, "supported") as string;
                        if (supported != null && !string.Equals(supported, "true", StringComparison.OrdinalIgnoreCase) && supported != "1") continue;
                        string code = l.Key.Trim().ToLowerInvariant();
                        if (code.Length > 0 && !app.Languages.Contains(code)) app.Languages.Add(code);
                    }
                var extended = AchievementSchema.Get(info, "extended") as AchievementSchema.Kv;
                string dlc = AchievementSchema.Get(extended, "listofdlc") as string;
                if (!string.IsNullOrEmpty(dlc))
                    foreach (var part in dlc.Split(','))
                    {
                        uint id;
                        if (uint.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0 && !app.Dlc.Contains(id))
                            app.Dlc.Add(id);
                    }
                return app;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        /// <summary>The app's name from the cache, or null. Cheap after the first call (the index is kept).</summary>
        public static string GetName(string appId)
        {
            var app = TryGetApp(appId);
            return app == null || app.Name.Length == 0 ? null : app.Name;
        }
    }
}
