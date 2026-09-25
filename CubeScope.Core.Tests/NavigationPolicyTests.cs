using CubeScope.Shell;

namespace CubeScope.Core.Tests;

/// <summary>
/// The native window has no address bar: a navigation away from the application replaces it
/// with no way back, and handing an arbitrary URI to the shell launches whatever program
/// Windows associates with its scheme.
/// </summary>
public class NavigationPolicyTests
{
    private const string App = "http://127.0.0.1:5311";

    [Theory]
    [InlineData("http://127.0.0.1:5311/")]
    [InlineData("http://127.0.0.1:5311/index.html?x=1#y")]
    [InlineData("blob:http://127.0.0.1:5311/6f1c1f7e-2d7a-4a53-9a8e-0d2f5b1e8b1a")] // result export
    public void Application_origin_stays_in_the_window(string target)
        => Assert.Equal(NavigationDecision.Allow, NavigationPolicy.Decide(target, App));

    [Theory]
    [InlineData("https://learn.microsoft.com/analysis-services")]
    [InlineData("http://example.com/")]
    [InlineData("http://127.0.0.1:9999/")] // another local port is another application
    public void External_web_pages_go_to_the_browser(string target)
        => Assert.Equal(NavigationDecision.OpenExternally, NavigationPolicy.Decide(target, App));

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("blob:https://evil.example/uuid")]
    [InlineData("not a uri")]
    [InlineData(null)]
    public void Other_schemes_are_blocked(string? target)
        => Assert.Equal(NavigationDecision.Block, NavigationPolicy.Decide(target, App));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("\\\\host\\share", false)]
    [InlineData(null, false)]
    public void Only_http_and_https_are_handed_to_the_shell(string? url, bool expected)
        => Assert.Equal(expected, NavigationPolicy.IsWebUrl(url));
}
