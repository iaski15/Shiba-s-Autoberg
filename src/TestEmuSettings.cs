using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gp;

static partial class TestMain
{
    /// <summary>Builds binary KeyValues for the appinfo.vdf tests: inline keys (v27/v28) or string-table indices (v29).</summary>
    sealed class KvWriter
    {
        readonly List<byte> b = new List<byte>();
        readonly List<string> table;
        public KvWriter(List<string> table) { this.table = table; }
        void Key(string k)
        {
            if (table == null) { b.AddRange(Encoding.UTF8.GetBytes(k)); b.Add(0); return; }
            int i = table.IndexOf(k);
            if (i < 0) { table.Add(k); i = table.Count - 1; }
            b.AddRange(BitConverter.GetBytes(i));
        }
        public KvWriter Open(string k) { b.Add(0); Key(k); return this; }
        public KvWriter Str(string k, string v) { b.Add(1); Key(k); b.AddRange(Encoding.UTF8.GetBytes(v)); b.Add(0); return this; }
        public KvWriter Int(string k, int v) { b.Add(2); Key(k); b.AddRange(BitConverter.GetBytes(v)); return this; }
        public KvWriter Close() { b.Add(8); return this; }
        public byte[] Bytes { get { return b.ToArray(); } }
    }

    /// <summary>A tiny appinfo.vdf: <paramref name="apps"/> maps appid → (name, listofdlc or null, languages).</summary>
    static void WriteAppInfo(string path, int version, IList<Tuple<uint, string, string, string[]>> apps)
    {
        var table = version >= 29 ? new List<string>() : null;
        var records = new List<byte>();
        foreach (var a in apps)
        {
            var kv = new KvWriter(table).Open("appinfo").Int("appid", (int)a.Item1).Open("common").Str("name", a.Item2).Str("type", "Game");
            if (a.Item4 != null)
            {
                kv.Open("supported_languages");
                foreach (var l in a.Item4)
                {
                    bool off = l.StartsWith("!");
                    kv.Open(off ? l.Substring(1) : l).Str("supported", off ? "false" : "true").Close();
                }
                kv.Close();
            }
            kv.Close();
            if (a.Item3 != null) kv.Open("extended").Str("listofdlc", a.Item3).Close();
            kv.Close().Close();   // appinfo, then the root
            byte[] data = kv.Bytes;
            var head = new List<byte>();
            head.AddRange(BitConverter.GetBytes(2u));                 // info state
            head.AddRange(BitConverter.GetBytes(1700000000u));        // last updated
            head.AddRange(BitConverter.GetBytes(0UL));                // PICS token
            head.AddRange(new byte[20]);                               // text SHA-1
            head.AddRange(BitConverter.GetBytes(12345u));             // change number
            if (version >= 28) head.AddRange(new byte[20]);            // binary SHA-1
            records.AddRange(BitConverter.GetBytes(a.Item1));
            records.AddRange(BitConverter.GetBytes((uint)(head.Count + data.Length)));
            records.AddRange(head);
            records.AddRange(data);
        }
        records.AddRange(BitConverter.GetBytes(0u));
        var file = new List<byte>();
        file.AddRange(BitConverter.GetBytes(version == 29 ? 0x07564429u : version == 28 ? 0x07564428u : 0x07564427u));
        file.AddRange(BitConverter.GetBytes(1u));
        if (version >= 29)
        {
            long tableAt = 16 + records.Count;
            file.AddRange(BitConverter.GetBytes(tableAt));
            file.AddRange(records);
            file.AddRange(BitConverter.GetBytes((uint)table.Count));
            foreach (var s in table) { file.AddRange(Encoding.UTF8.GetBytes(s)); file.Add(0); }
        }
        else file.AddRange(records);
        File.WriteAllBytes(path, file.ToArray());
    }

    /// <summary>Emulator settings form: IniFile, appinfo.vdf, DLC and LAN peer validation (feature C).</summary>
    static void SelfTestEmuSettings()
    {
        Console.WriteLine("\n[ini editing]");
        {
            string text = "# header comment\r\n[app::general]\r\nbranch_name=public\r\n\r\n[app::dlcs]\r\n# format: ID=name\r\nunlock_all=1\r\n1234=Old\r\n\r\n[app::paths]\r\n1234=./x\r\n";
            var ini = IniFile.Parse(Encoding.UTF8.GetBytes(text));
            Check(ini.Get("APP::DLCS", "Unlock_All") == "1" && ini.Get("app::paths", "1234") == "./x" && ini.Get("app::dlcs", "missing") == null,
                  "ini: lookups are per section and case-insensitive", null);
            ini.Set("app::dlcs", "1234", "New name");
            ini.Set("app::dlcs", "5678", "Second");
            Check(ini.Get("app::dlcs", "1234") == "New name" && ini.Get("app::paths", "1234") == "./x",
                  "ini: the same numeric key in another section is left alone", ini.Text);
            Check(ini.Text == "# header comment\r\n[app::general]\r\nbranch_name=public\r\n\r\n[app::dlcs]\r\n# format: ID=name\r\nunlock_all=1\r\n1234=New name\r\n5678=Second\r\n\r\n[app::paths]\r\n1234=./x\r\n",
                  "ini: comments, blank lines and order are kept; a new key goes after the section's last line", ini.Text.Replace("\r\n", "|"));
            ini.Set("user::general", "account_name", "Shiba");
            Check(ini.Text.EndsWith("1234=./x\r\n\r\n[user::general]\r\naccount_name=Shiba\r\n"), "ini: a missing section is appended", ini.Text.Replace("\r\n", "|"));
            Check(ini.Remove("app::dlcs", "1234") && !ini.Remove("app::dlcs", "1234") && ini.Get("app::paths", "1234") == "./x",
                  "ini: remove takes the key out of its own section only", null);
            Check(ini.Entries("app::dlcs").Select(e => e.Key).SequenceEqual(new[] { "unlock_all", "5678" }), "ini: entries list a section's keys in order", null);

            var bomIni = IniFile.Parse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[a]\nk=v\n")).ToArray());
            bomIni.Set("a", "k", "w");
            var bytes = bomIni.ToBytes();
            Check(bomIni.HasBom && bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) == "[a]\nk=w\n",
                  "ini: a UTF-8 BOM and LF line endings are kept", Encoding.UTF8.GetString(bytes));
            var plain = IniFile.Parse(Encoding.UTF8.GetBytes("[a]\r\nk=ü\r\n"));
            Check(!plain.HasBom && plain.ToBytes()[0] == (byte)'[' && plain.Get("a", "k") == "ü", "ini: no BOM is added, UTF-8 text survives", null);
            var empty = IniFile.Parse(new byte[0]);
            empty.Set("user::general", "language", "german");
            Check(empty.Text == "[user::general]\r\nlanguage=german\r\n", "ini: an empty file gets the section and key", empty.Text);
        }

        Console.WriteLine("\n[appinfo.vdf]");
        string work = Path.Combine(Path.GetTempPath(), "gp_selftest_emu_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        try
        {
            var apps = new List<Tuple<uint, string, string, string[]>>
            {
                Tuple.Create(1110910u, "Test Game", "1672210, 1672220,0,x", new[] { "english", "german", "!french" }),
                Tuple.Create(1672210u, "Season Pass", (string)null, (string[])null),
                Tuple.Create(42u, "Other", (string)null, (string[])null),
            };
            foreach (int version in new[] { 29, 28, 27 })
            {
                string vdf = Path.Combine(work, "appinfo_v" + version + ".vdf");
                WriteAppInfo(vdf, version, apps);
                var app = SteamAppInfo.TryGetApp(1110910u, vdf);
                Check(app != null && app.Name == "Test Game" && app.Dlc.SequenceEqual(new[] { 1672210u, 1672220u })
                      && app.Languages.SequenceEqual(new[] { "english", "german" }),
                      "appinfo v" + version + ": name, DLC list (junk dropped) and supported languages",
                      app == null ? "(null)" : app.Name + " | " + string.Join(",", app.Dlc) + " | " + string.Join(",", app.Languages.ToArray()));
                var dlc = SteamAppInfo.TryGetApp(1672210u, vdf);
                Check(dlc != null && dlc.Name == "Season Pass" && SteamAppInfo.TryGetApp(7u, vdf) == null,
                      "appinfo v" + version + ": another record by id, and an unknown id is null", null);
            }

            // the index follows the file: a rewritten cache is re-read
            string live = Path.Combine(work, "appinfo.vdf");
            WriteAppInfo(live, 29, apps);
            Check(SteamAppInfo.TryGetApp(42u, live).Name == "Other", "appinfo: first read", null);
            WriteAppInfo(live, 29, new[] { Tuple.Create(42u, "Renamed Game", (string)null, (string[])null) });
            File.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddMinutes(1));
            var renamed = SteamAppInfo.TryGetApp(42u, live);
            Check(renamed != null && renamed.Name == "Renamed Game", "appinfo: a changed file is indexed again", renamed == null ? "(null)" : renamed.Name);

            string bad = Path.Combine(work, "bad.vdf");
            File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            Check(SteamAppInfo.TryGetApp(42u, bad) == null && SteamAppInfo.TryGetApp(42u, Path.Combine(work, "none.vdf")) == null,
                  "appinfo: an unknown format or a missing file gives null", null);
            var truncated = File.ReadAllBytes(Path.Combine(work, "appinfo_v28.vdf"));
            File.WriteAllBytes(bad, truncated.Take(truncated.Length - 30).ToArray());
            Check(SteamAppInfo.TryGetApp(1110910u, bad) == null, "appinfo: a truncated file gives null instead of throwing", null);

            string prev = SteamAppInfo.PathOverride;
            SteamAppInfo.PathOverride = Path.Combine(work, "appinfo_v29.vdf");
            try
            {
                Check(SteamAppInfo.GetName("1672210") == "Season Pass" && SteamAppInfo.GetName("abc") == null && SteamAppInfo.GetName("7") == null,
                      "appinfo: GetName by AppID text", null);
                List<EmuSettings.DlcEntry> local;
                var info = EmuSettings.LocalAppInfo("1110910", out local);
                Check(info != null && local.Count == 2 && local[0].Name == "Season Pass" && local[1].Name == "DLC 1672220" && !local[0].Enabled,
                      "DLC from the local cache: named when Steam knows the DLC, \"DLC <id>\" otherwise, unticked",
                      string.Join(", ", local.Select(d => d.Id + "=" + d.Name).ToArray()));
            }
            finally { SteamAppInfo.PathOverride = prev; }

            // a smoke test against this PC's real cache, when there is one
            string real = SteamClient.InstallDir == null ? null : Path.Combine(SteamClient.InstallDir, "appcache", "appinfo.vdf");
            if (real != null && File.Exists(real))
            {
                var started = DateTime.UtcNow;
                var game = SteamAppInfo.TryGetApp(1110910u, real);
                Console.WriteLine("      | real appinfo.vdf: 1110910 = " + (game == null ? "(not in this PC's cache)"
                    : "\"" + game.Name + "\", " + game.Dlc.Count + " DLC" + (game.Dlc.Contains(1672210u) ? " incl. 1672210" : "")
                      + ", " + game.Languages.Count + " languages") + " in " + (DateTime.UtcNow - started).TotalMilliseconds.ToString("0") + " ms");
                Check(true, "appinfo: the real cache was read without throwing", null);
            }

            // ---- load / save round trip in a game's steam_settings
            Console.WriteLine("\n[emulator settings]");
            string ss = Path.Combine(work, "steam_settings");
            Directory.CreateDirectory(ss);
            File.WriteAllText(Path.Combine(ss, "configs.user.ini"), "# mine\r\n[user::general]\r\nip_country=DE\r\n");
            var v = new EmuSettings.Values { AccountName = "  Shiba<>|:*?\"Dog\t ", Language = "German", UnlockAllDlc = false };
            v.Dlc.Add(new EmuSettings.DlcEntry { Id = 1672210, Name = "Season=Pass\r\n", Enabled = true });
            v.Dlc.Add(new EmuSettings.DlcEntry { Id = 1672220, Name = "", Enabled = true });
            v.Dlc.Add(new EmuSettings.DlcEntry { Id = 99, Name = "Off", Enabled = false });
            v.LanPeers.AddRange(new[] { "26.12.0.5", " pc1:27015 ", "26.12.0.5" });
            var changed = EmuSettings.Save(ss, v);
            var back = EmuSettings.Load(ss);
            Check(back.AccountName == "ShibaDog" && back.Language == "german" && !back.UnlockAllDlc,
                  "settings: name sanitised like the emulator does, language and DLC lock saved", back.AccountName + " | " + back.Language);
            Check(back.Dlc.Select(d => d.Id + "=" + d.Name).SequenceEqual(new[] { "1672210=SeasonPass", "1672220=DLC 1672220" }),
                  "settings: only ticked DLC are written, as <id>=<clean name>", string.Join(", ", back.Dlc.Select(d => d.Id + "=" + d.Name).ToArray()));
            Check(back.LanPeers.SequenceEqual(new[] { "26.12.0.5", "pc1:27015" }) && changed.Count == 3,
                  "settings: LAN peers trimmed and de-duplicated; three files written", string.Join(", ", changed.Select(Path.GetFileName).ToArray()));
            string userText = File.ReadAllText(Path.Combine(ss, "configs.user.ini"));
            Check(userText.StartsWith("# mine\r\n[user::general]\r\nip_country=DE\r\n"), "settings: the user's own lines in configs.user.ini are kept", userText);
            Check(EmuSettings.Save(ss, back).Count == 0, "settings: saving the same values again writes nothing", null);

            var cleared = new EmuSettings.Values { UnlockAllDlc = true };
            EmuSettings.Save(ss, cleared);
            var after = EmuSettings.Load(ss);
            Check(after.AccountName == "" && after.Language == "" && after.UnlockAllDlc && after.Dlc.Count == 0
                  && !File.Exists(Path.Combine(ss, "custom_broadcasts.txt")) && File.ReadAllText(Path.Combine(ss, "configs.user.ini")).Contains("ip_country=DE"),
                  "settings: blank fields are removed (global applies), an empty peer list deletes custom_broadcasts.txt", null);
            string fresh = Path.Combine(work, "fresh", "steam_settings");
            Check(EmuSettings.Save(fresh, new EmuSettings.Values()).Count == 0 && !File.Exists(Path.Combine(fresh, "configs.app.ini"))
                  && !File.Exists(Path.Combine(fresh, "configs.user.ini")),
                  "settings: defaults alone create no files", null);
            Check(!Directory.Exists(Path.Combine(ss, ".gp-recovery")), "settings: no staging folders are left behind", null);

            // ---- the folder the emulator reads, found the way the patch finds it
            string game2 = Directory.CreateDirectory(Path.Combine(work, "game")).FullName;
            string exe2 = Path.Combine(game2, "Game.exe");
            WritePeWithImport(exe2, "steam_api64.dll");
            string tmp = Path.Combine(game2, "x.tmp");
            WriteNativeX64Pe(tmp);
            File.WriteAllBytes(Path.Combine(game2, "steam_api64.dll"), File.ReadAllBytes(tmp));
            var t1 = EmuSettings.Locate(exe2);
            Check(!t1.IsEmulator && string.Equals(t1.SettingsDir, Path.Combine(game2, "steam_settings"), StringComparison.OrdinalIgnoreCase),
                  "locate: Valve's dll is reported as not patched", t1.SettingsDir);
            File.WriteAllBytes(Path.Combine(game2, "steam_api64.dll"), File.ReadAllBytes(tmp).Concat(Encoding.ASCII.GetBytes("\0steam_settings\0")).ToArray());
            File.WriteAllText(Path.Combine(game2, "steam_appid.txt"), "1250\r\n");
            var t2 = EmuSettings.Locate(exe2);
            Check(t2.IsEmulator && !t2.OnlineFix && t2.AppId == "1250", "locate: the emulator dll and its AppID are found", t2.AppId);
            File.Delete(tmp);
        }
        catch (Exception ex) { Check(false, "emulator settings tests ran without throwing", ex.ToString()); }
        finally { try { Directory.Delete(work, true); } catch { } }

        Console.WriteLine("\n[DLC and LAN peer input]");
        uint id;
        Check(EmuSettings.TryParseDlcId(" 42 ", out id) && id == 42 && EmuSettings.TryParseDlcId("4294967295", out id)
              && !EmuSettings.TryParseDlcId("0", out id) && !EmuSettings.TryParseDlcId("4294967296", out id)
              && !EmuSettings.TryParseDlcId("12a", out id) && !EmuSettings.TryParseDlcId("-5", out id) && !EmuSettings.TryParseDlcId("", out id),
              "DLC ids: decimal uint32 above zero only", null);
        var goodPeers = new[] { "26.12.0.5", "26.12.0.5:47584", "my-host.example", "pc1", "pc1:27015", "192.168.1.255" };
        var badPeers = new[] { "256.1.1.1", "1.2.3", "1.2.3.4.5", "host:0", "host:70000", "host:", "-bad", "bad-", "a b", "", "a..b", "x_y", "1.2.3.4:port" };
        Check(goodPeers.All(EmuSettings.IsValidPeer), "LAN peers: IPv4 and host names, with or without a port, are accepted",
              string.Join(", ", goodPeers.Where(p => !EmuSettings.IsValidPeer(p)).ToArray()));
        Check(!badPeers.Any(EmuSettings.IsValidPeer), "LAN peers: malformed addresses, names and ports are rejected",
              string.Join(", ", badPeers.Where(EmuSettings.IsValidPeer).ToArray()));
        List<string> invalid;
        var parsed = EmuSettings.ParsePeers("26.0.0.1\r\n\r\n  26.0.0.1 \nnot valid\n10.0.0.2:1", out invalid);
        Check(parsed.SequenceEqual(new[] { "26.0.0.1", "10.0.0.2:1" }) && invalid.SequenceEqual(new[] { "not valid" }),
              "LAN peers: blank lines skipped, duplicates merged, bad lines reported", string.Join("|", parsed.ToArray()) + " / " + string.Join("|", invalid.ToArray()));

        var store = EmuSettings.ParseStoreDlc("{\"status\":1,\"appid\":1110910,\"name\":\"Test\",\"dlc\":[{\"id\":1672210,\"name\":\"Season = Pass\\nX\"},{\"id\":\"1672220\",\"name\":\"Two\"},{\"id\":0,\"name\":\"zero\"},{\"id\":1672210,\"name\":\"dup\"}]}");
        Check(store != null && store.Select(d => d.Id + "=" + d.Name).SequenceEqual(new[] { "1672210=Season  PassX", "1672220=Two" }),
              "store DLC list: ids as numbers or strings, names cleaned, junk and duplicates dropped",
              store == null ? "(null)" : string.Join(", ", store.Select(d => d.Id + "=" + d.Name).ToArray()));
        Check(EmuSettings.ParseStoreDlc("{\"status\":2}") == null && EmuSettings.ParseStoreDlc("<html>") == null
              && EmuSettings.ParseStoreDlc("{\"status\":1}").Count == 0,
              "store DLC list: a failed status or non-JSON is null, no dlc array is an empty list", null);
    }
}
