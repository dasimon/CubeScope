namespace CubeScope.Core.Tests;

/// <summary>
/// Target of the integration tests, parameterized by environment variables so that no
/// real identifier is hard-coded in the public repository. The [Category=Integration] tests
/// require a real SSAS Multidimensional server; set the variables below to run them
/// locally (otherwise neutral values that point nowhere):
///   CUBESCOPE_TEST_SERVER, CUBESCOPE_TEST_SERVER_DEV, CUBESCOPE_TEST_CATALOG,
///   CUBESCOPE_TEST_CATALOG_DEV, CUBESCOPE_TEST_CUBE, CUBESCOPE_TEST_HIERARCHY,
///   CUBESCOPE_TEST_MEASURE.
///
/// ⚠️ DEV IS A SERVER, NO LONGER A CATALOG. As long as dev lived on the production server
/// under another catalog name, "Server + CatalogDev" was enough to target dev. Since it got
/// its own server, both catalogs carry the SAME name: that combination would target
/// production. Every destructive test (ClearCache, deployment) must use
/// <see cref="ServerDev"/>, and call <see cref="AssertDevServerDistinct"/>.
/// </summary>
internal static class TestTarget
{
    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    public static string Server => Env("CUBESCOPE_TEST_SERVER", "localhost");

    /// <summary>Development server — the only target allowed for destructive tests.</summary>
    public static string ServerDev => Env("CUBESCOPE_TEST_SERVER_DEV", "localhost-dev");

    /// <summary>
    /// Refuses to continue if the dev server is the production server. A forgotten variable
    /// must not be able to silently land a ClearCache or a deployment on prod: better a red
    /// test than a modified production cube.
    /// </summary>
    public static void AssertDevServerDistinct()
    {
        if (string.Equals(Server, ServerDev, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"CUBESCOPE_TEST_SERVER_DEV equals the production server ({Server}). "
                + "Destructive test aborted: set a distinct dev server.");
    }
    public static string Catalog => Env("CUBESCOPE_TEST_CATALOG", "SsasDb");
    public static string CatalogDev => Env("CUBESCOPE_TEST_CATALOG_DEV", "SsasDbDev");
    public static string Cube => Env("CUBESCOPE_TEST_CUBE", "Cube");
    public static string Hierarchy => Env("CUBESCOPE_TEST_HIERARCHY", "[Dim].[Hier]");
    public static string Measure => Env("CUBESCOPE_TEST_MEASURE", "[Measures].[Amount]");
}
