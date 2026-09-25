using CubeScope.Server;

namespace CubeScope.Core.Tests;

/// <summary>
/// The local API has no token: what keeps a web page opened elsewhere in the browser from
/// driving it (DNS rebinding, CSRF, cross-site fetch) is this check on Host, Origin and
/// Sec-Fetch-Site. It decides, on every request, whether a third party can deploy a script
/// or clear a cache — hence tests on the pure decision.
/// </summary>
public class LocalRequestGuardTests
{
    [Theory]
    [InlineData("127.0.0.1:5311")]
    [InlineData("localhost:5173")] // Vite proxy without changeOrigin forwards its own Host
    [InlineData("LOCALHOST:5199")]
    [InlineData("127.0.0.1")]
    public void Loopback_host_without_other_headers_is_allowed(string host)
        => Assert.Null(LocalRequestGuard.Check(host, null, null));

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:5311")] // DNS rebinding: the name resolves to 127.0.0.1, the Host header gives it away
    [InlineData("127.0.0.1.evil.example")]
    [InlineData("localhost.evil.example:80")]
    [InlineData("")]
    [InlineData(null)]
    public void Foreign_or_missing_host_is_refused(string? host)
        => Assert.NotNull(LocalRequestGuard.Check(host, null, null));

    [Theory]
    [InlineData("http://127.0.0.1:5311")]
    [InlineData("http://localhost:5173")]
    public void Local_origin_is_allowed(string origin)
        => Assert.Null(LocalRequestGuard.Check("127.0.0.1:5311", origin, "same-origin"));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://evil.example:5311")]
    [InlineData("null")] // sandboxed iframe, file:// page
    [InlineData("file://")]
    [InlineData("not a url")]
    public void Foreign_origin_is_refused(string origin)
        => Assert.NotNull(LocalRequestGuard.Check("127.0.0.1:5311", origin, null));

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("CROSS-SITE")]
    public void Cross_or_same_site_fetch_is_refused(string site)
        => Assert.NotNull(LocalRequestGuard.Check("127.0.0.1:5311", null, site));

    [Theory]
    [InlineData("same-origin")] // the page's own fetch, and the /api/leaving beacon
    [InlineData("none")]        // initial navigation of the WebView / typed URL
    public void Same_origin_or_user_navigation_is_allowed(string site)
        => Assert.Null(LocalRequestGuard.Check("127.0.0.1:5311", null, site));

    [Fact]
    public void Csp_forbids_inline_scripts_and_framing()
    {
        string csp = LocalRequestGuard.ContentSecurityPolicy;
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
    }
}
