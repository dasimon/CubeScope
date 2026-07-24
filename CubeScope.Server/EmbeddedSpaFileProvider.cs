using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace CubeScope.Server;

/// <summary>
/// Sert la SPA embarquée dans l'assembly (EmbeddedResource préfixés « spa/ ») sans
/// aucune dépendance au système de fichiers → l'exe single-file est déplaçable et
/// fonctionne seul. Repli côté hôte : si aucune ressource « spa/ » n'est embarquée
/// (build de dev), on garde le provider physique + proxy Vite.
/// </summary>
public sealed class EmbeddedSpaFileProvider : IFileProvider
{
    private readonly Assembly _asm;
    // chemin normalisé (ex. "assets/index-x.js") -> nom de ressource réel
    private readonly Dictionary<string, string> _files;

    public EmbeddedSpaFileProvider(Assembly asm, string prefix)
    {
        _asm = asm;
        _files = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                n => n[prefix.Length..].Replace('\\', '/').TrimStart('/'),
                n => n,
                StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _files.Count;

    public IFileInfo GetFileInfo(string subpath)
    {
        var key = subpath.TrimStart('/').Replace('\\', '/');
        return _files.TryGetValue(key, out var res)
            ? new EmbeddedFile(_asm, res, key)
            : new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class EmbeddedFile(Assembly asm, string resource, string name) : IFileInfo
    {
        public bool Exists => true;
        public long Length
        {
            get { using var s = asm.GetManifestResourceStream(resource)!; return s.Length; }
        }
        public string? PhysicalPath => null;
        public string Name => Path.GetFileName(name);
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => asm.GetManifestResourceStream(resource)!;
    }
}
