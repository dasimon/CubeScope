namespace CubeScope.Core.Ssas;

/// <summary>
/// Construit la requête MDX qui rend les enfants d'un nœud de l'explorateur.
///
/// POURQUOI MDX ET PAS `$SYSTEM.MDSCHEMA_MEMBERS` : filtrer cette DMV par membre fait scanner
/// toute la dimension — gel constaté sur une dimension titres de plusieurs milliers d'ISIN
/// (voir « Pièges connus » du CLAUDE.md, même leçon que pour la résolution des captions).
/// `StrToMember(...).Children` résout par clé et ne lit que le cran demandé.
/// </summary>
public static class MemberChildrenQuery
{
    /// <summary>
    /// <paramref name="parent"/> est soit une hiérarchie (premier cran, sous le dossier
    /// « Membres »), soit un membre. L'appelant le sait par le nœud déplié : on ne le devine
    /// pas depuis la chaîne, un unique name de membre et de hiérarchie ayant la même allure.
    ///
    /// <paramref name="limit"/> plafonne le cran. Le nombre réel d'enfants remonte par
    /// `CHILDREN_CARDINALITY`, ce qui permet d'annoncer franchement combien de membres sont
    /// masqués plutôt que de tronquer en silence — et de savoir si un nœud est une feuille
    /// sans une requête de plus.
    /// </summary>
    public static string Build(string cube, string parent, bool isHierarchy, int limit)
    {
        // Doublées pour la chaîne passée à StrToMember/StrToSet : un membre nommé « L'Oréal »
        // casserait la requête, et un nom venu du cube ne doit pas pouvoir injecter du MDX.
        string p = parent.Replace("'", "''");
        string ensemble = isHierarchy
            ? $"StrToSet('{p}.Levels(0).Members')"
            : $"StrToMember('{p}').Children";

        return $"SELECT HEAD({ensemble}, {limit}) "
             + "DIMENSION PROPERTIES MEMBER_CAPTION, CHILDREN_CARDINALITY ON 0 "
             + $"FROM [{cube.Replace("]", "]]")}]";
    }
}
