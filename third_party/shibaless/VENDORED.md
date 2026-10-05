# Shibaless — our fork of Steamless

Shibaless is our fork of the Steamless SteamStub unpackers, compiled into Goldberg Patcher and run in-process.
It is upstream Steamless with every name changed to Shibaless (folders, files, namespaces, types:
`Shibaless.API`, `Shibaless.Unpacker.*`, `ShibalessPlugin`, `ShibalessOptions`, ...) plus one small patch.
atom0s's copyright/license header in each file and the base plugin's `Author` credit are kept as they are.

- Upstream: https://github.com/atom0s/Steamless
- Pinned commit: `cd770bf9749d3e4f438d23ac643917ad1a804257` (2024-03-29, "Bumped the copyright year of the project.")
  — the commit whose project versions match the Steamless binaries v0.5 shipped:
  Steamless.API 1.0.0.8, Variant10.x86 1.0.0.0, Variant20.x86 1.0.0.3, Variant21.x86 1.0.0.9,
  Variant30.x86 1.0.0.1, Variant30.x64 1.0.0.3, Variant31.x86 1.0.0.3, Variant31.x64 1.0.0.3
  (Steamless.CLI 3.1.0.0). `a571eeb` has the same versions; `cd770bf` only adds a copyright bump on top.
- License: upstream `LICENSE`, kept unmodified beside this file.

## What is vendored

From upstream, renamed by `Rename-FromUpstream.ps1`:

| Upstream | Here |
| --- | --- |
| `Steamless.API/` | `Shibaless.API/` |
| `Steamless.Unpacker.Variant{10,20,21,30,31}.x86/`, `Variant{30,31}.x64/` | `Shibaless.Unpacker.*` (same suffixes) |

- `SharpDisasm.dll` (in the Variant20/21 folders): upstream ships it as a prebuilt binary (SharpDisasm 1.0.0.0,
  SHA-256 `6d3ce990…bc2cf`, identical to the copy the v0.5 payload carried), so it is vendored as a binary too.
  It is a separate library, so it keeps its own name.

Not vendored: `Steamless/` (WPF GUI), `Steamless.CLI/`, `ExamplePlugin/`, `repo/`, `Steamless.sln`.

## Our patches

1. **rename** — `Rename-FromUpstream.ps1`: `Steamless` → `Shibaless` in every folder name, file name and
   code/csproj line except the license header and the `Author` credit. Mechanical; re-run it, never hand-edit.
2. **in-memory-output** — unpackers can hand the rebuilt image back instead of writing `<file>.unpacked.exe`.
   Every changed line carries a `GOLDBERG PATCH (in-memory-output)` comment.
   - `Shibaless.API/Model/ShibalessOptions.cs`: new `OutputStreamFactory` (`Func<string, Stream>`, default
     null = upstream behaviour) and `OpenOutputFile(path)`, which every unpacker now calls to open its output.
   - `Shibaless.API/Extensions/FileStreamExtensions.cs`: `WriteBytes` extends `Stream` instead of `FileStream`.
   - `Shibaless.Unpacker.*/Main.cs` (all 7): the output stream is declared `Stream` and opened via
     `this.Options.OpenOutputFile(unpackedPath)` — two lines each, nothing else touched.
   - Limitation: `RecalculateFileChecksum` re-opens the output by path, so it cannot be combined with a
     factory. We never set it (Steamless.CLI's default is off too).

Proof neither patch changed behaviour: `_selftest.exe --corpus` is byte-identical to the official Steamless.CLI
on all 9 real SteamStub exes available (8× Variant 3.1 x64, 1× Variant 2.1 x86).

## How it is built

Not with the `.csproj` files (kept only for reference): `build.ps1` compiles these sources straight into
`Goldberg Patcher.exe` and `_selftest.exe` with csc. `Properties/AssemblyInfo.cs` and the WPF-only
`Model/ViewModelBase.cs` / `Model/NavigatedEventArgs.cs` are left out of that compile.

## Rebasing onto a newer upstream

1. Clone upstream and check out the new commit; copy the upstream folders listed above (plus `LICENSE`)
   into a scratch folder.
2. `.\Rename-FromUpstream.ps1 -Tree <scratch folder>`.
3. Copy the result over this tree (keep this file and the script), then `git diff`: the only differences
   left should be upstream's changes plus the `GOLDBERG PATCH` lines — re-apply those if they were lost.
4. Update the pinned commit and versions above.
5. Run `.\build.ps1`, `.\_selftest.exe`, and `.\_selftest.exe --corpus <folder>` — every corpus exe must be
   SAME / SAME* against the official Steamless.CLI in `tools\steamless\` (replace those binaries with the
   matching official release first).
