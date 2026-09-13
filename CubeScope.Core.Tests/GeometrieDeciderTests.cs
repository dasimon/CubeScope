using CubeScope.Shell;

namespace CubeScope.Core.Tests;

/// <summary>
/// `GeometrieDecider.GeometrieUtilisable` decides whether a saved window geometry is still
/// displayable, on the virtual screen (all monitors) rather than the primary screen alone — the
/// bug fixed here wrongly rejected any position on a secondary screen.
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
    /// Secondary screen to the LEFT of the primary (virtual screen starting at -1920). This is the
    /// case the old code (bounded by `SystemParameters.WorkArea`, limited to the primary) wrongly
    /// rejected: with the same values, `x=-1800 < bornes.Left(0) - largeur(1280) + 100 = -1180` was
    /// true → window deemed off-screen even though the screen is plugged in.
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
    /// Secondary screen ABOVE the primary (negative `VirtualScreenTop`). The old Y check
    /// (`etat.Y < bornes.Top`, with no tolerance) rejected every negative Y without exception.
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
    /// Same geometry as the "secondary screen to the left" case, but the virtual screen has
    /// shrunk to the primary alone (0..1920): the screen was unplugged since the last session.
    /// Must be rejected, unlike the previous case — that is the distinction the safeguard
    /// must preserve.
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
        // 220 px of the window remain visible on screen (>= 100 px threshold).
        bool encoreUtilisable = GeometrieDecider.GeometrieUtilisable(
            x: 1700, y: 100, largeur: 300, hauteur: 800,
            ecranX: 0, ecranY: 0, ecranLargeur: 1920, ecranHauteur: 1080);
        Assert.True(encoreUtilisable);

        // Shifted 150 px further: only 70 px remain visible (< 100 px threshold).
        bool plusUtilisable = GeometrieDecider.GeometrieUtilisable(
            x: 1850, y: 100, largeur: 300, hauteur: 800,
            ecranX: 0, ecranY: 0, ecranLargeur: 1920, ecranHauteur: 1080);
        Assert.False(plusUtilisable);
    }
}
