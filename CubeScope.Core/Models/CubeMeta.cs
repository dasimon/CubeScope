namespace CubeScope.Core.Models;

/// <summary>Arbre de métadonnées d'un cube (sans les membres — chargés en lazy pour l'autocomplétion).</summary>
public sealed record CubeMeta(
    string CubeName,
    IReadOnlyList<MeasureFolder> MeasureFolders,
    IReadOnlyList<DimensionMeta> Dimensions);

/// <summary>Groupe de mesures par dossier d'affichage ("" = racine).</summary>
public sealed record MeasureFolder(string Folder, IReadOnlyList<MeasureMeta> Measures);

public sealed record MeasureMeta(string Name, string UniqueName, string Description = "");

public sealed record DimensionMeta(string Name, string UniqueName, IReadOnlyList<HierarchyMeta> Hierarchies, string Description = "");

public sealed record HierarchyMeta(string Name, string UniqueName, IReadOnlyList<LevelMeta> Levels, string Description = "");

public sealed record LevelMeta(string Name, string UniqueName, int Number);

public sealed record MemberMeta(string Caption, string UniqueName);

/// <summary>
/// Un cran de l'arbre des membres (explorateur). <paramref name="ChildrenCount"/> vaut le
/// nombre RÉEL d'enfants, indépendant du plafond de chargement : 0 identifie une feuille (pas
/// de flèche de dépliage), et au-delà du plafond il donne le nombre de membres non affichés.
/// -1 si le serveur ne l'a pas renvoyé — l'appelant traite alors le nœud comme dépliable.
/// </summary>
public sealed record MemberNode(string Caption, string UniqueName, long ChildrenCount);

/// <summary>
/// Un cran de drill-down. <paramref name="HasMore"/> dit que le plafond a coupé : l'interface
/// l'annonce au lieu de tronquer en silence. Le nombre exact de membres masqués, lui, se déduit
/// côté client de la cardinalité du parent — le serveur n'a donc pas à le recompter.
/// </summary>
public sealed record MemberChildren(IReadOnlyList<MemberNode> Nodes, bool HasMore);

/// <summary>Delta d'un compteur perfmon autour d'une requête.</summary>
public sealed record CounterDelta(string Category, string Counter, long Delta);
