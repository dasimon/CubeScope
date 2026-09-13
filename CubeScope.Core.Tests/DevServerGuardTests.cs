using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

/// <summary>
/// The prod/dev discriminant was the CATALOG NAME ("contains dev") as long as dev lived on the
/// prod server under another name. Since dev got its own server, both catalogs carry the same
/// name: the SERVER decides, and only through an explicit list. These tests lock down the
/// rule on which the refusal to deploy to production depends.
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
        // Fail-closed: a missing configuration must get in the way, never allow. If the empty
        // list returned "true", a fresh machine would deploy to production without a single warning.
        Assert.False(DevServerGuard.IsDev([], "SRV-DEV"));
    }

    [Theory]
    [InlineData("srv-dev")]
    [InlineData("SRV-DEV")]
    [InlineData("  SRV-DEV  ")]
    public void La_casse_et_les_espaces_ne_changent_rien(string saisi)
    {
        // A Windows server name is case-insensitive, and a space picked up by copy-paste
        // must not turn a dev server into production.
        Assert.True(DevServerGuard.IsDev(["SRV-DEV"], saisi));
    }

    [Fact]
    public void La_liste_aussi_est_normalisee()
        => Assert.True(DevServerGuard.IsDev(["  srv-dev "], "SRV-DEV"));

    [Fact]
    public void Un_serveur_vide_n_est_jamais_de_dev()
    {
        // Otherwise a list containing an empty entry (botched input) would declare a
        // connection with no server as "dev".
        Assert.False(DevServerGuard.IsDev(["", "SRV-DEV"], ""));
        Assert.False(DevServerGuard.IsDev(["SRV-DEV"], "   "));
    }

    [Fact]
    public void Une_correspondance_partielle_ne_suffit_pas()
    {
        // "contains" was precisely the flaw of the old rule on the catalog name:
        // a server named SRV-DEV-PROD is not SRV-DEV.
        Assert.False(DevServerGuard.IsDev(["SRV-DEV"], "SRV-DEV-PROD"));
        Assert.False(DevServerGuard.IsDev(["SRV-DEV"], "SRV"));
    }
}
