using System.Text.RegularExpressions;

namespace CubeScope.Core.Project;

/// <summary>
/// Validates the paths received from the API BEFORE any disk access. A UNC path
/// (\\host\share, //host/share, \\?\UNC\…) or a URI (XDocument.Load also fetches http://)
/// would make the server open an outbound SMB/HTTP connection with the user's Windows
/// credentials — leaking the NTLM hash to whoever controls the target. Even File.Exists
/// connects, so the decision is made on the string alone.
/// Accepted: a fully qualified local path ("C:\…"). Known limit: a mapped network drive
/// letter or a junction to a share is not detected — those are set up by the user, not
/// chosen by a remote page.
/// </summary>
public static partial class LocalPathGuard
{
    // Scheme of at least 2 characters: "C:" (drive letter) must not read as a URI scheme.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+:")]
    private static partial Regex UriScheme();

    /// <returns>The normalized full path.</returns>
    public static string EnsureLocal(string? path)
    {
        string p = (path ?? "").Trim();
        if (p.Length == 0)
            throw new InvalidOperationException("Path required.");
        if (UriScheme().IsMatch(p))
            throw new InvalidOperationException($"Refused path (URI): {p}");
        // Covers \\host, //host, mixed separators, and the \\?\ / \\.\ device forms (incl. \\?\UNC\).
        if (p.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"Refused path (network or device path): {p}");
        if (!Path.IsPathFullyQualified(p))
            throw new InvalidOperationException($"Refused path (not a full local path): {p}");

        string full = Path.GetFullPath(p);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"Refused path (network or device path): {p}");
        return full;
    }

    /// <summary><see cref="EnsureLocal"/> + the ".cube" extension (open/save/calcprops/deploy).</summary>
    public static string EnsureLocalCubeFile(string? path)
    {
        string full = EnsureLocal(path);
        if (!string.Equals(Path.GetExtension(full), ".cube", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refused path (a .cube file is expected): {full}");
        return full;
    }
}
