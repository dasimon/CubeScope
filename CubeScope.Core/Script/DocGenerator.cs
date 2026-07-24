using System.Text;
using CubeScope.Core.Ai;
using CubeScope.Core.Models;

namespace CubeScope.Core.Script;

/// <summary>
/// Documentation Markdown déterministe du cube : structure (dimensions, mesures par
/// dossier) + membres calculés / sets du script avec expression et dépendances directes.
/// L'explication IA reste à la demande, membre par membre (jamais en boucle sur tout).
/// </summary>
public static class DocGenerator
{
    public static string Generate(CubeMeta meta, CubeScript script, string server, string catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Cube [{meta.CubeName}]");
        sb.AppendLine();
        sb.AppendLine($"> Généré par CubeScope le {DateTime.Now:yyyy-MM-dd HH:mm} — serveur `{server}`, catalogue `{catalog}`.");
        sb.AppendLine();

        // --- Dimensions ---
        sb.AppendLine($"## Dimensions ({meta.Dimensions.Count})");
        sb.AppendLine();
        foreach (var d in meta.Dimensions)
        {
            sb.AppendLine($"- **{d.Name}** `{d.UniqueName}`");
            foreach (var h in d.Hierarchies)
                sb.AppendLine($"  - {h.Name} : {string.Join(" > ", h.Levels.Select(l => l.Name))}");
        }
        sb.AppendLine();

        // --- Mesures physiques ---
        int measureCount = meta.MeasureFolders.Sum(f => f.Measures.Count);
        sb.AppendLine($"## Mesures ({measureCount})");
        sb.AppendLine();
        foreach (var f in meta.MeasureFolders)
        {
            sb.AppendLine($"### {(f.Folder == "" ? "(racine)" : f.Folder)}");
            sb.AppendLine();
            foreach (var m in f.Measures)
                sb.AppendLine($"- {m.Name}");
            sb.AppendLine();
        }

        // --- Script : membres calculés et sets ---
        var calculated = script.Commands.Where(c => c.Kind == "CalculatedMember").ToList();
        var sets = script.Commands.Where(c => c.Kind == "NamedSet").ToList();
        var scopes = script.Commands.Where(c => c.Kind == "Scope").ToList();

        sb.AppendLine($"## Membres calculés ({calculated.Count})");
        sb.AppendLine();
        foreach (var c in calculated)
        {
            sb.AppendLine($"### {c.Name}");
            sb.AppendLine();
            sb.AppendLine("```mdx");
            sb.AppendLine(c.Expression.Trim());
            sb.AppendLine("```");
            var deps = DirectDependencies(c, script, meta);
            if (deps.Count > 0)
                sb.AppendLine($"Dépend de : {string.Join(", ", deps.Select(d => $"`{d}`"))}");
            sb.AppendLine();
        }

        if (sets.Count > 0)
        {
            sb.AppendLine($"## Sets nommés ({sets.Count})");
            sb.AppendLine();
            foreach (var c in sets)
            {
                sb.AppendLine($"### {c.Name}");
                sb.AppendLine();
                sb.AppendLine("```mdx");
                sb.AppendLine(c.Expression.Trim());
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        if (scopes.Count > 0)
        {
            sb.AppendLine($"## Blocs SCOPE ({scopes.Count})");
            sb.AppendLine();
            foreach (var c in scopes)
                sb.AppendLine($"- Ligne {c.StartLine} : `{c.Name}`");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Dépendances directes (non récursives) d'une commande, pour la doc.</summary>
    private static IReadOnlyList<string> DirectDependencies(ScriptCommand cmd, CubeScript script, CubeMeta meta)
    {
        var refs = MdxContextBuilder.ExtractReferences(cmd.Expression);
        var deps = new List<string>();

        foreach (var other in script.Commands.Where(c =>
                     c.Kind is "CalculatedMember" or "NamedSet" &&
                     !c.Name.Equals(cmd.Name, StringComparison.OrdinalIgnoreCase)))
            if (refs.Contains(DependencyService.LastSegment(other.Name)))
                deps.Add(other.Name);

        foreach (var m in meta.MeasureFolders.SelectMany(f => f.Measures))
            if (refs.Contains(m.Name) &&
                !deps.Any(d => DependencyService.LastSegment(d).Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                deps.Add(m.UniqueName);

        return deps.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
    }
}
