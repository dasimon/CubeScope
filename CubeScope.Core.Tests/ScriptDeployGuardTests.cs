using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

/// <summary>
/// La garde « catalogue de dev » vivait UNIQUEMENT dans l'interface : un appel direct à l'API
/// la contournait. Tant que prod et dev portaient des noms de catalogue différents, le risque
/// restait théorique ; depuis que le dev a son propre serveur et le même nom de catalogue que
/// la production, il ne l'est plus. La garde descend donc dans le service.
///
/// Ces tests n'ont besoin d'aucun serveur : le refus doit tomber AVANT toute connexion AMO —
/// c'est tout l'intérêt d'une garde, ne pas toucher la cible pour découvrir qu'on n'aurait pas dû.
/// </summary>
public class ScriptDeployGuardTests
{
    private readonly ScriptDeployService _svc = new();

    [Fact]
    public void Refuse_un_serveur_absent_de_la_liste()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _svc.Deploy("SRV-PROD", "Ratios", "CubeDemo", "-- mdx", force: false,
                devServers: ["SRV-DEV"]));

        // Le message doit nommer le serveur refusé : « déploiement refusé » sans dire lequel
        // enverrait chercher au mauvais endroit.
        Assert.Contains("SRV-PROD", ex.Message);
    }

    [Fact]
    public void Force_ne_contourne_PAS_la_garde()
    {
        // `force` veut dire « écrase un script serveur qui a divergé », pas « déploie en
        // production ». Si le bouton Forcer ouvrait la prod, la garde ne vaudrait rien.
        Assert.Throws<InvalidOperationException>(() =>
            _svc.Deploy("SRV-PROD", "Ratios", "CubeDemo", "-- mdx", force: true,
                devServers: ["SRV-DEV"]));
    }

    [Fact]
    public void Refuse_quand_la_liste_est_vide()
    {
        // Fail-closed : sur un poste où rien n'a été déclaré, on ne déploie nulle part.
        Assert.Throws<InvalidOperationException>(() =>
            _svc.Deploy("SRV-DEV", "Ratios", "CubeDemo", "-- mdx", force: false,
                devServers: []));
    }

    [Fact]
    public void Un_serveur_declare_passe_la_garde()
    {
        // Il passe la garde, puis échoue à la connexion AMO — ce qui prouve justement que la
        // garde l'a laissé passer. Le message d'une garde et celui d'une connexion ratée
        // doivent rester distincts, sinon on ne sait plus lequel des deux a parlé.
        var ex = Record.Exception(() =>
            _svc.Deploy("serveur-inexistant-pour-ce-test", "Ratios", "CubeDemo", "-- mdx",
                force: false, devServers: ["serveur-inexistant-pour-ce-test"]));

        Assert.NotNull(ex);
        Assert.DoesNotContain("déploiement", ex!.Message.ToLowerInvariant());
    }
}
