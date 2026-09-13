namespace CubeScope.Core.Ssas;

/// <summary>
/// Builds the MDX query that returns the children of an explorer node.
///
/// WHY MDX AND NOT `$SYSTEM.MDSCHEMA_MEMBERS`: filtering this DMV by member scans
/// the whole dimension — a freeze was observed on a securities dimension with several thousand ISINs
/// (see "Known pitfalls" in CLAUDE.md, same lesson as for caption resolution).
/// `StrToMember(...).Children` resolves by key and reads only the requested level.
/// </summary>
public static class MemberChildrenQuery
{
    /// <summary>
    /// <paramref name="parent"/> is either a hierarchy (first level, under the
    /// "Membres" folder) or a member. The caller knows which from the expanded node: we do not guess
    /// it from the string, since member and hierarchy unique names look the same.
    ///
    /// <paramref name="limit"/> caps the level. The real number of children comes back through
    /// `CHILDREN_CARDINALITY`, which lets us state plainly how many members are
    /// hidden rather than truncating silently — and know whether a node is a leaf
    /// without an extra query.
    /// </summary>
    public static string Build(string cube, string parent, bool isHierarchy, int limit)
    {
        // Doubled for the string passed to StrToMember/StrToSet: a member named "L'Oréal"
        // would break the query, and a name coming from the cube must not be able to inject MDX.
        string p = parent.Replace("'", "''");
        string ensemble = isHierarchy
            ? $"StrToSet('{p}.Levels(0).Members')"
            : $"StrToMember('{p}').Children";

        return $"SELECT HEAD({ensemble}, {limit}) "
             + "DIMENSION PROPERTIES MEMBER_CAPTION, CHILDREN_CARDINALITY ON 0 "
             + $"FROM [{cube.Replace("]", "]]")}]";
    }
}
