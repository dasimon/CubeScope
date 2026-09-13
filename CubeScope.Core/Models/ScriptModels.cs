namespace CubeScope.Core.Models;

/// <summary>The MDX Script of a cube: full text + detected commands.</summary>
public sealed record CubeScript(
    string CubeName,
    string FullText,
    IReadOnlyList<ScriptCommand> Commands);

/// <summary>
/// A command detected in the script. Kind: CalculatedMember, NamedSet, Scope, Autre.
/// StartLine (1-based) enables navigation in the editor. Section = path of the enclosing
/// `// #region` region ("A / B" if nested), null outside any region.
/// </summary>
public sealed record ScriptCommand(
    string Kind,
    string Name,
    string Expression,
    int StartLine,
    string? Section = null);

/// <summary>Node of the dependency graph of a calculated member / set.</summary>
public sealed record DependencyNode(
    string Name,
    string Kind, // CalculatedMember | NamedSet | Measure | Hierarchy | Inconnu
    IReadOnlyList<DependencyNode> Dependencies);

/// <summary>Dependencies of an item: what it uses (tree) and what uses it (list).</summary>
public sealed record DependencyGraph(
    DependencyNode Root,
    IReadOnlyList<string> UsedBy);
