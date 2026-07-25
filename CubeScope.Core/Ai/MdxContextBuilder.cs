using System.Text;
using System.Text.RegularExpressions;
using CubeScope.Core.Models;

namespace CubeScope.Core.Ai;

/// <summary>
/// Construit le contexte cube injecté dans les prompts IA : extraction des [références]
/// du MDX par matching de tokens (approche pragmatique actée, ~95 %), puis sélection des
/// seules métadonnées pertinentes du CubeMeta — jamais les centaines de mesures en bloc.
/// </summary>
public static partial class MdxContextBuilder
{
    [GeneratedRegex(@"\[(?:[^\]]|\]\])+\]", RegexOptions.Compiled)]
    private static partial Regex BracketedSegment();

    /// <summary>Segments crochetés du MDX, décrochetés et dédupliqués (insensible casse).</summary>
    internal static IReadOnlySet<string> ExtractReferences(string mdx)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in BracketedSegment().Matches(mdx))
            refs.Add(m.Value[1..^1].Replace("]]", "]"));
        return refs;
    }

    /// <summary>Bloc texte compact des métadonnées référencées par le MDX.</summary>
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
