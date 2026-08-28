[README.md](https://github.com/user-attachments/files/31574058/README.md)
# Goldberg Patcher

A one-click auto-patcher for [Goldberg Steam Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) (GSE). Drop a game executable, enter the AppID, and it takes care of everything: DRM unpacking, installing the emulator dlls, `steam_appid.txt`, interface generation, and setting up the `steam_settings` folder.

Written in C# (.NET Framework 4.8, WinForms) as a single self-contained Windows executable.

## Features

- **Automatic game analysis** — detects x86/x64 (including .NET AnyCPU executables) from PE headers and picks the matching `steam_api.dll` / `steam_api64.dll`
- **DRM unpacking** — uses [Steamless](https://gitlab.com/Mr_Goldberg/steamless) to automatically unpack common Steam DRM variants so the game runs without Steam
- **Backup & restore** — originals are saved to `<game>\goldberg_backup\`; online-fix mode restores them before applying its own setup
- **Interface generation** — runs GSE's `generate_interfaces` tool against the *original* dll so the emulator responds to exactly the interfaces the game requests
- **steam_settings scaffolding** — optionally creates a ready-to-edit `steam_settings` folder from GSE's example files, with the generated `steam_interfaces.txt` placed inside
- **Online-fix mode** — keeps the original Steamworks dll and registers the game on your real Steam account as Spacewar (AppID 480), so multiplayer traffic goes through Steam's own servers without replacing anything
- **Self-contained binary** — all tools and payload files are embedded in the exe and restored next to it when missing; one file is all you need

## Quick start

1. Run `Goldberg Patcher.exe` (Windows 10/11, x64 or x86)
2. Drag & drop your game `.exe` onto the window (or click it to browse)
3. Enter the Steam AppID — found on [steamdb.info](https://steamdb.info/) under *App ID* (the app tries to detect/cache it for you)
4. Adjust the options if needed, then click **Patch Game**

| Option | Default | What it does |
| --- | --- | --- |
| Unpack DRM (Steamless) | on | Runs Steamless on the exe to remove Steam DRM |
| Backup originals | on | Copies replaced files to `goldberg_backup\` before overwriting |
| Write steam_appid.txt | on | Writes the AppID next to the dlls and beside the game exe |
| Create steam_settings folder | off | Creates a settings folder from GSE's examples, ready for custom configs |
| Generate interfaces | on | Generates `steam_interfaces.txt` from the original Steamworks dll |

After patching, launch the game normally. If it does not work out of the box, read [release/README.release.md](release/README.release.md) — it documents every GSE configuration option (achievements, stats, controller bindings, leaderboards, etc.).

### Online-fix mode

For games that need to talk to a real Steam backend (some multiplayer titles), enable **Online fix**. The patcher:

1. Restores the original dlls from `goldberg_backup\` if this game was previously patched
2. Keeps your genuine `steam_api(64).dll` in place
3. Writes `steam_appid.txt` with AppID `480` (Spacewar)

The game then attaches to your real Steam account as Spacewar and all traffic is routed through Valve's servers. You must be online with Steam running.

## Building from source

Requirements: Windows, Visual Studio Build Tools with the Roslyn C# compiler (`csc.exe`) and .NET Framework 4.8 reference assemblies.

```powershell
.\build.ps1
```

The script compiles two binaries:

- `_selftest.exe` — headless console self-test (PE analysis + payload check)
- `Goldberg Patcher.exe` — the GUI app, with the entire toolchain embedded as resources (~60 files from `steamless\`, `release\regular\`, and `release\tools\`)

## Repository layout

```
src/                      C# sources (Core.cs = patch pipeline + PE reader, Ui.cs, MainForm.cs)
Goldberg Patcher.exe      built GUI app (self-contained)
_selftest.exe             built console self-test
steamless/                Steamless CLI + unpacker plugins (DRM removal)
release/regular/          Goldberg emulator steam_api.dll / steam_api64.dll
release/experimental/     experimental GSE builds (CPY dll crack support, overlay)
release/tools/            GSE command-line helpers (generate_interfaces, lobby_connect)
release/steam_settings.EXAMPLE   example config tree used when scaffolding settings
build.ps1                 build script
```

## Credits

- [Mr. Goldberg — Goldberg Steam Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) — the emulator itself; see [release/CREDITS.md](release/CREDITS.md) for its third-party licenses
- [Steamless](https://gitlab.com/Mr_Goldberg/steamless) — Steam DRM unpacker used in this tool

## License

This repository bundles third-party binaries (GSE, Steamless and their dependencies) under their respective licenses; see [release/CREDITS.md](release/CREDITS.md).
