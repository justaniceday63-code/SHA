using ShaGui.Core.Hashing;
using ShaGui.Core.Native;

namespace ShaGui.Core.Storage;

/// <summary>
/// Walks a folder tree and produces a deterministic manifest.
/// </summary>
/// <remarks>
/// <para><b>Ownership split:</b> enumeration, recursion and skip policy are
/// managed (C#); only the read+hash loop per file goes to the native DLL.
/// That keeps the C ABI small and lets the walker use modern .NET file APIs.</para>
///
/// <para><b>Determinism rules</b> (docs/MANIFEST.md):</para>
/// <list type="bullet">
///   <item>Paths are root-relative, NFC, '/'-separated, compared as UTF-8
///   bytes ordinally.</item>
///   <item>Entries are sorted by that order — not by OS enumeration order,
///   which differs between NTFS (USN order) and ReFS and between runs.</item>
///   <item>Hidden files are included by default; reparse points are skipped
///   by default; empty directories get a 'D' entry so the tree can be
///   recreated.</item>
///   <item>Any skipped item is recorded — a manifest with skips is never
///   "complete", so the UI cannot claim verification.</item>
/// </list>
/// </remarks>
public static class FolderManifestBuilder
{
    /// <summary>Builds a manifest, streaming each file through the native layer.</summary>
    public static FolderManifest Build(string rootPath, FolderManifestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        options ??= new FolderManifestOptions();

        string root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"folder not found: {root}");
        }
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var entries = new List<ManifestEntry>();
        var skipped = new List<SkippedItem>();
        long filesTotal = 0;
        bool cancelled = false;

        // --- Phase 1: enumerate (count work, apply skip policy) -----------
        Report(options, ManifestStage.Enumerating, 0, 0, root);
        var filesToHash = new List<string>();
        Enumerate(root, root, options, entries, skipped, filesToHash,
            ref filesTotal, ref cancelled);

        // Sort BEFORE hashing so progress order is deterministic too.
        entries.Sort(ManifestEntryComparer.Instance);
        filesToHash.Sort(ManifestPathComparer.Instance);

        // --- Phase 2: hash ------------------------------------------------
        long done = 0;
        string current = string.Empty;
        foreach (var file in filesToHash)
        {
            if (options.CancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                skipped.Add(new SkippedItem
                {
                    Path = file,
                    Reason = SkipReason.Cancelled,
                    Detail = "cancelled before hashing",
                });
                continue;
            }

            current = file;
            Report(options, ManifestStage.Hashing, done, filesTotal, file);

            string full = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            var result = ShaHasher.HashFile(full, options.Algorithm,
                new HashFileOptions
                {
                    BufferSize = options.BufferSize,
                    CancellationToken = options.CancellationToken,
                });

            if (result.Success)
            {
                // Replace the placeholder added during enumeration with the
                // real size + hash (position preserved; we re-sort at the end).
                int idx = entries.FindIndex(e =>
                    e.Kind == EntryKind.File && e.Path == file);
                if (idx >= 0)
                {
                    entries[idx] = entries[idx] with
                    {
                        Size = new FileInfo(full).Length,
                        Hash = result.Hex,
                        Algorithm = options.Algorithm == ShaAlgo.Sha1
                            ? ShaAlgoForManifest.Sha1
                            : ShaAlgoForManifest.Sha256,
                    };
                }
            }
            else
            {
                skipped.Add(new SkippedItem
                {
                    Path = file,
                    Reason = result.Status switch
                    {
                        ShaStatus.Changed => SkipReason.ChangedDuringHash,
                        ShaStatus.Access => SkipReason.ReadFailed,
                        ShaStatus.PathTooLong => SkipReason.PathTooLong,
                        ShaStatus.Cancelled => SkipReason.Cancelled,
                        _ => SkipReason.ReadFailed,
                    },
                    Detail = result.Status.ToString(),
                });
                entries.RemoveAll(e => e.Kind == EntryKind.File && e.Path == file);
            }
            done++;
        }

        Report(options, ManifestStage.Hashing, done, filesTotal, current);

        // --- Phase 3: finalize -------------------------------------------
        Report(options, ManifestStage.Finalizing, done, filesTotal, root);
        entries.Sort(ManifestEntryComparer.Instance);

        return new FolderManifest
        {
            Root = root,
            Algorithm = options.Algorithm,
            Entries = entries,
            Skipped = skipped,
            CreatedUtc = DateTimeOffset.UtcNow,
            FilesTotal = filesTotal,
            WasCancelled = cancelled,
        };
    }

    private static void Report(FolderManifestOptions o, ManifestStage stage,
        long done, long total, string path)
    {
        o.Progress?.Invoke(new ManifestProgress
        {
            Stage = stage,
            FilesDone = done,
            FilesTotal = total,
            CurrentPath = path,
        });
    }

    /// <summary>
    /// Depth-first enumeration. Directories are appended before their
    /// contents are visited so the pre-sort order is stable; the final
    /// canonical sort happens in <see cref="Build"/>.
    /// </summary>
    private static void Enumerate(string root, string current,
        FolderManifestOptions options, List<ManifestEntry> entries,
        List<SkippedItem> skipped, List<string> filesToHash,
        ref long filesTotal, ref bool cancelled)
    {
        if (cancelled || options.CancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            return;
        }

        IEnumerable<string> dirs;
        IEnumerable<string> files;
        try
        {
            dirs = Directory.EnumerateDirectories(current);
            files = Directory.EnumerateFiles(current);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                or IOException)
        {
            skipped.Add(new SkippedItem
            {
                Path = Rel(root, current),
                Reason = SkipReason.Inaccessible,
                Detail = ex.Message,
            });
            return;
        }

        foreach (var d in dirs)
        {
            if (cancelled || options.CancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return;
            }

            string rel = Rel(root, d);
            var info = new DirectoryInfo(d);

            if (!options.FollowReparsePoints && IsReparsePoint(info))
            {
                skipped.Add(new SkippedItem
                {
                    Path = rel,
                    Reason = SkipReason.ReparsePoint,
                    Detail = "symlink/junction not followed",
                });
                continue;
            }

            if (!options.IncludeHidden && IsHidden(info))
            {
                continue;   // policy-excluded, not a failure: not recorded as skipped
            }

            // Directory entry — emitted even when empty (spec: empty dirs
            // get a 'D' entry so the structure round-trips).
            entries.Add(new ManifestEntry
            {
                Kind = EntryKind.Directory,
                Path = ManifestPath.Normalize(rel),
                Size = 0,
                Hash = null,
            });

            Enumerate(root, d, options, entries, skipped, filesToHash,
                ref filesTotal, ref cancelled);
        }

        foreach (var f in files)
        {
            if (cancelled || options.CancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return;
            }

            string rel = Rel(root, f);
            var info = new FileInfo(f);

            if (!options.FollowReparsePoints && IsReparsePoint(info))
            {
                skipped.Add(new SkippedItem
                {
                    Path = rel,
                    Reason = SkipReason.ReparsePoint,
                    Detail = "symlink not followed",
                });
                continue;
            }

            if (!options.IncludeHidden && IsHidden(info))
            {
                continue;
            }

            filesTotal++;
            string norm = ManifestPath.Normalize(rel);
            entries.Add(new ManifestEntry
            {
                Kind = EntryKind.File,
                Path = norm,
                Size = info.Length,
                Hash = null,   // filled in during the hashing phase
                Algorithm = options.Algorithm == ShaAlgo.Sha1
                    ? ShaAlgoForManifest.Sha1
                    : ShaAlgoForManifest.Sha256,
            });
            filesToHash.Add(norm);
        }
    }

    private static string Rel(string root, string full)
    {
        if (full.Length <= root.Length)
        {
            return string.Empty;
        }
        string r = full[root.Length..].TrimStart(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return r.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsReparsePoint(FileSystemInfo info)
        => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsHidden(FileSystemInfo info)
        => (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}
