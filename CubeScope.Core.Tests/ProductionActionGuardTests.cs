using CubeScope.Core.Models;
using CubeScope.Core.Project;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;

namespace CubeScope.Core.Tests;

public class ProductionActionGuardTests
{
    private const string Dev = "SRV-DEV";
    private const string Prod = "SRV-PROD";
    private static readonly string[] DevServers = [Dev];

    public static IEnumerable<object?[]> AllCombinations()
    {
        foreach (var kind in Enum.GetValues<ProcessObjectKind>())
            foreach (var mode in Enum.GetValues<ProcessMode>())
                foreach (var server in new[] { Dev, Prod })
                    foreach (var confirm in new string?[] { null, "", "AUTRE", Prod, $"  {Prod.ToLowerInvariant()}  " })
                        yield return [kind, mode, server, confirm];
    }

    /// <summary>
    /// The whole matrix (kind × mode × dev/prod × confirmation absent, empty, wrong, right, right
    /// with case and spaces), each verdict stated from the rule, not from the code.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Matrice_complete(ProcessObjectKind kind, ProcessMode mode, string server, string? confirm)
    {
        string? refusal = ProductionActionGuard.Check(server, DevServers, confirm, kind, mode);

        bool modeValid = mode != ProcessMode.Update || kind == ProcessObjectKind.Dimension;
        bool destructiveDimension = kind == ProcessObjectKind.Dimension
            && mode is ProcessMode.Full or ProcessMode.Data;
        bool production = server == Prod;
        bool confirmed = confirm is not null && confirm.Trim().Equals(server, StringComparison.OrdinalIgnoreCase);

        bool allowed = modeValid && (!production || (!destructiveDimension && confirmed));
        Assert.Equal(allowed, refusal is null);
    }

    [Theory]
    [InlineData(ProcessMode.Full)]
    [InlineData(ProcessMode.Data)]
    public void Dimension_destructrice_refusee_en_prod_meme_confirmee(ProcessMode mode)
    {
        var refusal = ProductionActionGuard.Check(Prod, DevServers, Prod, ProcessObjectKind.Dimension, mode);
        Assert.NotNull(refusal);
        Assert.Contains("production", refusal);
    }

    [Theory]
    [InlineData(ProcessMode.Full)]
    [InlineData(ProcessMode.Data)]
    public void Dimension_destructrice_autorisee_en_dev_sans_confirmation(ProcessMode mode)
    {
        Assert.Null(ProductionActionGuard.Check(Dev, DevServers, null, ProcessObjectKind.Dimension, mode));
    }

    [Fact]
    public void Liste_dev_vide_tout_serveur_est_prod()
    {
        // Fail-closed: without a declared dev server, the dev server itself counts as production.
        Assert.NotNull(ProductionActionGuard.Check(Dev, [], null, ProcessObjectKind.Partition, ProcessMode.Full));
        Assert.Null(ProductionActionGuard.Check(Dev, [], "srv-dev", ProcessObjectKind.Partition, ProcessMode.Full));
    }

    [Fact]
    public void Confirmation_d_un_autre_serveur_refusee()
    {
        Assert.NotNull(ProductionActionGuard.Check(Prod, DevServers, Dev, ProcessObjectKind.Cube, ProcessMode.Default));
    }

    [Fact]
    public void Update_reserve_aux_dimensions()
    {
        Assert.NotNull(ProductionActionGuard.HardRefusal(false, ProcessObjectKind.Partition, ProcessMode.Update));
        Assert.Null(ProductionActionGuard.HardRefusal(true, ProcessObjectKind.Dimension, ProcessMode.Update));
    }

    [Fact]
    public async Task Refus_avant_toute_connexion_et_journalise()
    {
        // An unreachable server: if the guard came after Connect, this would be a connection error.
        using var store = new StateStore(Path.Combine(Path.GetTempPath(), $"cs-guard-{Guid.NewGuid():N}.db"));
        var svc = new ProcessService(new SsasSession(), store);
        var request = new ProcessRequest(new ProcessTarget(ProcessObjectKind.Partition, "C", "MG", "P"),
            ProcessMode.Full, ConfirmServer: "pas-le-bon");

        await Assert.ThrowsAsync<ProductionActionRefusedException>(() =>
            svc.RunAsync("no-such-host.cubescope.invalid", "Db", request, []));

        var log = Assert.Single(store.GetProcessLog());
        Assert.False(log.Success);
        Assert.True(log.IsProduction);
        Assert.Equal("Full", log.Mode);
        Assert.Contains("retapez", log.Error);
        Assert.Equal("C › MG › P", log.ObjectLabel); // the refused target is identified
    }

    [Theory]
    [InlineData(ProcessMode.Default, Microsoft.AnalysisServices.ProcessType.ProcessDefault)]
    [InlineData(ProcessMode.Full, Microsoft.AnalysisServices.ProcessType.ProcessFull)]
    [InlineData(ProcessMode.Data, Microsoft.AnalysisServices.ProcessType.ProcessData)]
    [InlineData(ProcessMode.Update, Microsoft.AnalysisServices.ProcessType.ProcessUpdate)]
    public void Correspondance_des_modes_amo(ProcessMode mode, Microsoft.AnalysisServices.ProcessType expected)
        => Assert.Equal(expected, ProcessService.ToProcessType(mode));
}
