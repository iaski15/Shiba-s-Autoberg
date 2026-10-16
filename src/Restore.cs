using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Gp
{
    /// <summary>What every patch run wrote below the game root, kept beside the originals as
    /// &lt;backup folder&gt;\ledger.txt and merged across runs: for each file, whether a patch created it, the hash
    /// it had before the first patch, and the hash the last patch wrote. Restore original uses it to tell
    /// Shibaberg's files from the game's own and from later game updates; OriginalBackups.Preserve uses it to
    /// notice that a game update replaced an original.</summary>
    public sealed class BackupLedger
    {
        public const string FileName = "ledger.txt";

        public sealed class Entry
        {
            public string Path = "";
            public bool Created;
            /// <summary>Hash before the first patch touched it; "absent" when a patch created it.</summary>
            public string Original = "absent";
            /// <summary>Hash of what the last patch wrote.</summary>
            public string Written = "";
        }

        public string Root = "";
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<Entry> Entries { get { return entries.Values; } }

        static string Full(string root, string path)
        {
            return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root, path));
        }

        /// <summary>The entry for a file, by full path; null when no patch wrote it.</summary>
        public Entry Find(string fullPath)
        {
            Entry e;
            return entries.TryGetValue(System.IO.Path.GetFullPath(fullPath), out e) ? e : null;
        }

        /// <summary>The ledger in a backup folder, or null when there is none (installs patched before it existed).</summary>
        public static BackupLedger Load(string backupDir)
        {
            try
            {
                string path = System.IO.Path.Combine(backupDir, FileName);
                if (string.IsNullOrEmpty(backupDir) || !File.Exists(path)) return null;
                var ledger = new BackupLedger();
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("root=")) { ledger.Root = line.Substring(5); continue; }
                    var f = line.Split('|');
                    if (f.Length != 4 || ledger.Root.Length == 0) continue;
                    var e = new Entry { Path = Full(ledger.Root, f[0]), Created = f[1] == "1", Original = f[2], Written = f[3] };
                    ledger.entries[e.Path] = e;
                }
                return ledger.Root.Length == 0 ? null : ledger;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        void Save(string backupDir)
        {
            var sb = new StringBuilder();
            sb.Append("# Shibaberg: files patches wrote below the game root. path|created by a patch|sha256 before the first patch|sha256 the last patch wrote\r\n");
            sb.Append("root=").Append(Root).Append("\r\n");
            string prefix = Root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            foreach (var e in entries.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                string rel = e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? e.Path.Substring(prefix.Length) : e.Path;
                sb.Append(rel).Append('|').Append(e.Created ? "1" : "0").Append('|').Append(e.Original).Append('|').Append(e.Written).Append("\r\n");
            }
            var record = SafePersistence.WriteText(System.IO.Path.Combine(backupDir, FileName), sb.ToString());
            Recovery.Discard(new[] { record });
        }

        /// <summary>Merges a successful run's writes into the ledger. A file the run replaced from a local recovery
        /// copy (not an exe or dll: those go through OriginalBackups first) gets its previous content preserved in
        /// the backup tree now, before the next run's pruning deletes that copy - unless that content was
        /// Shibaberg's own (an older patch's steam_appid.txt, an activated sample), in which case the file counts
        /// as created by a patch. Never throws: a ledger problem must not fail a finished patch.</summary>
        public static void Record(string backupDir, string gameRoot, IEnumerable<FileWriteRecord> writes, Action<LogLevel, string> log)
        {
            try
            {
                if (string.IsNullOrEmpty(backupDir)) return;
                string backupFull = System.IO.Path.GetFullPath(backupDir).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                var ledger = Load(backupDir) ?? new BackupLedger { Root = System.IO.Path.GetFullPath(gameRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar) };
                foreach (var w in writes ?? Enumerable.Empty<FileWriteRecord>())
                {
                    if (!w.Completed || string.IsNullOrEmpty(w.Destination)) continue;
                    string full = System.IO.Path.GetFullPath(w.Destination);
                    if (full.StartsWith(backupFull, StringComparison.OrdinalIgnoreCase)) continue;   // the backups themselves
                    Entry e;
                    if (ledger.entries.TryGetValue(full, out e))
                    {
                        // An external recovery is the verified original backup, re-taken after a game update.
                        if (w.ExternalRecovery && !e.Created) e.Original = w.PreviousHash;
                    }
                    else
                    {
                        e = new Entry { Path = full };
                        if (w.PreviousHash == "absent") e.Created = true;
                        else if (w.ExternalRecovery) e.Original = w.PreviousHash;
                        else if (File.Exists(w.RecoveryPath) && GameRestore.LooksPatcherMade(full, w.RecoveryPath)) e.Created = true;
                        else if (File.Exists(w.RecoveryPath) && OriginalBackups.PreserveContent(backupDir, full, w.RecoveryPath, log) != null)
                            e.Original = w.PreviousHash;
                        else e.Created = true;   // nothing to put back: treat it like a file the patch made
                        ledger.entries[full] = e;
                    }
                    e.Written = w.StagedHash;
                }
                ledger.Save(backupDir);
            }
            catch (Exception ex)
            {
                if (log != null) log(LogLevel.Dim, "Could not update the backup ledger: " + ex.Message);
            }
        }
    }

    public enum RestoreAction { Restore, Delete, Leave }

    public sealed class RestoreItem
    {
        public RestoreAction Action;
        public string Path = "";
        /// <summary>The verified original (Restore).</summary>
        public string Source = "";
        public string Hash = "";
        public string Reason = "";
        /// <summary>A file left as it is that still needs the user (an emulator with no original, a file changed
        /// since the patch) - as opposed to the user's own files, which are simply not Shibaberg's to touch.</summary>
        public bool Attention;
    }

    public sealed class RestorePlan
    {
        public string Exe = "";
        public string GameRoot = "";
        public string AppId = "";
        public bool HasLedger;
        public readonly List<RestoreItem> Items = new List<RestoreItem>();
        /// <summary>Backup folders, removed once everything was restored.</summary>
        public readonly List<string> BackupDirs = new List<string>();
        /// <summary>Folders whose .gp-recovery staging is removed with them.</summary>
        public readonly List<string> TouchedDirs = new List<string>();
        /// <summary>A backup was found that this plan does not use, so the backups must stay.</summary>
        public bool KeepBackups;
        public readonly List<string> Notes = new List<string>();

        public IEnumerable<RestoreItem> Of(RestoreAction a) { return Items.Where(i => i.Action == a); }
        public bool NothingToDo { get { return !Items.Any(i => i.Action != RestoreAction.Leave); } }

        /// <summary>The plan as text: what is put back, what goes, what stays and why.</summary>
        public List<string> Describe(int max = int.MaxValue)
        {
            var lines = new List<string>();
            Func<string, string> rel = p => PatchRunner.ShortRel(GameRoot, p);
            foreach (var group in new[] { RestoreAction.Restore, RestoreAction.Delete, RestoreAction.Leave })
            {
                var items = Of(group).ToList();
                if (items.Count == 0) continue;
                lines.Add(group == RestoreAction.Restore ? "Put back the original:" : group == RestoreAction.Delete ? "Remove (made by Shibaberg):" : "Leave as it is:");
                foreach (var i in items) lines.Add("   " + rel(i.Path) + (i.Reason.Length > 0 ? "  – " + i.Reason : ""));
            }
            foreach (var n in Notes) lines.Add(n);
            if (lines.Count > max) { int more = lines.Count - (max - 1); lines = lines.Take(max - 1).ToList(); lines.Add("   … and " + more + " more line(s)"); }
            return lines;
        }
    }

    public sealed class RestoreResult
    {
        public int Restored, Deleted, Left, Failed;
        /// <summary>How many of <see cref="Left"/> still need the user.</summary>
        public int NeedAttention;
        public readonly List<string> Messages = new List<string>();
        public bool Blocked;

        /// <summary>Everything put back, nothing left that still needs the user.</summary>
        public bool Complete { get { return !Blocked && Failed == 0 && NeedAttention == 0 && (Restored > 0 || Deleted > 0); } }

        public string Summary
        {
            get
            {
                if (Blocked) return Messages.Count > 0 ? Messages[0] : "Restore refused.";
                if (Restored == 0 && Deleted == 0 && Failed == 0) return Left > 0 ? "Nothing could be restored (" + Left + " left as they are)." : "Nothing to restore.";
                var parts = new List<string>();
                if (Restored > 0) parts.Add(Restored + " restored");
                if (Deleted > 0) parts.Add(Deleted + " removed");
                if (Left > 0) parts.Add(Left + " left as they are");
                if (Failed > 0) parts.Add(Failed + " failed");
                return string.Join(", ", parts) + ".";
            }
        }
    }

    /// <summary>Restore original: puts a patched game back to how it was before Shibaberg touched it, any time
    /// after the patch (Undo last patch only covers the most recent run). Originals come from the verified
    /// backups (shibaberg_backup, or goldberg_backup from older versions); only files that are provably
    /// Shibaberg's are deleted; anything that changed since the patch is left alone.</summary>
    public static class GameRestore
    {
        static readonly Regex OurAppIdTxt = new Regex(@"^\d{1,10}\r\n$", RegexOptions.CultureInvariant);

        /// <summary>Whether the content at <paramref name="contentPath"/>, sitting at <paramref name="fullPath"/>,
        /// is something a patch writes: a steam_appid.txt of exactly "&lt;appid&gt;\r\n", an upstream sample in
        /// steam_settings (either name), the generated interface list or overlay pointer, or an emulator dll.</summary>
        internal static bool LooksPatcherMade(string fullPath, string contentPath = null, string exampleDir = null)
        {
            try
            {
                contentPath = contentPath ?? fullPath;
                string name = Path.GetFileName(fullPath);
                var info = new FileInfo(contentPath);
                if (!info.Exists) return false;
                if (string.Equals(name, "steam_appid.txt", StringComparison.OrdinalIgnoreCase))
                    return info.Length <= 16 && OurAppIdTxt.IsMatch(File.ReadAllText(contentPath));
                if (PatchRunner.IsSteamApiName(name))
                    return PatchRunner.LooksLikeBundledGoldberg(contentPath) || InstallCheck.IsEmulatorDll(contentPath);
                string settings = SettingsRoot(fullPath);
                if (settings == null)
                    return string.Equals(name, "steam_interfaces.txt", StringComparison.OrdinalIgnoreCase);   // the no-scaffold placement
                if (string.Equals(name, "steam_interfaces.txt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "shibaberg_cloud.txt", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(Path.GetFullPath(fullPath)).Equals(settings, StringComparison.OrdinalIgnoreCase);
                return IsSampleCopy(settings, fullPath, contentPath, exampleDir);
            }
            catch { return false; }
        }

        /// <summary>The steam_settings folder a path lies in (at any depth), or null.</summary>
        static string SettingsRoot(string fullPath)
        {
            for (string d = Path.GetDirectoryName(Path.GetFullPath(fullPath)); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (string.Equals(Path.GetFileName(d), "steam_settings", StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        /// <summary>A file in steam_settings that is byte-identical to the upstream sample it is named after, under
        /// the sample's own name or its ".EXAMPLE"-stripped one.</summary>
        static bool IsSampleCopy(string settingsDir, string fullPath, string contentPath, string exampleDir)
        {
            exampleDir = exampleDir ?? Tools.SettingsExampleDir;
            if (!Directory.Exists(exampleDir)) return false;
            string rel = Path.GetFullPath(fullPath).Substring(settingsDir.Length).TrimStart(Path.DirectorySeparatorChar);
            string root = Path.GetFullPath(exampleDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string kept = Path.Combine(exampleDir, rel);
            string sample = File.Exists(kept) ? kept : Directory.GetFiles(exampleDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(SettingsScaffold.Activated(Path.GetFullPath(f).Substring(root.Length)), rel, StringComparison.OrdinalIgnoreCase));
            return sample != null && new FileInfo(sample).Length == new FileInfo(contentPath).Length
                && SafePersistence.Hash(sample) == SafePersistence.Hash(contentPath);
        }

        static void Add(RestorePlan plan, Dictionary<string, RestoreItem> byPath, RestoreItem item)
        {
            item.Path = Path.GetFullPath(item.Path);
            // A file planned for deletion is deleted only if it still has this content when the plan is applied.
            if (item.Action == RestoreAction.Delete && item.Hash.Length == 0) item.Hash = Hash(item.Path);
            RestoreItem existing;
            if (byPath.TryGetValue(item.Path, out existing))
            {
                // a decision already taken from a backup or the ledger wins over a heuristic one
                if (existing.Action != RestoreAction.Leave || item.Action == RestoreAction.Leave) return;
                plan.Items.Remove(existing);
            }
            byPath[item.Path] = item;
            plan.Items.Add(item);
        }

        static string Hash(string path) { return File.Exists(path) ? SafePersistence.Hash(path) : "absent"; }

        /// <summary>Works out what Restore original would do, changing nothing.</summary>
        /// <param name="appIdHint">Only for the "verify in Steam" advice when no steam_appid.txt says it.</param>
        public static RestorePlan Plan(string exe, string appIdHint, string exampleDir = null)
        {
            var plan = new RestorePlan { Exe = Path.GetFullPath(exe) };
            plan.GameRoot = PatchRunner.SearchRoot(plan.Exe);
            string exeDir = Path.GetDirectoryName(plan.Exe);
            string companion = PatchRunner.UnrealCompanion(plan.Exe);
            var libs = PatchRunner.FindSteamApiFiles(plan.GameRoot);
            var dirs = new List<string> { exeDir };
            if (companion != null) dirs.Add(Path.GetDirectoryName(companion));
            dirs.AddRange(libs.Select(Path.GetDirectoryName));
            dirs = dirs.Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plan.TouchedDirs.AddRange(dirs);
            plan.AppId = PatchRunner.FindExistingAppId(dirs.ToArray());
            if (plan.AppId.Length == 0) plan.AppId = AppIdDetector.Normalize(appIdHint);
            string rootPrefix = plan.GameRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var byPath = new Dictionary<string, RestoreItem>(StringComparer.OrdinalIgnoreCase);

            // ---- the ledgers and the backups beside every library / exe folder
            var ledgers = new List<BackupLedger>();
            foreach (var dir in dirs)
            {
                string b = OriginalBackups.Existing(dir);
                if (b == null || plan.BackupDirs.Contains(b, StringComparer.OrdinalIgnoreCase)) continue;
                plan.BackupDirs.Add(b);
                var ledger = BackupLedger.Load(b);
                if (ledger != null) ledgers.Add(ledger);
            }
            plan.HasLedger = ledgers.Count > 0;
            Func<string, BackupLedger.Entry> ledgerOf = p => ledgers.Select(l => l.Find(p)).FirstOrDefault(e => e != null);

            foreach (var b in plan.BackupDirs)
                foreach (var original in Originals(b))
                {
                    string target = original.Key, backup = original.Value;
                    if (!Path.GetFullPath(target).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) { plan.KeepBackups = true; continue; }
                    string backupHash = SafePersistence.Hash(backup);
                    if (PatchRunner.LooksLikeBundledGoldberg(backup) || (PatchRunner.IsSteamApiName(Path.GetFileName(target)) && InstallCheck.IsEmulatorDll(backup)))
                    {
                        Add(plan, byPath, new RestoreItem { Action = RestoreAction.Leave, Path = target, Reason = "its backup is itself the emulator, not an original", Attention = true });
                        continue;
                    }
                    string current = Hash(target);
                    if (current == backupHash) continue;   // already the original
                    var entry = ledgerOf(target);
                    bool ours = current == "absent"
                        || (entry != null && entry.Written == current)
                        || LooksPatcherMade(target, null, exampleDir)
                        || UnpackedByUs(target, backup);
                    if (ours) Add(plan, byPath, new RestoreItem { Action = RestoreAction.Restore, Path = target, Source = backup, Hash = backupHash });
                    else
                    {
                        plan.KeepBackups = true;
                        Add(plan, byPath, new RestoreItem { Action = RestoreAction.Leave, Path = target, Reason = "changed since the patch (a game update?) – the backup is kept", Attention = true });
                    }
                }

            // ---- files the ledger says a patch created
            foreach (var entry in ledgers.SelectMany(l => l.Entries))
            {
                if (!entry.Created || !File.Exists(entry.Path)) continue;
                if (!entry.Path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (Hash(entry.Path) == entry.Written)
                    Add(plan, byPath, new RestoreItem { Action = RestoreAction.Delete, Path = entry.Path });
                else
                    Add(plan, byPath, new RestoreItem { Action = RestoreAction.Leave, Path = entry.Path, Reason = "changed after the patch", Attention = true });
            }

            // ---- the same files found by their content (installs patched before the ledger)
            foreach (var dir in dirs)
            {
                string appIdTxt = Path.Combine(dir, "steam_appid.txt");
                if (File.Exists(appIdTxt) && LooksPatcherMade(appIdTxt, null, exampleDir) && ledgerOf(appIdTxt) == null)
                    Add(plan, byPath, new RestoreItem { Action = RestoreAction.Delete, Path = appIdTxt });
                string legacyIfaces = Path.Combine(dir, "steam_interfaces.txt");
                if (File.Exists(legacyIfaces) && ledgerOf(legacyIfaces) == null && OriginalBackups.Existing(dir) != null)
                    Add(plan, byPath, new RestoreItem { Action = RestoreAction.Delete, Path = legacyIfaces });
                string settings = Path.Combine(dir, "steam_settings");
                if (Directory.Exists(settings))
                    foreach (var f in Directory.GetFiles(settings, "*", SearchOption.AllDirectories))
                    {
                        if (f.IndexOf(Path.DirectorySeparatorChar + ".gp-recovery" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (ledgerOf(f) != null || byPath.ContainsKey(Path.GetFullPath(f))) continue;
                        if (LooksPatcherMade(f, null, exampleDir)) Add(plan, byPath, new RestoreItem { Action = RestoreAction.Delete, Path = f });
                        else Add(plan, byPath, new RestoreItem { Action = RestoreAction.Leave, Path = f, Reason = "not provably made by Shibaberg" });
                    }
            }

            // ---- an emulator dll with nothing to put back
            foreach (var lib in libs)
            {
                RestoreItem item;
                if (byPath.TryGetValue(Path.GetFullPath(lib), out item) && item.Action == RestoreAction.Restore) continue;
                if (!InstallCheck.IsEmulatorDll(lib)) continue;
                var entry = ledgerOf(lib);
                if (entry != null && entry.Created && Hash(lib) == entry.Written) continue;   // a dll the patch added: deleted above
                Add(plan, byPath, new RestoreItem { Action = RestoreAction.Leave, Path = lib, Reason = "the emulator, and no original was backed up", Attention = true });
                string id = plan.AppId.Length > 0 ? plan.AppId : "<appid>";
                string note = "No original of " + Path.GetFileName(lib) + " was kept: let Steam put it back with Verify integrity of game files (steam://validate/" + id + ").";
                if (!plan.Notes.Contains(note)) plan.Notes.Add(note);
            }
            return plan;
        }

        /// <summary>The verified originals in a backup folder: target path → backup file. Current and v0.5 layouts
        /// (sources\&lt;key&gt;\&lt;name&gt; + .source.txt, which names the target), and the flat legacy layout
        /// (goldberg_backup\&lt;name&gt; beside the library), which is trusted only for files still present.</summary>
        static Dictionary<string, string> Originals(string backupDir)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string sources = Path.Combine(backupDir, "sources");
            if (Directory.Exists(sources))
                foreach (var manifest in Directory.GetFiles(sources, "*.source.txt", SearchOption.AllDirectories))
                {
                    string backup = manifest.Substring(0, manifest.Length - ".source.txt".Length);
                    if (!File.Exists(backup) || backup.IndexOf(".corrupt-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    try
                    {
                        var lines = File.ReadAllLines(manifest);
                        if (lines.Length != 2 || lines[1] != SafePersistence.Hash(backup)) continue;
                        string target = Path.GetFullPath(lines[0]);
                        // the current (16-digit) location wins over a v0.5 one for the same file
                        if (!found.ContainsKey(target) || backup.Length < found[target].Length) found[target] = backup;
                    }
                    catch { }
                }
            string parent = Path.GetDirectoryName(Path.GetFullPath(backupDir).TrimEnd(Path.DirectorySeparatorChar));
            foreach (var flat in Directory.GetFiles(backupDir))
            {
                string name = Path.GetFileName(flat);
                string target = Path.Combine(parent, name);
                if (name.Equals(BackupLedger.FileName, StringComparison.OrdinalIgnoreCase) || found.ContainsKey(target) || !File.Exists(target)) continue;
                if (!PatchRunner.IsSteamApiName(name) && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (new FileInfo(flat).Length > 0) found[target] = flat;
            }
            return found;
        }

        /// <summary>An exe whose live copy has no SteamStub while its backup has one: Shibaless unpacked it.</summary>
        static bool UnpackedByUs(string target, string backup)
        {
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            return Shibaless.ShibalessUnpacker.HasStubSection(backup) == true && Shibaless.ShibalessUnpacker.HasStubSection(target) == false;
        }

        /// <summary>Carries the plan out. Every original is copied back through a staged, hash-checked replace; if
        /// one fails, the ones already put back are undone and nothing is deleted. Files are deleted only after
        /// all originals are back, and the backup folders and staging areas only once nothing failed or stayed
        /// behind.</summary>
        public static RestoreResult Apply(RestorePlan plan, Action<LogLevel, string> log, CancellationToken ct = default(CancellationToken))
        {
            var result = new RestoreResult();
            Action<LogLevel, string> say = (l, m) => { if (log != null) log(l, m); };
            if (CloudSaves.GameRunning(plan.GameRoot))
            {
                result.Blocked = true;
                result.Messages.Add("The game is running from " + plan.GameRoot + " – close it first.");
                say(LogLevel.Error, result.Messages[0]);
                return result;
            }

            var writes = new List<FileWriteRecord>();
            try
            {
                foreach (var item in plan.Of(RestoreAction.Restore))
                {
                    ct.ThrowIfCancellationRequested();
                    SafePersistence.Copy(item.Source, item.Path, writes, null, null, item.Hash);
                    if (SafePersistence.Hash(item.Path) != item.Hash) throw new IOException(Path.GetFileName(item.Path) + " did not verify after the copy.");
                    result.Restored++;
                    say(LogLevel.Ok, "Restored " + PatchRunner.ShortRel(plan.GameRoot, item.Path));
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Messages.Add(ex is OperationCanceledException ? "Cancelled." : "Could not restore: " + ex.Message);
                say(LogLevel.Error, result.Messages[result.Messages.Count - 1]);
                var undo = Recovery.RollbackWrites(writes, say);
                if (undo.Failed == 0) Recovery.Discard(writes);
                say(undo.Failed == 0 ? LogLevel.Warn : LogLevel.Error, "Restore stopped; " + (undo.Failed == 0
                    ? "the " + result.Restored + " file(s) already put back were returned to how they were."
                    : "undoing it was incomplete: " + undo.Summary));
                result.Restored = 0;
                return result;
            }
            Recovery.Discard(writes);   // every original is back and verified; the staged copies are not needed

            foreach (var item in plan.Of(RestoreAction.Delete))
            {
                try
                {
                    if (!File.Exists(item.Path)) continue;
                    if (item.Hash.Length > 0 && SafePersistence.Hash(item.Path) != item.Hash)
                    {
                        result.Left++; result.NeedAttention++;
                        say(LogLevel.Warn, "Left " + PatchRunner.ShortRel(plan.GameRoot, item.Path) + " – it changed after the restore was planned.");
                        continue;
                    }
                    File.Delete(item.Path);
                    result.Deleted++;
                    say(LogLevel.Ok, "Removed " + PatchRunner.ShortRel(plan.GameRoot, item.Path));
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Messages.Add("Could not remove " + Path.GetFileName(item.Path) + ": " + ex.Message);
                    say(LogLevel.Warn, result.Messages[result.Messages.Count - 1]);
                }
            }
            foreach (var item in plan.Of(RestoreAction.Leave))
            {
                result.Left++;
                if (item.Attention) result.NeedAttention++;
                say(item.Attention ? LogLevel.Warn : LogLevel.Dim, "Left " + PatchRunner.ShortRel(plan.GameRoot, item.Path) + " – " + item.Reason);
            }
            foreach (var n in plan.Notes) say(LogLevel.Warn, n);
            RemoveEmptySettings(plan);

            if (result.Failed == 0 && !plan.KeepBackups)
            {
                foreach (var b in plan.BackupDirs) TryDeleteDir(b);
                foreach (var d in plan.TouchedDirs) TryDeleteDir(Path.Combine(d, ".gp-recovery"));
                // The undo record of the last patch points at the files just restored: it has nothing left to undo.
                string prefix = plan.GameRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Recovery.LoadJournal(Recovery.JournalPath).Any(e => e.Destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    Recovery.DiscardLastPatch();
                if (plan.BackupDirs.Count > 0) say(LogLevel.Dim, "Backup folder(s) removed: " + string.Join(", ", plan.BackupDirs.Select(b => PatchRunner.ShortRel(plan.GameRoot, b)).ToArray()));
            }
            else if (plan.BackupDirs.Count > 0) say(LogLevel.Dim, "The backup folder(s) are kept, since not everything could be restored.");

            if (result.Failed == 0 && (result.Restored > 0 || result.Deleted > 0))
            {
                try { PlayniteLink.Forget(plan.Exe); } catch { }
            }
            return result;
        }

        static void RemoveEmptySettings(RestorePlan plan)
        {
            foreach (var d in plan.TouchedDirs)
            {
                string settings = Path.Combine(d, "steam_settings");
                if (!Directory.Exists(settings)) continue;
                try
                {
                    foreach (var sub in Directory.GetDirectories(settings, "*", SearchOption.AllDirectories).OrderByDescending(s => s.Length))
                        if (Directory.GetFileSystemEntries(sub).Length == 0) Directory.Delete(sub);
                    if (Directory.GetFileSystemEntries(settings).Length == 0) Directory.Delete(settings);
                }
                catch { }
            }
        }

        static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}
