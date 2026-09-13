# Goldberg Patcher (Autoberg) — Bug Report

Date: 2026-08-24 · Review scope: all of `src\` (`Core.cs`, `Batch.cs`, `MainForm.cs`, `Ui.cs`, `TestMain.cs`) plus `build.ps1`.
Method: full code read, clean build via `build.ps1`, `_selftest.exe` run (38/38 pass), byte-level PE header dumps, and a standalone C# harness replicating `PeReader.Analyze` exactly.

**Headline:** the app builds and its self-test passes 38/38, but one of those checks ("AnyCPU arch resolution") only passes **by coincidence**. The PE parser has a confirmed offset bug with demonstrated failure modes (see Bug 1).

---

## Bug 1 — `PeReader.Analyze` reads `SizeOfOptionalHeader` from the wrong offset (HIGH)

**File:** `src\Core.cs`, line 54 (`PeReader.Analyze`)

```csharp
fs.Position = peOff;
uint sig = br.ReadUInt32();          // stream pos → peOff+4
ushort machine = br.ReadUInt16();    // peOff+4   ✓ Machine
ushort numSections = br.ReadUInt16();// peOff+6   ✓ NumberOfSections
int optSize = br.ReadUInt16();       // ← reads peOff+8..9  ✗ (low half of TimeDateStamp!)
fs.Position = peOff + 18;            // characteristics read is fine, but too late for optSize
```

The comment says `// peOff+16 (skip to it)` — **no seek happens**. Per the PE spec, `IMAGE_FILE_HEADER` is: Machine(+4), NumberOfSections(+6), TimeDateStamp(+8, 4 bytes), PointerToSymbolTable(+12, 4 bytes), **SizeOfOptionalHeader(+16)**, Characteristics(+18). The code never skips over the timestamp and symbol-table pointer, so `optSize` = low 16 bits of the build timestamp.

Verified against raw bytes (`steamless\Steamless.CLI.exe`, peOff=0x80):
- real `SizeOfOptionalHeader` @ 0x90 = `0xE0` (this particular file is itself malformed — it ships with that field zeroed and a year-2104 timestamp, which makes the "correct" parse ambiguous for *that* file only)
- what the code reads @ 0x88 = `0xDB25`

### Demonstrated consequences

1. **Valid small executables fail to analyze.** The bogus section-table offset (`optOff + optSize`) lands past EOF on files under ~57 KB → `EndOfStreamException`. Reproduced: a trivial, perfectly valid 7,680-byte .NET console exe run through the real `PeReader.Analyze` threw
   `EndOfStreamException: Unable to read beyond the end of the stream.`
   User-visible symptom in the app: *"Could not read the executable as a PE file."* for any small launcher/stub exe (whether it crashes depends on the timestamp's low half — nondeterministic per build).

2. **AnyCPU / 32BITPREFERRED resolution is garbage-in-garbage-out.** The section table is read from random file bytes, and `RvaToFile` has a latent signed/unsigned flaw: sections are keyed by `(int)vaddr`, so high-bit "addresses" (e.g. `0xFF01FFFF`) become *negative* longs and trivially satisfy `rva >= va`. For Steamless.CLI.exe this produced a false match at file offset 8200; the random bytes there happened to lack the 32BIT flags, so the exe was reported as *"AnyCPU (runs x64)"*. A different build timestamp flips the outcome.

3. **The self-test's "AnyCPU arch resolution" check passes by luck.** It would fail or crash if `Steamless.CLI.exe` were rebuilt with a different timestamp — i.e., the one test that should catch this bug is itself unreliable.

### Practical impact on patching

- Native game exes: unaffected (the section table is only consulted for .NET flag resolution; x86/x64 machine types still resolve correctly via the switch at lines 102–108).
- Small PE files (< ~57 KB with a large timestamp low-half): hard failure with a misleading error.
- AnyCPU reporting / display: unreliable (see above); arch *selection* for typical .NET exes still lands right because of the machine-type fallback, but for the wrong reason.

### Fix

```csharp
fs.Position = peOff + 16;            // SizeOfOptionalHeader
int optSize = br.ReadUInt16();
```

(Optionally also guard `RvaToFile` against negative keys / validate that mapped offsets stay inside the file, and add a self-test case using a small synthetic PE so this can't regress silently.)

---

## Secondary issues (verified in code)

| # | Where | Issue | Severity |
|---|-------|-------|----------|
| 2 | `Core.cs`, `Ext.TakeLastVisible<T>(this IList<T> list, int n)` (~line 700) | Fake extension method: ignores `n` and returns the whole list. Only harmless because it's called as `TakeLastVisible(outputLines.Count)`. Dead/misleading code — either implement "take last N" or delete it. | Low (code smell) |
| 3 | `Core.cs`, `InstallGoldbergDlls` | `Log(... + dllName + (File.Exists(dst) ? "" : ""))` — both ternary branches are empty strings; dead code. | Cosmetic |
| 4 | `Core.cs`, `AppIdDetector.Detect`; `MainForm.ApplyApiSearch` | The exe's own folder is appended **last** to the `steam_appid.txt` search order (`dirs.Add(dir)` after all steam_api-dll dirs), so a stale appid file in a deep subfolder beats the one beside the exe — which is the file Steam actually reads. Exe dir should be checked first. | Low (wrong AppID possible on odd installs) |
| 5 | `Core.cs`, `AppSettings.Save/Load` | No escaping of `=`: keys are written as `folder:<path>=<id>` and parsed by splitting on the *first* `=`, so a game path containing `=` corrupts `settings.ini` parsing on next load (wrong key, truncated value). | Low (data corruption on rare paths) |
| 6 | `Core.cs`, `Payload.ExtractMissing()` | Restores embedded payload files only on **size** mismatch — a same-size corrupted file is kept. Also swallows all exceptions (`catch { }`), so in a read-only exe folder the "self-contained" restore fails silently and the app later reports missing tools without explaining why extraction failed. | Low–Medium (robustness) |
| 7 | `Ui.cs`, `DropZone` | Dead code: `CreateGraphicsSafe()` returns null and is used in an empty `using`; `TruncateForDraw` calls `CreateGraphics()` during `OnPaint` (legal but wasteful — measure with the paint `Graphics` instead). | Cosmetic / perf |
| 8 | `Ui.cs`, `Banner.ParentForm_Resize()` | No-op method called from `OnVisibleChanged`. Dead code. | Cosmetic |
| 9 | `Core.cs`, `PatchRunner.Run` | PE analysis (arch selection) happens **before** Steamless unpacking; the packed exe's machine type is used to pick the steam_api dll. Fine in practice (unpackers preserve arch), but worth a comment or re-analysis after unpack. | Informational |
| 10 | `Batch.cs`, `BatchForm.Detect` | In the `ContinueWith` callback, `t.Result` rethrows if the detection task faults; it lands on the UI thread and is only caught by the global `Application.ThreadException` handler (MessageBox). Wrap in try/catch for a cleaner failure. | Low (robustness) |

---

## What was checked and found OK

- `build.ps1`: locates Roslyn csc + .NET 4.8 refs, embeds 81 payload files with manifest; builds both exes cleanly.
- `_selftest.exe`: 38/38 pass — **except** the "AnyCPU arch resolution" check passes for the wrong reason (Bug 1.3). All pipeline checks (unpack skip, dll replace + backup, appid txt, steam_settings copy, online-fix restore-from-backup, batch AppID detection, Steam Store JSON parsing/matching) are genuinely valid.
- `FindSteamApiFiles`: junction-safe reparse-point pruning, depth ranking and the 40-file/50k-dir caps behave as documented (verified by self-test).
- `--batch` CLI exit codes match AGENTS.md (0 = all patched, 1 = failures, 2 = nothing patched).
- Online-fix mode: correctly refuses to run when only a bundled Goldberg dll is present with no backup; restores originals from `goldberg_backup\`.
