using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

/// <summary>
/// The "dev catalog" guard lived ONLY in the UI: a direct API call bypassed it. As long as
/// prod and dev had different catalog names, the risk stayed theoretical; since dev got its
/// own server and the same catalog name as production, it no longer is. So the guard moves
/// down into the service.
///
/// These tests need no server: the refusal must happen BEFORE any AMO connection — that is
/// the whole point of a guard, not touching the target to find out we should not have.
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

        // The message must name the refused server: "déploiement refusé" without saying which
        // one would send people looking in the wrong place.
        Assert.Contains("SRV-PROD", ex.Message);
    }

    [Fact]
    public void Force_ne_contourne_PAS_la_garde()
    {
        // `force` means "overwrite a server script that has diverged", not "deploy to
        // production". If the Forcer button opened up prod, the guard would be worthless.
        Assert.Throws<InvalidOperationException>(() =>
            _svc.Deploy("SRV-PROD", "Ratios", "CubeDemo", "-- mdx", force: true,
                devServers: ["SRV-DEV"]));
    }

    [Fact]
    public void Refuse_quand_la_liste_est_vide()
    {
        // Fail-closed: on a machine where nothing has been declared, nothing deploys anywhere.
        Assert.Throws<InvalidOperationException>(() =>
            _svc.Deploy("SRV-DEV", "Ratios", "CubeDemo", "-- mdx", force: false,
                devServers: []));
    }

    [Fact]
    public void Un_serveur_declare_passe_la_garde()
    {
        // It passes the guard, then fails at the AMO connection — which is precisely what proves
        // the guard let it through. A guard's message and a failed connection's message must
        // stay distinct, otherwise there is no telling which of the two spoke.
        var ex = Record.Exception(() =>
            _svc.Deploy("serveur-inexistant-pour-ce-test", "Ratios", "CubeDemo", "-- mdx",
                force: false, devServers: ["serveur-inexistant-pour-ce-test"]));

        Assert.NotNull(ex);
        Assert.DoesNotContain("déploiement", ex!.Message.ToLowerInvariant());
    }
}
