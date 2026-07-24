using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

/// <summary>
/// Tests d'intégration contre un vrai serveur SSAS, en LECTURE SEULE sur le catalogue de
/// prod — jamais de ClearCache ni d'écriture ici. Cible définie par variables d'env
/// (voir <see cref="TestTarget"/>). Nécessitent l'accès réseau au serveur.
/// </summary>
[Trait("Category", "Integration")]
public class SsasIntegrationTests : IDisposable
{
    private static string Server => TestTarget.Server;
    private readonly SsasSession _session = new();

    [Fact]
    public async Task Connect_ListsCatalogs_ContainsConfiguredCatalog()
    {
        var catalogs = await _session.ConnectAsync(Server);
        Assert.Contains(TestTarget.Catalog, catalogs);
    }

    [Fact]
    public async Task Execute_TwoAxesQuery_MapsToGrid()
    {
        await _session.ConnectAsync(Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new QueryService(_session);

        // Découvre une hiérarchie réelle plutôt que d'en supposer une.
        // Piège : crocheter les colonnes DMV — HIERARCHY est un mot réservé MDX
        var hier = await _session.ExecuteDmvAsync($"""
            SELECT [HIERARCHY_UNIQUE_NAME] FROM $SYSTEM.MDSCHEMA_HIERARCHIES
            WHERE [CUBE_NAME] = '{TestTarget.Cube}' AND [HIERARCHY_ORIGIN] = 2
            """);
        string hierarchy = (string)hier.Rows[0]["HIERARCHY_UNIQUE_NAME"];

        var r = await svc.ExecuteAsync($$"""
            SELECT { [Measures].DefaultMember } ON COLUMNS,
                   Head({{hierarchy}}.Members, 3) ON ROWS
            FROM [{{TestTarget.Cube}}]
            """);

        Assert.Equal(2, r.AxesCount);
        Assert.True(r.Columns.Count >= 2); // 1 en-tête de ligne + au moins 1 mesure
        Assert.True(r.Rows.Count is > 0 and <= 3);
        Assert.True(r.DurationMs >= 0);
    }

    [Fact]
    public async Task Execute_CellPropertiesValueOnly_StillReturnsValues()
    {
        // Piège constaté : "CELL PROPERTIES VALUE" (requête copiée d'Excel/SSMS) ne renvoie
        // pas FORMATTED_VALUE — la grille doit se replier sur Value, pas afficher du vide.
        await _session.ConnectAsync(Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new QueryService(_session);

        var r = await svc.ExecuteAsync($$"""
            SELECT { {{TestTarget.Measure}} } ON COLUMNS
            FROM [{{TestTarget.Cube}}]
            CELL PROPERTIES VALUE
            """);

        var cell = Assert.Single(r.Rows)["v0"];
        Assert.NotNull(cell);
        Assert.False(string.IsNullOrWhiteSpace(cell.ToString()));
    }

    [Fact]
    public async Task Execute_InvalidMdx_ThrowsWithServerMessage()
    {
        await _session.ConnectAsync(Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new QueryService(_session);

        var ex = await Record.ExceptionAsync(() => svc.ExecuteAsync("SELECT N'IMPORTE QUOI"));

        Assert.NotNull(ex);
        Assert.False(string.IsNullOrWhiteSpace(ex!.Message));
    }

    [Fact]
    public async Task ClearCache_OnDevCatalogOnly_ResolvesIdViaAmoAndSucceeds()
    {
        // Règle de sûreté : tout ce qui vide le cache cible un catalogue de DEV, jamais la prod.
        await _session.ConnectAsync(Server);
        await _session.SetCatalogAsync(TestTarget.CatalogDev);
        var svc = new CacheService(_session);

        var (databaseId, durationMs) = await svc.ClearCacheAsync();

        Assert.False(string.IsNullOrWhiteSpace(databaseId));
        Assert.True(durationMs >= 0);
        // L'ID résolu est mis en cache : 2ᵉ résolution sans AMO
        var id2 = await svc.ResolveDatabaseIdAsync(Server, TestTarget.CatalogDev);
        Assert.Equal(databaseId, id2);
    }

    [Fact]
    public async Task GetScript_OnConfiguredCube_ReadsAndParsesMdxScript()
    {
        await _session.ConnectAsync(Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new CubeScope.Core.Script.ScriptService(_session);

        var script = await svc.GetScriptAsync(TestTarget.Cube);

        Assert.False(string.IsNullOrWhiteSpace(script.FullText), "script vide");
        Assert.NotEmpty(script.Commands);
        // Un cube réel a des dizaines de mesures calculées dans son script
        Assert.True(script.Commands.Count(c => c.Kind == "CalculatedMember") > 10,
            $"attendu : dizaines de membres calculés, obtenu {script.Commands.Count(c => c.Kind == "CalculatedMember")}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DeployScript_Idempotent_OnDevCatalog()
    {
        // Lit le script actuel du cube de DEV puis le redéploie à l'identique :
        // aucune divergence attendue (force=false suffit), et l'état final = l'état initial.
        string text;
        using (var amo = new Microsoft.AnalysisServices.Server())
        {
            amo.Connect($"Data Source={TestTarget.Server};Integrated Security=SSPI;");
            try
            {
                var cube = amo.Databases.GetByName(TestTarget.CatalogDev).Cubes.FindByName(TestTarget.Cube)
                    ?? throw new InvalidOperationException($"Cube introuvable : {TestTarget.Cube}");
                text = string.Join("\n\n", cube.MdxScripts[0].Commands
                    .Cast<Microsoft.AnalysisServices.Command>()
                    .Select(c => c.Text?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
            }
            finally { amo.Disconnect(); }
        }

        var result = new CubeScope.Core.Project.ScriptDeployService()
            .Deploy(TestTarget.Server, TestTarget.CatalogDev, TestTarget.Cube, text, force: false);

        Assert.True(result.Deployed);
        Assert.False(result.Differs);
    }

    public void Dispose() => _session.Dispose();
}
