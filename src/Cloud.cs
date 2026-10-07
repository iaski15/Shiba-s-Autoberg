using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Gp
{
    // --------------------------------------------------------------------- cloud saves
    // Our own "Steam Cloud": a game's save folders zipped and kept in the user's Google Drive
    // ("Shibaberg Saves\<Game> (<appid>)\"), signed in natively - no Drive app.

    /// <summary>Where a game keeps its saves. Inside a backup the folders are stored tokenised (%APPDATA%...)
    /// so a backup made on one PC restores to the right place on another.</summary>
    public static class SaveLocator
    {
        static string Folder(Environment.SpecialFolder f) { return Environment.GetFolderPath(f); }
        public static string LocalLow { get { return Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"); } }

        // Longest first: LocalLow and the AppData folders all sit inside the profile.
        static IEnumerable<KeyValuePair<string, string>> Tokens()
        {
            yield return new KeyValuePair<string, string>("%LOCALLOW%", LocalLow);
            yield return new KeyValuePair<string, string>("%LOCALAPPDATA%", Folder(Environment.SpecialFolder.LocalApplicationData));
            yield return new KeyValuePair<string, string>("%APPDATA%", Folder(Environment.SpecialFolder.ApplicationData));
            yield return new KeyValuePair<string, string>("%DOCUMENTS%", Folder(Environment.SpecialFolder.MyDocuments));
            yield return new KeyValuePair<string, string>("%USERPROFILE%", Folder(Environment.SpecialFolder.UserProfile));
        }

        public static string Tokenize(string path)
        {
            path = Path.GetFullPath(path).TrimEnd('\\');
            foreach (var t in Tokens())
                if (path.StartsWith(t.Value + "\\", StringComparison.OrdinalIgnoreCase)) return t.Key + path.Substring(t.Value.Length);
            return path;
        }

        public static string Expand(string tokenised)
        {
            foreach (var t in Tokens())
                if (tokenised.StartsWith(t.Key + "\\", StringComparison.OrdinalIgnoreCase)) return t.Value + tokenised.Substring(t.Key.Length);
            return tokenised;
        }

        /// <summary>A restore deletes the folder before refilling it, so it must be a game's own folder: absolute,
        /// no "..", and at least one level below the profile / AppData folders (never one of them, or a drive).</summary>
        public static bool IsSafeRoot(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path.Contains("..")) return false;
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (full.Split('\\').Length < 3) return false;   // "C:\x" at least needs "C:\x\y"
            foreach (var t in Tokens())
            {
                string root = t.Value.TrimEnd('\\');
                if (root.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>The game folder for a patched exe folder: climbs out of Unreal's &lt;Project&gt;\Binaries\Win64.</summary>
        public static string GameDir(string exeFolder)
        {
            return PatchRunner.SearchRoot(Path.Combine(exeFolder, "game.exe"));
        }

        /// <summary>Existing save folders: the emulator's own storage (Steam Cloud files, achievements, stats),
        /// Unity's and Unreal's standard save locations, and folders the user added.</summary>
        public static List<string> Detect(string gameDir, string appId, IEnumerable<string> extra)
        {
            var found = new List<string>();
            Action<string> add = p =>
            {
                if (Directory.Exists(p) && IsSafeRoot(p) && !found.Any(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase)))
                    found.Add(Path.GetFullPath(p).TrimEnd('\\'));
            };
            add(Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), "GSE Saves", appId));
            try
            {
                // Unity: <game>_Data\app.info holds company and product; saves go to LocalLow\<company>\<product>.
                foreach (var data in Directory.GetDirectories(gameDir, "*_Data"))
                {
                    string info = Path.Combine(data, "app.info");
                    if (!File.Exists(info)) continue;
                    var lines = File.ReadAllLines(info);
                    if (lines.Length >= 2 && PlainName(lines[0]) && PlainName(lines[1]))
                        add(Path.Combine(LocalLow, lines[0].Trim(), lines[1].Trim()));
                }
                // Unreal: <Project>\Binaries beside Engine\; saves go to %LOCALAPPDATA%\<Project>\Saved\SaveGames.
                foreach (var proj in Directory.GetDirectories(gameDir))
                {
                    string name = Path.GetFileName(proj);
                    if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.Combine(proj, "Binaries"))) continue;
                    add(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), name, "Saved", "SaveGames"));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var e in extra ?? Enumerable.Empty<string>()) add(e);
            return found;
        }

        static bool PlainName(string s)
        {
            s = s.Trim();
            return s.Length > 0 && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && s != "." && s != "..";
        }
    }

    /// <summary>One zip per backup: "&lt;n&gt;/&lt;relative path&gt;" per save folder plus a manifest mapping n to the
    /// folder's tokenised path.</summary>
    public static class SaveArchive
    {
        const string Manifest = "shibaberg-saves.txt";
        const string Header = "shibaberg-saves 1";

        public static int Pack(IList<string> roots, string zipPath)
        {
            int files = 0;
            using (var fs = File.Create(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var man = new StringBuilder(Header).Append('\n');
                for (int i = 0; i < roots.Count; i++)
                {
                    string root = Path.GetFullPath(roots[i]).TrimEnd('\\');
                    man.Append(i).Append('|').Append(SaveLocator.Tokenize(root)).Append('\n');
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var e = zip.CreateEntry(i + "/" + f.Substring(root.Length + 1).Replace('\\', '/'), CompressionLevel.Optimal);
                        e.LastWriteTime = File.GetLastWriteTime(f);
                        // the game may hold its save open: read anyway, like Steam does
                        using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dst = e.Open())
                            src.CopyTo(dst);
                        files++;
                    }
                }
                using (var w = new StreamWriter(zip.CreateEntry(Manifest).Open(), new UTF8Encoding(false))) w.Write(man.ToString());
            }
            return files;
        }

        /// <summary>The save folders a backup holds, expanded for this PC.</summary>
        public static Dictionary<int, string> Roots(ZipArchive zip)
        {
            var entry = zip.GetEntry(Manifest);
            if (entry == null) throw new InvalidDataException("Not a Shibaberg save backup (no manifest).");
            var roots = new Dictionary<int, string>();
            using (var r = new StreamReader(entry.Open(), Encoding.UTF8))
            {
                if (r.ReadLine() != Header) throw new InvalidDataException("Unknown save backup version.");
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    int bar = line.IndexOf('|');
                    int n;
                    if (bar <= 0 || !int.TryParse(line.Substring(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out n))
                        throw new InvalidDataException("Bad manifest line: " + line);
                    string path = SaveLocator.Expand(line.Substring(bar + 1));
                    if (!SaveLocator.IsSafeRoot(path)) throw new InvalidDataException("Backup names an unsafe folder: " + path);
                    roots[n] = Path.GetFullPath(path).TrimEnd('\\');
                }
            }
            return roots;
        }

        /// <summary>Makes each saved folder exactly the backup's copy. The current files are zipped to
        /// <paramref name="safetyZip"/> first, and every entry is checked before anything on disk changes.</summary>
        public static int Restore(string zipPath, string safetyZip)
        {
            using (var zip = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
            {
                var roots = Roots(zip);
                var plan = new List<KeyValuePair<ZipArchiveEntry, string>>();
                foreach (var e in zip.Entries)
                {
                    if (e.FullName == Manifest || e.FullName.EndsWith("/")) continue;
                    int slash = e.FullName.IndexOf('/');
                    int n;
                    string root;
                    if (slash <= 0 || !int.TryParse(e.FullName.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out n) || !roots.TryGetValue(n, out root))
                        throw new InvalidDataException("Unexpected entry in backup: " + e.FullName);
                    string dest = Path.GetFullPath(Path.Combine(root, e.FullName.Substring(slash + 1).Replace('/', '\\')));
                    if (!dest.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Backup entry escapes its folder: " + e.FullName);
                    plan.Add(new KeyValuePair<ZipArchiveEntry, string>(e, dest));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(safetyZip));
                Pack(roots.Values.ToList(), safetyZip);

                foreach (var root in roots.Values)
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    Directory.CreateDirectory(root);
                }
                foreach (var p in plan)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p.Value));
                    using (var src = p.Key.Open())
                    using (var dst = File.Create(p.Value))
                        src.CopyTo(dst);
                    File.SetLastWriteTime(p.Value, p.Key.LastWriteTime.LocalDateTime);
                }
                return plan.Count;
            }
        }
    }

    public sealed class DriveFile
    {
        public string Id = "";
        public string Name = "";
        public long Size;
        public DateTime Created;
    }

    /// <summary>Thrown when the stored Google sign-in no longer works (revoked, expired); the user signs in again.</summary>
    public sealed class GoogleSignInExpiredException : Exception
    {
        public GoogleSignInExpiredException() : base("Google sign-in expired – sign in again.") { }
    }

    /// <summary>Google sign-in + the few Drive v3 calls cloud saves need. OAuth for installed apps: the user's
    /// browser, a loopback redirect and PKCE; scope drive.file, so Shibaberg only sees files it created. The
    /// refresh token is kept DPAPI-encrypted for this Windows user.</summary>
    public sealed class GoogleDrive
    {
        const string Scope = "https://www.googleapis.com/auth/drive.file";
        const string TokenUrl = "https://oauth2.googleapis.com/token";
        const string Files = "https://www.googleapis.com/drive/v3/files";
        const string FolderType = "application/vnd.google-apps.folder";

        static string TokenFile { get { return Path.Combine(AppPaths.StateDir, "google.dat"); } }
        /// <summary>Plain "signed in as" line for the in-game overlay (it can't read the DPAPI token file).</summary>
        public static string AccountFile { get { return Path.Combine(AppPaths.StateDir, "cloud", "account.txt"); } }
        public static bool Configured { get { return GoogleClient.Id.Length > 0; } }

        readonly string refresh;
        string access;
        DateTime accessUntil;
        public string Email { get; private set; }

        GoogleDrive(string refreshToken, string email) { refresh = refreshToken; Email = email ?? ""; }

        /// <summary>The saved sign-in, or null.</summary>
        public static GoogleDrive Load()
        {
            try
            {
                var raw = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser)).Split('\n');
                return raw[0].Length > 0 ? new GoogleDrive(raw[0], raw.Length > 1 ? raw[1] : "") : null;
            }
            catch { return null; }
        }

        void Save()
        {
            Directory.CreateDirectory(AppPaths.StateDir);
            File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(refresh + "\n" + Email), null, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(AccountFile));
            File.WriteAllText(AccountFile, Email.Length > 0 ? Email : "Google account", new UTF8Encoding(false));
        }

        /// <summary>Drops the saved sign-in without contacting Google (it already refused the token).</summary>
        public static void Forget()
        {
            try { File.Delete(TokenFile); } catch { }
            try { File.Delete(AccountFile); } catch { }
        }

        public void SignOut()
        {
            try { PostForm("https://oauth2.googleapis.com/revoke", "token=" + Uri.EscapeDataString(refresh)); } catch { }
            Forget();
        }

        /// <summary>Opens Google's sign-in page in the default browser and waits for it to redirect back.</summary>
        public static GoogleDrive SignIn(CancellationToken ct)
        {
            if (!Configured) throw new InvalidOperationException("Google sign-in isn't set up in this build (no google_client.json).");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                string redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                string verifier = Base64Url(RandomBytes(32));
                string state = Base64Url(RandomBytes(16));
                string challenge;
                using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
                Process.Start("https://accounts.google.com/o/oauth2/v2/auth?client_id=" + Uri.EscapeDataString(GoogleClient.Id)
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&response_type=code&scope=" + Uri.EscapeDataString(Scope)
                    + "&code_challenge=" + challenge + "&code_challenge_method=S256&access_type=offline&prompt=consent&state=" + state);

                Dictionary<string, string> query;
                while (true)
                {
                    // the browser may also ask for /favicon.ico; only the redirect carries state
                    var accept = listener.AcceptTcpClientAsync();
                    try { accept.Wait(ct); }
                    catch (AggregateException ex) { throw ex.InnerException; }
                    using (var client = accept.Result)
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 10000;
                        string requestLine = new StreamReader(stream, Encoding.ASCII).ReadLine() ?? "";
                        string target = requestLine.Split(' ').Length > 1 ? requestLine.Split(' ')[1] : "";
                        query = ParseQuery(target.Contains("?") ? target.Substring(target.IndexOf('?') + 1) : "");
                        string v;
                        bool ours = query.TryGetValue("state", out v) && v == state;
                        string page = ours && query.ContainsKey("code")
                            ? "Signed in to Shibaberg. You can close this tab."
                            : "Sign-in didn't complete. You can close this tab and try again from Shibaberg.";
                        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Shibaberg</title><body style=\"font:16px Segoe UI,sans-serif;background:#FFF6EA;color:#3A2418;padding:48px\"><h2>" + page + "</h2>");
                        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length);
                        stream.Write(body, 0, body.Length);
                        if (ours) break;
                    }
                }
                string code, error;
                if (query.TryGetValue("error", out error)) throw new IOException("Google sign-in was cancelled (" + error + ").");
                if (!query.TryGetValue("code", out code)) throw new IOException("Google sign-in returned no code.");

                var tok = PostForm(TokenUrl, "code=" + Uri.EscapeDataString(code) + "&client_id=" + Uri.EscapeDataString(GoogleClient.Id)
                    + "&client_secret=" + Uri.EscapeDataString(GoogleClient.Secret) + "&code_verifier=" + verifier
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&grant_type=authorization_code");
                string refreshToken = Val(tok, "refresh_token");
                if (refreshToken.Length == 0) throw new IOException("Google did not return a refresh token.");
                var d = new GoogleDrive(refreshToken, "");
                d.TakeAccess(tok);
                d.Email = Val(d.Call("GET", "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)").Element("user"), "emailAddress");
                d.Save();
                return d;
            }
            finally { listener.Stop(); }
        }

        void TakeAccess(XElement tok)
        {
            access = Val(tok, "access_token");
            int secs;
            int.TryParse(Val(tok, "expires_in"), out secs);
            accessUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, secs) - 60);
        }

        string Token()
        {
            if (access != null && DateTime.UtcNow < accessUntil) return access;
            try
            {
                TakeAccess(PostForm(TokenUrl, "refresh_token=" + Uri.EscapeDataString(refresh) + "&client_id=" + Uri.EscapeDataString(GoogleClient.Id)
                    + "&client_secret=" + Uri.EscapeDataString(GoogleClient.Secret) + "&grant_type=refresh_token"));
            }
            catch (IOException ex) when (ex.Message.Contains("invalid_grant")) { throw new GoogleSignInExpiredException(); }
            return access;
        }

        // ---- Drive ------------------------------------------------------------------------------------

        /// <summary>Finds or creates a folder: by name, or - for a game folder - by its appid tag, which survives
        /// the folder being renamed.</summary>
        public string Folder(string name, string parentId, string appId)
        {
            string q = "mimeType='" + FolderType + "' and trashed=false and '" + parentId + "' in parents and "
                + (appId == null ? "name='" + name.Replace("\\", "\\\\").Replace("'", "\\'") + "'" : "appProperties has { key='appid' and value='" + appId + "' }");
            var hit = Call("GET", Files + "?fields=files(id)&q=" + Uri.EscapeDataString(q)).Element("files");
            var first = hit == null ? null : hit.Elements().FirstOrDefault();
            if (first != null) return Val(first, "id");
            string meta = "{\"name\":" + Js(name) + ",\"mimeType\":\"" + FolderType + "\",\"parents\":[" + Js(parentId) + "]"
                + (appId == null ? "" : ",\"appProperties\":{\"appid\":" + Js(appId) + "}") + "}";
            return Val(Call("POST", Files + "?fields=id", meta), "id");
        }

        /// <summary>Resumable upload: one session, the whole file in one PUT (streamed, not buffered).</summary>
        public string Upload(string parentId, string name, string path)
        {
            long len = new FileInfo(path).Length;
            var init = Req("POST", "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id");
            init.Headers["X-Upload-Content-Type"] = "application/zip";
            init.Headers["X-Upload-Content-Length"] = len.ToString(CultureInfo.InvariantCulture);
            WriteJson(init, "{\"name\":" + Js(name) + ",\"parents\":[" + Js(parentId) + "]}");
            string session;
            using (var resp = Send(init)) session = resp.Headers["Location"];
            if (string.IsNullOrEmpty(session)) throw new IOException("Google Drive did not start the upload.");

            var put = (HttpWebRequest)WebRequest.Create(session);
            put.Method = "PUT";
            put.ContentType = "application/zip";
            put.ContentLength = len;
            put.AllowWriteStreamBuffering = false;
            put.Timeout = 60000;
            put.ReadWriteTimeout = 300000;
            using (var s = put.GetRequestStream())
            using (var f = File.OpenRead(path))
                f.CopyTo(s);
            using (var resp = Send(put))
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return Val(ParseJson(r.ReadToEnd()), "id");
        }

        /// <summary>Files in a folder, newest first.</summary>
        public List<DriveFile> List(string folderId)
        {
            var r = Call("GET", Files + "?pageSize=100&orderBy=createdTime%20desc&fields=files(id,name,size,createdTime)&q="
                + Uri.EscapeDataString("'" + folderId + "' in parents and trashed=false"));
            var files = r.Element("files");
            if (files == null) return new List<DriveFile>();
            return files.Elements().Select(e =>
            {
                long size;
                DateTime created;
                long.TryParse(Val(e, "size"), out size);
                DateTime.TryParse(Val(e, "createdTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out created);
                return new DriveFile { Id = Val(e, "id"), Name = Val(e, "name"), Size = size, Created = created.ToLocalTime() };
            }).ToList();
        }

        public void Download(string fileId, string path)
        {
            var req = Req("GET", Files + "/" + Uri.EscapeDataString(fileId) + "?alt=media");
            req.ReadWriteTimeout = 300000;
            using (var resp = Send(req))
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(path))
                src.CopyTo(dst);
        }

        public void Delete(string fileId)
        {
            using (Send(Req("DELETE", Files + "/" + Uri.EscapeDataString(fileId)))) { }
        }

        // ---- HTTP + JSON ------------------------------------------------------------------------------

        HttpWebRequest Req(string method, string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + Token();
            req.UserAgent = "Shibaberg/" + BuildInfo.Version;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            return req;
        }

        XElement Call(string method, string url, string json = null)
        {
            var req = Req(method, url);
            if (json != null) WriteJson(req, json);
            else if (method == "POST") req.ContentLength = 0;
            using (var resp = Send(req))
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return ParseJson(r.ReadToEnd());
        }

        static XElement PostForm(string url, string form)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Timeout = 30000;
            byte[] body = Encoding.ASCII.GetBytes(form);
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = Send(req))
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string text = r.ReadToEnd();
                return text.Trim().Length == 0 ? new XElement("root") : ParseJson(text);
            }
        }

        static void WriteJson(HttpWebRequest req, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.ContentType = "application/json; charset=UTF-8";
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        }

        /// <summary>GetResponse, with Google's error body turned into a readable IOException.</summary>
        static HttpWebResponse Send(HttpWebRequest req)
        {
            try { return (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new IOException("Can't reach Google: " + ex.Message, ex);
                string body = "";
                try { using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd(); } catch { }
                resp.Dispose();
                string detail = body;
                try
                {
                    var j = ParseJson(body);
                    var err = j.Element("error");
                    detail = err == null ? body : (err.HasElements ? Val(err, "message") : err.Value + " " + Val(j, "error_description"));
                }
                catch { }
                throw new IOException("Google " + (int)resp.StatusCode + ": " + detail.Trim(), ex);
            }
        }

        internal static XElement ParseJson(string json)
        {
            using (var r = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max))
                return XElement.Load(r);
        }

        static string Val(XElement e, string name)
        {
            var c = e == null ? null : e.Element(name);
            return c == null ? "" : c.Value;
        }

        internal static string Js(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in q.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                d[Uri.UnescapeDataString(part.Substring(0, eq).Replace('+', ' '))] = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            }
            return d;
        }

        static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        static string Base64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    }

    /// <summary>Back up / list / restore one game's saves in Drive: "Shibaberg Saves\&lt;Game&gt; (&lt;appid&gt;)\&lt;date&gt; - &lt;PC&gt;.zip",
    /// newest <see cref="Keep"/> kept. Every backup and restore updates the game's sync record (which Drive backup
    /// this PC's saves match, and a fingerprint of them then), which is what lets a session-start pull tell
    /// "the cloud moved on" from "this PC played since".</summary>
    public static class CloudSaves
    {
        public const int Keep = 10;
        public const string RootFolder = "Shibaberg Saves";

        /// <summary>Where a restore leaves the local files it replaced.</summary>
        public static string SafetyDir { get { return Path.Combine(AppPaths.StateDir, "save_backups"); } }
        static string StateFile(string appId, string ext) { return Path.Combine(AppPaths.StateDir, "cloud", appId + ext); }

        static string GameFolder(GoogleDrive d, string game, string appId)
        {
            return d.Folder(game + " (" + appId + ")", d.Folder(RootFolder, "root", null), appId);
        }

        /// <summary>Changes whenever any save file is added, removed, resized or rewritten; "" when there are none.</summary>
        public static string Fingerprint(IEnumerable<string> roots)
        {
            var sb = new StringBuilder();
            foreach (var root in roots.Where(Directory.Exists))
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(f);
                    sb.Append(SaveLocator.Tokenize(f)).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
            if (sb.Length == 0) return "";
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "");
        }

        /// <summary>Last synced Drive backup id + the local fingerprint at that moment.</summary>
        public static KeyValuePair<string, string> Record(string appId)
        {
            try
            {
                var parts = File.ReadAllText(StateFile(appId, ".sync")).Trim().Split('|');
                if (parts.Length == 2) return new KeyValuePair<string, string>(parts[0], parts[1]);
            }
            catch { }
            return new KeyValuePair<string, string>("", "");
        }

        static void SetRecord(string appId, string backupId, string fingerprint)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile(appId, ".sync")));
            File.WriteAllText(StateFile(appId, ".sync"), backupId + "|" + fingerprint);
        }

        /// <summary>One line the in-game overlay shows under "Cloud saves".</summary>
        public static void SetStatus(string appId, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile(appId, ".status")));
                // the overlay font only has Latin glyphs
                File.WriteAllText(StateFile(appId, ".status"), DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + text.Replace("–", "-").Replace("…", "...").Replace("“", "\"").Replace("”", "\""), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string Backup(GoogleDrive d, string game, string appId, IList<string> roots, Action<LogLevel, string> log)
        {
            if (roots.Count == 0) throw new InvalidOperationException("No save folders found for " + game + " – add one with “+ Folder”.");
            string tmp = Path.Combine(Path.GetTempPath(), "shibaberg_save_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                string fingerprint = Fingerprint(roots);
                int files = SaveArchive.Pack(roots, tmp);
                string name = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + " - " + Environment.MachineName + ".zip";
                string folder = GameFolder(d, game, appId);
                string id = d.Upload(folder, name, tmp);
                SetRecord(appId, id, fingerprint);
                log(LogLevel.Ok, string.Format("{0}: backed up {1} files ({2:N0} KB) → Drive\\{3}\\{4} ({5})", game, files, new FileInfo(tmp).Length / 1024.0, RootFolder, game, name));
                foreach (var old in d.List(folder).Skip(Keep))
                {
                    d.Delete(old.Id);
                    log(LogLevel.Dim, "Removed old backup " + old.Name);
                }
                return name;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static List<DriveFile> Backups(GoogleDrive d, string game, string appId)
        {
            return d.List(GameFolder(d, game, appId));
        }

        public static void Restore(GoogleDrive d, DriveFile backup, string game, string appId, string gameDir, Action<LogLevel, string> log)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "shibaberg_save_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                d.Download(backup.Id, tmp);
                string safety = Path.Combine(SafetyDir, appId + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + " before restore.zip");
                int files = SaveArchive.Restore(tmp, safety);
                SetRecord(appId, backup.Id, Fingerprint(Roots(gameDir, appId)));
                log(LogLevel.Ok, game + ": restored " + files + " files from " + backup.Name);
                log(LogLevel.Dim, "The saves it replaced are kept in " + safety);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>The game's save folders: detected + the ones added in Shibaberg's Cloud saves window.</summary>
        public static List<string> Roots(string gameDir, string appId)
        {
            List<string> extra;
            AppSettings.Load().SaveDirs.TryGetValue(appId, out extra);
            return SaveLocator.Detect(gameDir, appId, extra);
        }

        /// <summary>Session start: take the newest Drive backup unless this PC played since its last sync (then
        /// this PC's saves win and go up at exit; the Drive copy stays in the backup history).</summary>
        public static string Pull(GoogleDrive d, string gameDir, string appId, Action<LogLevel, string> log)
        {
            string game = Path.GetFileName(gameDir.TrimEnd('\\'));
            var latest = Backups(d, game, appId).FirstOrDefault();
            if (latest == null) return "No cloud save yet – it uploads when you quit the game.";
            var rec = Record(appId);
            if (latest.Id == rec.Key) return "Up to date with the cloud.";
            string local = Fingerprint(Roots(gameDir, appId));
            if (local.Length > 0 && local != rec.Value)
                return "This PC's saves are newer than its last sync – kept them; they upload when you quit.";
            Restore(d, latest, game, appId, gameDir, log);
            return "Loaded the cloud save from " + Path.GetFileNameWithoutExtension(latest.Name) + ".";
        }

        /// <summary>Uploads when the saves changed since the last sync.</summary>
        public static string Push(GoogleDrive d, string gameDir, string appId, bool force, Action<LogLevel, string> log)
        {
            string game = Path.GetFileName(gameDir.TrimEnd('\\'));
            var roots = Roots(gameDir, appId);
            string local = Fingerprint(roots);
            if (local.Length == 0) return "No saves found to upload.";
            var rec = Record(appId);
            if (!force && rec.Key.Length > 0 && local == rec.Value) return "Saves unchanged – nothing to upload.";
            Backup(d, game, appId, roots, log);
            return "Backed up to Google Drive.";
        }

        /// <summary>True while any process runs from inside the game folder: its saves may be mid-write.</summary>
        public static bool GameRunning(string gameDir)
        {
            string dir = Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }
    }

    /// <summary>What the overlay build of the emulator runs (no window) to sync a game around a play session:
    ///   --cloud-signin | --cloud-signout
    ///   --cloud-pull  &lt;appid&gt; &lt;exe folder&gt;          before the game reads its saves (the emulator waits for it)
    ///   --cloud-push  &lt;appid&gt; &lt;exe folder&gt;          "Back up now" in the overlay
    ///   --cloud-watch &lt;appid&gt; &lt;exe folder&gt; &lt;pid&gt;    waits for the game to exit, then pushes
    /// Results go to %APPDATA%\GoldbergPatcher\cloud\&lt;appid&gt;.status for the overlay to show.</summary>
    public static class CloudCli
    {
        public static bool Handles(string[] a) { return a != null && a.Length > 0 && a[0].StartsWith("--cloud-", StringComparison.OrdinalIgnoreCase); }

        public static int Run(string[] a)
        {
            string cmd = a[0].ToLowerInvariant();
            if (cmd == "--cloud-signin")
            {
                try { GoogleDrive.SignIn(CancellationToken.None); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            if (cmd == "--cloud-signout")
            {
                var d0 = GoogleDrive.Load();
                if (d0 != null) d0.SignOut(); else GoogleDrive.Forget();
                return 0;
            }

            uint n;
            if (a.Length < 3 || !uint.TryParse(a[1], out n) || n == 0 || !Directory.Exists(a[2])) { Console.Error.WriteLine("usage: " + cmd + " <appid> <exe folder> [pid]"); return 1; }
            string appId = a[1], gameDir = SaveLocator.GameDir(a[2]);
            if (cmd == "--cloud-watch")
            {
                int pid;
                if (a.Length < 4 || !int.TryParse(a[3], out pid)) return 1;
                using (var once = new Mutex(false, @"Local\ShibabergCloudWatch_" + pid))
                {
                    if (!once.WaitOne(0)) return 0;   // one watcher per game process
                    try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(); } catch (ArgumentException) { }
                    Thread.Sleep(2000);   // let the game's last writes land
                    return Sync(appId, gameDir, false, false);
                }
            }
            if (cmd == "--cloud-pull") return Sync(appId, gameDir, true, false);
            if (cmd == "--cloud-push") return Sync(appId, gameDir, false, true);
            Console.Error.WriteLine("unknown command " + cmd);
            return 1;
        }

        static int Sync(string appId, string gameDir, bool pull, bool force)
        {
            var d = GoogleDrive.Load();
            if (d == null) { CloudSaves.SetStatus(appId, "Not signed in – saves stay on this PC only."); return 0; }
            // one sync per game at a time: "Back up now" and the exit upload can meet
            using (var gate = new Mutex(false, @"Local\ShibabergCloud_" + appId))
            {
                bool owned;
                try { owned = gate.WaitOne(TimeSpan.FromMinutes(2)); } catch (AbandonedMutexException) { owned = true; }
                try
                {
                    CloudSaves.SetStatus(appId, pull ? "Checking the cloud…" : "Uploading…");
                    Action<LogLevel, string> log = (l, m) => Console.WriteLine(m);
                    CloudSaves.SetStatus(appId, pull ? CloudSaves.Pull(d, gameDir, appId, log) : CloudSaves.Push(d, gameDir, appId, force, log));
                    return 0;
                }
                catch (GoogleSignInExpiredException)
                {
                    GoogleDrive.Forget();
                    CloudSaves.SetStatus(appId, "Google sign-in expired – sign in again (Shift+Tab → Cloud saves).");
                    return 1;
                }
                catch (Exception ex)
                {
                    CloudSaves.SetStatus(appId, "Cloud sync failed: " + ex.Message);
                    Console.Error.WriteLine(ex);
                    return 1;
                }
                finally { if (owned) gate.ReleaseMutex(); }
            }
        }
    }
}