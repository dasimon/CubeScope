using System.Text;
using System.Text.RegularExpressions;
using CubeScope.Core.Models;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Builds the MDX of the graphical builder. Pure function, one direction only: model → MDX.
/// The builder never reads back hand-written MDX (settled decision: tokenizer, no AST).
///
/// Escaping lives here and nowhere else:
/// - a bare identifier we wrap ourselves (the cube name) goes through <see cref="Bracket"/>
///   (<c>]</c> → <c>]]</c>);
/// - unique names come from the cube metadata, already escaped by the server (<c>[A]]B]</c>,
///   <c>&amp;[L'Oréal]</c>): they are passed through verbatim, but only after
///   <see cref="IsUniqueName"/> has checked they are nothing but bracketed segments — the
///   generated text never contains a string literal, so <c>'</c> needs no doubling and a crafted
///   name cannot inject MDX.
/// </summary>
public static partial class MdxQueryBuilder
{
    public static string Build(BuilderModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Cube)) throw Invalid("Aucun cube sélectionné.");

        var measures = model.Measures.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var m in measures) RequireUniqueName(m);
        ValidateAxes(model);

        var filters = MergeFilters(model.Filters);
        var axisHierarchies = model.Columns.Concat(model.Rows)
            .Select(i => i.Hierarchy).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Measures innermost on COLUMNS, after the column hierarchies (pivot-table layout).
        var columnSets = model.Columns.Select(ItemSet).ToList();
        if (measures.Count > 0) columnSets.Add(Set(measures));
        var rowSets = model.Rows.Select(ItemSet).ToList();
        // Rows without anything on COLUMNS: MDX axes are contiguous, ROWS cannot exist alone.
        if (columnSets.Count == 0 && rowSets.Count > 0) columnSets.Add("{[Measures].DefaultMember}");

        var sb = new StringBuilder("SELECT");
        if (columnSets.Count > 0)
        {
            sb.Append('\n').Append("  ").Append(model.NonEmptyColumns ? "NON EMPTY " : "")
              .Append(string.Join(" * ", columnSets)).Append(" ON COLUMNS");
            if (rowSets.Count > 0)
                sb.Append(",\n  ").Append(model.NonEmptyRows ? "NON EMPTY " : "")
                  .Append(string.Join(" * ", rowSets)).Append(" ON ROWS");
        }

        // One member → WHERE, unless its hierarchy is on an axis (MDX refuses a hierarchy in both
        // places, a sub-select accepts it). Several members → sub-select, nested one per hierarchy.
        var slicer = filters.Where(f => f.Members.Count == 1 && !axisHierarchies.Contains(f.Hierarchy)).ToList();
        var subSelects = filters.Except(slicer).ToList();

        string from = Bracket(model.Cube);
        foreach (var f in subSelects)
            from = $"(SELECT {Set(f.Members)} ON COLUMNS FROM {from})";
        sb.Append('\n').Append("FROM ").Append(from);

        if (slicer.Count > 0)
            sb.Append('\n').Append("WHERE (").Append(string.Join(", ", slicer.Select(f => f.Members[0]))).Append(')');

        return sb.ToString();
    }

    /// <summary>The set of one axis item, ranked if asked.</summary>
    private static string ItemSet(BuilderAxisItem item)
    {
        string set = item.Members is { Count: > 0 } members
            ? Set(members)
            : $"{item.Level}.Members";
        if (item.Rank is not { } rank) return set;
        return rank.TopCount is { } n
            ? $"TOPCOUNT({set}, {n}, {rank.Measure})"
            : $"ORDER({set}, {rank.Measure}, {(rank.Descending ? "BDESC" : "BASC")})";
    }

    private static string Set(IEnumerable<string> members) => "{" + string.Join(", ", members) + "}";

    private static void ValidateAxes(BuilderModel model)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (axis, items) in new[] { ("COLUMNS", model.Columns), ("ROWS", model.Rows) })
        {
            foreach (var item in items)
            {
                RequireUniqueName(item.Hierarchy);
                if (string.Equals(item.Hierarchy, "[Measures]", StringComparison.OrdinalIgnoreCase))
                    throw Invalid("Les mesures se placent dans la zone Mesures, pas sur un axe.");
                // The same hierarchy twice (same axis or both) is refused by the server; say it here.
                if (seen.TryGetValue(item.Hierarchy, out var other))
                    throw Invalid($"La hiérarchie {item.Hierarchy} est déjà sur {other} : une hiérarchie ne peut figurer qu'une fois.");
                seen[item.Hierarchy] = axis;

                if (item.Members is { Count: > 0 } members) foreach (var m in members) RequireUniqueName(m);
                else if (item.Level is null) throw Invalid($"{item.Hierarchy} : choisir un niveau ou des membres.");
                else RequireUniqueName(item.Level);

                if (item.Rank is { } rank)
                {
                    RequireUniqueName(rank.Measure);
                    if (rank.TopCount is <= 0) throw Invalid("Top N : N doit être supérieur à 0.");
                }
            }
        }
    }

    /// <summary>Several filters on the same hierarchy are one filter: their members are merged.</summary>
    private static List<BuilderFilter> MergeFilters(IReadOnlyList<BuilderFilter> filters)
    {
        var merged = new List<BuilderFilter>();
        foreach (var group in filters.GroupBy(f => f.Hierarchy, StringComparer.OrdinalIgnoreCase))
        {
            RequireUniqueName(group.Key);
            if (string.Equals(group.Key, "[Measures]", StringComparison.OrdinalIgnoreCase))
                throw Invalid("Les mesures se placent dans la zone Mesures, pas en filtre.");
            // Ordinal: two keys differing only by case are two members on a case-sensitive collation.
            var members = group.SelectMany(f => f.Members).Distinct(StringComparer.Ordinal).ToList();
            if (members.Count == 0) throw Invalid($"Filtre sur {group.Key} sans membre.");
            foreach (var m in members) RequireUniqueName(m);
            merged.Add(new BuilderFilter(group.First().Hierarchy, members));
        }
        return merged;
    }

    /// <summary>Bracketed MDX identifier: <c>]</c> doubled.</summary>
    public static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>
    /// Only bracketed segments, separated by dots, keys introduced by <c>&amp;</c>, composite keys
    /// chained (<c>&amp;[a]&amp;[b]</c>). Anything else — a function, a comma, a parenthesis — is refused.
    /// </summary>
    public static bool IsUniqueName(string? name) => name is not null && UniqueNameRegex().IsMatch(name);

    private static void RequireUniqueName(string? name)
    {
        if (!IsUniqueName(name)) throw Invalid($"Nom unique invalide : {name}");
    }

    private static MdxBuilderException Invalid(string message) => new(message);

    [GeneratedRegex(@"^\[(?:[^\]]|\]\])*\](?:\.&?\[(?:[^\]]|\]\])*\]|&\[(?:[^\]]|\]\])*\])*$")]
    private static partial Regex UniqueNameRegex();
}

/// <summary>The builder model cannot give valid MDX: a message for the user, not a crash.</summary>
public sealed class MdxBuilderException(string message) : InvalidOperationException(message);
