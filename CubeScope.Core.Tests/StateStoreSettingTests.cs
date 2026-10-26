using CubeScope.Core.State;
using Microsoft.Data.Sqlite;

namespace CubeScope.Core.Tests;

public class StateStoreSettingTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"cs-set-{Guid.NewGuid():N}.db");

    [Fact]
    public void Seuil_par_defaut_24_heures()
    {
        using var store = new StateStore(NewPath());
        Assert.Equal(24, store.GetStaleThresholdHours());
    }

    [Fact]
    public void Seuil_enregistre_est_relu_apres_reouverture()
    {
        string path = NewPath();
        using (var store = new StateStore(path)) store.SetStaleThresholdHours(72);
        using var reopened = new StateStore(path);
        Assert.Equal(72, reopened.GetStaleThresholdHours());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8761)]
    public void Seuil_hors_bornes_refuse(int hours)
    {
        using var store = new StateStore(NewPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetStaleThresholdHours(hours));
        Assert.Equal(24, store.GetStaleThresholdHours());
    }

    [Fact]
    public void Migration_depuis_une_base_v9_existante()
    {
        string path = NewPath();
        using (var store = new StateStore(path)) store.SetDevServer("SRV-DEV", true);
        // Back to v9 without the Setting table: what a database written by the previous version looks like.
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE Setting; PRAGMA user_version = 9;";
            cmd.ExecuteNonQuery();
        }

        using var migrated = new StateStore(path);
        Assert.Equal(24, migrated.GetStaleThresholdHours());
        migrated.SetStaleThresholdHours(48);
        Assert.Equal(48, migrated.GetStaleThresholdHours());
        Assert.Equal(["SRV-DEV"], migrated.GetDevServers());
    }

    [Fact]
    public void Valeur_illisible_retombe_sur_le_defaut()
    {
        string path = NewPath();
        using (var store = new StateStore(path)) store.SetStaleThresholdHours(12);
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE Setting SET Value = 'abc'";
            cmd.ExecuteNonQuery();
        }
        using var reopened = new StateStore(path);
        Assert.Equal(24, reopened.GetStaleThresholdHours());
    }
}
