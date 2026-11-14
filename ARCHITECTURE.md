# Architecture

SHA visualization is three layers with one hard boundary
between them: **C owns the cryptography, C# owns everything else, and the
boundary between them is a frozen ABI.**

```
┌─────────────────────────────────────────────────────────────┐
│ ShaGui.App   WinUI 3 (net8.0-windows10.0.19041.0, x64)      │
│   MainWindow (NavigationView shell) + 6 pages               │
│   Services/NativeHealth — one startup probe, shown in UI     │
├─────────────────────────────────────────────────────────────┤
│ ShaGui.Core  managed core (net8.0, x64)                      │
│   Hashing/   ShaHasher, ShaTraceSession (P/Invoke)           │
│   Storage/   FolderManifestBuilder — canonical folder text   │
│   History/   HistoryStore — SQLite runs table                │
│   Settings/  SettingsStore — SQLite settings table           │
│   Data/      AppDatabase — the single SQLite connection      │
│   Native/    NativeMethods, NativeStructs, enums, SafeHandle │
├────────────────────────── ABI (frozen) ─────────────────────┤
│ native/      shaNative.dll — C17, x64                        │
│   sha1.c, sha256.c   compression + message schedule          │
│   sha_ctx.c          streaming context lifecycle             │
│   sha_file.c         single-file loop + progress callback    │
│   sha_trace.c        educational trace sink                  │
│   shagui_native.h    SHAGUI_ABI_VERSION 1 — source of truth  │
└─────────────────────────────────────────────────────────────┘
```

## 1. The native boundary

`native/include/shagui_native.h` is the source of truth; `docs/NATIVE_API.md`
explains it. Rules that keep the boundary honest:

- **Plain C, cdecl, UTF-8 strings, POD structs.** `sha_trace_step_t` is frozen
  at 128 bytes on x64 and `sha_progress_t` at 1072; the size is asserted in
  tests on **both** sides, so drift fails the build instead of corrupting data.
- **`SHAGUI_ABI_VERSION 1`** is checked at startup (`ShaHasher.EnsureAbi`) and
  by the interop tests. The app refuses to run against a mismatched DLL.
- **Exactly one callback** crosses into managed code: `sha_progress_cb`, used
  for progress reporting and cancellation polling. No other managed entry point
  exists.
- The DLL is loaded via P/Invoke from `ShaGui.Core/Native/NativeMethods.cs`;
  the context handle is a `SafeHandle` (`ShaContextHandle`) so disposal is
  deterministic and finalizer-safe.

Failure is loud by design: `App.xaml.cs` probes the backend once at startup
(`NativeHealth.Probe`) and the Home/About pages render the result. A missing
DLL surfaces in the UI, not as a `DllNotFoundException` on a worker thread.

## 2. Hashing flow

```
VerifyPage (UI thread)
  └─ async handler, CancellationTokenSource per run
       └─ Task.Run  ──►  ShaHasher.HashText / HashFileAsync
                           ├─ sha_hash_buffer / sha_hash_file (native)
                           ├─ progress callback → IProgress<HashProgress>
                           │     └─ RunProgress bar; cancel → sha_cancel_*
                           └─ HashResult { Hex, Elapsed, Success, Status }
       └─ back on UI thread: DigestBox, ResultBar (match/mismatch),
            HistoryStore.Add (when RecordHistory is on)
```

Everything expensive runs off the UI thread; only the final assignment touches
controls. Text hashing encodes UTF-8 once (`Encoding.UTF8.GetByteCount` /
`GetBytes` — no double encoding). File and folder runs accept a
`CancellationToken` that becomes a native `sha_cancel_t`, so Cancel interrupts
the native loop rather than abandoning it.

## 3. Folder manifests

`FolderManifestBuilder.Build` walks a tree and emits the canonical text
specified in `docs/MANIFEST.md`: UTF-8, LF, TAB-separated, sorted by a
rule that is machine-independent. Symlinks/reparse points are **reported, never
silently followed** (`FollowReparsePoints` is opt-in in Settings). The
manifest's `ContentHash()` is itself a digest over the canonical text, so "did
anything change?" reduces to one compare.

## 4. Learn page trace

`ShaTraceSession` wraps a native trace-enabled context:

1. `sha_trace_configure(ctx, enable, firstBlock, blockCount)` scopes the trace
   so a large input stays fast — only the selected block range is recorded.
2. Each stage pushes `sha_trace_step_t` rows: input words, padding, block
   split, message schedule, compression, state update, digest.
3. `Steps`/`StepsOf`/`CountOf` expose them to the UI (80 vs 64 rounds is
   asserted by tests, not by convention).
4. `ReconstructDigest()` rebuilds the digest from the traced state-update rows
   alone — a test proves the visualization corresponds to the real computation.

**Display rule (easy to get wrong):** compression rounds carry the registers
that *enter* the round in `words_before`; only state-update rows write the
after-state into `words`. `LearnPage` joins both rows per round (they share
the round index): the detail panel's "before" state and the table's operands
come from `words_before`, the updated register columns from `words`.

The session owns a native context: `LearnPage` disposes it on `Unloaded`, with
the `SafeHandle` finalizer as the backstop.

## 5. State: one SQLite database

`AppDatabase` owns the **single** `SqliteConnection` (SQLite connections are
not safe for concurrent use; runs serialize through it):

| Table | Purpose |
| --- | --- |
| `runs` | history entries: algorithm, kind, subject, bytes, elapsed, digest, status; `ix_runs_recorded` for the default sort |
| `settings` | key/value user settings (default algorithm, buffer size, hidden/reparse policy, history on/off) |

The database is opened once in `App.OnLaunched`. **A failure is non-fatal**:
`App.Database` stays null, `DatabaseError` explains why, and hashing keeps
working — History/Settings pages display the error instead of crashing.
`App.Settings` is the in-memory authority; `SettingsStore` persists edits.

History rows and the benchmark chart are two projections of the same query, so
the chart can never disagree with the list.

## 6. UI structure

`MainWindow` is a `NavigationView` shell; sections map to pages by key
(`home`, `verify`, `learn`, `collision`, `history`, `about`, plus the built-in
Settings item). Home's shortcut cards call `MainWindow.SelectSection`.

Interactive controls carry stable **AutomationIds** — the UI test suite
(`tests/ui-tests.ps1`) asserts their presence as an accessibility audit, and
drives the whole app through them (no coordinate clicks). InfoBars expose their
title as a child text element; tests read that child, not the bar itself.

## 7. Threading and lifetime rules

- **UI thread:** only control mutation. Hashing/manifest building always runs
  in `Task.Run`.
- **Cancellation:** one `CancellationTokenSource` per run; the token maps to a
  native `sha_cancel_t` polled inside the native loop.
- **Native contexts:** held by `SafeHandle`; pages dispose explicitly on
  `Unloaded` (Learn), and the finalizer is the backstop, never the plan.
- **SQLite:** one connection, owned by `AppDatabase`; pages must tolerate
  `App.Database == null`.

## 8. Build, test, packaging

**Build order is native → managed** (`scripts\build-native.ps1`, then
`dotnet build`); `ShaGui.Core` errors early with instructions if
`shaNative.dll` is absent. Two compilers are supported: MSVC (shipping) and
GCC from MSYS2 (second opinion, isolated output dir). Native test executables
compile the backend sources directly (`SHAGUI_STATIC`, no import library).

| Layer | Suite | Count |
| --- | --- | --- |
| Native | `test_vectors`, `test_streaming`, `test_file`, `test_trace` | 4 executables |
| Managed | `ShaGui.Core.Tests` (xUnit: interop layout, manifests, history, trace) | 93 |
| End-to-end | `tests\ui-tests.ps1` (`winapp ui`) | 23 + screenshots |

Known-answer vectors are generated by `scripts\gen_kat.py` from Python
`hashlib` (OpenSSL-backed), so C is checked against an independent
implementation rather than against itself.

**Packaging** is SDK-native via `winapp package` with the single-project MSIX
setup (`Package.appxmanifest`, `EnableMsixTooling`, `runFullTrust`). A
development certificate must match the manifest's `Publisher`
(`winapp cert generate --manifest …`); `devcert.pfx` is gitignored.

**Native AOT decision: not used — JIT self-contained publish.** Reasons:

1. The app already ships `SelfContained=true` publish profiles, so the .NET
   runtime travels with it; AOT's main win (no runtime) is already largely
   captured, while its cost — trimming reflection-heavy WinUI/XAML paths and
   CsWinRT projection warnings — lands squarely on the UI layer.
2. The csproj opts into `PublishTrimmed` for Release; WinUI 3 + trimming is
   exactly the combination that needs per-release validation of XAML codegen.
   Adding AOT on top would double that validation surface.
3. The performance-critical code is **already native** (the C DLL). AOT would
   not speed up hashing; it would only shrink the managed host.

If size or startup ever becomes a requirement, the path is
`<PublishAot>true</PublishAot>` + fixing CsWinRT/IL warnings per release, with
the UI suite as the acceptance gate. That is a deliberate future decision, not
the default.

## 9. Repository conventions

- `Directory.Build.props`: x64-only, nullable enabled, deterministic builds.
- `AGENTS.md`: run impact analysis before editing symbols; run graph change
  analysis before committing; treat `UNKNOWN` risk as unresolved, not as safe.
- `.gitignore`: bin/obj, native build output, test results, `ui-test-artifacts/`,
  `*.msix`, `devcert.pfx` state — the SHAttered PDFs **are** committed so the
  demo works offline (refresh via `scripts\fetch-shattered.ps1`).
