using CubeScope.Core.State;

namespace CubeScope.Core.Tests;

public class StateStoreDevServerTests
{
    private static StateStore Neuf()
        => new(Path.Combine(Path.GetTempPath(), $"cs-dev-{Guid.NewGuid():N}.db"));

    [Fact]
    public void Aucun_serveur_de_dev_au_depart()
    {
        // Fail-closed : un poste neuf ne déclare rien, donc tout déploiement est traité
        // comme visant la production.
        using var store = Neuf();
        Assert.Empty(store.GetDevServers());
    }

    [Fact]
    public void Un_serveur_marque_est_relu()
    {
        using var store = Neuf();
        store.SetDevServer("SRV-DEV", true);
        Assert.Equal(["SRV-DEV"], store.GetDevServers());
    }

    [Fact]
    public void Marquer_deux_fois_ne_duplique_pas()
    {
        using var store = Neuf();
        store.SetDevServer("SRV-DEV", true);
        store.SetDevServer("SRV-DEV", true);
        Assert.Single(store.GetDevServers());
    }

    [Fact]
    public void Un_serveur_peut_etre_retire()
    {
        using var store = Neuf();
        store.SetDevServer("SRV-DEV", true);
        store.SetDevServer("SRV-DEV", false);
        Assert.Empty(store.GetDevServers());
    }

    [Fact]
    public void Retirer_un_serveur_absent_ne_leve_pas()
    {
        using var store = Neuf();
        store.SetDevServer("INCONNU", false);
        Assert.Empty(store.GetDevServers());
    }

    [Fact]
    public void La_liste_survit_a_la_reouverture()
    {
        string chemin = Path.Combine(Path.GetTempPath(), $"cs-dev-{Guid.NewGuid():N}.db");
        using (var premier = new StateStore(chemin)) premier.SetDevServer("SRV-DEV", true);
        using var second = new StateStore(chemin);
        Assert.Equal(["SRV-DEV"], second.GetDevServers());
    }

    [Fact]
    public void Un_nom_vide_n_est_pas_enregistre()
    {
        // Une entrée vide rendrait « dev » une connexion sans serveur si la garde
        // comparait naïvement.
        using var store = Neuf();
        store.SetDevServer("   ", true);
        Assert.Empty(store.GetDevServers());
    }
}
