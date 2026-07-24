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

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
