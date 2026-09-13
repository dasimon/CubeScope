using CubeScope.Core.Models;

namespace CubeScope.Core.Project;

/// <summary>
/// Server-side file browser (the server has full access to the local disk;
/// the browser hides the real system path). Used to pick an SSDT project .cube file
/// without having to type the path by hand.
/// </summary>
public sealed class FileBrowserService
{
    /// <summary>
    /// Lists a local folder: subfolders + .cube files, with the parent and the drives.
    /// null/empty/non-existent path → fallback to the user profile; a file → its folder.
    /// Resilient enumeration (inaccessible folders skipped, no exception).
    /// </summary>
    public DirectoryListing List(string? path)
    {
        // GetFullPath: guarantee an absolute path whatever the input (an existing folder
        // passed as a relative path would otherwise be returned as is in DirectoryListing.Path).
        string dir = System.IO.Path.GetFullPath(ResolveDirectory(path));

        var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };

        var directories = Directory.EnumerateDirectories(dir, "*", enumOptions)
            .Select(d => new FileEntry(System.IO.Path.GetFileName(d), d, true))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cubeFiles = Directory.EnumerateFiles(dir, "*.cube", enumOptions)
            .Select(f => new FileEntry(System.IO.Path.GetFileName(f), f, false))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? parent = Directory.GetParent(dir)?.FullName;
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name).ToList();

        return new DirectoryListing(dir, parent, drives, directories, cubeFiles);
    }

    private static string ResolveDirectory(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            if (Directory.Exists(path))
                return path;
            if (File.Exists(path))
                return System.IO.Path.GetDirectoryName(path)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
