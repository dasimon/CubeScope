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

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
