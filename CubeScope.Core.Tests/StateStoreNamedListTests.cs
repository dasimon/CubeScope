using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using Microsoft.Data.Sqlite;

namespace CubeScope.Core.Tests;

public class StateStoreNamedListTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"cs-list-{Guid.NewGuid():N}.db");

    [Fact]
    public void Liste_enregistree_relue_avec_ses_cles_dans_l_ordre()
    {
        string path = NewPath();
        long id;
        using (var store = new StateStore(path)) id = store.AddNamedList(" Fonds actions ", "[Fonds].[Code].[Code]", ["B2", "A1"]);
        using var reopened = new StateStore(path);
        var list = Assert.Single(reopened.GetNamedLists());
        Assert.Equal(id, list.Id);
        Assert.Equal("Fonds actions", list.Name);
        Assert.Equal("[Fonds].[Code].[Code]", list.Level);
        Assert.Equal(["B2", "A1"], list.Keys);
        Assert.Equal(DateTimeKind.Utc, list.CreatedUtc.Kind);
    }

    [Fact]
    public void Liste_supprimee()
    {
        using var store = new StateStore(NewPath());
        long id = store.AddNamedList("L", "[A].[B].[C]", ["x"]);
        store.DeleteNamedList(id);
        Assert.Empty(store.GetNamedLists());
    }

    [Fact]
    public void Nom_vide_ou_liste_vide_refuses()
    {
        using var store = new StateStore(NewPath());
        Assert.Throws<ArgumentException>(() => store.AddNamedList("  ", "[A].[B].[C]", ["x"]));
        Assert.Throws<ArgumentException>(() => store.AddNamedList("L", "[A].[B].[C]", []));
    }

    [Fact]
    public void Niveau_invalide_ou_liste_trop_longue_refuses()
    {
        using var store = new StateStore(NewPath());
        Assert.Throws<ArgumentException>(() => store.AddNamedList("L", "Titre.ISIN", ["x"]));
        var many = Enumerable.Range(0, KeyList.MaxKeys + 1).Select(i => i.ToString()).ToList();
        Assert.Throws<ArgumentException>(() => store.AddNamedList("L", "[A].[B].[C]", many));
    }

    [Fact]
    public void Ligne_illisible_ne_cache_pas_les_autres_listes()
    {
        string path = NewPath();
        using (var store = new StateStore(path))
        {
            store.AddNamedList("Bonne", "[A].[B].[C]", ["x"]);
            store.AddNamedList("Abimee", "[A].[B].[C]", ["y"]);
        }
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE NamedList SET KeysJson = '{pas du json' WHERE Name = 'Abimee'";
            cmd.ExecuteNonQuery();
        }
        using var reopened = new StateStore(path);
        var lists = reopened.GetNamedLists();
        Assert.Equal(2, lists.Count);
        Assert.Empty(lists.Single(l => l.Name == "Abimee").Keys);
        Assert.Equal(["x"], lists.Single(l => l.Name == "Bonne").Keys);
    }

    [Fact]
    public void Migration_depuis_une_base_v10()
    {
        string path = NewPath();
        using (var store = new StateStore(path)) store.SetStaleThresholdHours(48);
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE NamedList; PRAGMA user_version = 10;";
            cmd.ExecuteNonQuery();
        }
        using var migrated = new StateStore(path);
        Assert.Empty(migrated.GetNamedLists());
        migrated.AddNamedList("L", "[A].[B].[C]", ["x"]);
        Assert.Single(migrated.GetNamedLists());
        Assert.Equal(48, migrated.GetStaleThresholdHours());
    }
}
