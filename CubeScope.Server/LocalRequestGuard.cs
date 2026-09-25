using Microsoft.AspNetCore.Http;

namespace CubeScope.Server;

/// <summary>
/// The local API has no token: anything that can reach 127.0.0.1 could drive it. Listening
/// on loopback only keeps other machines out, NOT the other web pages open in the user's
/// browser. Three checks close that gap, applied to every request (/api, /hubs, SPA):
/// - Host must name the loopback: defeats DNS rebinding (evil.example resolving to 127.0.0.1
///   still sends "Host: evil.example");
/// - Origin, when present, must be a loopback origin: defeats cross-origin fetch/WebSocket
///   (a WebSocket is not subject to CORS, only its Origin header gives it away);
/// - Sec-Fetch-Site must not be cross-site/same-site: defeats simple CSRF (form posts,
///   sendBeacon from another site). "none" (typed URL, WebView initial navigation) and
///   "same-origin" (the page itself, including the /api/leaving beacon) go through.
/// Any port is accepted: in dev the page lives on Vite (localhost:5173) and the proxy
/// forwards its Host unchanged.
/// </summary>
public static class LocalRequestGuard
{
    /// <summary>
    /// CSP of every response (it is what matters on index.html). No inline script and no
    /// eval: the Vite build emits external modules only, and Monaco loads its worker from
    /// a same-origin file (blob: kept for its fallback). 'unsafe-inline' styles: PrimeVue
    /// and Monaco inject &lt;style&gt; elements at runtime. data: images/fonts: CSS-inlined
    /// assets of PrimeVue/Monaco. Not applied in dev: Vite serves index.html itself.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; font-src 'self' data:; "
        + "connect-src 'self' ws://127.0.0.1:* ws://localhost:*; worker-src 'self' blob:; "
        + "object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    /// <returns>null when the request may proceed, otherwise the reason for refusing it.</returns>
    public static string? Check(string? host, string? origin, string? secFetchSite)
    {
        if (string.IsNullOrEmpty(host) || !IsLoopbackName(new HostString(host).Host))
            return "Refused: Host header is not a loopback name.";

        if (!string.IsNullOrEmpty(origin)
            && !(Uri.TryCreate(origin, UriKind.Absolute, out var o)
                 && o.Scheme == Uri.UriSchemeHttp && IsLoopbackName(o.Host)))
            return "Refused: cross-origin request.";

        if (string.Equals(secFetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)
            || string.Equals(secFetchSite, "same-site", StringComparison.OrdinalIgnoreCase))
            return "Refused: cross-site request.";

        return null;
    }

    private static bool IsLoopbackName(string? name) =>
        string.Equals(name, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase);
}
