# Vendored Steamless

- Upstream: https://github.com/atom0s/Steamless
- Pinned commit: `cd770bf9749d3e4f438d23ac643917ad1a804257` (2024-03-29, "Bumped the copyright year of the project.")
  — the commit whose project versions match the Steamless binaries v0.5 shipped:
  Steamless.API 1.0.0.8, Variant10.x86 1.0.0.0, Variant20.x86 1.0.0.3, Variant21.x86 1.0.0.9,
  Variant30.x86 1.0.0.1, Variant30.x64 1.0.0.3, Variant31.x86 1.0.0.3, Variant31.x64 1.0.0.3
  (Steamless.CLI 3.1.0.0). `a571eeb` has the same versions; `cd770bf` only adds a copyright bump on top.
- License: upstream `LICENSE`, kept unmodified beside this file.

## What is vendored

Upstream layout and file names, unchanged, so the tree can be rebased:

- `Steamless.API/`
- `Steamless.Unpacker.Variant10.x86/`, `Variant20.x86/`, `Variant21.x86/`, `Variant30.x86/`, `Variant30.x64/`,
  `Variant31.x86/`, `Variant31.x64/`
- `SharpDisasm.dll` (in the Variant20/21 folders): upstream ships it as a prebuilt binary (SharpDisasm 1.0.0.0,
  SHA-256 `6d3ce990…bc2cf`, identical to the copy the v0.5 payload carried), so it is vendored as a binary too.

Not vendored: `Steamless/` (WPF GUI), `Steamless.CLI/`, `ExamplePlugin/`, `repo/`, `Steamless.sln`.

## Our patches

None yet.

## How it is built

Not with the `.csproj` files (kept only for rebasing): `build.ps1` compiles these sources straight into
`Goldberg Patcher.exe` and `_selftest.exe` with csc. `Properties/AssemblyInfo.cs` and the WPF-only
`Model/ViewModelBase.cs` / `Model/NavigatedEventArgs.cs` are left out of that compile.

## Rebasing onto a newer upstream

1. Clone upstream and check out the new commit.
2. Copy the folders listed above over this tree (plus `LICENSE`), keeping this file.
3. `git diff` — re-apply every patch listed under "Our patches" (each site is marked `GOLDBERG PATCH`).
4. Update the pinned commit and versions above.
5. Run `.\build.ps1`, `.\_selftest.exe`, and `.\_selftest.exe --corpus <folder>` — every corpus exe must be
   SAME / SAME* against the official Steamless.CLI in `tools\steamless\` (replace those binaries with the
   matching official release first).
