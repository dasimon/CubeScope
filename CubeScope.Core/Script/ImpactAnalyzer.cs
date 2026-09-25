using System.Text;
using System.Text.Json.Serialization;
using CubeScope.Core.Ai;

namespace CubeScope.Core.Script;

/// <summary>Kind of change to a script command between two versions of the script.</summary>
/// <remarks>
/// Explicit <see cref="JsonStringEnumConverter"/>: by default System.Text.Json serializes an
/// enum as an integer (checked empirically) — the rest of the code base works around that by calling
/// <c>.ToString()</c> by hand in anonymous objects (see <c>/api/stats/status</c>), which
/// does not apply here since <see cref="MemberChange"/> exposes the typed enum directly.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeKind { Added, Removed, Changed }

/// <summary>What changed in a <see cref="ChangeKind.Changed"/> calculated member / named set.</summary>
/// <remarks>Properties = everything but the expression: FORMAT_STRING, VISIBLE, NON_EMPTY_BEHAVIOR…,
/// and the CREATE header itself (e.g. HIDDEN added).</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeDetail { Expression, Properties, ExpressionAndProperties }

/// <summary>
/// A script command added/removed/changed between the old and the new script. Kind =
/// CalculatedMember / NamedSet (Name = unique name) or Scope / Autre (Name = first line of the
/// statement). For members/sets, <see cref="ImpactedDownstream"/> lists the members of the NEW
/// script that depend on it transitively (for a removed member, these are now-broken references);
/// it is empty for Scope / Autre. <see cref="Detail"/> is set only for a Changed member/set.
/// <see cref="StartLine"/> = line in the new script (in the old one for a Removed command).
/// </summary>
public sealed record MemberChange(
    string Name,
    string Kind,
    ChangeKind Change,
    IReadOnlyList<string> ImpactedDownstream,
    ChangeDetail? Detail = null,
    int StartLine = 0);

public sealed record ImpactReport(IReadOnlyList<MemberChange> Changes);

/// <summary>
/// Impact analysis between two versions of the MDX Script (typically: script deployed on the
/// server vs project script, before overwriting). Reuses <see cref="ScriptParser"/> for
/// splitting and <see cref="MdxContextBuilder.ExtractReferences"/> (token matching,
/// same pragmatic approach as <see cref="DependencyService"/>) for dependencies.
/// Every command is compared on its full text normalized for whitespace and comments: a mere
/// reformatting is not a change, any other edit is (case included).
/// </summary>
public static class ImpactAnalyzer
{
    private sealed record PendingChange(string Name, string Kind, ChangeKind Change, ChangeDetail? Detail, int StartLine);

    public static ImpactReport Analyze(string oldScript, string newScript)
    {
        var oldCmds = ScriptParser.ParseDetailed(oldScript ?? "");
        var newCmds = ScriptParser.ParseDetailed(newScript ?? "");
        var oldByName = IndexByName(oldCmds);
        var newByName = IndexByName(newCmds);

        var changes = new List<PendingChange>();

        foreach (var (name, cmd) in newByName)
        {
            if (!oldByName.TryGetValue(name, out var oldCmd))
                changes.Add(new(name, cmd.Command.Kind, ChangeKind.Added, null, cmd.Command.StartLine));
            else if (CompareNamed(oldCmd, cmd) is { } detail)
                changes.Add(new(name, cmd.Command.Kind, ChangeKind.Changed, detail, cmd.Command.StartLine));
        }
        foreach (var (name, cmd) in oldByName)
        {
            if (!newByName.ContainsKey(name))
                changes.Add(new(name, cmd.Command.Kind, ChangeKind.Removed, null, cmd.Command.StartLine));
        }
        changes.AddRange(DiffUnnamed(oldCmds, newCmds));

        // References of each member of the NEW script (basis of the downstream impact computation).
        var refsByName = newByName.ToDictionary(
            kv => kv.Key,
            kv => MdxContextBuilder.ExtractReferences(kv.Value.Command.Expression),
            StringComparer.OrdinalIgnoreCase);

        var result = changes
            .Select(c => new MemberChange(c.Name, c.Kind, c.Change,
                IsNamed(c.Kind) ? CollectDownstream(c.Name, refsByName) : [],
                c.Detail, c.StartLine))
            .OrderBy(c => c.Change switch { ChangeKind.Removed => 0, ChangeKind.Changed => 1, _ => 2 })
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.StartLine)
            .ToList();

        return new ImpactReport(result);
    }

    private static bool IsNamed(string kind) => kind is "CalculatedMember" or "NamedSet";

    private static Dictionary<string, ScriptParser.ParsedCommand> IndexByName(IReadOnlyList<ScriptParser.ParsedCommand> commands)
    {
        var dict = new Dictionary<string, ScriptParser.ParsedCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commands.Where(c => IsNamed(c.Command.Kind)))
            dict.TryAdd(c.Command.Name, c);
        return dict;
    }

    /// <summary>What differs between two versions of a member/set, null if nothing significant.</summary>
    private static ChangeDetail? CompareNamed(ScriptParser.ParsedCommand oldCmd, ScriptParser.ParsedCommand newCmd)
    {
        bool expression = NormalizeText(oldCmd.Command.Expression) != NormalizeText(newCmd.Command.Expression);
        bool properties = NormalizeText(oldCmd.Definition) != NormalizeText(newCmd.Definition);
        return (expression, properties) switch
        {
            (true, true) => ChangeDetail.ExpressionAndProperties,
            (true, false) => ChangeDetail.Expression,
            (false, true) => ChangeDetail.Properties,
            _ => null,
        };
    }

    /// <summary>
    /// SCOPE blocks and other statements (assignments, FREEZE…) have no name: they are grouped
    /// by (Kind, key) — key = normalized SCOPE header (up to the first ';') or left-hand side of
    /// an assignment (up to the first '='). Within a group, identical statements (normalized text,
    /// as a multiset) cancel out; the rest are paired in order as Changed, the surplus is Added
    /// or Removed.
    /// </summary>
    private static IEnumerable<PendingChange> DiffUnnamed(
        IReadOnlyList<ScriptParser.ParsedCommand> oldCmds, IReadOnlyList<ScriptParser.ParsedCommand> newCmds)
    {
        var oldGroups = oldCmds.Where(c => !IsNamed(c.Command.Kind)).ToLookup(GroupKey);
        var newGroups = newCmds.Where(c => !IsNamed(c.Command.Kind)).ToLookup(GroupKey);

        foreach (var key in oldGroups.Select(g => g.Key).Union(newGroups.Select(g => g.Key)))
        {
            var oldLeft = oldGroups[key].ToList();
            var newLeft = new List<ScriptParser.ParsedCommand>();
            foreach (var n in newGroups[key])
            {
                string text = NormalizeText(n.Text);
                int match = oldLeft.FindIndex(o => NormalizeText(o.Text) == text);
                if (match >= 0) oldLeft.RemoveAt(match);
                else newLeft.Add(n);
            }

            int paired = Math.Min(oldLeft.Count, newLeft.Count);
            for (int i = 0; i < newLeft.Count; i++)
            {
                var n = newLeft[i].Command;
                yield return new(n.Name, n.Kind, i < paired ? ChangeKind.Changed : ChangeKind.Added, null, n.StartLine);
            }
            foreach (var o in oldLeft.Skip(paired))
                yield return new(o.Command.Name, o.Command.Kind, ChangeKind.Removed, null, o.Command.StartLine);
        }
    }

    private static string GroupKey(ScriptParser.ParsedCommand c)
    {
        string text = c.Text;
        int cut = c.Command.Kind == "Scope" ? text.IndexOf(';') : text.IndexOf('=');
        return c.Command.Kind + "|" + NormalizeText(cut < 0 ? text : text[..cut]);
    }

    /// <summary>
    /// Text normalized for comparison: comments removed, whitespace dropped except a single
    /// space where it is significant (between two identifier characters: "CREATE MEMBER").
    /// Strings and [bracketed] identifiers are kept verbatim.
    /// </summary>
    internal static string NormalizeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            char next = i + 1 < n ? text[i + 1] : '\0';

            if ((c == '/' && next == '/') || (c == '-' && next == '-'))
            {
                int nl = text.IndexOf('\n', i);
                i = nl < 0 ? n : nl;
                pendingSpace = true;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                pendingSpace = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                i++;
                continue;
            }

            if (pendingSpace && sb.Length > 0 && NeedsSpace(sb[^1], c)) sb.Append(' ');
            pendingSpace = false;

            // Strings and bracketed identifiers: copied verbatim up to their closing character.
            char close = c switch { '"' => '"', '\'' => '\'', '[' => ']', _ => '\0' };
            if (close != '\0')
            {
                int end = text.IndexOf(close, i + 1);
                int stop = end < 0 ? n : end + 1;
                sb.Append(text, i, stop - i);
                i = stop;
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Is the whitespace between these two characters significant?</summary>
    private static bool NeedsSpace(char before, char after) =>
        (IsIdentifierChar(before) && IsIdentifierChar(after))
        || (before, after) is ('-', '-') or ('/', '/') or ('/', '*'); // would otherwise read as a comment

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
