using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace CubeScope.Server;

/// <summary>
/// Serves the SPA embedded in the assembly (EmbeddedResource entries prefixed "spa/") without
/// any file system dependency → the single-file exe can be moved and
/// works on its own. Host-side fallback: if no "spa/" resource is embedded
/// (dev build), the physical provider + Vite proxy are kept.
/// </summary>
public sealed class EmbeddedSpaFileProvider : IFileProvider
{
    private readonly Assembly _asm;
    // normalized path (e.g. "assets/index-x.js") -> actual resource name
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
