using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

/// <summary>
/// Le drill-down de l'explorateur passe par MDX et non par MDSCHEMA_MEMBERS : filtrer cette
/// DMV par membre scanne toute la dimension et gèle sur une dimension titres (piège documenté).
/// `StrToMember(...).Children` résout par clé, sans scan. Ces tests verrouillent la forme de
/// la requête — l'exécution réelle contre un cube est couverte à part, en intégration.
/// </summary>
public class MemberChildrenQueryTests
{
    [Fact]
    public void Une_hierarchie_descend_sur_son_premier_niveau()
    {
        string mdx = MemberChildrenQuery.Build(
            "Portefeuilles", "[Devise].[Devise]", isHierarchy: true, limit: 500);

        // Le premier cran sous « Membres » doit rendre le sommet de la hiérarchie (le (All)
        // habituel), pas ses enfants — sinon on saute un niveau par rapport à SSMS.
        Assert.Contains("[Devise].[Devise].Levels(0).Members", mdx);
        Assert.DoesNotContain(".Children", mdx);
    }

    [Fact]
    public void Un_membre_descend_sur_ses_enfants()
    {
        string mdx = MemberChildrenQuery.Build(
            "Portefeuilles", "[Devise].[Devise].[All]", isHierarchy: false, limit: 500);

        Assert.Contains("StrToMember('[Devise].[Devise].[All]').Children", mdx);
        Assert.DoesNotContain("Levels(0)", mdx);
    }

    [Fact]
    public void Le_plafond_est_pose_par_HEAD()
    {
        string mdx = MemberChildrenQuery.Build(
            "Portefeuilles", "[Devise].[Devise].[All]", isHierarchy: false, limit: 42);

        Assert.Contains("HEAD(", mdx);
        Assert.Contains(", 42)", mdx);
    }

    [Fact]
    public void La_cardinalite_des_enfants_est_demandee()
    {
        string mdx = MemberChildrenQuery.Build(
            "Portefeuilles", "[Devise].[Devise].[All]", isHierarchy: false, limit: 500);

        // Elle sert deux fois : distinguer une feuille d'un nœud dépliable, et annoncer
        // le nombre exact de membres masqués par le plafond. Sans elle, il faudrait une
        // requête de plus par nœud.
        Assert.Contains("CHILDREN_CARDINALITY", mdx);
    }

    [Fact]
    public void Une_apostrophe_dans_le_nom_est_doublee()
    {
        // Un membre nommé « L'Oréal » casserait la chaîne passée à StrToMember, et pire,
        // permettrait d'injecter du MDX par un nom de membre venu du cube.
        string mdx = MemberChildrenQuery.Build(
            "Portefeuilles", "[Titre].[Titre].&[L'Oréal]", isHierarchy: false, limit: 500);

        Assert.Contains("&[L''Oréal]", mdx);
    }

    [Fact]
    public void Un_crochet_fermant_dans_le_nom_du_cube_est_double()
    {
        string mdx = MemberChildrenQuery.Build(
            "Cube]bizarre", "[Devise].[Devise]", isHierarchy: true, limit: 500);

        Assert.Contains("FROM [Cube]]bizarre]", mdx);
    }

    [Fact]
    public void Un_seul_axe_est_demande()
    {
        // Aucune cellule n'est lue : seules les positions de l'axe comptent. Un second axe
        // ne servirait qu'à faire calculer des valeurs pour rien.
        string mdx = MemberChildrenQuery.Build(
            "Portefeuilles", "[Devise].[Devise]", isHierarchy: true, limit: 500);

        Assert.Contains("ON 0", mdx);
        Assert.DoesNotContain("ON 1", mdx);
    }
}
