namespace CubeScope.Core.Tests;

/// <summary>
/// Cible des tests d'intégration, paramétrée par variables d'environnement pour ne
/// coder aucun identifiant réel dans le dépôt public. Les tests [Category=Integration]
/// nécessitent un vrai serveur SSAS Multidimensional ; définir les variables ci-dessous
/// pour les lancer localement (sinon valeurs neutres qui ne pointent nulle part) :
///   CUBESCOPE_TEST_SERVER, CUBESCOPE_TEST_CATALOG, CUBESCOPE_TEST_CATALOG_DEV,
///   CUBESCOPE_TEST_CUBE, CUBESCOPE_TEST_HIERARCHY, CUBESCOPE_TEST_MEASURE.
/// </summary>
internal static class TestTarget
{
    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    public static string Server => Env("CUBESCOPE_TEST_SERVER", "localhost");
    public static string Catalog => Env("CUBESCOPE_TEST_CATALOG", "SsasDb");
    public static string CatalogDev => Env("CUBESCOPE_TEST_CATALOG_DEV", "SsasDbDev");
    public static string Cube => Env("CUBESCOPE_TEST_CUBE", "Cube");
    public static string Hierarchy => Env("CUBESCOPE_TEST_HIERARCHY", "[Dim].[Hier]");
    public static string Measure => Env("CUBESCOPE_TEST_MEASURE", "[Measures].[Amount]");
}
