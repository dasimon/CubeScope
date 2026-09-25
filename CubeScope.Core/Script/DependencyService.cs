using CubeScope.Core.Ai;
using CubeScope.Core.Models;

namespace CubeScope.Core.Script;

/// <summary>
/// Dependency graph of calculated members / sets by token matching
/// (settled decision: ~95% accuracy accepted, no AST). A dependency is
/// detected when an item's expression contains the unique name (or the last
/// segment) of another script item, of a measure or of a cube hierarchy.
/// </summary>
public static class DependencyService
{
    private const int MaxDepth = 8; // safeguard against depth (the real graph is small)

    public static DependencyGraph Resolve(CubeScript script, CubeMeta meta, string name)
    {
        var byName = IndexCommands(script);
        if (!byName.TryGetValue(name, out var root))
            throw new InvalidOperationException($"Élément introuvable dans le script : {name}");

        var rootNode = BuildTree(root, byName, meta);

        // Reverse dependents: any script item whose expression references `name`
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

    /// <summary>Node under construction (children filled breadth-first).</summary>
    private sealed class Builder(string name, string kind)
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public List<Builder> Children { get; } = [];

        public DependencyNode ToNode() => new(Name, Kind,
            Children.Select(c => c.ToNode()).OrderBy(c => c.Kind).ThenBy(c => c.Name).ToList());
    }

    /// <summary>
    /// Builds the tree breadth-first, expanding each script item ONCE, at its shallowest
    /// occurrence: any later occurrence (shared dependency of a "diamond", or cycle) is a leaf.
    /// Without this, a diamond-shaped graph doubles the tree at every level. References are
    /// extracted once per command.
    /// </summary>
    private static DependencyNode BuildTree(ScriptCommand root, Dictionary<string, ScriptCommand> byName, CubeMeta meta)
    {
        var refsByName = byName.Values.ToDictionary(
            c => c.Name, c => MdxContextBuilder.ExtractReferences(c.Expression), StringComparer.OrdinalIgnoreCase);
        var items = byName.Values.Select(c => (Cmd: c, Segment: LastSegment(c.Name))).ToList();
        var scriptSegments = new HashSet<string>(items.Select(x => x.Segment), StringComparer.OrdinalIgnoreCase);
        var measures = meta.MeasureFolders.SelectMany(f => f.Measures).ToList();

        var rootBuilder = new Builder(root.Name, root.Kind);
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Name };
        var queue = new Queue<(Builder Node, int Depth)>();
        queue.Enqueue((rootBuilder, 0));

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            var refs = refsByName[node.Name];
            string nodeSegment = LastSegment(node.Name);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Other script items (expanded once, breadth-first)
            foreach (var (cmd, segment) in items)
            {
                if (segment.Equals(nodeSegment, StringComparison.OrdinalIgnoreCase)) continue; // itself
                if (!ReferencesName(refs, cmd.Name, segment) || !seen.Add(cmd.Name)) continue;
                var child = new Builder(cmd.Name, cmd.Kind);
                node.Children.Add(child);
                if (depth + 1 < MaxDepth && expanded.Add(cmd.Name))
                    queue.Enqueue((child, depth + 1));
            }

            // 2. Physical measures (leaves)
            foreach (var m in measures)
                if (refs.Contains(m.Name) && !scriptSegments.Contains(m.Name) && seen.Add(m.UniqueName))
                    node.Children.Add(new Builder(m.UniqueName, "Measure"));

            // 3. Referenced hierarchies (leaves)
            foreach (var d in meta.Dimensions)
                foreach (var h in d.Hierarchies)
                    if (refs.Contains(d.Name) && refs.Contains(h.Name) && seen.Add(h.UniqueName))
                        node.Children.Add(new Builder(h.UniqueName, "Hierarchy"));
        }

        return rootBuilder.ToNode();
    }

    private static Dictionary<string, ScriptCommand> IndexCommands(CubeScript script)
    {
        var dict = new Dictionary<string, ScriptCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in script.Commands.Where(c => c.Kind is "CalculatedMember" or "NamedSet"))
            dict.TryAdd(c.Name, c);
        return dict;
    }

    /// <summary>The last bracketed segment of a unique name: "[Measures].[Marge]" → "Marge".</summary>
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

    /// <summary>Does the set of references contain this item (last segment)?</summary>
    private static bool ReferencesName(IReadOnlySet<string> refs, string uniqueName, string lastSegment) =>
        refs.Contains(lastSegment);
}
