namespace CubeScope.Shell;

public static class GeometrieDecider
{
    /// <summary>
    /// Seuil de recouvrement (en pixels indépendants du périphérique) en dessous duquel la
    /// fenêtre est considérée comme perdue hors champ plutôt que simplement décalée.
    /// </summary>
    private const double Seuil = 100;

    /// <summary>
    /// Une géométrie enregistrée reste utilisable si une portion suffisante de la fenêtre
    /// (<see cref="Seuil"/> px) recouvre le rectangle de l'écran virtuel — la réunion de TOUS
    /// les moniteurs, pas seulement le principal — sur les deux axes. Traité symétriquement en
    /// X et en Y : un écran secondaire peut être à gauche, à droite, au-dessus ou en dessous du
    /// principal, avec des coordonnées négatives dans n'importe quelle direction.
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
