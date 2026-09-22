# Goldberg Patcher

A one-click auto-patcher for [Goldberg Steam Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) (GSE). Drop a game executable, enter the AppID, and it takes care of everything: DRM unpacking, installing the emulator dlls, `steam_appid.txt`, interface generation, and setting up the `steam_settings` folder.

Written in C# (.NET Framework 4.8, WinForms) as a single self-contained Windows executable.

## Features

- **Automatic game analysis** — detects x86/x64 (including .NET AnyCPU executables) from PE headers, and reads the executable's import table to install the emulator under the exact name the loader will ask for
- **DRM unpacking** — uses [Steamless](https://github.com/atom0s/Steamless) to automatically unpack common Steam DRM variants so the game runs without Steam
- **Backup & restore** — originals are saved to `<game>\goldberg_backup\sources\<pathhash>\`; online-fix only reverts a dll that is provably one of the bundled Goldberg builds, restoring it from that backup tree
- **Interface generation** — runs GSE's `generate_interfaces` tool against the *original* dll so the emulator responds to exactly the interfaces the game requests
- **steam_settings scaffolding** — optionally creates a ready-to-edit `steam_settings` folder from GSE's example files, with the generated `steam_interfaces.txt` placed inside
- **Online-fix mode** — keeps the original Steamworks dll and registers the game on your real Steam account as Spacewar (AppID 480), so multiplayer traffic goes through Steam's own servers without replacing anything
- **Undo** — every write is journalled, so a patch that fails or is cancelled part-way can be rolled back; "Undo last patch" also works after a restart
- **Self-contained binary** — all tools and payload files are embedded in the exe, compressed, and extracted to `%LOCALAPPDATA%\GoldbergPatcher\payload\<build>\` on first run; one file is all you need

## Quick start

1. Run `Goldberg Patcher.exe` (Windows 10/11, x64 or x86)
2. Drag & drop your game `.exe` onto the window (or click it to browse)
3. Enter the Steam AppID — found on [steamdb.info](https://steamdb.info/) under *App ID* (the app tries to detect/cache it for you)
4. Adjust the options if needed, then click **Patch Game**

| Option | Default | What it does |
| --- | --- | --- |
| Unpack DRM (Steamless) | on | Runs Steamless on the exe to remove Steam DRM |
| Backup originals | on | Copies replaced files to `goldberg_backup\sources\` before overwriting |
| Write steam_appid.txt | on | Writes the AppID next to the dlls and beside the game exe |
| Create steam_settings folder | off | Creates a settings folder from GSE's examples, ready for custom configs |
| Auto-detect Steam AppID online | on | Looks the game up on the Steam Store when no local AppID is found |
| Generic online-fix | off | Keeps the original dll and presents the game as Spacewar (AppID 480) |

`steam_interfaces.txt` generation is not a toggle — it runs against the original dll whenever one is available and the emulator dll is being installed.

After patching, launch the game normally. If it does not work out of the box, read [release/README.release.md](release/README.release.md) — it documents every GSE configuration option (achievements, stats, controller bindings, leaderboards, etc.).

### Online-fix mode

For games that need to talk to a real Steam backend (some multiplayer titles), enable **Online fix**. The patcher:

1. Writes `steam_appid.txt` with AppID `480` (Spacewar) — the only file it changes by default
2. Keeps your genuine `steam_api(64).dll` in place; a live dll is never replaced or downgraded unless it is byte-identical to one of the bundled Goldberg emulator builds, in which case the original from `goldberg_backup\sources\` is restored (the emulator cannot attach to a real Steam client)
3. Creates the `steam_settings` scaffold folder

The game then attaches to your real Steam account as Spacewar and all traffic is routed through Valve's servers. You must be online with Steam running.

## Command line

The GUI executable is also headless-capable, which is how the live test drives it:

```text
Goldberg Patcher.exe --exe <game.exe> [--appid <id>] [--auto] [--exit-when-done]
Goldberg Patcher.exe --batch "<game.exe>|<id>;<game.exe>" [--online-fix] [--no-unpack] [--settings]
Goldberg Patcher.exe --verify-payload
```

- `--auto` is what actually starts a run; `--appid` on its own only pre-fills the box.
- `--batch` is the only mode that ignores `settings.ini`, so it is the way to script a patch without the GUI's saved options interfering.
- Exits: batch `0` = every entry patched, `1` = invalid input or failures, `2` = nothing patched. Single-game `0` = patched, `1` = bad arguments or failure, `3` = `--auto` could not resolve an AppID. `--verify-payload` `0` = payload intact, `1` = missing or corrupt.

## Building from source

Requirements: Windows and Visual Studio Build Tools with the Roslyn C# compiler (`csc.exe`). The script finds the compiler through `vswhere.exe` and prefers the .NET Framework 4.8 reference assemblies, falling back to the installed Framework if the targeting pack is absent.

```powershell
.\build.ps1          # build
.\build.ps1 -Verify  # build, then run the self-test
```

The script compiles two binaries:

- `_selftest.exe` — headless console self-test (PE analysis, recovery, payload, and regression coverage)
- `Goldberg Patcher.exe` — the GUI app, with the entire toolchain embedded as resources

The payload file list lives in `build.ps1`. Adding or removing files there changes the embedded set; a file listed but missing from disk fails the build rather than producing a broken exe.

## Verification

```powershell
.\_selftest.exe        # from the repo root - reads payload files beside itself
.\_live_test.ps1       # patches a throwaway game under %TEMP% and asserts the artifacts
```

`_live_test.ps1` exercises the real pipeline end to end: it patches a copy of the Steamless CLI standing in for a game exe, then asserts the exit code, `steam_appid.txt`, the dll replacement, the hash-verified backup, the absence of `.gp-recovery` litter, a rollback-ready journal, the retained recovery copy, and payload self-repair. It exits non-zero on failure, so it can gate a build.

## Repository layout

```
src/                      C# sources (Core.cs = patch pipeline + PE reader, Ui.cs, MainForm.cs, Batch.cs)
Goldberg Patcher.exe      built GUI app (self-contained)
_selftest.exe             built console self-test
steamless/                Steamless CLI + unpacker plugins (DRM removal)
release/regular/          Goldberg emulator steam_api.dll / steam_api64.dll
release/experimental/     experimental GSE builds (CPY dll crack support, overlay)
release/tools/            GSE command-line helpers (generate_interfaces, lobby_connect)
release/steam_settings.EXAMPLE   example config tree used when scaffolding settings
build.ps1                 build script
```

The review notes (`optimizations.md`, `plan.md`), `AGENTS.md` and the local agent state folder are kept in the working copy but not published — they document in-progress work and internal decisions.

## Credits

- [Mr. Goldberg — Goldberg Steam Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) — the emulator itself; see [release/CREDITS.md](release/CREDITS.md) for its third-party licenses
- [atom0s — Steamless](https://github.com/atom0s/Steamless) — Steam DRM unpacker used in this tool

## License

This repository bundles third-party binaries (GSE, Steamless and their dependencies) under their respective licenses; see [release/CREDITS.md](release/CREDITS.md).
