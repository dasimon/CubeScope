namespace CubeScope.Core.Models;

/// <summary>Le MDX Script d'un cube : texte complet + commandes repérées.</summary>
public sealed record CubeScript(
    string CubeName,
    string FullText,
    IReadOnlyList<ScriptCommand> Commands);

/// <summary>
/// Une commande repérée dans le script. Kind : CalculatedMember, NamedSet, Scope, Autre.
/// StartLine (1-based) permet la navigation dans l'éditeur. Section = chemin de la
/// région `// #region` englobante ("A / B" si imbriquée), null hors région.
/// </summary>
public sealed record ScriptCommand(
    string Kind,
    string Name,
    string Expression,
    int StartLine,
    string? Section = null);

/// <summary>Nœud du graphe de dépendances d'un membre calculé / set.</summary>
public sealed record DependencyNode(
    string Name,
    string Kind, // CalculatedMember | NamedSet | Measure | Hierarchy | Inconnu
    IReadOnlyList<DependencyNode> Dependencies);

/// <summary>Dépendances d'un élément : ce qu'il utilise (arbre) et qui l'utilise (liste).</summary>
public sealed record DependencyGraph(
    DependencyNode Root,
    IReadOnlyList<string> UsedBy);
