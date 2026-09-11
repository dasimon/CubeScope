using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

/// <summary>
/// Le discriminant prod/dev était le NOM DU CATALOGUE (« contient dev ») tant que le dev vivait
/// sur le serveur de prod sous un autre nom. Depuis que le dev a son propre serveur, les deux
/// catalogues portent le même nom : c'est le SERVEUR qui décide, et seulement via une liste
/// explicite. Ces tests verrouillent la règle, dont dépend le refus de déployer en production.
/// </summary>
public class DevServerGuardTests
{
    [Fact]
    public void Un_serveur_de_la_liste_est_un_serveur_de_dev()
        => Assert.True(DevServerGuard.IsDev(["SRV-DEV"], "SRV-DEV"));

    [Fact]
    public void Un_serveur_absent_de_la_liste_ne_l_est_pas()
        => Assert.False(DevServerGuard.IsDev(["SRV-DEV"], "SRV-PROD"));

    [Fact]
    public void Liste_vide_ne_declare_aucun_serveur_de_dev()
    {
        // Fail-closed : une configuration absente doit gêner, jamais autoriser. Si la liste
        // vide rendait « true », un poste neuf déploierait en production sans un avertissement.
        Assert.False(DevServerGuard.IsDev([], "SRV-DEV"));
    }

    [Theory]
    [InlineData("srv-dev")]
    [InlineData("SRV-DEV")]
    [InlineData("  SRV-DEV  ")]
    public void La_casse_et_les_espaces_ne_changent_rien(string saisi)
    {
        // Un nom de serveur Windows est insensible à la casse, et un espace collé au
        // copier-coller ne doit pas transformer un serveur de dev en production.
        Assert.True(DevServerGuard.IsDev(["SRV-DEV"], saisi));
    }

    [Fact]
    public void La_liste_aussi_est_normalisee()
        => Assert.True(DevServerGuard.IsDev(["  srv-dev "], "SRV-DEV"));

    [Fact]
    public void Un_serveur_vide_n_est_jamais_de_dev()
    {
        // Sinon une liste contenant une entrée vide (saisie ratée) déclarerait « dev »
        // une connexion sans serveur.
        Assert.False(DevServerGuard.IsDev(["", "SRV-DEV"], ""));
        Assert.False(DevServerGuard.IsDev(["SRV-DEV"], "   "));
    }

    [Fact]
    public void Une_correspondance_partielle_ne_suffit_pas()
    {
        // « contient » était justement le défaut de l'ancienne règle sur le nom de catalogue :
        // un serveur nommé SRV-DEV-PROD n'est pas SRV-DEV.
        Assert.False(DevServerGuard.IsDev(["SRV-DEV"], "SRV-DEV-PROD"));
        Assert.False(DevServerGuard.IsDev(["SRV-DEV"], "SRV"));
    }
}
