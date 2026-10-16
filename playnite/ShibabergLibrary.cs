using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace ShibabergPlaynite
{
    /// <summary>The "Shibaberg" library in Playnite: every game Shibaberg patched, with a Play button that starts
    /// the patched exe. Shibaberg writes the list (playnite.txt in its state folder, see src\Playnite.cs) and
    /// installs this extension; Playnite reads the list on every library update, and imports new entries at once
    /// when Shibaberg opens playnite://shibaberg/import. Built against Playnite.SDK 6.18 by build.ps1.</summary>
    public class ShibabergLibrary : LibraryPlugin
    {
        static readonly ILogger logger = LogManager.GetLogger();
        static readonly Guid PluginId = Guid.Parse("a2af5675-6842-452b-9357-83a70a2098ca");

        readonly string icon;

        public override Guid Id { get { return PluginId; } }
        public override string Name { get { return "Shibaberg"; } }
        public override string LibraryIcon { get { return icon; } }

        public ShibabergLibrary(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties { HasSettings = false, CanShutdownClient = false };
            icon = Path.Combine(Path.GetDirectoryName(typeof(ShibabergLibrary).Assembly.Location), "icon.png");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            api.UriHandler.RegisterSource("shibaberg", OnUri);
        }

        // ---------------------------------------------------------------- the list Shibaberg writes

        sealed class Entry
        {
            public string GameId, AppId, Name, Exe, InstallDir;
        }

        /// <summary>Shibaberg's state folder: SHIBABERG_STATE_DIR when set (test runs), else %APPDATA%\GoldbergPatcher.</summary>
        static string StateDir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("SHIBABERG_STATE_DIR");
                return !string.IsNullOrEmpty(over) ? over
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldbergPatcher");
            }
        }

        /// <summary>gameId, appId, name, exe, installDir, addedUtc - tab-separated, '#' starts a comment. Lines
        /// that do not have that shape are skipped.</summary>
        static List<Entry> ReadList(CancellationToken ct)
        {
            var list = new List<Entry>();
            string path = Path.Combine(StateDir, "playnite.txt");
            if (!File.Exists(path)) return list;
            string[] lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                lines = sr.ReadToEnd().Split('\n');
            foreach (var raw in lines)
            {
                ct.ThrowIfCancellationRequested();
                string line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                var f = line.Split('\t');
                if (f.Length < 6 || !f[0].StartsWith("sb-") || f[2].Trim().Length == 0 || f[3].Trim().Length == 0) continue;
                if (list.Any(e => e.GameId == f[0])) continue;
                list.Add(new Entry { GameId = f[0], AppId = f[1].Trim(), Name = f[2].Trim(), Exe = f[3].Trim(), InstallDir = f[4].Trim() });
            }
            return list;
        }

        // ---------------------------------------------------------------- metadata

        static bool IsSteamApp(string appId)
        {
            uint id;
            return uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0 && id != 480;
        }

        /// <summary>A path below the install folder, written with Playnite's {InstallDir} variable so the game
        /// survives the folder being moved and re-pointed in Playnite; absolute otherwise.</summary>
        static string UnderInstallDir(string path, string installDir)
        {
            if (string.IsNullOrEmpty(installDir)) return path;
            string root = installDir.TrimEnd('\\') + "\\";
            if (string.Equals(path.TrimEnd('\\'), installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "{InstallDir}";
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "{InstallDir}\\" + path.Substring(root.Length) : path;
        }

        /// <summary>True when the URL answers a HEAD request with 200 within 3 seconds.</summary>
        static bool Exists(string url, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = req.ReadWriteTimeout = 3000;
                using (ct.Register(() => req.Abort()))
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        GameMetadata Build(Entry e, bool withArt, CancellationToken ct)
        {
            string exeDir = Path.GetDirectoryName(e.Exe);
            var meta = new GameMetadata
            {
                Name = e.Name,
                GameId = e.GameId,
                Source = new MetadataNameProperty("Shibaberg"),
                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
                IsInstalled = File.Exists(e.Exe),
                InstallDirectory = e.InstallDir,
                GameActions = new List<GameAction>
                {
                    new GameAction
                    {
                        Type = GameActionType.File,
                        Name = "Play",
                        IsPlayAction = true,
                        Path = UnderInstallDir(e.Exe, e.InstallDir),
                        WorkingDir = UnderInstallDir(exeDir, e.InstallDir),
                        TrackingMode = TrackingMode.Default,
                    },
                },
            };
            if (IsSteamApp(e.AppId))
            {
                meta.Links = new List<Link> { new Link("Steam Store", "https://store.steampowered.com/app/" + e.AppId + "/") };
                if (withArt)
                {
                    string cdn = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/" + e.AppId + "/";
                    if (Exists(cdn + "library_600x900.jpg", ct)) meta.CoverImage = new MetadataFile(cdn + "library_600x900.jpg");
                    if (Exists(cdn + "library_hero.jpg", ct)) meta.BackgroundImage = new MetadataFile(cdn + "library_hero.jpg");
                }
            }
            string png = Path.Combine(StateDir, "playnite_icons", e.GameId + ".png");
            if (File.Exists(png)) meta.Icon = new MetadataFile(png);
            return meta;
        }

        bool InDatabase(string gameId)
        {
            return PlayniteApi.Database.Games.Any(g => g.PluginId == Id && g.GameId == gameId);
        }

        // ---------------------------------------------------------------- Playnite entry points

        /// <summary>Runs on Playnite's background library update. Artwork is only looked up for games Playnite does
        /// not have yet (for the others it is never used). Never throws: a broken list must not break the update.</summary>
        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new List<GameMetadata>();
            try
            {
                foreach (var e in ReadList(args.CancelToken))
                {
                    args.CancelToken.ThrowIfCancellationRequested();
                    games.Add(Build(e, !InDatabase(e.GameId), args.CancelToken));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.Error(ex, "Shibaberg: could not read the game list."); }
            return games;
        }

        /// <summary>playnite://shibaberg/import - sent by Shibaberg right after it patched a game while Playnite runs.</summary>
        void OnUri(PlayniteUriEventArgs args)
        {
            if (args.Arguments == null || args.Arguments.Length == 0 || !string.Equals(args.Arguments[0], "import", StringComparison.OrdinalIgnoreCase)) return;
            Task.Run(() =>
            {
                try
                {
                    var fresh = ReadList(CancellationToken.None).Where(e => !InDatabase(e.GameId))
                        .Select(e => Build(e, true, CancellationToken.None)).ToList();
                    if (fresh.Count == 0) return;
                    PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        int added = 0;
                        foreach (var meta in fresh)
                        {
                            // checked again on the UI thread: a library update may have imported it meanwhile
                            if (InDatabase(meta.GameId)) continue;
                            if (PlayniteApi.Database.ImportExclusions.ContainsItem(ImportExclusionItem.GetId(meta.GameId, Id))) continue;
                            PlayniteApi.Database.ImportGame(meta, this);
                            added++;
                        }
                        if (added > 0)
                            PlayniteApi.Notifications.Add(new NotificationMessage("shibaberg-import-" + DateTime.UtcNow.Ticks,
                                "Shibaberg: added " + added + " game" + (added == 1 ? "" : "s") + " to Playnite.", NotificationType.Info));
                    });
                }
                catch (Exception ex) { logger.Error(ex, "Shibaberg: live import failed."); }
            });
        }
    }
}
