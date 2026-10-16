using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Gp
{
    /// <summary>Edits one key of one section of an ini file and leaves every other line - comments, blank lines,
    /// order, other sections' identical keys - exactly as it was. Sections and keys match case-insensitively,
    /// like the emulator's SimpleIni. Keeps the file's line ending and its UTF-8 BOM (if it had one).</summary>
    public sealed class IniFile
    {
        readonly List<string> lines;
        readonly string newline;
        readonly bool bom;

        IniFile(List<string> lines, string newline, bool bom)
        {
            this.lines = lines;
            this.newline = newline;
            this.bom = bom;
        }

        /// <summary>The file at <paramref name="path"/>, or an empty one when it does not exist.</summary>
        public static IniFile Load(string path)
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : Parse(new byte[0]);
        }

        public static IniFile Parse(byte[] data)
        {
            bool bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(data, bom ? 3 : 0, data.Length - (bom ? 3 : 0));
            int lf = text.IndexOf('\n');
            string nl = lf > 0 && text[lf - 1] == '\r' ? "\r\n" : lf >= 0 ? "\n" : "\r\n";
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);   // the final newline
            return new IniFile(lines, nl, bom);
        }

        public bool HasBom { get { return bom; } }

        static bool IsComment(string trimmed) { return trimmed.StartsWith("#") || trimmed.StartsWith(";"); }

        static string SectionName(string line)
        {
            string t = line.Trim();
            return t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']' ? t.Substring(1, t.Length - 2).Trim() : null;
        }

        static string KeyOf(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || IsComment(t) || SectionName(t) != null) return null;
            int eq = t.IndexOf('=');
            return eq <= 0 ? null : t.Substring(0, eq).Trim();
        }

        /// <summary>[first line, end) of the section's body; -1 when the section is absent. "" is the part
        /// before the first section header.</summary>
        void Range(string section, out int header, out int start, out int end)
        {
            header = -1; start = -1; end = -1;
            if (section.Length == 0) { start = 0; header = -1; }
            for (int i = 0; i < lines.Count; i++)
            {
                string name = SectionName(lines[i]);
                if (name == null) continue;
                if (start >= 0) { end = i; return; }
                if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) { header = i; start = i + 1; }
            }
            if (start >= 0) end = lines.Count;
        }

        public string Get(string section, string key)
        {
            int header, start, end;
            Range(section, out header, out start, out end);
            for (int i = start; i >= 0 && i < end; i++)
                if (string.Equals(KeyOf(lines[i]), key, StringComparison.OrdinalIgnoreCase))
                    return lines[i].Substring(lines[i].IndexOf('=') + 1).Trim();
            return null;
        }

        /// <summary>The section's key/value pairs, in file order.</summary>
        public List<KeyValuePair<string, string>> Entries(string section)
        {
            var list = new List<KeyValuePair<string, string>>();
            int header, start, end;
            Range(section, out header, out start, out end);
            for (int i = start; i >= 0 && i < end; i++)
            {
                string k = KeyOf(lines[i]);
                if (k != null) list.Add(new KeyValuePair<string, string>(k, lines[i].Substring(lines[i].IndexOf('=') + 1).Trim()));
            }
            return list;
        }

        /// <summary>Sets the key in place where it already is, otherwise adds it after the section's last line
        /// (before the blank lines that separate it from the next one); adds the section when missing.</summary>
        public void Set(string section, string key, string value)
        {
            if (key.IndexOfAny(new[] { '=', '\r', '\n' }) >= 0 || key.Trim().Length == 0) throw new ArgumentException("Invalid ini key: " + key);
            value = (value ?? "").Replace("\r", "").Replace("\n", "");
            int header, start, end;
            Range(section, out header, out start, out end);
            if (start < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length != 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
                return;
            }
            for (int i = start; i < end; i++)
            {
                string k = KeyOf(lines[i]);
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                string indent = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length);
                lines[i] = indent + k + "=" + value;
                return;
            }
            int at = end;
            while (at > start && lines[at - 1].Trim().Length == 0) at--;
            lines.Insert(at, key + "=" + value);
        }

        /// <summary>Removes every occurrence of the key in the section; true when there was one.</summary>
        public bool Remove(string section, string key)
        {
            int header, start, end;
            Range(section, out header, out start, out end);
            bool removed = false;
            for (int i = end - 1; i >= start && start >= 0; i--)
                if (string.Equals(KeyOf(lines[i]), key, StringComparison.OrdinalIgnoreCase)) { lines.RemoveAt(i); removed = true; }
            return removed;
        }

        public string Text { get { return lines.Count == 0 ? "" : string.Join(newline, lines.ToArray()) + newline; } }

        public byte[] ToBytes()
        {
            byte[] body = new UTF8Encoding(false).GetBytes(Text);
            return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
        }
    }

    /// <summary>Per-game emulator settings, the part of steam_settings\ people actually change: player name and
    /// language (configs.user.ini), DLC (configs.app.ini) and LAN peers (custom_broadcasts.txt). A value left
    /// blank is not written, so the emulator falls back to the global %APPDATA%\GSE Saves\settings one.</summary>
    public static class EmuSettings
    {
        /// <summary>Valve's API language codes (partner.steamgames.com/doc/store/localization/languages).</summary>
        public static readonly string[] ApiLanguages =
        {
            "arabic", "bulgarian", "schinese", "tchinese", "czech", "danish", "dutch", "english", "finnish", "french",
            "german", "greek", "hungarian", "indonesian", "italian", "japanese", "koreana", "norwegian", "polish",
            "portuguese", "brazilian", "romanian", "russian", "spanish", "latam", "swedish", "thai", "turkish",
            "ukrainian", "vietnamese",
        };

        public sealed class Target
        {
            public string Exe = "";
            /// <summary>The Steamworks library the patch installs into; "" when the game has none.</summary>
            public string Dll = "";
            public string SettingsDir = "";
            public bool IsEmulator;
            public bool OnlineFix;
            public string AppId = "";
        }

        /// <summary>Finds the steam_settings folder the emulator reads for this game, exactly the way the patch
        /// picks its install folder (SearchRoot → FindSteamApiFiles → PickApiTarget with the imported name).</summary>
        public static Target Locate(string exe)
        {
            var t = new Target { Exe = Path.GetFullPath(exe) };
            string root = PatchRunner.SearchRoot(t.Exe);
            var found = PatchRunner.FindSteamApiFiles(root);
            if (found.Count == 0) return t;
            string preferred = PatchRunner.ImportedSteamApiName(t.Exe);
            if (preferred == null)
            {
                ExeArch arch = ExeArch.Unknown;
                try { arch = PeReader.Analyze(t.Exe).Arch; } catch { }
                preferred = arch == ExeArch.X64 ? "steam_api64.dll" : "steam_api.dll";
            }
            t.Dll = PatchRunner.PickApiTarget(found, root, preferred);
            string dllDir = Path.GetDirectoryName(t.Dll);
            t.SettingsDir = Path.Combine(dllDir, "steam_settings");
            t.IsEmulator = InstallCheck.IsEmulatorDll(t.Dll);
            t.OnlineFix = !t.IsEmulator && InstallCheck.LooksLikeOnlineFix(t.Exe);
            t.AppId = PatchRunner.FindExistingAppId(dllDir, Path.GetDirectoryName(t.Exe));
            return t;
        }

        public sealed class DlcEntry
        {
            public uint Id;
            public string Name = "";
            public bool Enabled;
        }

        public sealed class Values
        {
            public string AccountName = "";
            public string Language = "";
            public bool UnlockAllDlc = true;
            public readonly List<DlcEntry> Dlc = new List<DlcEntry>();
            public readonly List<string> LanPeers = new List<string>();
        }

        const string UserIni = "configs.user.ini", AppIni = "configs.app.ini", Broadcasts = "custom_broadcasts.txt";

        /// <summary>The emulator's global settings folder, which every game falls back to.</summary>
        public static string GlobalSettingsDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves", "settings"); }
        }

        public static Values Load(string settingsDir)
        {
            var v = new Values();
            var user = IniFile.Load(Path.Combine(settingsDir, UserIni));
            v.AccountName = user.Get("user::general", "account_name") ?? "";
            v.Language = (user.Get("user::general", "language") ?? "").ToLowerInvariant();
            var app = IniFile.Load(Path.Combine(settingsDir, AppIni));
            string unlock = app.Get("app::dlcs", "unlock_all");
            v.UnlockAllDlc = unlock == null || !(unlock == "0" || string.Equals(unlock, "false", StringComparison.OrdinalIgnoreCase));
            foreach (var e in app.Entries("app::dlcs"))
            {
                uint id;
                if (TryParseDlcId(e.Key, out id) && !v.Dlc.Any(d => d.Id == id))
                    v.Dlc.Add(new DlcEntry { Id = id, Name = e.Value, Enabled = true });
            }
            string peers = Path.Combine(settingsDir, Broadcasts);
            if (File.Exists(peers))
                v.LanPeers.AddRange(File.ReadAllLines(peers).Select(l => l.Trim()).Where(l => l.Length > 0));
            return v;
        }

        /// <summary>The global player name and language (shown as what a blank field means).</summary>
        public static Values LoadGlobal(string globalDir = null)
        {
            var v = new Values();
            try
            {
                var user = IniFile.Load(Path.Combine(globalDir ?? GlobalSettingsDir, UserIni));
                v.AccountName = user.Get("user::general", "account_name") ?? "";
                v.Language = (user.Get("user::general", "language") ?? "").ToLowerInvariant();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return v;
        }

        /// <summary>Writes the values into the game's steam_settings, changing only these keys. Files that would
        /// only carry defaults are not created; custom_broadcasts.txt is removed when the list is emptied.</summary>
        /// <returns>The files written or removed.</returns>
        public static List<string> Save(string settingsDir, Values v)
        {
            var changed = new List<string>();
            Directory.CreateDirectory(settingsDir);

            string userPath = Path.Combine(settingsDir, UserIni);
            var user = IniFile.Load(userPath);
            string name = SanitizeName(v.AccountName);
            if (name.Length == 0) user.Remove("user::general", "account_name");
            else user.Set("user::general", "account_name", name);
            string lang = (v.Language ?? "").Trim().ToLowerInvariant();
            if (lang.Length == 0) user.Remove("user::general", "language");
            else if (Regex.IsMatch(lang, "^[a-z]+$")) user.Set("user::general", "language", lang);
            if (WriteIfDifferent(userPath, user, name.Length > 0 || lang.Length > 0)) changed.Add(userPath);

            string appPath = Path.Combine(settingsDir, AppIni);
            var app = IniFile.Load(appPath);
            foreach (var e in app.Entries("app::dlcs"))
            {
                uint id;
                if (TryParseDlcId(e.Key, out id)) app.Remove("app::dlcs", e.Key);
            }
            app.Set("app::dlcs", "unlock_all", v.UnlockAllDlc ? "1" : "0");
            var enabled = v.Dlc.Where(d => d.Enabled && d.Id != 0).GroupBy(d => d.Id).Select(g => g.First()).ToList();
            foreach (var d in enabled)
            {
                string dlcName = CleanDlcName(d.Name);
                app.Set("app::dlcs", d.Id.ToString(CultureInfo.InvariantCulture), dlcName.Length > 0 ? dlcName : "DLC " + d.Id);
            }
            if (WriteIfDifferent(appPath, app, !v.UnlockAllDlc || enabled.Count > 0)) changed.Add(appPath);

            string peersPath = Path.Combine(settingsDir, Broadcasts);
            var peers = v.LanPeers.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (peers.Count == 0)
            {
                if (File.Exists(peersPath)) { File.Delete(peersPath); changed.Add(peersPath); }
            }
            else
            {
                string text = string.Join("\r\n", peers.ToArray()) + "\r\n";
                if (!File.Exists(peersPath) || File.ReadAllText(peersPath) != text)
                {
                    WriteBytes(peersPath, new UTF8Encoding(false).GetBytes(text));
                    changed.Add(peersPath);
                }
            }
            return changed;
        }

        static bool WriteIfDifferent(string path, IniFile ini, bool createIfMissing)
        {
            byte[] bytes = ini.ToBytes();
            if (File.Exists(path))
            {
                if (File.ReadAllBytes(path).SequenceEqual(bytes)) return false;
            }
            else if (!createIfMissing) return false;
            WriteBytes(path, bytes);
            return true;
        }

        /// <summary>Atomic replace through SafePersistence; these edits are not part of any undo record, so the
        /// staging area is dropped straight away.</summary>
        static void WriteBytes(string path, byte[] bytes)
        {
            var record = SafePersistence.Write(path, s => s.Write(bytes, 0, bytes.Length));
            Recovery.Discard(new[] { record });
        }

        /// <summary>A player name the emulator accepts: what its Settings::sanitize removes (" &lt; &gt; | : * ?
        /// and control characters) is dropped here too, and the ends are trimmed.</summary>
        public static string SanitizeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
                if (c >= 0x20 && c != 0x7F && "\"<>|:*?".IndexOf(c) < 0) sb.Append(c);
            return sb.ToString().Trim();
        }

        /// <summary>A DLC name safe as an ini value: no line breaks, no '='.</summary>
        public static string CleanDlcName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
                if (c != '=' && c != '\r' && c != '\n' && c >= 0x20) sb.Append(c);
            return sb.ToString().Trim();
        }

        /// <summary>A DLC AppID: a decimal uint32 above zero.</summary>
        public static bool TryParseDlcId(string text, out uint id)
        {
            id = 0;
            text = (text ?? "").Trim();
            return text.Length > 0 && text.Length <= 10 && text.All(c => c >= '0' && c <= '9')
                && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
        }

        static readonly Regex HostLabel = new Regex("^(?!-)[A-Za-z0-9-]{1,63}(?<!-)$", RegexOptions.CultureInvariant);

        /// <summary>One custom_broadcasts.txt line: an IPv4 address or a host name, optionally ":port".</summary>
        public static bool IsValidPeer(string line)
        {
            string s = (line ?? "").Trim();
            if (s.Length == 0 || s.Length > 260 || s.Any(char.IsWhiteSpace)) return false;
            int colon = s.LastIndexOf(':');
            if (colon >= 0)
            {
                int port;
                string p = s.Substring(colon + 1);
                if (p.Length == 0 || p.Length > 5 || !p.All(char.IsDigit) || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) return false;
                s = s.Substring(0, colon);
            }
            if (s.Length == 0 || s.Length > 253) return false;
            var parts = s.Split('.');
            if (parts.All(x => x.Length > 0 && x.All(char.IsDigit)))
                return parts.Length == 4 && parts.All(x => x.Length <= 3 && int.Parse(x, CultureInfo.InvariantCulture) <= 255);
            return parts.All(x => HostLabel.IsMatch(x)) && !parts[parts.Length - 1].All(char.IsDigit);
        }

        /// <summary>The non-blank lines of <paramref name="text"/>, split into valid peers and the rest.</summary>
        public static List<string> ParsePeers(string text, out List<string> invalid)
        {
            var valid = new List<string>();
            invalid = new List<string>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (IsValidPeer(line)) { if (!valid.Contains(line, StringComparer.OrdinalIgnoreCase)) valid.Add(line); }
                else invalid.Add(line);
            }
            return valid;
        }

        public const int MaxStoreResponse = 1024 * 1024;

        /// <summary>The DLC the Steam store lists for the app (id + name), or null when the store could not be
        /// reached or answered nonsense. Only ever called when the user asks for it.</summary>
        public static List<DlcEntry> FetchStoreDlc(string appId, CancellationToken ct)
        {
            string id = AppIdDetector.Normalize(appId);
            if (id.Length == 0) return null;
            try
            {
                var req = Web.Request("https://store.steampowered.com/api/dlcforapp/?appid=" + id);
                req.Timeout = 4000;
                req.ReadWriteTimeout = 4000;
                using (ct.Register(() => req.Abort()))
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    if (resp.ContentLength > MaxStoreResponse) return null;
                    var text = new StringBuilder();
                    var buffer = new char[4096];
                    int read;
                    while ((read = sr.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (text.Length + read > MaxStoreResponse) return null;
                        text.Append(buffer, 0, read);
                    }
                    return ParseStoreDlc(text.ToString());
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { ct.ThrowIfCancellationRequested(); return null; }
        }

        /// <summary>{"status":1,"dlc":[{"id":123,"name":"..."},...]} → entries; null when it is not that shape.</summary>
        internal static List<DlcEntry> ParseStoreDlc(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxStoreResponse) return null;
            try
            {
                var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 32, MaxStringContentLength = MaxStoreResponse };
                XElement root;
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), quotas))
                    root = XElement.Load(reader);
                var status = root.Element("status");
                if (status == null || status.Value != "1") return null;
                var list = new List<DlcEntry>();
                var dlc = root.Element("dlc");
                if (dlc == null || (string)dlc.Attribute("type") != "array") return list;
                foreach (var item in dlc.Elements("item"))
                {
                    var idEl = item.Element("id");
                    uint id;
                    if (idEl == null || !TryParseDlcId(idEl.Value, out id) || list.Any(d => d.Id == id)) continue;
                    var nameEl = item.Element("name");
                    list.Add(new DlcEntry { Id = id, Name = CleanDlcName(nameEl == null ? "" : nameEl.Value) });
                }
                return list;
            }
            catch (XmlException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>The game's DLC from the local Steam cache, named where the cache knows the DLC ("DLC &lt;id&gt;"
        /// otherwise), plus the languages it supports. Null when Steam has never seen the game.</summary>
        public static SteamAppInfo.App LocalAppInfo(string appId, out List<DlcEntry> dlc)
        {
            dlc = new List<DlcEntry>();
            var app = SteamAppInfo.TryGetApp(appId);
            if (app == null) return null;
            foreach (var id in app.Dlc)
            {
                var d = SteamAppInfo.TryGetApp(id);
                string name = d != null && d.Name.Length > 0 ? CleanDlcName(d.Name) : "";
                dlc.Add(new DlcEntry { Id = id, Name = name.Length > 0 ? name : "DLC " + id });
            }
            return app;
        }
    }
}
