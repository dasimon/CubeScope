using System.Text;
using System.Text.RegularExpressions;

namespace CubeScope.Core.Script;

/// <summary>Result of a rename: rewritten script + number of occurrences replaced.</summary>
public sealed record RenameResult(string NewScript, int Occurrences);

/// <summary>
/// Safe rename of a calculated member / named set in the MDX Script: rewrites the
/// definition AND every textual reference to its unique name. Reuses the
/// character-by-character scanning pattern of <see cref="ScriptParser.SplitStatements"/>
/// ('/"' strings, '//'/'--' line comments, '/* */' block comments,
/// '[bracket ids]' with ']]' escaping): strings and comments are copied as
/// is without looking for references in them. Outside strings/comments, each '[' starts
/// reading the longest chain of `[...]` segments joined by dots (whitespace
/// allowed around the dot, as in <see cref="ScriptParser"/>.Normalize) — comparison
/// and replacement apply to the WHOLE chain, never a single segment: so
/// [Measures].[Marge] never matches inside [Measures].[Marge Ratio] or
/// [Measures].[MargeBis].
/// </summary>
public static class MemberRenamer
{
    public static RenameResult Rename(string script, string oldUniqueName, string newUniqueName)
    {
        string normalizedOld = Normalize(oldUniqueName);
        var sb = new StringBuilder(script.Length);
        int count = 0;
        int i = 0;
        int n = script.Length;
        bool inString = false;
        char stringChar = '"';

        while (i < n)
        {
            char c = script[i];
            char next = i + 1 < n ? script[i + 1] : '\0';

            if (inString)
            {
                sb.Append(c);
                if (c == stringChar) inString = false;
                i++;
                continue;
            }

            if ((c == '/' && next == '/') || (c == '-' && next == '-'))
            {
                int nl = script.IndexOf('\n', i);
                int end = nl < 0 ? n : nl + 1;
                sb.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && next == '*')
            {
                int end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? n : end + 2;
                sb.Append(script, i, stop - i);
                i = stop;
                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                stringChar = c;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '[')
            {
                int chainStart = i;
                int chainEnd = ReadChain(script, i);
                string rawChain = script[chainStart..chainEnd];
                if (Normalize(rawChain) == normalizedOld)
                {
                    sb.Append(newUniqueName);
                    count++;
                }
                else
                {
                    sb.Append(rawChain);
                }
                i = chainEnd;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return new RenameResult(sb.ToString(), count);
    }

    /// <summary>"[Measures] . [X]" → "[Measures].[X]" (whitespace around the dots).</summary>
    private static string Normalize(string name) =>
        Regex.Replace(name, @"\]\s*\.\s*\[", "].[");

    /// <summary>
    /// Reads the longest chain of `[...]` segments joined by dots (whitespace allowed
    /// around the dot) starting at <paramref name="start"/> (which points to a '['). Handles
    /// the `]]` escape (a literal `]` inside a segment). Returns the index
    /// just after the last `]` of the chain.
    /// </summary>
    private static int ReadChain(string s, int start)
    {
        int n = s.Length;
        int i = start;
        while (true)
        {
            i++; // skip the opening '['
            while (i < n)
            {
                if (s[i] == ']')
                {
                    if (i + 1 < n && s[i + 1] == ']') { i += 2; continue; } // escaped ']]'
                    i++;
                    break;
                }
                i++;
            }

            int j = i;
            while (j < n && char.IsWhiteSpace(s[j])) j++;
            if (j < n && s[j] == '.')
            {
                int k = j + 1;
                while (k < n && char.IsWhiteSpace(s[k])) k++;
                if (k < n && s[k] == '[') { i = k; continue; }
            }
            break;
        }
        return i;
    }
}
