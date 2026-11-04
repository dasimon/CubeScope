using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CubeScope.Core.Updates;

/// <summary>Hands a verified download to the user: a copy in their Downloads folder, shown in
/// Explorer. The fallback when the application cannot replace itself (a protected folder, an add-in
/// locked by its host).</summary>
public static class UpdateDownloads
{
    // FOLDERID_Downloads: the folder can be moved by the user, so it is asked of the shell rather
    // than built from the profile path.
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Copies <paramref name="download"/> to <c>Downloads\<paramref name="fileName"/></c> and
    /// checks the copy's hash. Does not open anything: see <see cref="Reveal"/>.</summary>
    /// <returns>The path of the copy.</returns>
    /// <exception cref="UpdateException">The Downloads folder is unknown or the copy is not the verified file.</exception>
    /// <exception cref="IOException">The copy failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The copy was refused.</exception>
    public static string Save(VerifiedDownload download, string fileName)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("A file name, not a path, is expected.", nameof(fileName));

        var folderId = DownloadsFolderId;
        var hr = SHGetKnownFolderPath(ref folderId, 0, 0, out var downloads);
        if (hr != 0 || string.IsNullOrEmpty(downloads))
            throw new UpdateException($"the Downloads folder could not be found (0x{hr:X8})");

        var destination = Path.Combine(downloads, fileName);
        File.Copy(download.Path, destination, overwrite: true);
        if (!string.Equals(UpdateSwap.Sha256Of(destination), download.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new UpdateException($"the copy in {downloads} does not have the verified hash");
        }

        return destination;
    }

    /// <summary>Opens Explorer on the folder of <paramref name="path"/> with the file selected. False
    /// when Explorer could not be started: the file is there all the same, and the UI names it.</summary>
    public static bool Reveal(string path)
    {
        try
        {
            // Explorer by its full path: a bare name would be looked up through the search path.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(explorer, $"/select,\"{path}\"");
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out string? path);
}
