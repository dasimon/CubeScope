using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

/// <summary>
/// The explorer's drill-down goes through MDX, not MDSCHEMA_MEMBERS: filtering that DMV by
/// member scans the whole dimension and freezes on a securities dimension (documented pitfall).
/// `StrToMember(...).Children` resolves by key, with no scan. These tests lock down the shape of
/// the query — actual execution against a cube is covered separately, in integration.
/// </summary>
public class MemberChildrenQueryTests
{
    [Fact]
    public void Une_hierarchie_descend_sur_son_premier_niveau()
    {
        string mdx = MemberChildrenQuery.Build(
            "CubeDemo", "[Devise].[Devise]", isHierarchy: true, limit: 500);

        // The first step under "Membres" must return the top of the hierarchy (the usual
        // (All)), not its children — otherwise we skip a level compared to SSMS.
        Assert.Contains("[Devise].[Devise].Levels(0).Members", mdx);
        Assert.DoesNotContain(".Children", mdx);
    }

    [Fact]
    public void Un_membre_descend_sur_ses_enfants()
    {
        string mdx = MemberChildrenQuery.Build(
            "CubeDemo", "[Devise].[Devise].[All]", isHierarchy: false, limit: 500);

        Assert.Contains("StrToMember('[Devise].[Devise].[All]').Children", mdx);
        Assert.DoesNotContain("Levels(0)", mdx);
    }

    [Fact]
    public void Le_plafond_est_pose_par_HEAD()
    {
        string mdx = MemberChildrenQuery.Build(
            "CubeDemo", "[Devise].[Devise].[All]", isHierarchy: false, limit: 42);

        Assert.Contains("HEAD(", mdx);
        Assert.Contains(", 42)", mdx);
    }

    [Fact]
    public void La_cardinalite_des_enfants_est_demandee()
    {
        string mdx = MemberChildrenQuery.Build(
            "CubeDemo", "[Devise].[Devise].[All]", isHierarchy: false, limit: 500);

        // It serves twice: telling a leaf from an expandable node, and announcing the
        // exact number of members hidden by the cap. Without it, one more query per node
        // would be needed.
        Assert.Contains("CHILDREN_CARDINALITY", mdx);
    }

    [Fact]
    public void Une_apostrophe_dans_le_nom_est_doublee()
    {
        // A member named "L'Oréal" would break the string passed to StrToMember and, worse,
        // would allow MDX injection through a member name coming from the cube.
        string mdx = MemberChildrenQuery.Build(
            "CubeDemo", "[Produit].[Produit].&[L'Oréal]", isHierarchy: false, limit: 500);

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
        // No cell is read: only the axis positions matter. A second axis would only make
        // the server compute values for nothing.
        string mdx = MemberChildrenQuery.Build(
            "CubeDemo", "[Devise].[Devise]", isHierarchy: true, limit: 500);

        Assert.Contains("ON 0", mdx);
        Assert.DoesNotContain("ON 1", mdx);
    }

    // Autocompletion list: capped by the server, not after reading the whole dimension.
    [Fact]
    public void Members_AreCappedServerSideByHead()
    {
        string mdx = MemberChildrenQuery.BuildMembers("CubeDemo", "[Devise].[Devise]", limit: 1000);

        Assert.Contains("HEAD(StrToSet('[Devise].[Devise].Members'), 1000)", mdx);
        Assert.DoesNotContain("MDSCHEMA", mdx);
    }

    [Fact]
    public void Members_ComputeNoCell()
    {
        // Empty axis 0: the members come back on axis 1 and the default measure is never evaluated.
        string mdx = MemberChildrenQuery.BuildMembers("CubeDemo", "[Devise].[Devise]", limit: 1000);

        Assert.StartsWith("SELECT {} ON 0, ", mdx);
        Assert.Contains("DIMENSION PROPERTIES MEMBER_CAPTION ON 1", mdx);
    }

    [Fact]
    public void Members_EscapeQuoteInHierarchyAndBracketInCube()
    {
        string mdx = MemberChildrenQuery.BuildMembers("Cube]odd", "[Produit].[L'Oréal]", limit: 10);

        Assert.Contains("StrToSet('[Produit].[L''Oréal].Members')", mdx);
        Assert.EndsWith("FROM [Cube]]odd]", mdx);
    }
}
