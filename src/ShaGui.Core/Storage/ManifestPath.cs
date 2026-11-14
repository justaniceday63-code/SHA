using System.Text;

namespace ShaGui.Core.Storage;

/// <summary>
/// Canonical ordering and normalization for manifest paths.
/// </summary>
/// <remarks>
/// Two machines must produce byte-identical manifests for the same tree, so
/// the rules are fixed and documented (docs/MANIFEST.md):
/// <list type="number">
///   <item>Normalize to Unicode NFC (NFD on macOS/Linux would otherwise
///   split one name into two different byte sequences).</item>
///   <item>Convert '\' to '/' (Windows-native paths must not leak their
///   separator into a portable manifest).</item>
///   <item>Compare as UTF-8 bytes, ordinally. UTF-8 preserves code-point
///   order, and byte-wise comparison has no locale, culture or casing
///   involvement — unlike <see cref="string.Compare(string, string,
///   StringComparison)"/> which operates on UTF-16 code units and can
///   disagree with code-point order for supplementary characters.</item>
/// </list>
/// </remarks>
public static class ManifestPath
{
    /// <summary>Normalizes one path segment list to the canonical manifest form.</summary>
    public static string Normalize(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        string p = relativePath.Replace('\\', '/');

        // NFC: the form Windows and iOS produce natively.
        if (p.Length > 0 && !IsNormalized(p))
        {
            p = p.Normalize(NormalizationForm.FormC);
        }

        // Strip redundant './' and collapse '//' — but never touch a leading
        // '/' (absolute paths are rejected by the walker before this).
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }
        while (p.Contains("//", StringComparison.Ordinal))
        {
            p = p.Replace("//", "/", StringComparison.Ordinal);
        }
        if (p.Length > 1 && p[^1] == '/')
        {
            p = p.TrimEnd('/');
        }
        return p;
    }

    private static bool IsNormalized(string s)
    {
        // Cheap guard: Normalize() allocates only when a change is needed in
        // modern .NET, but comparing first avoids work for the common case.
        return string.Equals(s, s.Normalize(NormalizationForm.FormC),
            StringComparison.Ordinal);
    }

    /// <summary>UTF-8 byte-wise ordinal comparison — the canonical sort.</summary>
    public static int Compare(string a, string b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }
        if (a is null)
        {
            return -1;
        }
        if (b is null)
        {
            return 1;
        }

        // Short-circuit the ASCII fast path (the overwhelming majority of
        // real paths) without allocating.
        int common = Math.Min(a.Length, b.Length);
        bool ascii = true;
        for (int i = 0; i < common; i++)
        {
            if (a[i] > 0x7F)
            {
                ascii = false;
                break;
            }
            int d = a[i] - b[i];
            if (d != 0)
            {
                return d;
            }
        }
        if (ascii)
        {
            return a.Length - b.Length;
        }

        byte[] ba = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        int n = Math.Min(ba.Length, bb.Length);
        for (int i = 0; i < n; i++)
        {
            if (ba[i] != bb[i])
            {
                return ba[i] - bb[i];
            }
        }
        return ba.Length - bb.Length;
    }
}

/// <summary><see cref="IComparer{T}"/> over <see cref="ManifestPath.Compare"/>.</summary>
public sealed class ManifestPathComparer : IComparer<string>
{
    public static readonly ManifestPathComparer Instance = new();

    public int Compare(string? x, string? y) => ManifestPath.Compare(x!, y!);
}

/// <summary>Comparer that orders <see cref="ManifestEntry"/> by their canonical path.</summary>
public sealed class ManifestEntryComparer : IComparer<ManifestEntry>
{
    public static readonly ManifestEntryComparer Instance = new();

    public int Compare(ManifestEntry? x, ManifestEntry? y)
        => ManifestPath.Compare(x?.Path ?? string.Empty, y?.Path ?? string.Empty);
}
