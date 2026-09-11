using System.Data;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;

namespace CubeScope.Core.Tests;

public class MetadataServiceBuildTests
{
    private static DataTable Table(string[] cols, params object[][] rows)
    {
        var t = new DataTable();
        foreach (var c in cols) t.Columns.Add(c, c == "LEVEL_NUMBER" ? typeof(int) : typeof(string));
        foreach (var r in rows) t.Rows.Add(r);
        return t;
    }

    [Fact]
    public void Build_GroupsMeasuresByFolder_AndSorts()
    {
        var measures = Table(
            ["MEASURE_NAME", "MEASURE_UNIQUE_NAME", "MEASURE_DISPLAY_FOLDER", "DESCRIPTION"],
            ["Sales", "[Measures].[Sales]", "Perf", ""],
            ["Total", "[Measures].[Total]", "", ""],
            ["Alpha", "[Measures].[Alpha]", "Perf", ""]);
        var empty = Table(["DIMENSION_NAME", "DIMENSION_UNIQUE_NAME"]);
        var emptyH = Table(["DIMENSION_UNIQUE_NAME", "HIERARCHY_NAME", "HIERARCHY_UNIQUE_NAME"]);
        var emptyL = Table(["HIERARCHY_UNIQUE_NAME", "LEVEL_NAME", "LEVEL_UNIQUE_NAME", "LEVEL_NUMBER"]);

        var meta = MetadataService.Build("C", measures, empty, emptyH, emptyL);

        Assert.Equal(2, meta.MeasureFolders.Count);
        Assert.Equal("", meta.MeasureFolders[0].Folder); // racine d'abord (tri alpha)
        Assert.Equal("Perf", meta.MeasureFolders[1].Folder);
        Assert.Equal(["Alpha", "Sales"], meta.MeasureFolders[1].Measures.Select(m => m.Name));
    }

    [Fact]
    public void Build_MapsMeasureDescription_AndDefaultsDbNullToEmpty()
    {
        var measures = Table(
            ["MEASURE_NAME", "MEASURE_UNIQUE_NAME", "MEASURE_DISPLAY_FOLDER", "DESCRIPTION"],
            ["Sales", "[Measures].[Sales]", "", "Chiffre d'affaires total"],
            ["Total", "[Measures].[Total]", "", DBNull.Value]);
        var empty = Table(["DIMENSION_NAME", "DIMENSION_UNIQUE_NAME"]);
        var emptyH = Table(["DIMENSION_UNIQUE_NAME", "HIERARCHY_NAME", "HIERARCHY_UNIQUE_NAME"]);
        var emptyL = Table(["HIERARCHY_UNIQUE_NAME", "LEVEL_NAME", "LEVEL_UNIQUE_NAME", "LEVEL_NUMBER"]);

        var meta = MetadataService.Build("C", measures, empty, emptyH, emptyL);

        var byName = meta.MeasureFolders.Single().Measures.ToDictionary(m => m.Name);
        Assert.Equal("Chiffre d'affaires total", byName["Sales"].Description);
        Assert.Equal("", byName["Total"].Description);
    }

    [Fact]
    public void Build_AttachesHierarchiesAndLevels_ByUniqueName()
    {
        var emptyM = Table(["MEASURE_NAME", "MEASURE_UNIQUE_NAME", "MEASURE_DISPLAY_FOLDER"]);
        var dims = Table(["DIMENSION_NAME", "DIMENSION_UNIQUE_NAME", "DESCRIPTION"], ["Dates", "[Dates]", ""]);
        var hiers = Table(
            ["DIMENSION_UNIQUE_NAME", "HIERARCHY_NAME", "HIERARCHY_UNIQUE_NAME", "DESCRIPTION"],
            ["[Dates]", "Année", "[Dates].[Année]", ""]);
        var levels = Table(
            ["HIERARCHY_UNIQUE_NAME", "LEVEL_NAME", "LEVEL_UNIQUE_NAME", "LEVEL_NUMBER"],
            ["[Dates].[Année]", "Année", "[Dates].[Année].[Année]", 1],
            ["[Dates].[Année]", "(All)", "[Dates].[Année].[(All)]", 0]);

        var meta = MetadataService.Build("C", emptyM, dims, hiers, levels);

        var dim = Assert.Single(meta.Dimensions);
        var h = Assert.Single(dim.Hierarchies);
        Assert.Equal("[Dates].[Année]", h.UniqueName);
        Assert.Equal(2, h.Levels.Count);
        Assert.Equal(0, h.Levels[0].Number); // trié par LEVEL_NUMBER
    }

    [Fact]
    public void Build_DimensionWithoutHierarchies_YieldsEmptyList()
    {
        var emptyM = Table(["MEASURE_NAME", "MEASURE_UNIQUE_NAME", "MEASURE_DISPLAY_FOLDER"]);
        var dims = Table(["DIMENSION_NAME", "DIMENSION_UNIQUE_NAME", "DESCRIPTION"], ["Orpheline", "[Orpheline]", ""]);
        var emptyH = Table(["DIMENSION_UNIQUE_NAME", "HIERARCHY_NAME", "HIERARCHY_UNIQUE_NAME"]);
        var emptyL = Table(["HIERARCHY_UNIQUE_NAME", "LEVEL_NAME", "LEVEL_UNIQUE_NAME", "LEVEL_NUMBER"]);

        var meta = MetadataService.Build("C", emptyM, dims, emptyH, emptyL);

        Assert.Empty(Assert.Single(meta.Dimensions).Hierarchies);
    }
}

[Trait("Category", "Integration")]
public class MetadataServiceIntegrationTests : IDisposable
{
    private readonly SsasSession _session = new();
    private readonly StateStore _store =
        new(Path.Combine(Path.GetTempPath(), $"cubescope-meta-{Guid.NewGuid():N}.db"));

    /// <summary>
    /// Le drill-down de l'explorateur s'appuie sur CHILDREN_CARDINALITY pour deux décisions :
    /// distinguer une feuille d'un nœud dépliable, et annoncer combien de membres le plafond
    /// masque. Rien ne garantit a priori qu'un serveur le renvoie — ce test le CONSTATE, plutôt
    /// que de laisser le code s'appuyer sur une supposition.
    /// </summary>
    [Fact]
    public async Task GetChildren_DescendDeLaHierarchieAuxFeuilles()
    {
        // Lecture seule (aucun cache vidé) : le catalogue de référence convient, comme pour
        // le test de métadonnées voisin.
        await _session.ConnectAsync(TestTarget.Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new MetadataService(_session, _store);

        var meta = await svc.GetCubeMetaAsync(TestTarget.Cube);
        var hier = meta.Dimensions.SelectMany(d => d.Hierarchies).First(h => h.Levels.Count > 1);

        // Cran 1 : sous « Membres », le sommet de la hiérarchie — le (All) habituel.
        var racine = await svc.GetChildrenAsync(TestTarget.Cube, hier.UniqueName, isHierarchy: true);
        Assert.NotEmpty(racine.Nodes);
        Assert.All(racine.Nodes, n => Assert.NotEmpty(n.UniqueName));

        // La cardinalité doit être renseignée, sinon tout l'arbre perd sa capacité à
        // distinguer une feuille et à chiffrer ce qu'il masque.
        Assert.All(racine.Nodes, n => Assert.NotEqual(-1, n.ChildrenCount));

        // Cran 2 : les enfants du premier membre. Sur une hiérarchie à plusieurs niveaux,
        // le sommet a forcément des enfants.
        var sommet = racine.Nodes[0];
        Assert.True(sommet.ChildrenCount > 0, $"attendu : {sommet.UniqueName} a des enfants");

        var enfants = await svc.GetChildrenAsync(TestTarget.Cube, sommet.UniqueName, isHierarchy: false);
        Assert.NotEmpty(enfants.Nodes);
        Assert.All(enfants.Nodes, n => Assert.NotEqual(-1, n.ChildrenCount));

        // Le plafond ne doit jamais couper en silence : ce qu'on rend tient dans la limite,
        // et HasMore dit s'il en reste.
        var plafonne = await svc.GetChildrenAsync(
            TestTarget.Cube, sommet.UniqueName, isHierarchy: false, limit: 2);
        Assert.True(plafonne.Nodes.Count <= 2);
        Assert.Equal(sommet.ChildrenCount > 2, plafonne.HasMore);
    }

    [Fact]
    public async Task GetCubeMeta_OnConfiguredCube_ReturnsRichTree()
    {
        await _session.ConnectAsync(TestTarget.Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new MetadataService(_session, _store);

        var cubes = await svc.GetCubesAsync();
        Assert.Contains(TestTarget.Cube, cubes);

        var meta = await svc.GetCubeMetaAsync(TestTarget.Cube);
        Assert.True(meta.MeasureFolders.Sum(f => f.Measures.Count) > 100, "attendu : centaines de mesures");
        Assert.True(meta.Dimensions.Count > 10, "attendu : dizaines de dimensions");
        Assert.All(meta.Dimensions, d => Assert.NotEmpty(d.UniqueName));
        // Au moins une hiérarchie avec des niveaux
        Assert.Contains(meta.Dimensions.SelectMany(d => d.Hierarchies), h => h.Levels.Count > 0);
    }

    [Fact]
    public async Task GetMembers_OnKnownHierarchy_ReturnsCappedList()
    {
        await _session.ConnectAsync(TestTarget.Server);
        await _session.SetCatalogAsync(TestTarget.Catalog);
        var svc = new MetadataService(_session, _store);

        // Préfixe = dimension de la hiérarchie configurée (1er segment de son unique name)
        string dimPrefix = TestTarget.Hierarchy[..TestTarget.Hierarchy.IndexOf('.')];
        var members = await svc.GetMembersAsync(TestTarget.Cube, TestTarget.Hierarchy);

        Assert.NotEmpty(members);
        Assert.True(members.Count <= 1000);
        Assert.All(members, m => Assert.StartsWith(dimPrefix, m.UniqueName));
        // Deuxième appel : servi par le cache (même référence)
        var again = await svc.GetMembersAsync(TestTarget.Cube, TestTarget.Hierarchy);
        Assert.Same(members, again);
    }

    public void Dispose()
    {
        _session.Dispose();
        _store.Dispose();
    }
}
