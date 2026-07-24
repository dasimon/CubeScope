using CubeScope.Core.Models;

namespace CubeScope.Core.Project;

/// <summary>
/// Navigateur de fichiers côté serveur (le serveur a accès complet au disque local ;
/// le navigateur, lui, cache le vrai chemin système). Sert à choisir un fichier .cube
/// de projet SSDT sans devoir taper le chemin à la main.
/// </summary>
public sealed class FileBrowserService
{
    /// <summary>
    /// Liste un dossier local : sous-dossiers + fichiers .cube, avec le parent et les lecteurs.
    /// path null/vide/inexistant → repli sur le profil utilisateur ; un fichier → son dossier.
    /// Enumération résiliente (dossiers inaccessibles ignorés, pas d'exception).
    /// </summary>
    public DirectoryListing List(string? path)
    {
        string dir = ResolveDirectory(path);

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
