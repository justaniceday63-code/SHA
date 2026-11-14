# Native API — `shaNative.dll`

The C backend owns the cryptographic core and the single-file streaming loop.
Everything else — folder recursion, manifests, archives, UI — is managed code.

Source of truth: [`native/include/shagui_native.h`](../native/include/shagui_native.h)
(frozen at `SHAGUI_ABI_VERSION 1`). This document explains the boundary; the
header defines it. If the two ever disagree, the header wins and this file is
a bug.

---

## 1. Boundary rules

| Rule | Why |
| --- | --- |
| Plain C only (`/std:c17`, `-std=c17`) | No C++ types, exceptions or STL can cross a P/Invoke boundary. |
| All strings are UTF-8 | The header mandates it; on Windows the DLL converts to UTF-16 internally (`MultiByteToWideChar` + `_wfopen_s`), never through the ANSI code page. |
| cdecl, declared explicitly | One convention on x64 today; stating it protects the boundary if x86 is ever added. |
| Every cross-boundary struct is POD with a fixed layout | The C# side marshals it by layout, not field-by-field. `sha_trace_step_t` is **128 bytes on x64** and `sha_progress_t` is **1072 bytes**; both sizes are asserted in tests on both sides. |
| Only one callback exists: `sha_progress_cb` | No other managed code is ever entered from the DLL. |
| `SHAGUI_ABI_VERSION` bump = breaking change | The managed side asserts equality at startup (`ShaHasher.EnsureAbi`) and the interop tests re-assert it, so a header bump without a managed update fails in CI. |

## 2. Building

```powershell
# Shipping DLL (MSVC, x64, Release) + native test suite
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1 -Target all -Configuration Release -Compiler msvc

# Second compiler (gcc), output isolated under build\Release\gcc
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1 -Target all -Configuration Release -Compiler gcc
```

Output: `native\build\<Config>\<compiler>\shaNative.dll`. Per-compiler
directories exist so the gcc build can never overwrite the shipping MSVC DLL.

MSVC flags: `/nologo /W4 /std:c17 /utf-8 /D_CRT_SECURE_NO_WARNINGS /O2`.
gcc flags: `-Wall -Wextra -std=c17 -O2`. Both are expected to build warning-free.

Static compilation into the test executables uses `SHAGUI_STATIC` (no import
library); the DLL build defines `SHAGUI_BUILD` (→ `dllexport`).

## 3. Exported functions (18)

### Core streaming

| Export | Notes |
| --- | --- |
| `sha_create(algo, &ctx)` | One context = one owner = one thread. Not thread-safe. |
| `sha_destroy(ctx)` | Wipes the partial block first. |
| `sha_reset(ctx)` | Re-arms the context; keeps the trace configuration. |
| `sha_update(ctx, data, len)` | `len` is `uint64_t` — callers may feed > 4 GiB across calls. |
| `sha_final(ctx, out, cap, &written)` | Context is single-use afterwards until `sha_reset`. |
| `sha_hash_buffer(algo, data, len, out, cap, &written)` | One-shot convenience. |
| `sha_digest_length(algo)` | 20 / 32 bytes; 0 for an unknown algorithm. |
| `sha_algo_name(algo)` | Returns **static storage — never free it** (see §6). |
| `sha_status_string(status)` | Same rule as above. |
| `sha_abi_version()` | `1`. |

### Cancellation

```c
typedef struct sha_cancel { volatile long flag; } sha_cancel_t;
void sha_cancel_request(sha_cancel_t *c);   /* InterlockedExchange(1) */
void sha_cancel_clear(sha_cancel_t *c);     /* InterlockedExchange(0) */
int  sha_cancelled(const sha_cancel_t *c);  /* InterlockedCompareExchange read */
```

- The struct is caller-allocated plain storage, but **only these three
  functions may touch it** — the managed side must never write `.flag` with
  `Marshal.WriteInt32`, which has no ordering guarantee against a native read
  on another core.
- Its address must stay stable for the whole `sha_hash_file()` call (minutes
  for large inputs), so the managed wrapper allocates it in unmanaged memory
  (`ShaCancelSource`, `AllocHGlobal`) — never on the movable managed heap.

### File hashing

```c
sha_status_t sha_hash_file(const char *utf8_path, sha_algo_t algo,
                           uint64_t buf_size,          /* 0 => 1 MiB */
                           sha_progress_cb cb, void *cb_user,
                           sha_cancel_t *cancel,        /* may be NULL */
                           uint8_t *out, size_t cap,
                           uint64_t *elapsed_100ns);    /* may be NULL */
```

Behaviour worth knowing:

- **Fixed-size buffer, allocated once** — memory use is constant regardless of
  file size; files larger than RAM work.
- **Size sampled at open** and compared with bytes actually read: a file that
  changed underneath the run returns `SHA_ERR_CHANGED` instead of a digest of
  a moving target.
- **Progress throttled to ~10 Hz** (plus one initial and one final callback),
  so a 1 ms dispatcher is never flooded for a 50 GB file.
- **Elapsed time** covers only the read+hash loop (QPC), excluding open/close
  and UI work.
- **Cancellation** is checked before every read and by the callback's
  non-zero return; result is `SHA_ERR_CANCELLED`.
- `elapsed_100ns` is in 100 ns units — one `TimeSpan` tick — so the managed
  side converts with `TimeSpan.FromTicks((long)elapsed100ns)`.

### Educational trace

```c
sha_status_t sha_trace_configure(sha_ctx_t *ctx, int enable,
                                 uint32_t first_block, uint32_t block_count);
size_t                   sha_trace_count(const sha_ctx_t *ctx);
const sha_trace_step_t  *sha_trace_data(const sha_ctx_t *ctx);
sha_status_t             sha_trace_get(const sha_ctx_t *ctx, size_t i,
                                       sha_trace_step_t *out);
```

- Scoped to a **block range** (default block 0). A 1 MiB message would be
  16384 blocks × ~144 steps × 128 B ≈ 300 MB, so tracing is capped at
  `SHA_TRACE_MAX_STEPS` (100 000).
- `sha_trace_data()` returns a **contiguous array owned by the DLL**, valid
  until the next trace call or `sha_destroy()` — one P/Invoke for the whole
  trace, never one per step. The managed wrapper copies it out immediately.
- Tracing never changes the digest (asserted by `test_trace` and `TraceTests`).

## 4. Status codes and precedence

`SHA_OK 0`, `INVALID_ARG 1`, `UNSUPPORTED 2`, `IO 3`, `CANCELLED 4`,
`CHANGED 5`, `ACCESS 6`, `NOMEM 7`, `STATE 8`, `OVERFLOW 9`,
`TOO_LARGE_FOR_TRACE 10`, `PATH_TOO_LONG 11`, `UTF8 12`.

Rendered precedence when several conditions apply at once:

```
INVALID_ARG > CANCELLED > IO > ACCESS > CHANGED > OK
```

**A folder run that skipped anything at all never reports `SHA_OK`** — that
rule is enforced one layer up (`FolderManifest.IsComplete`), because C only
ever sees a single file.

Expected outcomes (`IO`, `ACCESS`, `CHANGED`, `CANCELLED`, `UTF8`,
`PATH_TOO_LONG`) are **returned as values, not thrown**: `ShaHasher.HashFile`
passes them through in `HashResult.Status` so the Verify UI can render them.
Everything else becomes `ShaNativeException`.

## 5. Data structures crossing the boundary

| Struct | Size | Managed mirror |
| --- | --- | --- |
| `sha_progress_t` | 1072 B | `ShaProgress` (`uint path[1024]` → `Utf8Path` inline array) |
| `sha_trace_step_t` | 128 B | `ShaTraceStep` (`Word8` inline arrays; .NET 8 `[InlineArray]` is blittable, so the trace is read as a span out of DLL memory with no per-field marshalling) |
| `sha_cancel_t` | `sizeof(long)` | opaque unmanaged allocation behind `ShaCancelSource` |

Both fixed sizes are asserted by `NativeInteropTests` (`Marshal.SizeOf`) and by
the native suite, so a layout drift fails the build rather than silently
corrupting the animation.

`sha_progress_t.struct_size` is set by C to `sizeof` on every callback so a
future extension can be detected by the reader.

## 6. Known interop traps (learned the hard way)

1. **`LibraryImport` string *returns* crash the process.** The source
   generator's marshaller faults natively when converting a `const char*`
   return, even though the same pointer reads fine via
   `Marshal.PtrToStringUTF8`. Both string-returning exports
   (`sha_algo_name`, `sha_status_string`) are therefore declared as `IntPtr`
   and converted once in `NativeMethods.ReadCString`. Do not "fix" this by
   changing the signature back.
2. **MSVC ICE (C1001) on empty brace initializers** — `{ }` in generated C
   data crashes the compiler; `scripts/gen_kat.py` emits `{ 0x00 }` for the
   empty payload (a dummy byte that is never read, `len` is 0).
3. **UTF-8 source needs `/utf-8` on MSVC** and the generated KAT header is
   ASCII-only (PowerShell pipes mangle non-ASCII bytes).
4. **Never compile gcc output into the MSVC output directory** — different
   ABI, and the shipping DLL would be replaced silently.

## 7. Tests

| Suite | What it proves | Run |
| --- | --- | --- |
| `test_vectors` | 19 known-answer vectors × 2 algorithms (from Python `hashlib` via `gen_kat.py`), incl. boundary lengths and embedded NULs | `native\build\Release\msvc\test_vectors.exe` |
| `test_streaming` | Chunked/patterned feeds, 1 M message, API state rules, argument validation, ABI/status/name helpers | `...\test_streaming.exe` |
| `test_trace` | Traced == untraced digest, reconstruction from steps, 7 stages, 80/64 rounds, block ranges, `trace_data` == `trace_get` | `...\test_trace.exe` |
| `test_file` | File == memory digest, UTF-8 paths, error paths, cancellation, progress throttling, and (opt-in) a 5 GiB file | `SHAGUI_TEST_BIG=1 .\test_file.exe` |

Exit code 0 = all passed. The gcc copies live under `build\Release\gcc\`.
The managed equivalents live in `tests/ShaGui.Core.Tests` (81 tests) and run
with `dotnet test ShaGui.sln -c Release`.
