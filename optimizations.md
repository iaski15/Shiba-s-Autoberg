# Optimizations & Known Flaws — Goldberg Patcher

Review of `src/Core.cs`, `src/Ui.cs`, `src/MainForm.cs`, `src/Batch.cs`, `src/TestMain.cs`, `build.ps1`.
~5,300 lines of C# reviewed line by line.

**Overall:** the code is well above average for a hobby tool. The PE reader is genuinely rigorous
(bounds-checked, spec-correct, with synthetic regression tests), the staged-write/journal layer is a
real design, and there is a self-test. The problems are concentrated in four places:

1. **The safety net is decorative** — a full journal/recovery system is written but never used to roll back,
   and it litters the game folder permanently.
2. **The Steamless integration is the weakest link** — output is discovered by scraping stdout with a regex.
3. **The UI is DPI-aware but not DPI-scaled** — it will clip badly on any HiDPI display.
4. **Everything is a child process + 60 extracted payload files** — the exact thing `plan.md` addresses.

---

## Severity legend

| Level | Meaning |
| --- | --- |
| **P0** | Data-safety, correctness, or user-visible breakage. Fix before shipping. |
| **P1** | Robustness / correctness on realistic inputs. Will bite real users. |
| **P2** | Performance or resource cost. Noticeable, not fatal. |
| **P3** | Maintainability, dead code, hygiene. Fix opportunistically. |

---

## P0 — Critical

### 1. The journal is write-only: there is no rollback path

`SafePersistence.Write` (`Core.cs:201-255`) builds a complete recovery record per file — staged copy,
previous-content copy, a journal with `state=prepared` / `state=completed`, SHA-256 of both sides — and
`PatchResult.Writes` (`Core.cs:447`) collects them all.

Then `PatchRunner.Run` does this (`Core.cs:871-877`):

```csharp
res.PartialChanges = !res.Success && res.Writes.Any(w => w.Completed);
...
res.Summary += res.PartialChanges ? " Partial changes remain; no automatic rollback was attempted." : ...;
```

So when a patch fails halfway (say the dll copied fine but `steam_appid.txt` hit a lock), the user is told
*"partial changes remain"* and handed a list of journal paths to sort out by hand. The recovery copies and
the hash verification are all there — nothing reads them.

**Impact:** a failed patch leaves a game in a half-patched state with a real risk of it no longer launching,
and no automated way back. This is the single biggest gap in the app.

**Fix:** add a `Recovery.Rollback(PatchResult)` that walks `res.Writes` in reverse, verifies
`staged-sha256` / `previous-sha256` from the journal, restores `RecoveryPath` over `Destination` (or deletes
the destination when `previous-sha256=absent`), and reports what it restored. Call it from the failure
branches in `PatchRunner.Run` when `Backup` is on, and expose a "Undo last patch" banner action using the
persisted journal. The `_selftest` already asserts the journal format (`TestMain.cs:433-436`) so the contract
is testable.

> **Fixed.** `Recovery.Rollback` replays the records newest-first, verifying the recovery copy against
> `previous-sha256` before it replaces anything, and *refusing* to touch a destination whose current hash no
> longer matches `staged-sha256` (so a file the user edited after patching is never clobbered — the risk
> register's scenario). `PatchRunner.Run` calls it whenever a run ends with completed writes and `Backup` is
> on, cancellation included. The journal is mirrored to `%APPDATA%\GoldbergPatcher\last-patch\journal.txt`, and
> the banner offers **Undo patch** after a patch *and* on startup, so the undo survives a restart. The
> self-test grew a `[rollback]` section covering all of the above.

### 2. Every write permanently litters the target folder with `.gp-recovery` trees

`SafePersistence.Write` (`Core.cs:207-217`) creates, for **every single file it touches**:

```
<parent>/.gp-recovery/<32-hex-guid>/
    <name>.staged-<8hex>
    <name>.previous          (the old file, when one existed)
    <name>.journal.txt
```

Nothing ever deletes these. `state=completed` is written and the directory is abandoned. A single
successful patch of a typical game writes: the exe, one or two dlls, `steam_appid.txt` (×2),
~40 `steam_settings` files, `steam_interfaces.txt` — **that is ~45 orphaned GUID folders per patch**.

The same code path backs `AppSettings.Save()` (`Core.cs:1735`), which runs on *every* game selection, every
successful AppID detection, and every batch completion. So `%APPDATA%\GoldbergPatcher\` accumulates the same
garbage at a much higher rate.

**Impact:** unbounded disk growth; `.gp-recovery` inside a Steam library folder will show up in Steam's
"verify integrity of game files" and can trip antivirus heuristics.

**Fix:** keep the record only until the write is confirmed, then delete the staging area on success
(keep it only on failure). Add a startup sweep that removes `.gp-recovery` folders whose journal says
`state=completed` and are older than N days, and prune `%APPDATA%\GoldbergPatcher\` on the same rule.

> **Fixed.** On success the staging files and the per-write journals are dropped (`Recovery.CollectStaging`) —
> they are superseded by the consolidated undo journal — while the `.previous` copies stay, because "Undo
> last patch" needs them. Growth is bounded to **one run's worth** instead of unbounded: `Recovery.SaveJournal`
> prunes the previous run's areas as it records the new one. `AppSettings.Save` no longer leaks at all — it
> discards its staging area immediately, since a regenerable settings file needs no undo record.
>
> Two further leaks only showed up under a live patch and are also fixed. The empty `.gp-recovery` **folder**
> itself is now removed once its last area goes — previously every patch stranded one empty folder per
> directory it touched (9 of them in a single run, measured). And `OriginalBackups.Preserve` now discards its
> own two writes: they belong to no run journal, so nothing else would ever have collected them, and
> `goldberg_backup` was accumulating `.gp-recovery` trees *inside itself*.
>
> The startup sweep is in too (`Recovery.SweepStale`, called once from `Program.Main`), which covers the one
> case collection cannot reach: a run that dies before it records anything. It is deliberately bounded — the
> app's own state directory plus the last game's folder, never recursing into the game tree, and it skips any
> root the current undo journal still refers to. The state directory gets a one-hour grace rather than the
> game folders' seven days, because nothing legitimate ever leaves a `.gp-recovery` there — running it removed
> the 18-file leak this very review had already accumulated.

### 3. The original game exe is stored twice on every unpack

`TryUnpack` (`Core.cs:985-989`) backs the exe up via `OriginalBackups.Preserve` into
`goldberg_backup\sources\<pathhash>\` **and then** calls `outputs.CopyAndDelete`, which goes through
`SafePersistence.Copy` → `Write` → `File.Replace(staged, path, recovery)` (`Core.cs:239`), which *also* keeps
the old file as `recovery`. Two full copies of a multi-hundred-MB executable.

**Impact:** doubles the disk cost of the most expensive operation in the tool, for no benefit — the
`goldberg_backup` copy is already hash-verified and restorable.

**Fix:** pass a flag through `SafePersistence.Copy` to suppress the recovery copy when the caller has already
preserved the original (or when `Backup` is off). Better: reuse the `goldberg_backup` copy as the recovery
source and skip the second copy entirely.

> **Fixed, by the second route.** `SafePersistence.Write`/`Copy`/`WriteText` take an optional
> `externalRecovery`. When the caller already holds a verified original — `TryUnpack` passes the
> `goldberg_backup` path — no copy is taken into the staging area and the replace runs as
> `File.Replace(staged, path, null)`. The journal records that external path as `recovery=`, so rollback
> restores from it. The replace is ordered so an external recovery source can never be handed to
> `File.Replace` as its *backup* argument, which would have overwritten the verified original.

### 4. "Self-contained exe" requires a writable application directory

`Payload.ExtractMissing` (`Core.cs:599-636`) writes ~60 files into
`AppDomain.CurrentDomain.BaseDirectory`. `Program.InitializePayload` (`MainForm.cs:114-127`) treats any
failure as fatal — the app shows *"Setup incomplete"* and exits.

So running `Goldberg Patcher.exe` from `C:\Program Files\`, a read-only share, a locked-down
`%ProgramFiles%` install, or an archive-mount path breaks the app entirely. A binary advertised as
"one file is all you need" that cannot run from most install locations is not self-contained.

**Fix:** extract to `%LOCALAPPDATA%\GoldbergPatcher\payload\<buildId>\` and point `Tools.*` at that root.
This also fixes the stale-payload problem: a file removed from the manifest in a newer build currently
stays on disk forever, and Steamless will keep loading the stale plugin.

### 5. The installed dll name is chosen from the CPU architecture, not from what the exe imports

`InstallGoldbergDlls` (`Core.cs:1138-1170`) picks `steam_api64.dll` vs `steam_api.dll` from `ExeArch` and
from which files happen to exist on disk. It never reads the target exe's **import table**.

Games exist where the 64-bit Steamworks library is imported under the name `steam_api.dll` (wrapper
layers, renamed redistributables), and games that import neither name but load it dynamically. In the first
case the patcher writes a correctly-architected dll under a name the loader will never ask for, reports
success, and the game still fails to start — the worst possible failure mode.

**Fix:** parse the import directory (`PeReader` already walks sections and data directories — the import
table is data directory index 1) and take the exact imported name. Fall back to the arch-derived name only
when the exe imports neither. Then *verify* after install by re-reading the import table and asserting the
name now resolves on disk.

> **Fixed.** `PeReader.ImportedDlls` walks data directory index 1 and returns the names in file order, sharing
> one bounds-checked header parse with `Analyze` (extracted as `ReadLayout` so the two cannot drift).
> `PatchRunner.ImportedSteamApiName` picks the Steamworks name out of that, and the pipeline uses it for the
> install name, for `PickApiTarget`, and in the summary. When the exe imports neither name it falls back to
> the architecture and says so in the log rather than pretending.
>
> A second, worse bug sat next to this one: `InstallGoldbergDlls` chose which bundled dll to copy **by
> destination name** (`dllName == "steam_api64.dll" ? ApiDll64 : ApiDll86`). So a 64-bit game importing
> `steam_api.dll` got the 32-bit library under the right name — the same silent breakage by a different
> route. The source is now chosen by architecture only, and the staged dll is checked against the target's
> architecture before it is allowed to land.
>
> Post-install verification: if the executable imports a Steamworks name and no such file exists in the
> install folder afterwards, the run now fails loudly instead of reporting success.
>
> Tested with a purpose-built synthetic PE (`WritePeWithImport`): a 64-bit executable importing
> `steam_api.dll` reports that name, `steam_api64.dll` reports its own, and an unrelated import is not
> mistaken for Steamworks. No binary in this repo imports a Steamworks dll, so the fixture is the only way
> to cover the decision.

### 6. Steamless output is discovered by regex-scraping its stdout

`Core.cs:936-955`:

```csharp
var m = Regex.Match(line, "[A-Za-z]:\\\\[^\"*?<>|]*\\.unpacked\\.exe", RegexOptions.IgnoreCase);
if (m.Success && outputs.IsCurrent(m.Value)) { outPath = m.Value; break; }
```

This is the load-bearing step of the whole DRM-removal feature, and it depends on:

- Steamless printing an **absolute, backslash-separated** path (forward slashes, `\\?\` prefixes and
  quoted paths all fail to match)
- Steamless not changing its output format between versions
- the path being on the same drive-letter form the regex accepts

When it misses, the code falls back to "most recently written `.unpacked.exe` in the input directory"
(`Core.cs:947-954`) — a heuristic that has to be defended by the `InvocationOutputs` fingerprinting
machinery. That machinery is good, but it exists only because the primary detection is unreliable.

**Fix (short term):** call Steamless with an explicit output path if the CLI supports one, or take a
before/after snapshot of the directory and pick the new file — filesystem truth instead of text parsing.
**Fix (real):** unpack in-process; see `plan.md`.

> **Fixed, by the snapshot route.** The regex is gone; discovery now *only* uses the before/after fingerprint
> that `InvocationOutputs` was already computing. That deletes the failure modes listed above (forward slashes,
> quoted paths, `\\?\` prefixes, format changes between Steamless versions) and removes the "newest
> `.unpacked.exe` in the directory" heuristic, because the fingerprint *is* the primary mechanism now rather
> than its defender. The stdout text is still read for one thing only: choosing between two log messages.
>
> The real fix — unpacking in-process so there is no file to discover at all — remains plan.md §5.

### 7. .NET Framework 4.8 + `System.Web.Extensions`

`build.ps1:30` requires `System.Web.Extensions.dll` solely for `JavaScriptSerializer`
(`Core.cs:13`, `Core.cs:1586`). That single dependency:

- pins the app to the legacy .NET Framework, so no single-file publish, no trimming, no self-contained
  runtime, no AOT
- pulls a deprecated, heavyweight assembly into a tool that parses one small JSON document
- blocks porting to .NET 8 (where `System.Text.Json` is in-box and the app could be a true single file)

**Fix:** replace with `System.Text.Json` (or a 60-line hand parser for the 3 fields actually used). This is
a prerequisite for `plan.md` Phase 6.

---

## P1 — Robustness

### 8. CLI modes write to a console that does not exist

The GUI is built `/target:winexe` (`build.ps1:94`). `RunBatchCli` (`MainForm.cs:129-189`) and the argument
error path (`MainForm.cs:78`) use `Console.WriteLine` / `Console.Error.WriteLine`. A Windows-subsystem
process launched from PowerShell/cmd inherits **no console** unless the parent creates pipes.

`_live_test.ps1` reads `$LASTEXITCODE` (works) but every diagnostic line from `--batch` is silently
discarded. The documented batch engine is effectively un-debuggable from a shell.

**Fix:** call `AttachConsole(ATTACH_PARENT_PROCESS)` when `args.Length > 0` and reopen
`Console.Out`/`Console.Error` on it. Or ship a small companion console exe.

### 9. Batch AppID detection rescans the whole install tree once per game

`AppIdDetector.Detect` (`Core.cs:1337`) calls `PatchRunner.FindSteamApiFiles(dir, ct)` — a full recursive
directory walk, capped at 50,000 directories (`Core.cs:1210`) — and `BatchForm` runs this for **every row**
with a concurrency of 3 (`Batch.cs:387`, `Batch.cs:656`).

For a 50-game library this is 50 independent full-tree walks. `FindSteamApiFiles` returns as soon as it has
40 hits, which helps, but a game with no dll walks until the directory cap.

**Fix:** `AppIdDetector` only needs the directories *likely* to hold `steam_appid.txt` — the exe's own folder
and its immediate parent. A bounded 2–3 level walk would find >99% of cases at a fraction of the cost.
Cache results per folder for the lifetime of the batch.

### 10. The interface-generation tool is picked from the dll *filename*

`Core.cs:1012`:

```csharp
bool x64 = Path.GetFileName(target).IndexOf("64", StringComparison.Ordinal) >= 0;
```

The architecture should come from `PeReader.Analyze(target).Arch`, which the code already has. If a game
ships a 64-bit library named `steam_api.dll` (see #5), the 32-bit `generate_interfaces_x86.exe` is run
against it and silently produces nothing — the tool's exit code is never checked either
(`Core.cs:1033-1042` only kills it after 60 s).

**Fix:** use the PE header; check the exit code; log it.

> **Fixed.** `TryGenerateInterfaces` reads the architecture from `PeReader.Analyze(target)`, and skips with a
> warning when it is unknown rather than guessing at the tool. It also drains stdout/stderr while waiting (a
> chatty tool would otherwise deadlock on a full pipe) and reports the tool's exit code plus the tail of its
> output, so "produced nothing" no longer reads as "the dll does not export interfaces" — which sent people
> after the wrong problem. The same code path covers #15; only its output redirection overlapped.

### 11. The app opts into Per-Monitor-V2 DPI awareness but does not scale anything

`MainForm.cs:71` calls `SetProcessDpiAwarenessContext(-4)` (PerMonitorV2). Every layout in the app is a
hardcoded pixel rectangle (`new Rectangle(28, 116, 764, 116)`, `ClientSize = new Size(820, 780)`, …) and no
form sets `AutoScaleMode` (the default for a designer-less top-level `Form` is effectively `None`).

Meanwhile `Ui.F(...)` creates fonts in **points**, which the GDI+ stack scales with the monitor DPI.

At 150% DPI on a typical laptop panel, every label renders ~1.5× larger inside a rectangle that did not grow.
The result is clipped text, overlapping chips, and a window that occupies a small fraction of a 4K display.
At 100% it looks exactly as designed — which is presumably the only configuration it was developed on.

**Fix:** either drop the DPI-awareness call (Windows bitmap-scales it — blurry but correct), or set
`AutoScaleMode = AutoScaleMode.Dpi` and express every layout constant as a multiple of a
`ScaleFactor = DeviceDpi / 96f`. The second is more work but is the right answer, and the layout constants
are already concentrated at the top of each form.

> **Fixed, with one honest caveat.** Both forms now set `AutoScaleMode.Dpi` with
> `AutoScaleDimensions(96, 96)`, so WinForms scales every control's bounds by the display factor, and the
> hand-positioned paint geometry goes through `Dpi.S()` (which `Ui.S()` forwards to) so it scales in step.
> Fonts needed no help: they are created in points and GDI+ already maps those through the device DPI, which
> is exactly why the layout had to catch up.
>
> The part that needed real care: WinForms' auto-scale is a **one-off pass at load**. Anything that lays out
> later — `RecalcLog` on every resize and banner toggle, `LayoutRows` for the batch rows, `BatchRow.OnResize`
> — would have snapped the layout back to design coordinates, which is worse than not scaling at all. Those
> paths scale their constants explicitly.
>
> `Dpi` lives in `Core.cs` rather than `Ui.cs` because `_selftest.exe` is compiled from `Core.cs` +
> `TestMain.cs` only, so anything in `Ui.cs` is unreachable from the suite. Eight assertions cover the
> scaling arithmetic.
>
> **Not verified visually.** There is no way to render the window at 150% in this environment, so the
> arithmetic is tested and no regression was introduced, but the appearance at >100% has not been seen.
> That is the one thing to check on a HiDPI display.

### 12. One stale backup file permanently blocks patching

`OriginalBackups.Existing` (`Core.cs:347-372`) throws whenever a backup exists but fails hash verification,
and `Preserve` propagates that. `PatchRunner`'s `backup` lambda (`Core.cs:734-740`) is called before the dll
install, so the whole patch aborts with *"Backup verification failed; preserve and inspect …"*.

That is a defensible fail-safe, but there is no recovery path: no "re-backup from the current file", no
"discard corrupt backup", and no UI affordance. A user who once interrupted a patch is stuck until they
delete files by hand — while the app has all the information needed to offer a one-click repair.

**Fix:** on verification failure, log the mismatch, quarantine the bad backup to `<name>.corrupt-<ts>`, and
re-preserve from the live file (or refuse with an explicit banner action). Do not hard-fail the patch.

### 13. Nested mutex acquisition works only by accident

`SafePersistence.Locked` (`Core.cs:175-189`) takes a named mutex `Local\GoldbergPatcher-<pathkey>`.
`OriginalBackups.Preserve` takes `Locked(destination, …)` and then calls `SafePersistence.Copy(content, destination, …)`,
which calls `Write(destination, …)`, which calls `Locked(destination, …)` **again**. Same for
`SettingsScaffold.Apply` (`Core.cs:509-517`).

This works solely because Windows mutexes are recursive **for the owning thread**. Any future refactor that
moves the inner call to a different thread (a `Task.Run`, a `Parallel.ForEach` over the settings files)
turns this into a 30-second stall followed by *"Another instance is writing …"*.

**Fix:** make the lock re-entrant explicitly — track the held path in a `[ThreadStatic]` set and skip
re-acquisition, or split into `LockedCore` (assumes held) and `Locked` (acquires).

### 14. Only the UI thread's exceptions are handled

`MainForm.cs:99-110` installs `Application.ThreadException`. Missing:

- `AppDomain.CurrentDomain.UnhandledException` — a background-thread exception kills the process with no
  `errors.log` entry and no message
- `TaskScheduler.UnobservedTaskException` — the `BatchPatcher` and `ScanSelectionAsync` paths can fault
  silently

**Fix:** register both, write to `errors.log` with a full stack trace, and show a non-blocking status.

### 15. `TryGenerateInterfaces` spawns a process with no output redirection

`Core.cs:1025-1042` sets `RedirectStandardOutput = false` and never checks `ExitCode`. If
`generate_interfaces` fails to load the dll (wrong architecture, missing VC runtime, packed dll), the code
sees no `steam_interfaces.txt` and logs *"Interface dump produced nothing (dll may not export interfaces)"* —
a misleading diagnosis that will send users chasing the wrong problem.

**Fix:** redirect stdout/stderr, log the tail on failure, and include the exit code.

### 16. Online AppID lookup cost is unbounded per batch item

`SteamLookup.FindBestForExe` (`Core.cs:1604-1614`) tries up to 3 candidate titles sequentially, each with an
8-second timeout (`Core.cs:1629-1630`) — up to 24 s per game, with 3 concurrent detections. The candidate
list is built by walking *every* path component ≥ 4 chars (`Core.cs:1494-1513`), so `D:\Bionis\Autoberg\MyGame\g.exe`
queries `MyGame`, `Autoberg`, and `Bionis`.

**Fix:** cap at 2 candidates; require the candidate to be the exe's own folder first and stop on first hit;
share a single `HttpClient` with a 4 s timeout; add a per-batch request budget.

---

## P2 — Performance & resources

### 17. `LogView` trimming is O(n²) per appended line

`LogView.AppendLine` (`Ui.cs:699-719`) calls `TrimLines()`, and `AppendText` also raises `OnTextChanged`
which calls `TrimLines()` again (`Ui.cs:721-726`). `TrimLines` (`Ui.cs:727-757`) reads the **entire**
`RichTextBox.Text` and allocates a `List<int>` with one entry per line, then discards it — twice per line.

With `MaxLines = 1500`, appending line *n* costs two full-text scans of a growing buffer. A verbose
Steamless run logs ~50 lines per game (`Core.cs:961`); a 20-game batch logs thousands.

**Fix:** keep an integer line counter; only scan when `counter > MaxLines`, and then trim in one pass.
Use `GetFirstCharIndexFromLine` instead of a manual scan. Consider `SuspendLayout` around batch drains.

### 18. Quadratic string truncation inside `OnPaint`

`BatchForm.OnPaint` (`Batch.cs:549-552`):

```csharp
if (g.MeasureString(sub, Ui.F(8.5f, false)).Width > subMaxW)
    while (sub.Length > 1 && g.MeasureString(sub + "…", Ui.F(8.5f, false)).Width > subMaxW)
        sub = sub.Substring(0, sub.Length - 1);
```

A `MeasureString` plus a `string` allocation per character removed, executed on every repaint (including
every resize tick). `Ui.TruncMiddle` (`Ui.cs:76-94`) already does a binary search for exactly this.

**Fix:** replace with `Ui.TruncMiddle(g, sub, Ui.F(8.5f, false), subMaxW)`.

### 19. Two full-window gradient fills on every main-window repaint

`MainForm.OnPaint` (`MainForm.cs:1149-1150`) calls `AmbientGlow` twice, each building a `GraphicsPath` with
an ellipse and a `PathGradientBrush` filled across the **entire client rectangle**. That is two
whole-window gradient rasterizations per paint, and the main form repaints on every resize
(`Resize += delegate { RecalcLog(); }` → `SetBounds` on the log card).

**Fix:** render the ambient glow once into a cached `Bitmap` keyed on size, and blit it. Or drop it — it is
18/255 and 12/255 alpha, barely visible.

### 20. ~20 MB of SHA-256 on every launch

`Payload.ExtractMissing` (`Core.cs:610-628`) hashes each payload file whose **size matches** — which is
exactly the case for the two ~9–11 MB emulator dlls. So every start hashes ~20 MB, plus ~60 small files.

**Fix:** write a `.payload-ok` stamp containing the build id + manifest hash after a successful verification,
and skip per-file hashing when the stamp matches. Re-verify on demand (a `--verify-payload` flag) and after
any file's mtime changes.

> **Fixed.** `.payload-ok` records the manifest's own hash plus each file's size and write time. A file whose
> size *and* write time both match the stamp is skipped without hashing; anything else is hashed as before,
> and a repair rewrites the stamp. The stamp is invalidated automatically whenever the manifest changes, so
> it can never describe a different build. `--verify-payload` ignores it and hashes everything (exit 0 =
> intact, 1 = missing or corrupt).
>
> **Measured, and it is smaller than this item implies.** Hashing all 88 payload files (41.9 MB) takes
> **78 ms**, of which the two emulator dlls are 15 ms — SHA-256 runs at roughly 1.3 GB/s. The volume figure
> above is accurate; the user-visible cost is tens of milliseconds, and only the first launch after an
> install pays it. The real win in this row is #21.
>
> Residual trade-off, stated plainly: a file whose length *and* write time are both preserved across a
> same-length overwrite would pass the fast path. That is not a realistic corruption mode (a partial write
> changes the length; any rewrite changes the write time) and `--verify-payload` covers it.

### 21. Compress the embedded payload — measured 21.8 MB → ~8.4 MB

Measured on the actual binaries in this repo:

| File | Raw | Deflate | Ratio |
| --- | --- | --- | --- |
| `release/regular/x86/steam_api.dll` | 8.76 MB | 3.06 MB | 0.35 |
| `release/regular/x64/steam_api64.dll` | 10.90 MB | 3.31 MB | 0.30 |
| `steamless/Steamless.CLI.exe` | 0.11 MB | 0.02 MB | 0.16 |
| **Total** | **19.77 MB** | **6.39 MB** | |

The current exe is 21.81 MB — essentially just the two uncompressed dlls. Deflating the payload at build
time and inflating on extract (the extraction path already copies through a `FileStream`, so wrapping it in
`DeflateStream` is a few lines) brings the shipped binary to **~8.4 MB, a 61% reduction**, with no
behavioural change. PE files compress well because of their zero-filled section padding.

**Fix:** `DeflateStream`/`GZipStream` per resource in `build.ps1` + inflate in `Payload.ExtractCore`.
Keep the manifest SHA-256 as the *uncompressed* hash so verification is unchanged.

> **Fixed — and it came in better than this estimate.** `build.ps1` deflates each payload file to a scratch
> copy and embeds whichever is smaller, recording `res|path|sha256|uncompressedLength|deflate|raw` in the
> manifest. The hash stays the hash of the uncompressed bytes, so verification is untouched; the extract path
> inflates and then checks both the inflated length and that hash before the file is allowed to replace
> anything.
>
> Measured on the current build: payload **21.65 MB → 7.37 MB**, and the shipped exe
> **21.82 MB → 7.54 MB — a 65% reduction**, against this item's 8.4 MB prediction. The extra comes from the
> ~40 `steam_settings` files, which this estimate did not count: the controller glyph PNGs deflate well too.
>
> Two details worth keeping: the "keep whichever is smaller" rule means a payload file that does not compress
> is still embedded raw, and a build whose manifest changes invalidates the verification stamp of #20 by
> construction.

### 22. `Ui.RoundPath` allocates a `GraphicsPath` per fill and per stroke

`Ui.FillRound` and `Ui.StrokeRound` (`Ui.cs:67-74`) each construct a fresh `GraphicsPath` with four arcs.
They are called several times per control per paint, across ~15 custom controls.

**Fix:** cache paths keyed on `(width, height, radius)` in a small dictionary, or draw the rounded rect with
a single `AddRoundedRectangle`-style helper and reuse one path for fill+stroke.

### 23. `Ui.FromHex` runs inside paint and log hot paths

`Ui.FromHex("#67707F")` is evaluated for every dim log line (`Ui.cs:707`), and `Ui.FromHex("#5A6373")` on
every `Toggle`/`GradientButton`/`StatusBarCtl` paint (`Ui.cs:504`, `Ui.cs:537`, `MainForm.cs:317`). Each call
does two `Substring` allocations plus three `Convert.ToInt32` parses.

**Fix:** promote the ad-hoc hex strings to `static readonly Color` fields alongside the existing palette.

### 24. Roslyn discovery recurses the entire Visual Studio tree on every build

`build.ps1:14-16` does `Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio" -Recurse -Filter csc.exe`
and only *then* filters for `*Roslyn*`. On a machine with several VS versions and workloads this enumerates
tens of thousands of files before discarding most of them.

**Fix:** use `vswhere.exe` (`-latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\csc.exe`),
which is the supported way and returns in milliseconds. Or filter with `-Include *Roslyn*csc.exe` so the
recursion prunes.

---

## P3 — Maintainability & hygiene

### 25. `GenerateInterfaces` is a dead setting

`AppSettings.GenerateInterfaces` is persisted (`Core.cs:1703`, `Core.cs:1730`) and
`PatchOptions.GenerateInterfaces` is honoured by the pipeline (`Core.cs:779`) — but both call sites hardcode
`true`: `MainForm.cs:892` and `Core.cs:1441`. There is no UI toggle, so the option can never be turned off.
Either wire up a `Toggle` or delete the setting.

### 26. Dead code

| Item | Location | Note |
| --- | --- | --- |
| `BufferedRunLog.UseLogDirectory` + `LogDirScope` | `Batch.cs:243-254` | Never called anywhere |
| `NativeMethods.ExtractAssociatedIcon` / `DestroyIcon` | `Ui.cs:246-249` | Declared, never used (`DropZone` uses `Icon.ExtractAssociatedIcon`) |
| `ExtractAssociatedIcon` HICON leak | `Ui.cs:301` | The managed wrapper leaks the HICON on some paths — the P/Invoke pair above was presumably meant to fix it |
| `MainForm.AmbientGlow` + `using System.Drawing.Drawing2D` | `MainForm.cs:1113` | Only used by the two low-alpha glows; candidate for removal (see #19) |
| `steamless/ExamplePlugin.dll` + `ExamplePlugin.zip` | embedded via `build.ps1:67` | Steamless's *sample* plugin; ships in the payload and gets loaded at runtime |
| `string otherName` outer-scope assignment | `Core.cs:710` | Recomputed in `InstallGoldbergDlls`; the outer value is used only in the `res.Unpacked` branch |

### 27. `%APPDATA%\GoldbergPatcher` is a magic string in three files

- `Core.cs:1680` (`AppSettings.Dir`)
- `Batch.cs:227-228` (`BufferedRunLog.logDirectory`)
- `MainForm.cs:103` (the `ThreadException` handler)

Three independent definitions of the app's state directory. Any change desynchronises them silently.

**Fix:** a single `AppPaths.StateDir` used by all three.

### 28. `README.md` contains the entire document twice

Lines 1–79 and lines 81–159 are byte-identical. Delete one.

### 29. The version number is hardcoded in a paint method

`Ui.cs:232` draws `"v0.4"` as a literal. It will drift from the release the first time it is forgotten.

**Fix:** read `AssemblyInformationalVersionAttribute` (set it via `build.ps1`) and render that.

### 30. `FindExistingAppId` is an instance method that uses no instance state

`Core.cs:1238`. Because of this, two call sites construct a throwaway `PatchRunner` just to reach it:
`Core.cs:1344` (`new PatchRunner().FindExistingAppId(...)`) and `MainForm.cs:855-859`
(`runner_FillAppId`). Make it `static` and delete the wrapper.

### 31. Stray indentation

`Core.cs:712` — `                                if (foundApi.Count > 0)` sits at 32 spaces inside a method that
indents at 16. Cosmetic, but it suggests a bad merge.

### 32. Committed build artifacts

`Goldberg Patcher.exe` (21.8 MB) and `_selftest.exe` are tracked in git. `AGENTS.md` explicitly says there is
no CI and that verification is running the self-test — but the 21.8 MB blob will be re-added to history on
every build. Add both to `.gitignore` and attach them to releases instead.

### 33. Exit code 3 is undocumented

`StartupArgs.Usage` (`MainForm.cs:26`) documents `0`, `1`, `2`. The code also returns `3` when `--auto` cannot
resolve an AppID (`MainForm.cs:651`) and on patch failure with `--exit-when-done` (`MainForm.cs:1005`).
Document it, or fold it into `1`.

### 34. `--batch` cannot do online-fix or settings scaffolding

`MainForm.cs:167` hardcodes
`new BatchPrefs { UnpackDrm = true, Backup = true, WriteAppIdTxt = true, CreateSettings = false, OnlineFix = false }`
with no way to override from the command line, while the GUI batch dialog exposes all five. Add
`--online-fix` / `--no-unpack` / `--settings` flags.

### 35. Documentation drifts from the code

- `README.md:11,27` says originals are saved to `<game>\goldberg_backup\`; the code writes to
  `<game>\goldberg_backup\sources\<pathhash>\<name>` (`Core.cs:326-329`).
- `README.md:3` links Steamless to `gitlab.com/Mr_Goldberg/steamless`; that URL requires GitLab sign-in and
  is not publicly resolvable. The bundled plugins match atom0s' Steamless — link the real upstream.
- `README.md:55` says "~60 files"; the manifest is generated at build time and will drift.

### 36. `FindSteamApiFiles` sorts by string length as a proxy for depth

`Core.cs:1235` returns `results.OrderBy(r => r.Length)`, but `PickApiTarget` (`Core.cs:1174-1185`) recomputes
depth properly with `ShortRel(...).Split('\\').Length`. The initial sort is redundant work and the two
orderings can disagree for paths of equal length at different depths. Drop the sort.

### 37. The batch row draws its remove button twice

`BatchRow` creates a real `Button` with `Text = "×"` (`Batch.cs:79-88`) **and** paints another `"×"` glyph at
the same rectangle (`Batch.cs:208-209`). Depending on the button's default rendering, the glyph is
double-drawn or misaligned, and the parent's `OnMouseUp` handler (`Batch.cs:167-174`) can never fire over the
button — so `hoverRemove` is dead state. Pick one: either the button or the custom paint.

### 38. The drop zone computes its hit-test rectangle during `OnPaint`

`DropZone.changeRect` is assigned inside `OnPaint` (`Ui.cs:424`) and read by `OnMouseMove`
(`Ui.cs:353`, `Ui.cs:359-362`). Before the first paint it is `Rectangle.Empty`, and the hit region is a side
effect of rendering.

**Fix:** compute it in `OnResize`/`OnLayout`, or expose a `ChangeLinkBounds` property.

### 39. `TestMain.ReviewRegressions` has an unguarded cleanup

`TestMain.cs:591`: `finally { Directory.Delete(dir, true); }` — unlike every other cleanup in the file, this
one has no `try/catch`. If a handle is still open (which the file-locking tests make plausible), the
self-test crashes with an unhandled exception and the whole run reports nothing.

### 40. `Ui.fontCache` never evicts

`Ui.cs:32-52` caches `Font` objects in an unbounded dictionary keyed by `family+size+bold`, disposed only on
`ApplicationExit`. In practice the key space is small, so this is harmless — but `DropZone`, `BatchRow` and
`LogView` assign cached fonts to `Control.Font`, which means control disposal does not free them and any
future dynamic sizing would leak.

### 41. Box-drawing characters in console output

`Core.cs:681` (`──`), `Core.cs:848` (`✔`), `Core.cs:855` (`✖`) and `Core.cs:1474` are written via
`Console.WriteLine`. On a console with a non-UTF-8 code page they render as mojibake. Set
`Console.OutputEncoding = Encoding.UTF8` in the CLI paths, or use ASCII.

### 42. `JavaScriptSerializer` error surface

`SteamLookup.ParseItems` (`Core.cs:1599-1600`) catches only `ArgumentException` and
`InvalidOperationException`. `JavaScriptSerializer` can also surface `NotSupportedException` and
`IndexOutOfRangeException` on malformed input. It is swallowed by the callers today, but the catch list is
narrower than the API's documented behaviour.

### 43. `StartupArgs.Parse` accepts a value that starts with `-`

`MainForm.cs:37` rejects values starting with `--` but accepts `-x`. Harmless, but the guard's intent
("a flag is never a value") is only half-implemented.

---

## Suggested order of work

| Order | Items | Rationale |
| --- | --- | --- |
| 1 | #1, #2, #3 — **done** | Data safety and disk hygiene; all three live in `SafePersistence` and can be done together |
| 2 | #21, #20 — **done** | Largest user-visible win per line changed (8.4 MB exe, faster start) |
| 3 | #5, #6, #10 — **done** | Correctness of the core value proposition (dll placement, unpack detection) |
| 4 | #11 — **done** (arithmetic verified, appearance not) | Unblocks anyone on a HiDPI display |
| 5 | #4, #8, #14 | Removes the "it just doesn't start" and "no output" failure classes |
| 6 | #9, #17, #18, #19 | Performance, once correctness is settled |
| 7 | #12, #13, #15, #16 | Robustness hardening |
| 8 | #25–#43 | Cleanup, in any order |

## What is already good (keep it)

- `PeReader.Analyze` — bounds-checked at every offset, validates machine/optional-header agreement, handles
  AnyCPU and `32BITPREFERRED` correctly, and has synthetic regression tests (`TestMain.cs:38-134`). Do not
  rewrite it.
- `InvocationOutputs` — the before/after hash fingerprinting that prevents a stale `.unpacked.exe` from
  replacing a fresh exe is genuinely careful work.
- `OriginalBackups` — verified manifests with source path + hash, and the "never promote a bundled Goldberg
  dll as an original" rule are exactly right.
- `SafePersistence.Locked`'s named-mutex-per-path design (modulo #13).
- `BufferedRunLog` — bounded queues, drop counting, disk batching, `CompleteAdding` on shutdown. The only
  log layer in the app that is properly designed.
- The self-test is real: 100+ assertions including adversarial PE fixtures and concurrent-update races.
