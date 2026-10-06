# Shibaberg — our fork of gbe_fork (Goldberg Steam Emulator)

Shibaberg is the Steam emulator Goldberg Patcher installs: `shibaberg\bin\x86\steam_api.dll` and
`shibaberg\bin\x64\steam_api64.dll` are built from this tree by `tools\build-shibaberg.ps1`.

It is a fork of **gbe_fork** (Detanup01), the maintained continuation of Mr_Goldberg's original Goldberg
Emulator, which is discontinued. Never rebase onto the original.

- Upstream: https://github.com/Detanup01/gbe_fork
- Pinned: tag `release-2026_07_19`, commit `64bd1fcfef82d397cd4bdba49adc08f0c49da31c`
  ("Merge pull request #575 from universal963/patch-fixisnu").
- Why that commit: the dlls the patcher shipped before Shibaberg are byte-identical to that release's
  `emu-win-release.7z` → `release\regular\` (SHA-256 x86 `8e804d38…cba1be`, x64 `8b1bd0be…04de1`; both were
  built by upstream CI with VS 2026). Shibaberg therefore starts from exactly the emulator we already ran.
- License: LGPL-3.0, upstream `LICENSE` kept unmodified. Copyright headers and `CREDITS.md` untouched.

## What is vendored

gbe_fork's whole tree at the pinned commit (`git archive`), minus its `third-party/*` submodules.
Those are branches of the same upstream repo, fetched by the build script at these commits:

| Submodule | Commit |
| --- | --- |
| `third-party/common/win` (premake, vswhere) | `d431ffceaadf4e279e70bc4adea3ae42be6820ac` |
| `third-party/build/win` (signing helper) | `556998fa1c2df35e7d208c1ddfb7445080ab2ec2` |
| `third-party/deps/win` (cmake, 7za) | `83308a20b093ff955fd8119726eb6d531027b2a6` |
| `third-party/deps/common` (curl, protobuf, mbedtls, zlib, …) | `92a4a130262083c4e887155cbe6bcab99baf36ea` |

Protobuf's cmake also downloads Abseil from GitHub during the dependency build (upstream behaviour).

## Our patches

1. **rename** — `Rename-FromUpstream.ps1`: in `resources\win\**\resources.rc`, the version-resource
   CompanyName / FileDescription / InternalName / ProductName `GSE…` → `Shibaberg…` (so the shipped dll says
   "Shibaberg Client API"). An explicit list on purpose: gbe_fork's other names are behaviour and stay —
   `steam_settings`, the `GSE Saves` folder (renaming hides existing saves), the `GseExeDir`/`GseAppPath`
   env vars, exports, interface version strings, `steam_api(64).dll`. Mechanical; re-run it, never hand-edit.

## How it is built

`tools\build-shibaberg.ps1` (not part of `build.ps1`; a cold run builds every C++ dependency):
copies this tree to a short work path, fetches the submodules above, builds the deps once, then runs the
same commands as upstream's `emu-build-all-win.yml`: premake `--genproto --dosstub --winrsrc --winsign`
for vs2026, and msbuild target `api_regular`, release, Win32 + x64. It copies the two dlls into
`bin\` and writes `bin\BUILD.txt` (commit, msbuild version, date, SHA-256s), which
the self-test checks against the files. Commit the rebuilt dlls with `BUILD.txt`.

## Rebasing onto a newer gbe_fork release

1. Pick a gbe_fork `release-*` tag; note its commit and its `third-party/*` gitlink commits
   (`git ls-tree <commit> third-party/common/win third-party/build/win third-party/deps/win third-party/deps/common`).
2. `git archive` that commit into a scratch folder, run `.\Rename-FromUpstream.ps1 -Tree <scratch>`.
3. Replace this tree with the result (keep this file and the script), `git diff` to review upstream's changes.
4. Update the commit + submodule commits here and in `tools\build-shibaberg.ps1`.
5. Before replacing the shipped dlls, add their current SHA-256s to `PreviousBundledDlls` in `src\Core.cs`
   so games patched by the previous release are still recognised as ours.
6. Run `tools\build-shibaberg.ps1`, `.\build.ps1`, `.\_selftest.exe`, `.\_live_test.ps1`, and a real launch
   test of an Unreal, a Unity and an old x86 game from temp copies.
