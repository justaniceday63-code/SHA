# Folder manifest format

A manifest is the canonical, reproducible description of a folder tree: one
line per file and directory, sorted by a rule that yields the same order on
any machine. Two machines that produce different canonical text for the same
tree would make "did anything change?" meaningless, so every rule below is
normative.

Produced by `FolderManifestBuilder.Build` (`src/ShaGui.Core/Storage/`),
consumed by the Verify UI and by `FolderManifest.ContentHash()`.

## 1. Text form

```
# shagui-manifest v1 algo=sha256
D	0	-	src
F	1048576	<64-hex>	src/ShaGui.Core/ShaGui.Core.csproj
F	42	<64-hex>	src/ShaGui.Core/Hashing/ShaHasher.cs
?	ReparsePoint	symlink/junction not followed	build_out
```

- **Encoding:** UTF-8, **no BOM**.
- **Line endings:** LF (`\n`) only — never CRLF, on any platform.
- **Header:** exactly `# shagui-manifest v1 algo=<sha1|sha256>`, one line, first.
- **Fields:** TAB-separated, in this order:

  | Prefix | Fields | Meaning |
  | --- | --- | --- |
  | `F` | `F \t size \t hash \t path` | Regular file. `size` decimal bytes, `hash` lower-case hex digest of the file's bytes. |
  | `D` | `D \t 0 \t - \t path` | Directory, emitted **even when empty** so the tree can be recreated from the manifest alone. |
  | `?` | `? \t reason \t detail \t path` | Skipped item. `reason` is a `SkipReason` name; `detail` is a single line with tabs/newlines replaced by spaces. |

- **A manifest with any `?` line is not complete** (`FolderManifest.IsComplete
  == false`). The UI must never render "verified" for it. This is the whole
  reason skips are recorded instead of silently dropped.
- Paths never contain tabs or newlines (Windows forbids them; the walker
  rejects nothing silently — a path it cannot represent becomes a skip).

## 2. Path rules (normative)

1. **Root-relative** — no leading `./`, no absolute prefix, no trailing `/`
   (except a path that *is* the root, which is never emitted).
2. **Separator is `/`** on every platform. Windows-native `\` never leaks.
3. **Unicode normalization: NFC.** NFD (common from macOS/Linux tools) would
   otherwise split one name into two different byte sequences and make equal
   trees disagree. `.Normalize(NormalizationForm.FormC)` is applied, and the
   operation is idempotent (asserted by tests).
4. **`//` is collapsed** and a redundant `./` prefix is stripped.

## 3. Ordering (normative)

Entries are sorted by **UTF-8 byte-wise ordinal comparison** of the canonical
path (`ManifestPath.Compare`):

- Convert both paths to UTF-8 bytes, then compare byte by byte, shorter is
  smaller on a prefix match.
- UTF-8 preserves code-point order, so this equals code-point ordering.
- **No culture, locale or casing is involved.** `string.Compare` with
  `CurrentCulture` (or even `Ordinal` over UTF-16) is *not* equivalent:
  UTF-16 code-unit order disagrees with code-point order for supplementary
  characters, and culture comparison may ignore format characters (a BOM
  searched with `CurrentCulture` matches at position 0 in any string — a real
  bug we hit).

ASCII-only paths take a fast path that compares chars without allocating.

Sorting happens **after** hashing and again at finalization, so neither OS
enumeration order (NTFS USN order differs between runs and from ReFS) nor
hash completion order affects the output.

## 4. Walk policy

| Item | Default | Rationale |
| --- | --- | --- |
| Hidden/system files | **Included** | Excluding them would make "verified" a lie about the visible subset. |
| Reparse points (symlinks, junctions) | **Skipped, recorded as `ReparsePoint`** | Following them can loop or escape the tree; a manifest that silently left the root is worse than one that documents the skip. Opt-in via `FolderManifestOptions.FollowReparsePoints`. |
| Empty directories | **Emitted as `D`** | Structure round-trips. |
| Unreadable directory (ACL/sharing) | **Recorded as `Inaccessible`**, subtree not walked | Never silent. |
| File changing during its own hash | **Recorded as `ChangedDuringHash`** | The native layer detects size drift and refuses to emit a digest. |
| Cancellation | Remaining items recorded as `Cancelled`, `WasCancelled = true`, `IsComplete = false` | A cancelled run is never "complete". |
| Policy-excluded items (`IncludeHidden = false`) | **Not recorded** | A policy choice is not a failure — nothing was unverifiable. |

## 5. Canonicalization and change detection

- `ToCanonicalText()` — the exact bytes described above (header + sorted
  entries + skips). Stable across runs, machines and platforms for the same
  tree.
- `ContentHash()` — `SHA256(UTF8(ToCanonicalText()))`, lower-case hex. One
  number for "did anything change?". It changes when a file's content, size,
  name, or presence changes, and when entries are added or removed; it is
  identical across runs over an untouched tree (all asserted by
  `ManifestTests`).

The manifest header (algorithm) and skips are part of the text, so a manifest
produced with SHA-1 can never be confused with one produced with SHA-256, and
an incomplete run can never hash to the same value as a complete one.

## 6. What the manifest deliberately does *not* cover (v1)

- Timestamps, ownership, ACLs — not part of "same bytes" and not portable.
- Archive *contents*: ZIP/7Z/RAR are hashed as raw byte streams (spec §5b was
  dropped for v1); the manifest only sees them as opaque files.
- Case-folding: `README.md` and `readme.md` are distinct entries — Windows
  case-insensitive renames are reported as differences, which is the honest
  outcome for a tool whose job is detecting change.
