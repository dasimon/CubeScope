using CubeScope.Core.State;

namespace CubeScope.Core.Tests;

public class StateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cubescope-test-{Guid.NewGuid():N}.db");
    private readonly StateStore _store;

    public StateStoreTests() => _store = new StateStore(_dbPath);

    [Fact]
    public void RecentConnection_UpsertNoDuplicates_MostRecentFirst()
    {
        _store.AddRecentConnection("SSAS-SERVER", "CatalogA");
        _store.AddRecentConnection("SSAS-SERVER", "CatalogB");
        _store.AddRecentConnection("SSAS-SERVER", "CatalogA"); // re-connexion → upsert

        var recents = _store.GetRecentConnections();

        Assert.Equal(2, recents.Count);
        Assert.Equal("CatalogA", recents[0].Catalog);
    }

    [Fact]
    public void RecentConnection_NullCatalog_RoundTripsAndUpserts()
    {
        _store.AddRecentConnection("SSAS-SERVER", null);
        _store.AddRecentConnection("SSAS-SERVER", null);

        var recents = _store.GetRecentConnections();

        var only = Assert.Single(recents);
        Assert.Null(only.Catalog);
    }

    [Fact]
    public void History_InsertAndReadBack_NewestFirst()
    {
        _store.AddHistory("SSAS-SERVER", "CatalogA", "SELECT ...", true, 120, 4, null);
        _store.AddHistory("SSAS-SERVER", "CatalogA", "SELECT BAD", false, 30, 0, "Erreur de syntaxe");

        var h = _store.GetHistory();

        Assert.Equal(2, h.Count);
        Assert.False(h[0].Success);
        Assert.Equal("Erreur de syntaxe", h[0].Error);
        Assert.True(h[1].Success);
        Assert.Equal(120, h[1].DurationMs);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        _store.AddRecentConnection("S", "C");
        // Réouverture du même fichier → Migrate ne doit rien casser ni dupliquer
        using var second = new StateStore(_dbPath);
        Assert.Single(second.GetRecentConnections());
    }

    [Fact]
    public void RecentProjects_UpsertAndOrderByLastUsed()
    {
        using var store = new StateStore(Path.Combine(Path.GetTempPath(), $"cubescope-test-{Guid.NewGuid():N}.db"));
        store.AddRecentProject(@"C:\proj\Cube1.cube");
        store.AddRecentProject(@"C:\proj\Cube2.cube");
        store.AddRecentProject(@"C:\proj\Cube1.cube"); // ré-ouverture → remonte en tête
        var list = store.GetRecentProjects();
        Assert.Equal(2, list.Count);
        Assert.Equal(@"C:\proj\Cube1.cube", list[0].Path);
    }

    [Fact]
    public void Snippets_AddListDelete()
    {
        var id1 = _store.AddSnippet("Ventes par devise", "SELECT { [Measures].[Ventes] } ON 0 FROM [Cube]");
        var id2 = _store.AddSnippet("Alpha", "SELECT { [Measures].[Alpha] } ON 0 FROM [Cube]");

        Assert.True(id1 > 0);
        Assert.True(id2 > 0);
        Assert.NotEqual(id1, id2);

        var list = _store.GetSnippets();
        Assert.Equal(2, list.Count);
        // ORDER BY Name COLLATE NOCASE : "Alpha" avant "Ventes par devise"
        Assert.Equal("Alpha", list[0].Name);
        Assert.Equal("SELECT { [Measures].[Alpha] } ON 0 FROM [Cube]", list[0].Mdx);
        Assert.Equal("Ventes par devise", list[1].Name);
        Assert.Equal(id2, list[0].Id);
        Assert.Equal(id1, list[1].Id);

        _store.DeleteSnippet(id2);

        var remaining = _store.GetSnippets();
        var only = Assert.Single(remaining);
        Assert.Equal("Ventes par devise", only.Name);
        Assert.Equal(id1, only.Id);
    }

    [Fact]
    public void ProfileRuns_AddAndList()
    {
        _store.AddProfileRun("SSAS-SERVER", "CatalogA", "SELECT ... A", 100, 20, 80, 3, 1, 2);
        _store.AddProfileRun("SSAS-SERVER", "CatalogA", "SELECT ... B", 150, 40, 110, 5, 0, 4);

        var runs = _store.GetProfileRuns();

        Assert.Equal(2, runs.Count);
        // Newest first
        Assert.Equal("SELECT ... B", runs[0].Mdx);
        Assert.Equal(150, runs[0].TotalMs);
        Assert.Equal(40, runs[0].StorageEngineMs);
        Assert.Equal(110, runs[0].FormulaEngineMs);
        Assert.Equal(5, runs[0].SubcubeCount);
        Assert.Equal(0, runs[0].CacheHits);
        Assert.Equal(4, runs[0].AggregationHits);
        Assert.Equal("CatalogA", runs[0].Catalog);
        Assert.Equal("SSAS-SERVER", runs[0].Server);

        Assert.Equal("SELECT ... A", runs[1].Mdx);
        Assert.Equal(100, runs[1].TotalMs);
        Assert.Equal(20, runs[1].StorageEngineMs);
        Assert.Equal(80, runs[1].FormulaEngineMs);
        Assert.Equal(3, runs[1].SubcubeCount);
        Assert.Equal(1, runs[1].CacheHits);
        Assert.Equal(2, runs[1].AggregationHits);
    }

    [Fact]
    public void DeployLog_AddAndList()
    {
        _store.AddDeployLog("SSAS-SERVER", "DemoDbDev", "DemoCube", @"C:\proj\Cube1.cube", 1234, false);
        _store.AddDeployLog("SSAS-SERVER", null, "DemoDb", @"C:\proj\Cube2.cube", 5678, true);

        var log = _store.GetDeployLog();

        Assert.Equal(2, log.Count);
        // Newest first
        Assert.Equal("DemoDb", log[0].CubeName);
        Assert.Null(log[0].Catalog);
        Assert.True(log[0].Forced);
        Assert.Equal(5678, log[0].ScriptChars);
        Assert.Equal(@"C:\proj\Cube2.cube", log[0].ProjectPath);
        Assert.Equal("SSAS-SERVER", log[0].Server);

        Assert.Equal("DemoCube", log[1].CubeName);
        Assert.Equal("DemoDbDev", log[1].Catalog);
        Assert.False(log[1].Forced);
        Assert.Equal(1234, log[1].ScriptChars);
        Assert.Equal(@"C:\proj\Cube1.cube", log[1].ProjectPath);
    }

    [Fact]
    public void Regression_AddListDelete()
    {
        var id1 = _store.AddRegressionCase("Ventes", "SELECT ... V", "{\"cellCount\":4}");
        var id2 = _store.AddRegressionCase("Alpha", "SELECT ... A", "{\"cellCount\":2}");

        Assert.True(id1 > 0);
        Assert.NotEqual(id1, id2);

        var list = _store.GetRegressionCases();
        Assert.Equal(2, list.Count);
        // ORDER BY Name COLLATE NOCASE : "Alpha" avant "Ventes"
        Assert.Equal("Alpha", list[0].Name);
        Assert.Equal("{\"cellCount\":2}", list[0].ExpectedJson);
        Assert.Equal(id2, list[0].Id);
        Assert.Equal("Ventes", list[1].Name);

        _store.DeleteRegressionCase(id2);

        var only = Assert.Single(_store.GetRegressionCases());
        Assert.Equal("Ventes", only.Name);
        Assert.Equal(id1, only.Id);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
