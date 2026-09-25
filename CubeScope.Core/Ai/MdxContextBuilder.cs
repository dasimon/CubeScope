using System.Text;
using System.Text.RegularExpressions;
using CubeScope.Core.Models;

namespace CubeScope.Core.Ai;

/// <summary>
/// Builds the cube context injected into AI prompts: extracts the [references]
/// from the MDX by token matching (settled pragmatic approach, ~95%), then selects only the
/// relevant CubeMeta metadata — never the hundreds of measures in bulk.
/// </summary>
public static partial class MdxContextBuilder
{
    [GeneratedRegex(@"\[(?:[^\]]|\]\])+\]", RegexOptions.Compiled)]
    private static partial Regex BracketedSegment();

    /// <summary>Bracketed segments of the MDX, unbracketed and deduplicated (case-insensitive).</summary>
    internal static IReadOnlySet<string> ExtractReferences(string mdx)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in BracketedSegment().Matches(mdx))
            refs.Add(m.Value[1..^1].Replace("]]", "]"));
        return refs;
    }

    // Left-to-right scan: comments, strings and bracketed identifiers are consumed whole, so
    // a "FROM" inside them ([From Date], // … from [X]) is never taken for the clause.
    [GeneratedRegex("""//[^\n]*|--[^\n]*|/\*[\s\S]*?\*/|"[^"]*"|'[^']*'|\[(?:[^\]]|\]\])*\]|\bFROM\s*(?:\[(?<b>(?:[^\]]|\]\])+)\]|(?<p>[A-Za-z_]\w*))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex FromClauseScan();

    /// <summary>
    /// Cube named by the FROM clause, unbracketed. Sub-select (FROM (SELECT … FROM [X])): the
    /// outer FROM is followed by "(", so the first FROM naming a cube is the inner one — the
    /// real cube. null if none (no FROM, or unparseable).
    /// </summary>
    internal static string? CubeFromMdx(string mdx)
    {
        foreach (Match m in FromClauseScan().Matches(mdx))
        {
            if (m.Groups["b"].Success) return m.Groups["b"].Value.Replace("]]", "]");
            if (m.Groups["p"].Success) return m.Groups["p"].Value;
        }
        return null;
    }

    /// <summary>The cube of the FROM clause if the catalog has it, otherwise the first cube (null if none).</summary>
    internal static string? ChooseCube(IReadOnlyList<string> cubes, string mdx)
    {
        string? named = CubeFromMdx(mdx);
        return cubes.FirstOrDefault(c => string.Equals(c, named, StringComparison.OrdinalIgnoreCase))
            ?? cubes.FirstOrDefault();
    }

    /// <summary>Compact text block of the metadata referenced by the MDX.</summary>
    public static string Build(CubeMeta meta, string mdx)
    {
        var refs = ExtractReferences(mdx);
        var sb = new StringBuilder();
        sb.AppendLine($"Cube : [{meta.CubeName}]");

        var measures = meta.MeasureFolders.SelectMany(f => f.Measures)
            .Where(m => refs.Contains(m.Name)).ToList();
        if (measures.Count > 0)
        {
            sb.AppendLine("Mesures référencées :");
            foreach (var m in measures) sb.AppendLine($"  - {m.UniqueName}");
        }

        var dims = meta.Dimensions
            .Where(d => refs.Contains(d.Name) ||
                        d.Hierarchies.Any(h => refs.Contains(h.Name) ||
                                               h.Levels.Any(l => refs.Contains(l.Name))))
            .ToList();
        if (dims.Count > 0)
        {
            sb.AppendLine("Dimensions référencées (hiérarchies → niveaux) :");
            foreach (var d in dims)
            {
                sb.AppendLine($"  - {d.UniqueName}{(d.Description.Length > 0 ? $"  — {d.Description}" : "")}");
                foreach (var h in d.Hierarchies.Where(h => refs.Contains(h.Name) || refs.Contains(d.Name)
                             || h.Levels.Any(l => refs.Contains(l.Name))))
                    sb.AppendLine($"      {h.UniqueName}{(h.Description.Length > 0 ? $"  — {h.Description}" : "")} : niveaux {string.Join(" > ", h.Levels.Select(l => l.Name))}");
            }
        }

        sb.AppendLine($"(Le cube compte {meta.MeasureFolders.Sum(f => f.Measures.Count)} mesures et {meta.Dimensions.Count} dimensions au total.)");
        return sb.ToString();
    }
}
