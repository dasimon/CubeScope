using System.Text.Json.Serialization;
using CubeScope.Core.Ai;
using CubeScope.Core.Models;

namespace CubeScope.Core.Script;

/// <summary>Nature d'un changement de membre calculé / set entre deux versions du script.</summary>
/// <remarks>
/// <see cref="JsonStringEnumConverter"/> explicite : par défaut System.Text.Json sérialise un
/// enum en entier (vérifié empiriquement) — le reste du code base contourne ça en appelant
/// <c>.ToString()</c> à la main dans des objets anonymes (voir <c>/api/stats/status</c>), ce qui
/// ne s'applique pas ici puisque <see cref="MemberChange"/> expose l'enum typé directement.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeKind { Added, Removed, Changed }

/// <summary>
/// Un membre calculé (ou set nommé) ajouté/supprimé/modifié entre l'ancien et le nouveau
/// script, avec la liste des membres du NOUVEAU script qui en dépendent transitivement
/// (pour un membre supprimé, ce sont des références désormais cassées).
/// </summary>
public sealed record MemberChange(
    string Name,
    string Kind,
    ChangeKind Change,
    IReadOnlyList<string> ImpactedDownstream);

public sealed record ImpactReport(IReadOnlyList<MemberChange> Changes);

/// <summary>
/// Analyse d'impact entre deux versions du MDX Script (typiquement : script déployé sur le
/// serveur vs script du projet, avant écrasement). Réutilise <see cref="ScriptParser"/> pour
/// le découpage et <see cref="MdxContextBuilder.ExtractReferences"/> (matching de tokens,
/// même approche pragmatique que <see cref="DependencyService"/>) pour les dépendances.
/// </summary>
public static class ImpactAnalyzer
{
    public static ImpactReport Analyze(string oldScript, string newScript)
    {
        var oldByName = IndexByName(ScriptParser.Parse(oldScript ?? ""));
        var newByName = IndexByName(ScriptParser.Parse(newScript ?? ""));

        var changes = new List<(string Name, string Kind, ChangeKind Change)>();

        foreach (var (name, cmd) in newByName)
        {
            if (!oldByName.TryGetValue(name, out var oldCmd))
                changes.Add((name, cmd.Kind, ChangeKind.Added));
            else if (!string.Equals(oldCmd.Expression.Trim(), cmd.Expression.Trim(), StringComparison.Ordinal))
                changes.Add((name, cmd.Kind, ChangeKind.Changed));
        }
        foreach (var (name, cmd) in oldByName)
        {
            if (!newByName.ContainsKey(name))
                changes.Add((name, cmd.Kind, ChangeKind.Removed));
        }

        // Références de chaque membre du NOUVEAU script (base du calcul d'impact aval).
        var refsByName = newByName.ToDictionary(
            kv => kv.Key,
            kv => MdxContextBuilder.ExtractReferences(kv.Value.Expression),
            StringComparer.OrdinalIgnoreCase);

        var result = changes
            .Select(c => new MemberChange(c.Name, c.Kind, c.Change, CollectDownstream(c.Name, refsByName)))
            .OrderBy(c => c.Change switch { ChangeKind.Removed => 0, ChangeKind.Changed => 1, _ => 2 })
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ImpactReport(result);
    }

    private static Dictionary<string, ScriptCommand> IndexByName(IReadOnlyList<ScriptCommand> commands)
    {
        var dict = new Dictionary<string, ScriptCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commands.Where(c => c.Kind is "CalculatedMember" or "NamedSet"))
            dict.TryAdd(c.Name, c);
        return dict;
    }

    /// <summary>
    /// Fermeture transitive des membres du nouveau script qui dépendent de <paramref name="name"/>
    /// (directement ou via un autre membre déjà impacté). Marche même si <paramref name="name"/>
    /// n'existe plus dans le nouveau script (cas Removed : dépendants = références cassées).
    /// Garde-fou cycle : ensemble visité, chaque membre n'est mis en file qu'une fois.
    /// </summary>
    private static List<string> CollectDownstream(string name, IReadOnlyDictionary<string, IReadOnlySet<string>> refsByName)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var queue = new Queue<string>();
        queue.Enqueue(name);
        var result = new List<string>();

        while (queue.Count > 0)
        {
            string lastSeg = DependencyService.LastSegment(queue.Dequeue());
            foreach (var (depName, refs) in refsByName)
            {
                if (visited.Contains(depName) || !refs.Contains(lastSeg)) continue;
                visited.Add(depName);
                result.Add(depName);
                queue.Enqueue(depName);
            }
        }
        return result.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
