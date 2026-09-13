using System.Text.Json.Serialization;
using CubeScope.Core.Ai;
using CubeScope.Core.Models;

namespace CubeScope.Core.Script;

/// <summary>Kind of change to a calculated member / set between two versions of the script.</summary>
/// <remarks>
/// Explicit <see cref="JsonStringEnumConverter"/>: by default System.Text.Json serializes an
/// enum as an integer (checked empirically) — the rest of the code base works around that by calling
/// <c>.ToString()</c> by hand in anonymous objects (see <c>/api/stats/status</c>), which
/// does not apply here since <see cref="MemberChange"/> exposes the typed enum directly.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeKind { Added, Removed, Changed }

/// <summary>
/// A calculated member (or named set) added/removed/changed between the old and the new
/// script, with the list of members of the NEW script that depend on it transitively
/// (for a removed member, these are now-broken references).
/// </summary>
public sealed record MemberChange(
    string Name,
    string Kind,
    ChangeKind Change,
    IReadOnlyList<string> ImpactedDownstream);

public sealed record ImpactReport(IReadOnlyList<MemberChange> Changes);

/// <summary>
/// Impact analysis between two versions of the MDX Script (typically: script deployed on the
/// server vs project script, before overwriting). Reuses <see cref="ScriptParser"/> for
/// splitting and <see cref="MdxContextBuilder.ExtractReferences"/> (token matching,
/// same pragmatic approach as <see cref="DependencyService"/>) for dependencies.
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

        // References of each member of the NEW script (basis of the downstream impact computation).
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
    /// Transitive closure of the members of the new script that depend on <paramref name="name"/>
    /// (directly or through another member already impacted). Works even if <paramref name="name"/>
    /// no longer exists in the new script (Removed case: dependents = broken references).
    /// Cycle safeguard: visited set, each member is enqueued only once.
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
