namespace CubeScope.Core.Tests;

/// <summary>
/// Cible des tests d'intégration, paramétrée par variables d'environnement pour ne
/// coder aucun identifiant réel dans le dépôt public. Les tests [Category=Integration]
/// nécessitent un vrai serveur SSAS Multidimensional ; définir les variables ci-dessous
/// pour les lancer localement (sinon valeurs neutres qui ne pointent nulle part) :
///   CUBESCOPE_TEST_SERVER, CUBESCOPE_TEST_SERVER_DEV, CUBESCOPE_TEST_CATALOG,
///   CUBESCOPE_TEST_CATALOG_DEV, CUBESCOPE_TEST_CUBE, CUBESCOPE_TEST_HIERARCHY,
///   CUBESCOPE_TEST_MEASURE.
///
/// ⚠️ DEV EST UN SERVEUR, PLUS UN CATALOGUE. Tant que le dev vivait sur le serveur de
/// production sous un autre nom de catalogue, « Server + CatalogDev » suffisait à viser le
/// dev. Depuis qu'il a son propre serveur, les deux catalogues portent le MÊME nom : cette
/// combinaison viserait la production. Tout test destructif (ClearCache, déploiement) doit
/// utiliser <see cref="ServerDev"/>, et appeler <see cref="AssertDevServerDistinct"/>.
/// </summary>
internal static class TestTarget
{
    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    public static string Server => Env("CUBESCOPE_TEST_SERVER", "localhost");

    /// <summary>Serveur de développement — seule cible permise aux tests destructifs.</summary>
    public static string ServerDev => Env("CUBESCOPE_TEST_SERVER_DEV", "localhost-dev");

    /// <summary>
    /// Refuse de continuer si le serveur de dev est le serveur de production. Une variable
    /// oubliée ne doit pas pouvoir faire tomber un ClearCache ou un déploiement sur la prod
    /// en silence : mieux vaut un test rouge qu'un cube de production modifié.
    /// </summary>
    public static void AssertDevServerDistinct()
    {
        if (string.Equals(Server, ServerDev, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"CUBESCOPE_TEST_SERVER_DEV vaut le serveur de production ({Server}). "
                + "Test destructif interrompu : posez un serveur de dev distinct.");
    }
    public static string Catalog => Env("CUBESCOPE_TEST_CATALOG", "SsasDb");
    public static string CatalogDev => Env("CUBESCOPE_TEST_CATALOG_DEV", "SsasDbDev");
    public static string Cube => Env("CUBESCOPE_TEST_CUBE", "Cube");
    public static string Hierarchy => Env("CUBESCOPE_TEST_HIERARCHY", "[Dim].[Hier]");
    public static string Measure => Env("CUBESCOPE_TEST_MEASURE", "[Measures].[Amount]");
}
