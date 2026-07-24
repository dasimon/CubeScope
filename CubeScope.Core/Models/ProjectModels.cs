namespace CubeScope.Core.Models;

/// <summary>Le MDX Script lu depuis un fichier .cube de projet SSDT (source de vérité).</summary>
public sealed record ProjectScript(
    string Path,
    string CubeName,
    string FullText,
    IReadOnlyList<ScriptCommand> Commands,
    bool CanEdit,
    string? ReadOnlyReason);

/// <summary>Projet .cube récemment ouvert (persisté en SQLite).</summary>
public sealed record RecentProject(string Path, DateTime LastUsedUtc);

/// <summary>Résultat d'un déploiement du script seul. Differs = le serveur portait un
/// script différent et force était false : rien n'a été écrit, ServerText à examiner.</summary>
public sealed record DeployScriptResult(
    bool Deployed,
    bool Differs,
    string? ServerText,
    long DurationMs);

/// <summary>Une entrée du navigateur de fichiers (dossier ou fichier .cube).</summary>
public sealed record FileEntry(string Name, string Path, bool IsDirectory);

/// <summary>Contenu d'un dossier pour le navigateur de fichiers côté serveur.</summary>
public sealed record DirectoryListing(
    string Path,
    string? Parent,
    IReadOnlyList<string> Drives,
    IReadOnlyList<FileEntry> Directories,
    IReadOnlyList<FileEntry> CubeFiles);

/// <summary>Un snippet MDX réutilisable, persisté en SQLite (bibliothèque locale).</summary>
public sealed record Snippet(long Id, string Name, string Mdx, DateTime CreatedUtc);
