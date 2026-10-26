using CubeScope.Core.Models;
using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

public class ProcessingStateEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    private static PartitionSnapshot P(string name, ObjectState state, DateTime? lp)
        => new(name, name, state, lp, "Molap", null, null, 100, 0);

    private static MeasureGroupSnapshot Mg(string name, params PartitionSnapshot[] parts)
        => new(name, name, ObjectState.Processed, Now, parts.Sum(p => p.EstimatedSize), parts);

    private static DatabaseSnapshot Db(IReadOnlyList<CubeSnapshot> cubes, params DimensionSnapshot[] dims)
        => new("SRV", "Db", ObjectState.Processed, cubes, dims);

    private static CubeSnapshot Cube(string name, params MeasureGroupSnapshot[] groups)
        => new(name, name, ObjectState.Processed, Now, groups);

    [Fact]
    public void Un_objet_non_traite_reste_rouge_meme_avec_une_date_recente()
    {
        // Observed: an unprocessed cube keeps a LastProcessed date. The state must win.
        var r = ProcessingStateEvaluator.Evaluate(
            Db([Cube("C", Mg("MG", P("p1", ObjectState.Unprocessed, Now.AddMinutes(-5))))]), 24, Now);

        var p = r.Cubes[0].MeasureGroups[0].Partitions[0];
        Assert.Equal(FreshnessStatus.Unprocessed, p.Status);
        Assert.Equal(FreshnessStatus.Unprocessed, r.Cubes[0].MeasureGroups[0].Status);
        Assert.Equal(FreshnessStatus.Unprocessed, r.Cubes[0].Status);
        Assert.Equal(1, r.Summary.UnprocessedPartitions);
    }

    [Theory]
    [InlineData(23.99, FreshnessStatus.Ok)]
    [InlineData(24.0, FreshnessStatus.Ok)] // "beyond the threshold" = strictly older
    [InlineData(24.01, FreshnessStatus.Stale)]
    public void Seuil_de_retard(double ageHours, FreshnessStatus expected)
    {
        var r = ProcessingStateEvaluator.Evaluate(
            Db([Cube("C", Mg("MG", P("p1", ObjectState.Processed, Now.AddHours(-ageHours))))]), 24, Now);
        Assert.Equal(expected, r.Cubes[0].MeasureGroups[0].Partitions[0].Status);
    }

    [Fact]
    public void Fraicheur_du_parent_calculee_sur_ses_partitions_traitees()
    {
        var oldest = Now.AddHours(-30);
        var newest = Now.AddHours(-2);
        var r = ProcessingStateEvaluator.Evaluate(Db([Cube("C",
            Mg("A", P("a1", ObjectState.Processed, oldest), P("a2", ObjectState.Processed, Now.AddHours(-10))),
            Mg("B", P("b1", ObjectState.Processed, newest),
                // An unprocessed partition's date means nothing: kept out of the range.
                P("b2", ObjectState.Unprocessed, Now.AddDays(-400))))]), 24, Now);

        var cube = r.Cubes[0];
        Assert.Equal(oldest, cube.OldestPartitionUtc);
        Assert.Equal(newest, cube.NewestPartitionUtc);
        Assert.Equal(oldest, cube.MeasureGroups[0].OldestPartitionUtc);
        Assert.Equal(newest, cube.MeasureGroups[1].OldestPartitionUtc);
        Assert.Equal(newest, r.Summary.NewestProcessedUtc);
    }

    [Fact]
    public void Les_compteurs_remontent_aux_parents()
    {
        var r = ProcessingStateEvaluator.Evaluate(Db([Cube("C",
            Mg("A", P("a1", ObjectState.Processed, Now.AddHours(-30)), P("a2", ObjectState.Processed, Now.AddHours(-31))),
            Mg("B", P("b1", ObjectState.Processed, Now.AddHours(-1))))]), 24, Now);

        var cube = r.Cubes[0];
        Assert.Equal(FreshnessStatus.Stale, cube.Status);
        Assert.Equal(2, cube.StaleCount);
        Assert.Equal(0, cube.UnprocessedCount);
        Assert.Equal(FreshnessStatus.Stale, cube.MeasureGroups[0].Status);
        Assert.Equal(FreshnessStatus.Ok, cube.MeasureGroups[1].Status);
        Assert.Equal(2, r.Summary.StaleObjects);
    }

    [Fact]
    public void Le_rouge_l_emporte_sur_l_orange_chez_le_parent()
    {
        var r = ProcessingStateEvaluator.Evaluate(Db([Cube("C",
            Mg("A", P("a1", ObjectState.Processed, Now.AddHours(-30)), P("a2", ObjectState.Unprocessed, null)))]), 24, Now);
        Assert.Equal(FreshnessStatus.Unprocessed, r.Cubes[0].Status);
        Assert.Equal(1, r.Cubes[0].StaleCount);
        Assert.Equal(1, r.Cubes[0].UnprocessedCount);
    }

    [Fact]
    public void Cube_non_traite_compte_meme_sans_partition()
    {
        var cube = new CubeSnapshot("C", "C", ObjectState.Unprocessed, Now, [
            new MeasureGroupSnapshot("MG", "MG", ObjectState.Unprocessed, Now, 0, [])]);
        var r = ProcessingStateEvaluator.Evaluate(Db([cube]), 24, Now);

        Assert.Equal(FreshnessStatus.Unprocessed, r.Cubes[0].Status);
        Assert.Equal(FreshnessStatus.Unprocessed, r.Cubes[0].MeasureGroups[0].Status);
        Assert.Null(r.Cubes[0].OldestPartitionUtc);
        Assert.Equal(1, r.Summary.UnprocessedCubes);
        Assert.Null(r.Summary.NewestProcessedUtc);
    }

    [Fact]
    public void Cube_partiellement_traite_rouge_mais_pas_compte_comme_non_traite()
    {
        var cube = new CubeSnapshot("C", "C", ObjectState.PartiallyProcessed, Now, [
            Mg("A", P("a1", ObjectState.Processed, Now)),
            new MeasureGroupSnapshot("B", "B", ObjectState.Unprocessed, null, 0, [P("b1", ObjectState.Unprocessed, null)])]);
        var r = ProcessingStateEvaluator.Evaluate(Db([cube]), 24, Now);

        Assert.Equal(FreshnessStatus.Unprocessed, r.Cubes[0].Status);
        Assert.Equal(0, r.Summary.UnprocessedCubes);
        Assert.Equal(1, r.Summary.UnprocessedPartitions);
    }

    [Fact]
    public void Groupe_de_mesures_traite_sans_partition_est_ok()
    {
        var r = ProcessingStateEvaluator.Evaluate(Db([Cube("C", Mg("MG"))]), 24, Now);
        Assert.Equal(FreshnessStatus.Ok, r.Cubes[0].MeasureGroups[0].Status);
        Assert.Equal(FreshnessStatus.Ok, r.Cubes[0].Status);
    }

    [Fact]
    public void Objet_traite_sans_date_est_en_retard()
    {
        // No date = unknown age: never vouched for as fresh.
        var r = ProcessingStateEvaluator.Evaluate(
            Db([Cube("C", Mg("MG", P("p1", ObjectState.Processed, null)))]), 24, Now);
        Assert.Equal(FreshnessStatus.Stale, r.Cubes[0].MeasureGroups[0].Partitions[0].Status);
    }

    [Fact]
    public void Dimensions_evaluees_et_comptees()
    {
        var r = ProcessingStateEvaluator.Evaluate(Db([],
            new DimensionSnapshot("D1", "D1", ObjectState.Processed, Now.AddHours(-1)),
            new DimensionSnapshot("D2", "D2", ObjectState.Processed, Now.AddHours(-48)),
            new DimensionSnapshot("D3", "D3", ObjectState.Unprocessed, Now)), 24, Now);

        Assert.Equal([FreshnessStatus.Ok, FreshnessStatus.Stale, FreshnessStatus.Unprocessed],
            r.Dimensions.Select(d => d.Status));
        Assert.Equal(1, r.Summary.UnprocessedDimensions);
        Assert.Equal(1, r.Summary.StaleObjects);
    }

    [Fact]
    public void Base_sans_cube()
    {
        var r = ProcessingStateEvaluator.Evaluate(Db([]), 24, Now);
        Assert.Empty(r.Cubes);
        Assert.Equal(new ProcessingSummary(0, 0, 0, 0, null), r.Summary);
        Assert.Equal(24, r.ThresholdHours);
        Assert.Equal(Now, r.ReadAtUtc);
    }

    [Fact]
    public void Etat_amo_inconnu_retombe_sur_non_traite()
    {
        Assert.Equal(ObjectState.Processed, ProcessingStateService.Map(Microsoft.AnalysisServices.AnalysisState.Processed));
        Assert.Equal(ObjectState.PartiallyProcessed,
            ProcessingStateService.Map(Microsoft.AnalysisServices.AnalysisState.PartiallyProcessed));
        Assert.Equal(ObjectState.Unprocessed, ProcessingStateService.Map((Microsoft.AnalysisServices.AnalysisState)99));
    }

    [Fact]
    public void Date_amo_convertie_en_utc_et_minvalue_ignoree()
    {
        Assert.Null(ProcessingStateService.Utc(DateTime.MinValue));
        var local = new DateTime(2026, 10, 23, 23, 6, 13, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), ProcessingStateService.Utc(local));
        Assert.Equal(DateTimeKind.Utc, ProcessingStateService.Utc(local)!.Value.Kind);
        var utc = new DateTime(2026, 10, 23, 21, 6, 13, DateTimeKind.Utc);
        Assert.Equal(utc, ProcessingStateService.Utc(utc));
    }
}
