# Plan — Folding Steamless & Goldberg into a single binary (own fork)

Goal: stop shelling out to `steamless\Steamless.CLI.exe` and stop shipping ~60 extracted payload files.
Make the patcher a real single binary that does the unpacking and the emulator install itself.

> **Status of §1:** the licensing blocker is resolved — permission from the owner is in place. The plan below
> is rewritten around **forking the source**, which is both faster and safer than the clean-room rewrite the
> first draft assumed.

---

## 1. Licensing status

### 1.1 What the permission unlocks

With the owner's permission, the CC BY-NC-ND 4.0 restrictions on Steamless (NoDerivatives, NonCommercial) no
longer bind *you*. That means all of the following are now on the table:

- fork the Steamless source and modify it
- **ILMerge / ILRepack its assemblies into your patcher exe** and distribute that exe
- publish the fork publicly
- patch Steamless itself instead of working around its CLI

**Do one thing first:** put the grant in writing in the repo — `docs/PERMISSIONS.md` with who granted it, the
date, the channel it came through (email / Discord / GitHub issue), and a link or screenshot. Two reasons:
it is the difference between "documented permission" and "someone said it was fine", and CC BY-NC-ND grants
to *you* do not automatically extend to everyone who downloads your binary. If the grant is not written down
and transferable, keep your distributed builds non-commercial and keep the attribution — that costs nothing.

### 1.2 What still applies

| Component | License | What it means now |
| --- | --- | --- |
| **Goldberg Steam Emulator** | LGPLv3+ | Forking and rebuilding is fine. If you distribute a *modified* GSE binary, LGPL flows to downstream recipients — so keep publishing your GSE fork's source. Cost is near zero, and it is good practice regardless. |
| **Steamless** | CC BY-NC-ND 4.0 (+ your private grant) | Your grant overrides ND/NC for you. Downstream recipients still only get the public CC terms, so keep the build non-commercial and keep the attribution. |
| **SharpDisasm** | **2-clause BSD** *(verified — `justinstenning/SharpDisasm`, incl. the udis86 port by Vivek Thampi)* | Fully permissive. Binary redistribution only requires reproducing the copyright notice and disclaimer — i.e. a `THIRD-PARTY-NOTICES` file. No obstacle to ILMerge. |
| Your patcher | yours | Unchanged. |

Practical consequence: ship a `THIRD-PARTY-NOTICES.md` and a credits/About entry naming Steamless (atom0s),
SharpDisasm (Justin Stenning, BSD-2), udis86 (Vivek Thampi, BSD-2) and GSE (Mr_Goldberg, LGPLv3). That
satisfies every attribution obligation in one file.

### 1.3 On "reverse engineering it"

You now have the source, so reverse engineering the **binary** is the wrong tool — it is slower, more
error-prone, and you would be reimplementing something you already hold a license to. The reverse
engineering effort should go into the **SteamStub format** only where Steamless' implementation is not
enough (see §5.4). Read the source; disassemble the games.

---

## 2. What the permission changes

The first draft of this plan had a large, risky centrepiece: a clean-room SteamStub v3.x unpacker written
from format documentation, with a golden corpus and a per-variant milestone schedule. **Delete that.**

| | Clean-room rewrite (old plan) | Fork + link in-process (now) |
| --- | --- | --- |
| Effort | Large — weeks | Small — days |
| Correctness risk | High: every variant is a fresh reverse-engineering problem | Low: you inherit a tested implementation covering v1, v2, v3.0.x, v3.1.x, x86 + x64 |
| Coverage | v3.x only, everything else falls back to the CLI | Everything Steamless supports, with no fallback needed |
| Maintenance | You own every bug forever | Rebase onto upstream releases |
| Legal | Clean | Clean (with the grant on file) |

The only thing the fork costs you is a dependency on upstream — mitigated by vendoring with a recorded
upstream commit so you can rebase.

**And the fork buys you something the clean-room route could not:** you can fix Steamless instead of working
around it. Three changes are worth making in your fork, and each deletes work in the patcher:

1. **Expose the unpackers as a library.** No plugin-directory reflection, no file output. Call them directly.
2. **Return structured results**, not log lines — see §5.2.
3. **Drop the GUI project** (`Steamless.exe`, 1.29 MB) from your fork's build. You only need the API and the
   unpackers.

---

## 3. Target architecture

```
BEFORE                                    AFTER
──────                                    ─────
Goldberg Patcher.exe  (21.8 MB)           Goldberg Patcher.exe  (single managed assembly)
 ├─ Core.cs (pipeline)                      ├─ Core (pipeline, rollback, journal GC)
 ├─ child process ──► Steamless.CLI.exe     ├─ Steamless.API + unpackers   ← ILMerged, called in-process
 │                     └─ Plugins/*.dll     │   (returns byte[] — no process, no temp file)
 ├─ ~60 payload files written to            ├─ SharpDisasm                 ← ILMerged (BSD-2)
 │   the app folder on every launch         ├─ embedded GSE dlls           ← your fork, deflated
 └─ needs a writable app directory          └─ managed interface scanner   ← replaces generate_interfaces
```

Three things change materially:

1. **No child process.** Unpacking happens in-process on an in-memory `byte[]`, so the output can be
   validated with `PeReader` *before* anything touches disk, and written through `SafePersistence` so it
   gets the journal and rollback for free.
2. **No `generate_interfaces.exe`.** Interface enumeration becomes a managed PE scan (§7.3).
3. **No writable app directory.** Payload goes to `%LOCALAPPDATA%` and is compressed — measured
   19.77 MB → 6.39 MB (§9.2).

GSE stays an embedded native DLL. **There is no way to merge a native x86/x64 PE DLL into a managed .NET
EXE** — the only mechanisms are embedding as a resource (what you already do) or shipping it beside the exe.
Embedding is correct. What changes is that you now *build* it from your own fork.

---

## 4. Phase 1 — Vendor the Steamless fork into the solution

```
third_party/steamless/            ← your fork, pinned
  VENDORED.md                     ← upstream repo, upstream commit, your patch list, rebase instructions
  Steamless.API/                  ← the unpacker base types
  Steamless.Unpacker.*/           ← the 5 variant projects
  LICENSE.md                      ← CC BY-NC-ND text, retained
```

Rules that keep this maintainable:

- Keep upstream's directory layout and file names. Your patches should be small, reviewable, and listed in
  `VENDORED.md`.
- Never rewrite upstream files wholesale — a fork that diverges structurally cannot be rebased.
- Do not build `Steamless.exe` (the GUI) or `Steamless.CLI` into your product; keep them in the fork only so
  rebases stay clean.

**Exit criterion:** `dotnet build` on your solution produces `Steamless.API.dll` and the five unpacker
assemblies, and a throwaway console harness can unpack a real SteamStub-packed exe through them.

---

## 5. Phase 2 — Call the unpackers in-process

### 5.1 The integration

Steamless' plugin model is: each unpacker derives from a base type in `Steamless.API` and implements

- a **probe** — "can I handle this image?", given the file path and the raw bytes
- an **unpack** — returns the rebuilt image as a `byte[]`

The CLI discovers implementations by reflecting over `*.dll` files in a plugin directory, tries each probe,
and writes the winner's bytes to `<name>.unpacked.exe`.

You skip all of that. Read the exact member names from the source, then:

```csharp
// UnpackerRegistry: direct instantiation, no reflection over files on disk
static readonly Unpacker[] Unpackers = {
    new Variant10_x86(), new Variant20_x86(), new Variant21_x86(),
    new Variant30_x86(), new Variant30_x64(), new Variant31_x86(), new Variant31_x64(),
};

public static byte[] TryUnpack(string path, byte[] image, out string unpackerName)
{
    foreach (var u in Unpackers)
    {
        if (!u.CanUnpack(path, image)) continue;
        var result = u.Unpack(path, image);
        if (result == null) continue;
        unpackerName = u.Name;
        return result;                      // caller validates before it touches disk
    }
    unpackerName = null;
    return null;
}
```

The exact signatures differ from the sketch — read them from the source. The shape is what matters.

### 5.2 What this deletes from `Core.cs`

| Deleted | Where | Why it existed |
| --- | --- | --- |
| `Process.Start` + async stdout/stderr plumbing | `Core.cs:897-933` | It was a child process |
| The `Thread.Sleep(120)` / 10-minute polling loop | `Core.cs:917-924` | Waiting on an opaque process |
| The stdout path regex | `Core.cs:941` | The only way to learn the output filename |
| The "newest `.unpacked.exe` in the directory" heuristic | `Core.cs:947-955` | Fallback when the regex missed |
| `InvocationOutputs` (the whole class) | `Core.cs:279-322` | Existed solely to stop a *stale file from a previous run* being mistaken for this run's output. With an in-memory result there is no stale file. |
| The 50-line Steamless log tail echo | `Core.cs:957-968` | Replaced by structured results |

That is roughly 150 lines of the most fragile code in the project, plus its self-test coverage
(`TestMain.cs:534-565`). Replace the log-scraping with a small result object from the unpacker
(`Name`, `Description`, `Success`, `Message`) and log that.

**Keep** the validation that follows: `Core.cs:970-999` — never replace the original with something that does
not parse as a PE. Add one check the file-based version could not do cheaply: assert the rebuilt image's
entry point lands inside a section with execute characteristics, and that the image has no section with the
SteamStub DRM characteristics.

### 5.3 Wire it into the pipeline

`TryUnpack` (`Core.cs:883-1006`) becomes roughly:

```
read the exe once into a byte[]
  → probe unpackers in order
  → if one claims it: validate the returned image, then SafePersistence.Write(exePath, …)
  → else: log "no Steam DRM detected", return unchanged
```

Because the write goes through `SafePersistence`, the unpack is now covered by the journal and (once §10 is
done) by rollback — which it never was before.

### 5.4 When to still disassemble something

Keep the option to disassemble **games**, not Steamless. If you hit a SteamStub build that the vendored
unpackers reject, that is a variant to add — and you can now add it to your fork with the rest of the
implementation available to crib from. `SharpDisasm` (already in the payload) is what the v3.x unpackers use
for that work.

---

## 6. Phase 3 — Merge to a single managed assembly

With the license resolved, ILMerge is now the right call:

```xml
<!-- ILRepack.MSBuild.Task -->
<ItemGroup>
  <MergeAsm Include="$(OutputPath)Steamless.API.dll" />
  <MergeAsm Include="$(OutputPath)Steamless.Unpacker.*.dll" />
  <MergeAsm Include="$(OutputPath)SharpDisasm.dll" />
</ItemGroup>
```

Gotchas to plan for:

- **Do Phase 2 first.** Steamless' plugin discovery reflects over files in a directory. After merging, those
  files do not exist, so any code path still doing file-based discovery will find nothing. Direct
  instantiation removes the problem entirely.
- **Internal type collisions.** Several unpackers declare internal helpers with similar names. Enable
  `Internalize` in ILRepack and rename on conflict, or set distinct root namespaces.
- **Attribution must survive the merge.** A merged assembly has no per-dll file headers. Ship
  `THIRD-PARTY-NOTICES.md` (§1.2) and list the components in the About dialog.
- **Alternative if ILRepack fights you:** add the unpacker projects as `ProjectReference` and use
  `dotnet publish` with `IncludeAllContentForSelfExtract`, or simply compile the unpacker sources into your
  own project. ILRepack is cleaner because upstream stays diffable.

---

## 7. Phase 4 — GSE from source

### 7.1 Reproducible build

```
third_party/goldberg_emulator/    ← your fork, pinned to a commit
tools/build-gse.ps1               ← → payload/gse/{x86,x64}/steam_api{,_64}.dll
```

Pipeline (upstream's documented Windows flow): `vcpkg install protobuf --triplet {x86,x64}-windows-static`,
then `build_win_release.bat` (or CMake directly), then copy the two dlls into `payload/gse/` and record the
commit hash in `payload/gse/BUILD.txt`.

**Why:** you currently ship whatever binary happened to be in `release/regular/`. Pinning a commit means you
can reproduce, audit, and patch the emulator — and apply the behavioural tweaks you will inevitably want
(default `steam_settings` contents, quieter logging, LAN-only defaults) under LGPL's terms.

### 7.2 Do not build `steamclient` / `steamnetworkingsockets`

The upstream CMake config also builds these. The patcher only installs `steam_api*`. Skip them — they are
large and unused.

### 7.3 Replace `generate_interfaces.exe` with a managed scanner

`generate_interfaces` produces `steam_interfaces.txt` for games whose original library predates May 2016, by
enumerating the interface versions the dll exposes. Do it statically in managed code:

1. Parse the dll's export table for the `SteamInternal_*` / `SteamAPI_*` exports.
2. Reach the `STEAMAPPS_INTERFACE_VERSION001`-style version strings from those exports' code references in
   `.rdata`, and map each to its interface name via the standard prefix table.
3. Emit `steam_interfaces.txt` in the same format, sorted and deduplicated.

**Parse statically — do not `LoadLibrary`.** It works for both architectures in one 64-bit process (a 64-bit
process cannot map a 32-bit dll), and it never executes third-party code. This is a pure PE-reading problem,
which is exactly what `PeReader` already does well.

This also fixes `optimizations.md` #10 — the architecture comes from the PE header instead of the dll's
filename.

---

## 8. Phase 5 — Install the *right* dll (import-table driven)

`optimizations.md` #5: the installed dll name is currently guessed from the CPU architecture. Parse the
target exe's **import directory** (data directory index 1) and use the exact name the loader will ask for.

```
read imports of game.exe
  ├─ contains "steam_api64.dll" → install steam_api64.dll (x64)
  ├─ contains "steam_api.dll"   → install steam_api.dll   (arch from the PE header, NOT the name)
  └─ neither                    → fall back to arch-derived name; log that it was a guess
```

Then **verify after install**: re-read the import table and assert every Steamworks import now resolves to a
file on disk, walking the loader's search order (exe dir, then the known Unity/Unreal plugin dirs).

This removes the worst failure mode in the app — a "successful" patch that leaves the game unable to start.

---

## 9. Phase 6 — Packaging

### 9.1 Move off .NET Framework 4.8

Prerequisite: remove `System.Web.Extensions` (`optimizations.md` #7 — it exists only for
`JavaScriptSerializer`). Then port to **.NET 8** with an SDK-style project:

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWindowsForms>true</UseWindowsForms>
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
```

What this buys:

- **a genuine single file** — no `_selftest.exe` beside it, no writable app directory, no 60 extracted files
  (`optimizations.md` #4 disappears)
- **no csc.exe discovery** — `build.ps1`'s fragile Roslyn hunt (`optimizations.md` #24) is replaced by
  `dotnet publish`
- **no .NET Framework prerequisite** on the target machine
- ILRepack becomes unnecessary if you also fold the unpacker sources into the single-file publish

Decide consciously: `SelfContained` is ~60 MB uncompressed, ~25–30 MB with
`EnableCompressionInSingleFile`. **A middle option** — `SelfContained=false` against the .NET 8 desktop
runtime — gets you single-file behaviour at ~10 MB.

### 9.2 Compress the payload (measured)

| File | Raw | Deflate | Ratio |
| --- | --- | --- | --- |
| `x86/steam_api.dll` | 8.76 MB | 3.06 MB | 0.35 |
| `x64/steam_api64.dll` | 10.90 MB | 3.31 MB | 0.30 |
| `Steamless.CLI.exe` | 0.11 MB | 0.02 MB | 0.16 |
| **Total** | **19.77 MB** | **6.39 MB** | |

Current exe: 21.81 MB → **~8.4 MB** with a deflated payload. Compress at build time in `build.ps1`
(`DeflateStream` per resource), inflate in `Payload.ExtractCore`. Keep the manifest SHA-256 as the hash of
the **uncompressed** bytes so verification logic is unchanged.

Note the third row stops mattering once §5 lands — the Steamless CLI is no longer a payload file at all.

### 9.3 Shrink the payload itself

- Drop `ExamplePlugin.dll` / `ExamplePlugin.zip` (`build.ps1:67`) — it is Steamless' sample plugin and should
  not be loaded at runtime.
- Drop `generate_interfaces_*.exe` once §7.3 lands.
- Drop `steamless\Steamless.CLI.exe` + `Plugins\*.dll` once §5 lands — the unpackers are compiled in.
- `steam_settings.EXAMPLE` is ~40 files including ~20 controller PNGs. Ship the text configs and fetch the
  glyph PNGs only if the user enables the controller overlay, or ship them deflated (nearly free after §9.2).

---

## 10. Phase 7 — Make the safety net real

`optimizations.md` #1–#3. This is a prerequisite for the fork being trustworthy, not an optional extra —
once you are rebuilding PE images yourself, a failed patch is much likelier to leave a game broken.

1. Implement `Recovery.Rollback(PatchResult)` — replay `res.Writes` in reverse using the journal's
   `previous-sha256`, restore or delete each destination, verify, report.
2. Call it from the failure branches in `PatchRunner.Run` (`Core.cs:857-877`).
3. Persist the journal outside the game folder (`%APPDATA%\GoldbergPatcher\last-patch\`) so an "Undo last
   patch" banner action survives a restart.
4. Garbage-collect `.gp-recovery` on success, and stop double-storing the exe (#3).

Do this **before** §5 ships to anyone but you. An in-process unpacker writing a bad image with no rollback is
worse than a child process writing a good one.

---

## 11. Phase 8 — Test matrix & CI

`AGENTS.md` says there is no CI. With a vendored unpacker and a hand-written interface scanner, that stops
being viable.

| Layer | What | Where |
| --- | --- | --- |
| Unit | PE model round-trip, import-table parse, interface scanner, AppID normalisation | extend `_selftest` |
| Unit | Rollback: inject a failure mid-pipeline, assert byte-identical restore | new `_selftest` section |
| Golden | Unpacker vs. corpus SHA-256s | `tests/corpus` (below) |
| Integration | The existing fake-game pipeline test (`TestMain.cs:136-260`) | keep |
| Live | `_live_test.ps1` against a throwaway game in `%TEMP%` | keep, extend to batch |
| Manual | One real game per DRM variant, launched | release checklist |

**Corpus** — you already own real targets (`AGENTS.md`: Assetto Corsa Rally, Mortal Shell, The Sinking City,
plus an `[OnlineFix]` game):

```
tests/corpus/
  v312_x64/<name>.packed.exe + .sha256
  v312_x86/ v30_x64/ v20_x86/ v10_x86/
  notpacked/<name>.exe          (probe must return false)
  hostile/truncated.exe, badmagic.exe, section-overflow.exe   (must fail cleanly, never throw)
```

Commit SHA-256s and a script that rebuilds the corpus from a local path — **do not commit the game
executables**.

Add a GitHub Actions workflow: `windows-latest`, `dotnet build` + `dotnet test` + `_selftest`. The Roslyn
hunt in `build.ps1` is the main reason there is no CI today — §9.1 removes it.

---

## 12. Risk register

| Risk | Impact | Mitigation |
| --- | --- | --- |
| A vendored unpacker produces a subtly wrong image that only breaks some games | High — silent game corruption | Never replace the original without a verified backup; golden corpus + a real-game launch test per variant; keep the CLI path one flag away until §5 has run against the whole corpus |
| The permission grant is verbal or non-transferable | Medium — undermines the fork's legal footing | `docs/PERMISSIONS.md` with the written grant; keep distributed builds non-commercial and attributed |
| Your fork diverges from upstream and cannot be rebased | Medium — you inherit maintenance forever | Keep patches small and listed in `VENDORED.md`; never restructure upstream directories |
| ILMerge fails on internal type collisions | Low — build friction | `Internalize` + rename on conflict; fall back to `ProjectReference` + single-file publish |
| .NET 8 port breaks the custom-painted UI | Medium | Port in one commit, verify all 15 custom controls at 100% and 150% DPI (`optimizations.md` #11 is the related latent bug) |
| Static interface scanning misses games that need `steam_interfaces.txt` | Medium | Keep `generate_interfaces` available as an opt-in fallback until the scanner has run against a broad corpus |
| Rollback restores a file the user has since modified | Medium | The journal records the hash it wrote; refuse to roll back a file whose current hash differs, and say so |
| Shipping a modified GSE binary without publishing its source | Low — license breach | Keep your GSE fork public; it is free and it is required |

---

## 13. Order of work

| # | Phase | Depends on | Rough size |
| --- | --- | --- | --- |
| 0 | §1.1 write `docs/PERMISSIONS.md` + `THIRD-PARTY-NOTICES.md` | — | trivial — do it first |
| 1 | §10 rollback + journal GC | — | medium — independent, and the highest-value fix in the repo |
| 2 | §9.2 payload compression | — | small — biggest visible win per line changed |
| 3 | §4 vendor the Steamless fork, build it | 0 | small |
| 4 | §5 in-process unpacking | 1, 3 | **medium — the main event** |
| 5 | §7.1 GSE from source | 0 | small |
| 6 | §9.1 .NET 8 port + drop `System.Web.Extensions` | 2 | medium — unblocks CI |
| 7 | §6 ILMerge | 4, 6 | small |
| 8 | §8 import-table driven install | 6 | small |
| 9 | §7.3 managed interface scanner | 6 | medium |
| 10 | §11 CI + corpus | 6 | small |
| 11 | §9.3 payload slimming | 4, 5, 9 | small |

Steps 1, 2 and 3 are independent and deliver value immediately. Step 4 is where the project actually lives
or dies — do not start it before step 1 is done.

---

## 14. Non-goals

- **Do not** reverse engineer `Steamless.CLI.exe`. You have the source and a license. Read the source.
- **Do not** try to merge the native GSE dll into the managed exe — it is not possible, and embedding as a
  resource is already the correct answer.
- **Do not** restructure upstream's directories in your fork. A structurally diverged fork cannot be rebased.
- **Do not** rewrite `PeReader`. Extend it. It is the best-tested code in the repo.
- **Do not** ship a modified GSE binary without publishing its source — LGPL requires it and the cost is
  zero.
