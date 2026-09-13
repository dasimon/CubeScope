namespace CubeScope.Shell;

public static class GeometrieDecider
{
    /// <summary>
    /// Overlap threshold (in device-independent pixels) below which the
    /// window is considered lost off screen rather than merely offset.
    /// </summary>
    private const double Seuil = 100;

    /// <summary>
    /// A saved geometry remains usable if a large enough portion of the window
    /// (<see cref="Seuil"/> px) overlaps the virtual screen rectangle — the union of ALL
    /// monitors, not only the primary one — on both axes. Handled symmetrically on
    /// X and Y: a secondary screen can be to the left, to the right, above or below the
    /// primary one, with negative coordinates in any direction.
    /// </summary>
    public static bool GeometrieUtilisable(
        double x, double y, double largeur, double hauteur,
        double ecranX, double ecranY, double ecranLargeur, double ecranHauteur)
    {
        double chevauchementX = Math.Min(x + largeur, ecranX + ecranLargeur) - Math.Max(x, ecranX);
        double chevauchementY = Math.Min(y + hauteur, ecranY + ecranHauteur) - Math.Max(y, ecranY);
        return chevauchementX >= Seuil && chevauchementY >= Seuil;
    }
}
