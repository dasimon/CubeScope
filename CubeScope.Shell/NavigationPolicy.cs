namespace CubeScope.Shell;

/// <summary>What the native window does with a navigation or a link.</summary>
public enum NavigationDecision
{
    /// <summary>The application itself (same origin): the WebView navigates.</summary>
    Allow,

    /// <summary>External web page: opened in the default browser, the WebView stays on the application.</summary>
    OpenExternally,

    /// <summary>Anything else (file:, other schemes, unparseable): neither navigated nor handed to the shell.</summary>
    Block,
}

public static class NavigationPolicy
{
    /// <summary>
    /// A link clicked in an AI answer used to REPLACE the application inside the window, with
    /// no address bar and no way back. Only the application's own origin stays in the WebView;
    /// blob: URLs of that origin too (CSV/TSV export downloads).
    /// </summary>
    public static NavigationDecision Decide(string? target, string appUrl)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || !Uri.TryCreate(appUrl, UriKind.Absolute, out var app))
            return NavigationDecision.Block;

        if (SameOrigin(uri, app)) return NavigationDecision.Allow;
        if (uri.Scheme == "blob"
            && Uri.TryCreate(target![(uri.Scheme.Length + 1)..], UriKind.Absolute, out var inner)
            && SameOrigin(inner, app))
            return NavigationDecision.Allow;

        return IsWebUrl(target) ? NavigationDecision.OpenExternally : NavigationDecision.Block;
    }

    /// <summary>
    /// Only http/https may be handed to the shell (Process.Start with UseShellExecute): any
    /// other scheme (file:, ms-*, search-ms:, a registered protocol handler…) would launch
    /// whatever program Windows associates with it.
    /// </summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.GetLeftPart(UriPartial.Authority), b.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);
}
