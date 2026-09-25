namespace CubeScope.Server;

/// <summary>
/// Cache-Control of the SPA files. Without it, the browser (and WebView2, whose cache persists
/// under %LOCALAPPDATA%) caches index.html heuristically — the embedded files carry no real
/// Last-Modified — and, when the port happens to be reused, shows the PREVIOUS version's UI after
/// an update. index.html is always revalidated; Vite's assets have a content hash in their name,
/// so they can be kept forever.
/// </summary>
public static class SpaCachePolicy
{
    public static string? For(string requestPath, string fileName)
    {
        if (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return "no-cache";
        if (requestPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            return "public, max-age=31536000, immutable";
        return null;
    }
}
