using System.Text.RegularExpressions;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Pasted lists of member keys (ISIN, fund codes…) for the builder. Pure functions: splitting
/// the pasted text, and turning a key into a member unique name under a level.
/// </summary>
public static partial class KeyList
{
    /// <summary>Hard cap of one resolution: beyond it, a pasted list is more likely a mistake.</summary>
    public const int MaxKeys = 5000;

    private static readonly char[] Separators = ['\r', '\n', ',', ';', '\t'];

    /// <summary>
    /// Splits on line breaks, commas, semicolons and tabs; trims each entry (zero-width spaces
    /// too, which survive a copy from a web page) and the double quotes of a CSV export; drops
    /// empty ones and duplicates (ordinal: two keys differing by case are two members on a
    /// case-sensitive collation), keeping the pasted order. No format conversion: a date key must
    /// be pasted as the server stores it (<c>2025-01-01T00:00:00</c> — any other form is not
    /// found, observed), numeric keys are converted by the server (<c>0042</c> finds 42, observed).
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var raw in text.Split(Separators))
        {
            string key = raw.Replace("\u200B", "").Replace("\uFEFF", "").Trim();
            if (key.Length >= 2 && key[0] == '"' && key[^1] == '"') key = key[1..^1].Replace("\"\"", "\"").Trim();
            if (key.Length > 0 && seen.Add(key)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// <c>{level}.&amp;[{key}]</c> with <c>]</c> doubled. A key already written as one or more
    /// bracketed parts (<c>&amp;[a]&amp;[b]</c>, a composite key) is appended as is. The result is
    /// checked like every builder name: a key cannot inject MDX.
    /// </summary>
    public static string MemberUniqueName(string level, string key)
    {
        if (!MdxQueryBuilder.IsUniqueName(level))
            throw new MdxBuilderException($"Nom unique de niveau invalide : {level}");
        string suffix = CompositeRegex().IsMatch(key) ? key : $"&[{key.Replace("]", "]]")}]";
        string name = $"{level}.{suffix}";
        if (!MdxQueryBuilder.IsUniqueName(name))
            throw new MdxBuilderException($"Clé invalide : {key}");
        return name;
    }

    [GeneratedRegex(@"^(?:&\[(?:[^\]]|\]\])*\])+$")]
    private static partial Regex CompositeRegex();
}
