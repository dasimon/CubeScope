namespace CubeScope.Core.Models;

/// <summary>Arbre de métadonnées d'un cube (sans les membres — chargés en lazy pour l'autocomplétion).</summary>
public sealed record CubeMeta(
    string CubeName,
    IReadOnlyList<MeasureFolder> MeasureFolders,
    IReadOnlyList<DimensionMeta> Dimensions);

/// <summary>Groupe de mesures par dossier d'affichage ("" = racine).</summary>
public sealed record MeasureFolder(string Folder, IReadOnlyList<MeasureMeta> Measures);

public sealed record MeasureMeta(string Name, string UniqueName, string Description = "");

public sealed record DimensionMeta(string Name, string UniqueName, IReadOnlyList<HierarchyMeta> Hierarchies);

public sealed record HierarchyMeta(string Name, string UniqueName, IReadOnlyList<LevelMeta> Levels);

public sealed record LevelMeta(string Name, string UniqueName, int Number);

public sealed record MemberMeta(string Caption, string UniqueName);

/// <summary>Delta d'un compteur perfmon autour d'une requête.</summary>
public sealed record CounterDelta(string Category, string Counter, long Delta);
