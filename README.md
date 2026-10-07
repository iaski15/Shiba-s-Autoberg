# Shibaberg

A one-click auto-patcher for [Goldberg Steam Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) (GSE). Drop a game executable, enter the AppID, and it takes care of everything: DRM unpacking, installing the emulator dlls, `steam_appid.txt`, interface generation, and setting up the `steam_settings` folder.

Written in C# (.NET Framework 4.8, WinForms) as a single self-contained Windows executable.

## Features

- **Automatic game analysis** — detects x86/x64 (including .NET AnyCPU executables) from PE headers, and reads the executable's import table to install the emulator under the exact name the loader will ask for
- **DRM unpacking** — removes SteamStub DRM (every variant Steamless handles, 1.0 to 3.1) with **Shibaless**, our fork of [Steamless](https://github.com/atom0s/Steamless), compiled into the app and run in-process: no helper process, no temporary `.unpacked.exe`, and the result is validated before it replaces the game exe
- **Backup & restore** — originals are saved to `<game>\goldberg_backup\sources\<pathhash>\`; online-fix only reverts a dll that is provably one of the bundled Goldberg builds, restoring it from that backup tree
- **Interface generation** — scans the *original* dll for its interface versions (what GSE's `generate_interfaces` does, in-process) so the emulator responds to exactly the interfaces the game requests
- **steam_settings scaffolding** — optionally creates a ready-to-edit `steam_settings` folder from GSE's example files, with the generated `steam_interfaces.txt` placed inside
- **Online-fix mode** — keeps the original Steamworks dll and registers the game on your real Steam account as Spacewar (AppID 480), so multiplayer traffic goes through Steam's own servers without replacing anything
- **Undo** — every write is journalled, so a patch that fails or is cancelled part-way can be rolled back; "Undo last patch" also works after a restart
- **In-game achievement popups** — an Xbox-style toast with the shiba, plus a restyled Shift+Tab overlay; the achievement list comes from the Steam client's local cache (`appcache\stats`), no web API
- **Cloud saves** — your saves keep going to the game's own folders and a copy is synced with **your own Google Drive**: pulled when the game starts, uploaded when it exits, last 10 backups per game kept; sign in from the Shift+Tab overlay or the Cloud saves window
- **Self-contained binary** — all tools and payload files are embedded in the exe, compressed, and extracted to `%LOCALAPPDATA%\GoldbergPatcher\payload\<build>\` on first run; one file is all you need

## Quick start

1. Download `Goldberg-Patcher-<version>.exe` from the [Releases](https://github.com/iaski15/Shiba-s-Autoberg/releases) page and run it (Windows 10/11, x64 or x86) – or build it yourself, see below
2. Drag & drop your game `.exe` onto the window (or click it to browse)
3. Enter the Steam AppID — found on [steamdb.info](https://steamdb.info/) under *App ID* (the app tries to detect/cache it for you)
4. Adjust the options if needed, then click **Patch Game**

| Option | Default | What it does |
| --- | --- | --- |
| Unpack DRM (Shibaless) | on | Removes SteamStub DRM from the exe (skipped instantly when it has no `.bind` section) |
| Backup originals | on | Copies replaced files to `goldberg_backup\sources\` before overwriting |
| Write steam_appid.txt | on | Writes the AppID next to the dlls and beside the game exe |
| Create steam_settings folder | off | Creates a settings folder from GSE's examples, ready for custom configs |
| Auto-detect Steam AppID online | on | Looks the game up on the Steam Store when no local AppID is found |
| Generic online-fix | off | Keeps the original dll and presents the game as Spacewar (AppID 480) |
| In-game overlay: achievements + cloud saves | off | Installs the overlay build of the emulator, the game's achievement list, and cloud-save syncing (Shibaberg.exe must stay where it was when patching) |

`steam_interfaces.txt` generation is not a toggle — it runs against the original dll whenever one is available and the emulator dll is being installed.

After patching, launch the game normally. If it does not work out of the box, read [shibaberg/post_build/README.release.md](shibaberg/post_build/README.release.md) — it documents every emulator configuration option (achievements, stats, controller bindings, leaderboards, etc.).

### Online-fix mode

For games that need to talk to a real Steam backend (some multiplayer titles), enable **Online fix**. The patcher:

1. Writes `steam_appid.txt` with AppID `480` (Spacewar) — the only file it changes by default
2. Keeps your genuine `steam_api(64).dll` in place; a live dll is never replaced or downgraded unless it is byte-identical to one of the bundled Goldberg emulator builds, in which case the original from `goldberg_backup\sources\` is restored (the emulator cannot attach to a real Steam client)
3. Creates the `steam_settings` scaffold folder

The game then attaches to your real Steam account as Spacewar and all traffic is routed through Valve's servers. You must be online with Steam running.

## Command line

The GUI executable is also headless-capable, which is how the live test drives it:

```text
Shibaberg.exe --exe <game.exe> [--appid <id>] [--auto] [--exit-when-done]
Shibaberg.exe --batch "<game.exe>|<id>;<game.exe>" [--online-fix] [--no-unpack] [--settings] [--achievements]
Shibaberg.exe --check <game.exe> [--appid <id>] [--online-fix]
Shibaberg.exe --verify-payload
```

- `--auto` is what actually starts a run; `--appid` on its own only pre-fills the box.
- `--batch` is the only mode that ignores `settings.ini`, so it is the way to script a patch without the GUI's saved options interfering.
- `--check` verifies an already-patched install without changing anything: SteamStub removed, the Steamworks library the game actually loads is the emulator (or Valve's original for online-fix, inferred when every `steam_appid.txt` says 480 and no library is the emulator), architecture match, and the AppID. The same check runs automatically after every patch.
- Exits: batch `0` = every entry patched, `1` = invalid input or failures, `2` = nothing patched. Single-game `0` = patched, `1` = bad arguments or failure, `3` = `--auto` could not resolve an AppID. `--check` `0` = no failed checks, `1` = a check failed. `--verify-payload` `0` = payload intact, `1` = missing or corrupt.

For development, `_selftest.exe --corpus <folder>` unpacks every SteamStub exe under a folder twice with Shibaless — through the in-memory path the patcher uses, and through the fork's untouched upstream path that writes `<exe>.unpacked.exe` exactly as Steamless.CLI does — and compares the results byte for byte (it works on temp copies and never writes into the folder). `SAME*` means only the certificate-table pointer differs: we move it with the overlay, upstream leaves it stale.

## Building from source

Requirements: Windows and Visual Studio Build Tools with the Roslyn C# compiler (`csc.exe`). The script finds the compiler through `vswhere.exe` and prefers the .NET Framework 4.8 reference assemblies, falling back to the installed Framework if the targeting pack is absent.

```powershell
.\build.ps1          # build
.\build.ps1 -Verify  # build, then run the self-test
```

The script compiles two binaries:

- `_selftest.exe` — headless console self-test (PE analysis, recovery, payload, and regression coverage)
- `Shibaberg.exe` — the GUI app, with the entire toolchain embedded as resources

The payload file list lives in `build.ps1`. Adding or removing files there changes the embedded set; a file listed but missing from disk fails the build rather than producing a broken exe.

## Verification

```powershell
.\_selftest.exe        # from the repo root - reads payload files beside itself
.\_live_test.ps1       # patches a throwaway game under %TEMP% and asserts the artifacts
```

`_live_test.ps1` exercises the real pipeline end to end: it patches a throwaway game made of stock 32-bit Windows files (`cmd.exe` as the game exe, `version.dll` as its original `steam_api.dll`), then asserts the exit code, `steam_appid.txt`, the dll replacement, the hash-verified backup, the absence of `.gp-recovery` litter, a rollback-ready journal, the retained recovery copy, and payload self-repair. It exits non-zero on failure, so it can gate a build.

## Repository layout

```
src/                      C# sources (Core.cs = patch pipeline + PE reader, Cloud.cs = cloud saves, Ui.cs, MainForm.cs, Batch.cs, CloudForm.cs)
Shibaberg.exe      built GUI app (self-contained) – build output, not tracked
_selftest.exe             built console self-test – build output, not tracked
shibaless/                Shibaless: our fork of Steamless (API + 7 unpackers), compiled into the app; see VENDORED.md
shibaberg/                Shibaberg: our fork of gbe_fork (the Goldberg emulator); see VENDORED.md
shibaberg/bin/            the shipped steam_api.dll / steam_api64.dll, built from shibaberg/, + BUILD.txt (commit, SHA-256s)
shibaberg/bin/overlay/    the overlay build (achievement toast, Shift+Tab overlay, cloud saves) + its own BUILD.txt
shibaberg/post_build/steam_settings.EXAMPLE   example config tree used when scaffolding settings
tools/build-shibaberg.ps1 rebuilds shibaberg/bin from shibaberg/ (not part of build.ps1)
build.ps1                 build script
```

The review notes (`optimizations.md`, `plan.md`), `AGENTS.md` and the local agent state folder are kept in the working copy but not published — they document in-progress work and internal decisions.

## Privacy

Shibaberg has no servers and collects nothing. Cloud saves are optional and only talk to Google:

- Your saves are uploaded to **your own Google Drive** (folder `Shibaberg Saves`). Shibaberg asks only for the `drive.file` permission, so it can see and change **only the files it created there** — nothing else in your Drive.
- Your Google sign-in is kept on your PC, encrypted for your Windows user (`%APPDATA%\GoldbergPatcher\google.dat`). Signing out revokes it.
- Nothing is shared with the developers or anyone else. To remove everything: sign out, then delete the `Shibaberg Saves` folder from your Drive.

Use of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.

## Credits

- [Mr. Goldberg — Goldberg Steam Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) — the original emulator (discontinued)
- [Detanup01 — gbe_fork](https://github.com/Detanup01/gbe_fork) — its maintained continuation; our fork of it, Shibaberg, lives in `shibaberg/` (see its `VENDORED.md`, and `CREDITS.md` for its third-party licenses)
- [atom0s — Steamless](https://github.com/atom0s/Steamless) — the SteamStub unpackers; our fork of them, Shibaless, lives in `shibaless/` (renamed throughout, plus one small patch) (see its `VENDORED.md`)

## License

This repository bundles third-party code and binaries (gbe_fork as Shibaberg, Steamless as Shibaless, and their dependencies) under their respective licenses; see [shibaberg/CREDITS.md](shibaberg/CREDITS.md) and each fork's `LICENSE`.
