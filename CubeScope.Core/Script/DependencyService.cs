using CubeScope.Core.Ai;
using CubeScope.Core.Models;

namespace CubeScope.Core.Script;

/// <summary>
/// Graphe de dépendances des membres calculés / sets par matching de tokens
/// (décision actée : ~95 % de précision assumé, pas d'AST). Une dépendance est
/// détectée quand l'expression d'un élément contient le unique name (ou le dernier
/// segment) d'un autre élément du script, d'une mesure ou d'une hiérarchie du cube.
/// </summary>
public static class DependencyService
{
    private const int MaxDepth = 8; // garde-fou cycles/profondeur (le graphe réel est petit)

    public static DependencyGraph Resolve(CubeScript script, CubeMeta meta, string name)
    {
        var byName = IndexCommands(script);
        if (!byName.TryGetValue(name, out var root))
            throw new InvalidOperationException($"Élément introuvable dans le script : {name}");

        var rootNode = BuildNode(root.Name, root.Kind, root.Expression, byName, meta, [], 0);

        // Dépendants inverses : tout élément du script dont l'expression référence `name`
        string lastSegment = LastSegment(name);
        var usedBy = script.Commands
            .Where(c => c.Kind is "CalculatedMember" or "NamedSet" && !NamesEqual(c.Name, name))
            .Where(c => ReferencesName(MdxContextBuilder.ExtractReferences(c.Expression), name, lastSegment))
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        return new DependencyGraph(rootNode, usedBy);
    }

    private static DependencyNode BuildNode(string name, string kind, string expression,
        Dictionary<string, ScriptCommand> byName, CubeMeta meta, HashSet<string> path, int depth)
    {
        if (depth >= MaxDepth || !path.Add(name.ToUpperInvariant()))
            return new DependencyNode(name, kind, []);

        var refs = MdxContextBuilder.ExtractReferences(expression);
        var children = new List<DependencyNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Autres éléments du script (récursif)
        foreach (var cmd in byName.Values.Where(c => !NamesEqual(c.Name, name)))
        {
            if (!ReferencesName(refs, cmd.Name, LastSegment(cmd.Name)) || !seen.Add(cmd.Name)) continue;
            children.Add(BuildNode(cmd.Name, cmd.Kind, cmd.Expression, byName, meta,
                [.. path], depth + 1));
        }

        // 2. Mesures physiques (feuilles)
        foreach (var m in meta.MeasureFolders.SelectMany(f => f.Measures))
            if (refs.Contains(m.Name) && !seen.Contains(m.UniqueName) &&
                !byName.Keys.Any(k => LastSegment(k).Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                if (seen.Add(m.UniqueName))
                    children.Add(new DependencyNode(m.UniqueName, "Measure", []));

        // 3. Hiérarchies référencées (feuilles)
        foreach (var d in meta.Dimensions)
            foreach (var h in d.Hierarchies)
                if (refs.Contains(d.Name) && refs.Contains(h.Name) && seen.Add(h.UniqueName))
                    children.Add(new DependencyNode(h.UniqueName, "Hierarchy", []));

        return new DependencyNode(name, kind, children.OrderBy(c => c.Kind).ThenBy(c => c.Name).ToList());
    }

    private static Dictionary<string, ScriptCommand> IndexCommands(CubeScript script)
    {
        var dict = new Dictionary<string, ScriptCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in script.Commands.Where(c => c.Kind is "CalculatedMember" or "NamedSet"))
            dict.TryAdd(c.Name, c);
        return dict;
    }

    /// <summary>Le dernier segment crocheté d'un unique name : "[Measures].[Marge]" → "Marge".</summary>
    internal static string LastSegment(string uniqueName)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(uniqueName, @"\[(?:[^\]]|\]\])+\]");
        return matches.Count > 0
            ? matches[^1].Value[1..^1].Replace("]]", "]")
            : uniqueName;
    }

    private static bool NamesEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(LastSegment(a), LastSegment(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>L'ensemble de références contient-il cet élément (dernier segment) ?</summary>
    private static bool ReferencesName(IReadOnlySet<string> refs, string uniqueName, string lastSegment) =>
        refs.Contains(lastSegment);
}
