using CubeScope.Core.State;

namespace CubeScope.Core.Tests;

public class StateStoreWindowTests
{
    [Fact]
    public void Aucun_etat_enregistre_rend_null()
    {
        using var store = new StateStore(Path.Combine(Path.GetTempPath(), $"cs-{Guid.NewGuid():N}.db"));
        Assert.Null(store.GetWindowState());
    }

    [Fact]
    public void L_etat_relu_est_celui_qui_a_ete_ecrit()
    {
        string chemin = Path.Combine(Path.GetTempPath(), $"cs-{Guid.NewGuid():N}.db");
        using var store = new StateStore(chemin);

        store.SaveWindowState(100, 200, 1280, 800, maximized: true);
        var etat = store.GetWindowState();

        Assert.NotNull(etat);
        Assert.Equal(100, etat!.X);
        Assert.Equal(200, etat.Y);
        Assert.Equal(1280, etat.Width);
        Assert.Equal(800, etat.Height);
        Assert.True(etat.Maximized);
    }

    [Fact]
    public void Un_second_enregistrement_remplace_le_premier()
    {
        string chemin = Path.Combine(Path.GetTempPath(), $"cs-{Guid.NewGuid():N}.db");
        using var store = new StateStore(chemin);

        store.SaveWindowState(0, 0, 800, 600, maximized: false);
        store.SaveWindowState(10, 20, 1600, 900, maximized: false);

        var etat = store.GetWindowState();
        Assert.Equal(1600, etat!.Width);
    }
}
