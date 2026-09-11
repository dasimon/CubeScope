using CubeScope.Shell;

namespace CubeScope.Core.Tests;

/// <summary>
/// `GeometrieDecider.GeometrieUtilisable` décide si une géométrie de fenêtre enregistrée reste
/// affichable, sur l'écran virtuel (tous moniteurs) et non le seul écran principal — le bug
/// corrigé ici rejetait à tort toute position sur un écran secondaire.
/// </summary>
public class GeometrieDeciderTests
{
    [Fact]
    public void Fenetre_entierement_sur_l_ecran_principal_est_utilisable()
    {
        bool utilisable = GeometrieDecider.GeometrieUtilisable(
            x: 100, y: 100, largeur: 1280, hauteur: 800,
            ecranX: 0, ecranY: 0, ecranLargeur: 1920, ecranHauteur: 1080);

        Assert.True(utilisable);
    }

    /// <summary>
    /// Écran secondaire à GAUCHE du principal (virtuel commençant à -1920). C'est le cas que
    /// l'ancien code (borné à `SystemParameters.WorkArea`, limité au principal) rejetait à tort :
    /// avec les mêmes valeurs, `x=-1800 < bornes.Left(0) - largeur(1280) + 100 = -1180` était vrai
    /// → fenêtre jugée hors champ alors que l'écran est bien branché.
    /// </summary>
    [Fact]
    public void Fenetre_sur_ecran_secondaire_a_gauche_est_utilisable()
    {
        bool utilisable = GeometrieDecider.GeometrieUtilisable(
            x: -1800, y: 100, largeur: 1280, hauteur: 800,
            ecranX: -1920, ecranY: 0, ecranLargeur: 3840, ecranHauteur: 1080);

        Assert.True(utilisable);
    }

    /// <summary>
    /// Écran secondaire AU-DESSUS du principal (`VirtualScreenTop` négatif). L'ancien test en Y
    /// (`etat.Y < bornes.Top`, sans tolérance) rejetait tout Y négatif sans exception.
    /// </summary>
    [Fact]
    public void Fenetre_sur_ecran_secondaire_au_dessus_est_utilisable()
    {
        bool utilisable = GeometrieDecider.GeometrieUtilisable(
            x: 100, y: -1000, largeur: 1280, hauteur: 800,
            ecranX: 0, ecranY: -1080, ecranLargeur: 1920, ecranHauteur: 2160);

        Assert.True(utilisable);
    }

    /// <summary>
    /// Même géométrie que le cas « écran secondaire à gauche », mais l'écran virtuel s'est
    /// réduit au seul principal (0..1920) : l'écran a été débranché depuis la dernière session.
    /// Doit être rejeté, contrairement au cas précédent — c'est la distinction que le garde-fou
    /// doit préserver.
    /// </summary>
    [Fact]
    public void Meme_geometrie_mais_ecran_debranche_est_non_utilisable()
    {
        bool utilisable = GeometrieDecider.GeometrieUtilisable(
            x: -1800, y: 100, largeur: 1280, hauteur: 800,
            ecranX: 0, ecranY: 0, ecranLargeur: 1920, ecranHauteur: 1080);

        Assert.False(utilisable);
    }

    [Fact]
    public void Debord_a_droite_utilisable_au_dessus_du_seuil_puis_non_en_dessous()
    {
        // 220 px de la fenêtre restent visibles à l'écran (>= seuil de 100 px).
        bool encoreUtilisable = GeometrieDecider.GeometrieUtilisable(
            x: 1700, y: 100, largeur: 300, hauteur: 800,
            ecranX: 0, ecranY: 0, ecranLargeur: 1920, ecranHauteur: 1080);
        Assert.True(encoreUtilisable);

        // Décalée de 150 px de plus : seuls 70 px restent visibles (< seuil de 100 px).
        bool plusUtilisable = GeometrieDecider.GeometrieUtilisable(
            x: 1850, y: 100, largeur: 300, hauteur: 800,
            ecranX: 0, ecranY: 0, ecranLargeur: 1920, ecranHauteur: 1080);
        Assert.False(plusUtilisable);
    }
}
