using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

/// <summary>
/// Integration tests against a real SSAS server, READ-ONLY on the prod catalog — never a
/// ClearCache or a write here. Target defined by environment variables
/// (see <see cref="TestTarget"/>). They require network access to the server.
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

        // Discovers a real hierarchy rather than assuming one.
        // Pitfall: bracket the DMV columns — HIERARCHY is an MDX reserved word
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
        Assert.True(r.Columns.Count >= 2); // 1 row header + at least 1 measure
        Assert.True(r.Rows.Count is > 0 and <= 3);
        Assert.True(r.DurationMs >= 0);
    }

    [Fact]
    public async Task Execute_CellPropertiesValueOnly_StillReturnsValues()
    {
        // Observed pitfall: "CELL PROPERTIES VALUE" (query copied from Excel/SSMS) does not return
        // FORMATTED_VALUE — the grid must fall back to Value, not display blanks.
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
        // Safety rule: anything that clears the cache targets the dev SERVER, never prod.
        // The catalog name no longer protects anything — prod and dev share the same one.
        TestTarget.AssertDevServerDistinct();
        await _session.ConnectAsync(TestTarget.ServerDev);
        await _session.SetCatalogAsync(TestTarget.CatalogDev);
        var svc = new CacheService(_session);

        var (databaseId, durationMs) = await svc.ClearCacheAsync();

        Assert.False(string.IsNullOrWhiteSpace(databaseId));
        Assert.True(durationMs >= 0);
        // The resolved ID is cached: 2nd resolution without AMO
        var id2 = await svc.ResolveDatabaseIdAsync(TestTarget.ServerDev, TestTarget.CatalogDev);
        Assert.Equal(databaseId, id2);
    }

    [Fact]
    public async Task GetScript_OnConfiguredCube_ReadsAndParsesMdxScript()
    {
        await _session.ConnectAsync(Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new CubeScope.Core.Script.ScriptService(_session);

        var script = await svc.GetScriptAsync(TestTarget.Cube);

        Assert.False(string.IsNullOrWhiteSpace(script.FullText), "empty script");
        Assert.NotEmpty(script.Commands);
        // A real cube has dozens of calculated measures in its script
        Assert.True(script.Commands.Count(c => c.Kind == "CalculatedMember") > 10,
            $"expected: dozens of calculated members, got {script.Commands.Count(c => c.Kind == "CalculatedMember")}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DeployScript_Idempotent_OnDevCatalog()
    {
        // Reads the current script of the DEV cube then redeploys it unchanged:
        // no divergence expected (force=false is enough), and final state = initial state.
        TestTarget.AssertDevServerDistinct();
        string text;
        using (var amo = new Microsoft.AnalysisServices.Server())
        {
            amo.Connect($"Data Source={TestTarget.ServerDev};Integrated Security=SSPI;");
            try
            {
                var cube = amo.Databases.GetByName(TestTarget.CatalogDev).Cubes.FindByName(TestTarget.Cube)
                    ?? throw new InvalidOperationException($"Cube not found: {TestTarget.Cube}");
                text = string.Join("\n\n", cube.MdxScripts[0].Commands
                    .Cast<Microsoft.AnalysisServices.Command>()
                    .Select(c => c.Text?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
            }
            finally { amo.Disconnect(); }
        }

        var result = new CubeScope.Core.Project.ScriptDeployService()
            .Deploy(TestTarget.ServerDev, TestTarget.CatalogDev, TestTarget.Cube, text,
                force: false, devServers: [TestTarget.ServerDev]);

        Assert.True(result.Deployed);
        Assert.False(result.Differs);
    }

    public void Dispose() => _session.Dispose();
}
