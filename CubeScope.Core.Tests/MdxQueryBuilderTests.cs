using CubeScope.Core.Models;
using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

public class MdxQueryBuilderTests
{
    private const string Ventes = "[Measures].[Ventes]";
    private const string Marge = "[Measures].[Marge]";
    private const string Annee = "[Dates].[Année]";
    private const string Fonds = "[Fonds].[Fonds]";

    private static BuilderModel Model(
        IReadOnlyList<string>? measures = null,
        IReadOnlyList<BuilderAxisItem>? columns = null,
        IReadOnlyList<BuilderAxisItem>? rows = null,
        IReadOnlyList<BuilderFilter>? filters = null,
        string cube = "Cube",
        bool nonEmptyColumns = true,
        bool nonEmptyRows = true)
        => new(cube, measures ?? [], columns ?? [], rows ?? [], filters ?? [], nonEmptyColumns, nonEmptyRows);

    private static BuilderAxisItem Level(string hierarchy, BuilderRank? rank = null)
        => new(hierarchy, $"{hierarchy}.[Niveau]", null, rank);

    private static BuilderAxisItem Members(string hierarchy, params string[] members)
        => new(hierarchy, null, members, null);

    [Fact]
    public void Zero_axe_cube_seul()
    {
        Assert.Equal("SELECT\nFROM [Cube]", MdxQueryBuilder.Build(Model()));
    }

    [Fact]
    public void Un_axe_mesures_seules()
    {
        Assert.Equal(
            "SELECT\n  NON EMPTY {[Measures].[Ventes], [Measures].[Marge]} ON COLUMNS\nFROM [Cube]",
            MdxQueryBuilder.Build(Model(measures: [Ventes, Marge])));
    }

    [Fact]
    public void Deux_axes_mesures_et_lignes()
    {
        Assert.Equal(
            "SELECT\n  NON EMPTY {[Measures].[Ventes]} ON COLUMNS,\n"
            + "  NON EMPTY [Fonds].[Fonds].[Niveau].Members ON ROWS\nFROM [Cube]",
            MdxQueryBuilder.Build(Model(measures: [Ventes], rows: [Level(Fonds)])));
    }

    [Fact]
    public void Lignes_seules_mesure_par_defaut_en_colonnes()
    {
        // ROWS cannot exist without COLUMNS: the default measure fills axis 0.
        Assert.Equal(
            "SELECT\n  NON EMPTY {[Measures].DefaultMember} ON COLUMNS,\n"
            + "  NON EMPTY [Fonds].[Fonds].[Niveau].Members ON ROWS\nFROM [Cube]",
            MdxQueryBuilder.Build(Model(rows: [Level(Fonds)])));
    }

    [Fact]
    public void Crossjoin_sur_les_lignes_et_mesures_au_plus_profond_des_colonnes()
    {
        var mdx = MdxQueryBuilder.Build(Model(
            measures: [Ventes],
            columns: [Level(Annee)],
            rows: [Level(Fonds), Members("[Devise].[Devise]", "[Devise].[Devise].&[EUR]")]));
        Assert.Equal(
            "SELECT\n  NON EMPTY [Dates].[Année].[Niveau].Members * {[Measures].[Ventes]} ON COLUMNS,\n"
            + "  NON EMPTY [Fonds].[Fonds].[Niveau].Members * {[Devise].[Devise].&[EUR]} ON ROWS\nFROM [Cube]",
            mdx);
    }

    [Fact]
    public void Non_empty_desactivable_par_axe()
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], rows: [Level(Fonds)],
            nonEmptyColumns: false, nonEmptyRows: false));
        Assert.DoesNotContain("NON EMPTY", mdx);
    }

    [Fact]
    public void Where_un_membre_par_hierarchie_en_tuple()
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], filters: [
            new BuilderFilter(Annee, ["[Dates].[Année].&[2025]"]),
            new BuilderFilter("[Devise].[Devise]", ["[Devise].[Devise].&[EUR]"])]));
        Assert.EndsWith("FROM [Cube]\nWHERE ([Dates].[Année].&[2025], [Devise].[Devise].&[EUR])", mdx);
    }

    [Fact]
    public void Plusieurs_membres_d_une_hierarchie_en_sous_select()
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], filters: [
            new BuilderFilter(Annee, ["[Dates].[Année].&[2024]", "[Dates].[Année].&[2025]"])]));
        Assert.EndsWith(
            "FROM (SELECT {[Dates].[Année].&[2024], [Dates].[Année].&[2025]} ON COLUMNS FROM [Cube])", mdx);
        Assert.DoesNotContain("WHERE", mdx);
    }

    [Fact]
    public void Sous_selects_imbriques_et_where_ensemble()
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], filters: [
            new BuilderFilter(Annee, ["[Dates].[Année].&[2024]", "[Dates].[Année].&[2025]"]),
            new BuilderFilter(Fonds, ["[Fonds].[Fonds].&[A]", "[Fonds].[Fonds].&[B]"]),
            new BuilderFilter("[Devise].[Devise]", ["[Devise].[Devise].&[EUR]"])]));
        Assert.EndsWith(
            "FROM (SELECT {[Fonds].[Fonds].&[A], [Fonds].[Fonds].&[B]} ON COLUMNS FROM "
            + "(SELECT {[Dates].[Année].&[2024], [Dates].[Année].&[2025]} ON COLUMNS FROM [Cube]))\n"
            + "WHERE ([Devise].[Devise].&[EUR])", mdx);
    }

    [Fact]
    public void Filtre_sur_une_hierarchie_deja_sur_un_axe_passe_en_sous_select()
    {
        // MDX refuses a hierarchy both on an axis and in WHERE; a sub-select accepts it.
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], rows: [Level(Fonds)],
            filters: [new BuilderFilter(Fonds, ["[Fonds].[Fonds].&[A]"])]));
        Assert.Contains("FROM (SELECT {[Fonds].[Fonds].&[A]} ON COLUMNS FROM [Cube])", mdx);
        Assert.DoesNotContain("WHERE", mdx);
    }

    [Fact]
    public void Deux_filtres_sur_la_meme_hierarchie_fusionnes()
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], filters: [
            new BuilderFilter(Annee, ["[Dates].[Année].&[2024]"]),
            new BuilderFilter(Annee, ["[Dates].[Année].&[2025]", "[Dates].[Année].&[2024]"])]));
        Assert.Contains("(SELECT {[Dates].[Année].&[2024], [Dates].[Année].&[2025]} ON COLUMNS FROM [Cube])", mdx);
    }

    [Fact]
    public void Top_n_sur_une_mesure()
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes],
            rows: [Level(Fonds, new BuilderRank(Marge, 10))]));
        Assert.Contains("NON EMPTY TOPCOUNT([Fonds].[Fonds].[Niveau].Members, 10, [Measures].[Marge]) ON ROWS", mdx);
    }

    [Theory]
    [InlineData(true, "BDESC")]
    [InlineData(false, "BASC")]
    public void Tri_sur_une_mesure(bool descending, string keyword)
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes],
            rows: [Level(Fonds, new BuilderRank(Ventes, null, descending))]));
        Assert.Contains($"ORDER([Fonds].[Fonds].[Niveau].Members, [Measures].[Ventes], {keyword}) ON ROWS", mdx);
    }

    [Fact]
    public void Top_n_sur_des_membres_choisis()
    {
        var item = new BuilderAxisItem(Fonds, $"{Fonds}.[Niveau]", ["[Fonds].[Fonds].&[A]", "[Fonds].[Fonds].&[B]"],
            new BuilderRank(Ventes, 1));
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], rows: [item]));
        // Chosen members take precedence over the level.
        Assert.Contains("TOPCOUNT({[Fonds].[Fonds].&[A], [Fonds].[Fonds].&[B]}, 1, [Measures].[Ventes])", mdx);
    }

    [Fact]
    public void Noms_avec_crochet_et_apostrophe_passent_tels_quels()
    {
        // Already escaped by the server: ] doubled inside the brackets, ' left alone (no string literal).
        var mdx = MdxQueryBuilder.Build(Model(
            measures: ["[Measures].[Taux]] (%)]"],
            rows: [Members("[Titre].[Nom]", "[Titre].[Nom].&[L'Oréal]", "[Titre].[Nom].&[A]]B]")],
            cube: "Cube ]x['"));
        Assert.Contains("{[Measures].[Taux]] (%)]}", mdx);
        Assert.Contains("{[Titre].[Nom].&[L'Oréal], [Titre].[Nom].&[A]]B]}", mdx);
        Assert.EndsWith("FROM [Cube ]]x[']", mdx);
    }

    [Theory]
    [InlineData("[Dim].[Hier].&[1]&[2]")]
    [InlineData("[Dates].[Année - Trimestre].[Année].&[2025-01-01T00:00:00]")]
    [InlineData("[Measures].[A]]B]")]
    [InlineData("[Dim].[Hier].&[]")]
    public void Noms_uniques_valides(string name) => Assert.True(MdxQueryBuilder.IsUniqueName(name));

    [Theory]
    [InlineData("")]
    [InlineData("Ventes")]
    [InlineData("[A].[B].Members")]
    [InlineData("[A]} ON 0 FROM [X]) --")]
    [InlineData("[A],[B]")]
    [InlineData("[A]]")]
    [InlineData("[A].&[1].Children")]
    public void Noms_uniques_refuses(string name) => Assert.False(MdxQueryBuilder.IsUniqueName(name));

    [Fact]
    public void Nom_injecte_refuse_avant_generation()
    {
        var ex = Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(
            measures: [Ventes], rows: [Members(Fonds, "[Fonds].[Fonds].&[A]} ON 0 FROM [X] --")])));
        Assert.Contains("Nom unique invalide", ex.Message);
    }

    [Fact]
    public void Meme_hierarchie_sur_deux_axes_refusee()
    {
        var ex = Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(
            measures: [Ventes], columns: [Level(Fonds)], rows: [Level(Fonds)])));
        Assert.Contains("COLUMNS", ex.Message);
    }

    [Fact]
    public void Mesures_sur_un_axe_refusees()
    {
        Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(
            rows: [Members("[Measures]", Ventes)])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Top_n_nul_ou_negatif_refuse(int n)
    {
        Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(
            measures: [Ventes], rows: [Level(Fonds, new BuilderRank(Ventes, n))])));
    }

    [Fact]
    public void Filtre_sans_membre_refuse()
    {
        Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(
            measures: [Ventes], filters: [new BuilderFilter(Fonds, [])])));
    }

    [Fact]
    public void Element_sans_niveau_ni_membre_refuse()
    {
        Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(
            rows: [new BuilderAxisItem(Fonds, null, [], null)])));
    }

    [Fact]
    public void Cube_absent_refuse()
    {
        Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(cube: " ")));
    }

    [Fact]
    public void Filtre_sur_les_mesures_refuse()
    {
        Assert.Throws<MdxBuilderException>(() => MdxQueryBuilder.Build(Model(
            measures: [Ventes], filters: [new BuilderFilter("[Measures]", [Marge])])));
    }

    [Fact]
    public void Cles_differant_par_la_casse_restent_deux_membres()
    {
        var mdx = MdxQueryBuilder.Build(Model(measures: [Ventes], filters: [
            new BuilderFilter("[Titre].[Code]", ["[Titre].[Code].&[ab]", "[Titre].[Code].&[AB]"])]));
        Assert.Contains("{[Titre].[Code].&[ab], [Titre].[Code].&[AB]}", mdx);
    }

    [Fact]
    public void Mesure_en_double_dedupliquee()
    {
        Assert.Contains("{[Measures].[Ventes]} ON COLUMNS",
            MdxQueryBuilder.Build(Model(measures: [Ventes, Ventes])));
    }
}
