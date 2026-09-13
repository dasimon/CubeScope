using System.Text.RegularExpressions;
using CubeScope.Core.Models;

namespace CubeScope.Core.Script;

/// <summary>
/// Pragmatic splitting of the MDX Script (settled decision: tokenizer, no AST).
/// Statements separated by ';' outside strings/comments/parentheses; SCOPE…END SCOPE
/// blocks grouped together (nesting handled by counting). ~95% accepted.
/// </summary>
public static partial class ScriptParser
{
    [GeneratedRegex(@"^\s*CREATE\s+(HIDDEN\s+)?MEMBER\s+(?:CURRENTCUBE\s*\.\s*)?(?<name>(\[(?:[^\]]|\]\])+\]\s*\.\s*)*\[(?:[^\]]|\]\])+\])\s+AS\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateMember();

    [GeneratedRegex(@"^\s*CREATE\s+(HIDDEN\s+|DYNAMIC\s+|STATIC\s+|SESSION\s+)*SET\s+(?:CURRENTCUBE\s*\.\s*)?(?<name>(\[(?:[^\]]|\]\])+\]\s*\.\s*)*\[(?:[^\]]|\]\])+\])\s+AS\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateSet();

    [GeneratedRegex(@"^\s*(?://|--)\s*#region\b[ \t]*(?<name>.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RegionStart();

    [GeneratedRegex(@"^\s*(?://|--)\s*#endregion\b", RegexOptions.IgnoreCase)]
    private static partial Regex RegionEnd();

    // Properties that end the expression of a CREATE MEMBER (level-0 comma + keyword)
    private static readonly string[] MemberProperties =
    [
        "FORMAT_STRING", "VISIBLE", "DISPLAY_FOLDER", "ASSOCIATED_MEASURE_GROUP",
        "NON_EMPTY_BEHAVIOR", "SOLVE_ORDER", "FORE_COLOR", "BACK_COLOR", "FONT_FLAGS",
        "FONT_NAME", "FONT_SIZE", "LANGUAGE", "CAPTION",
    ];

    public static IReadOnlyList<ScriptCommand> Parse(string script)
    {
        var sections = SectionsPerLine(script);
        var commands = new List<ScriptCommand>();
        foreach (var (text, startLine) in SplitStatements(script))
        {
            string afterComments = StripLeadingComments(text);
            string trimmed = afterComments.Trim();
            if (trimmed.Length == 0) continue;
            int contentLine = ContentLine(text, afterComments, startLine);
            string? section = sections.Count == 0
                ? null
                : sections[Math.Clamp(contentLine, 1, sections.Count) - 1];

            var m = CreateMember().Match(trimmed);
            if (m.Success)
            {
                commands.Add(new ScriptCommand("CalculatedMember", Normalize(m.Groups["name"].Value),
                    ExtractMemberExpression(trimmed[(m.Index + m.Length)..]), startLine, section));
                continue;
            }
            var s = CreateSet().Match(trimmed);
            if (s.Success)
            {
                commands.Add(new ScriptCommand("NamedSet", Normalize(s.Groups["name"].Value),
                    trimmed[(s.Index + s.Length)..].Trim(), startLine, section));
                continue;
            }
            if (trimmed.StartsWith("SCOPE", StringComparison.OrdinalIgnoreCase))
            {
                string firstLine = trimmed.Split('\n')[0].Trim();
                commands.Add(new ScriptCommand("Scope", firstLine, trimmed, startLine, section));
                continue;
            }
            if (trimmed.StartsWith("CALCULATE", StringComparison.OrdinalIgnoreCase)) continue; // the root CALCULATE;
            commands.Add(new ScriptCommand("Autre", trimmed.Split('\n')[0].Trim(), trimmed, startLine, section));
        }
        return commands;
    }

    /// <summary>"[Measures] . [X]" → "[Measures].[X]" (whitespace around the dots).</summary>
    private static string Normalize(string name) =>
        Regex.Replace(name, @"\]\s*\.\s*\[", "].[");

    /// <summary>Removes the comments (-- or // lines, /* */ blocks) at the start of a statement.</summary>
    internal static string StripLeadingComments(string text)
    {
        string t = text;
        while (true)
        {
            string trimmed = t.TrimStart();
            if (trimmed.StartsWith("--") || trimmed.StartsWith("//"))
            {
                int nl = trimmed.IndexOf('\n');
                if (nl < 0) return "";
                t = trimmed[(nl + 1)..];
            }
            else if (trimmed.StartsWith("/*"))
            {
                int end = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) return "";
                t = trimmed[(end + 2)..];
            }
            else
            {
                return t;
            }
        }
    }

    /// <summary>
    /// The expression of a CREATE MEMBER runs up to the first level-0 comma
    /// followed by a known property (FORMAT_STRING = …), otherwise all the rest.
    /// </summary>
    internal static string ExtractMemberExpression(string afterAs)
    {
        int depth = 0;
        bool inString = false, inBracket = false;
        char stringChar = '"';
        for (int i = 0; i < afterAs.Length; i++)
        {
            char c = afterAs[i];
            if (inString) { if (c == stringChar) inString = false; continue; }
            if (inBracket) { if (c == ']') inBracket = false; continue; }
            switch (c)
            {
                case '"' or '\'': inString = true; stringChar = c; break;
                case '[': inBracket = true; break;
                case '(' or '{': depth++; break;
                case ')' or '}': depth--; break;
                case ',' when depth == 0:
                    string rest = afterAs[(i + 1)..].TrimStart();
                    if (MemberProperties.Any(p => rest.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        return afterAs[..i].Trim();
                    break;
            }
        }
        return afterAs.Trim();
    }

    /// <summary>
    /// Splits into statements at level-0 ';', grouping SCOPE…END SCOPE together
    /// (nesting counted). Also returns the start line (1-based).
    /// </summary>
    internal static IEnumerable<(string Text, int StartLine)> SplitStatements(string script)
    {
        var results = new List<(string, int)>();
        int stmtStart = 0, line = 1, stmtStartLine = 1, scopeDepth = 0;
        bool inString = false, inBracket = false, inLineComment = false, inBlockComment = false;
        char stringChar = '"';

        // Same peek-forward as the one used after each ';' (see below): the very
        // first statement must also have its stmtStartLine moved past any
        // leading blank lines of the script, otherwise ContentLine (which subtracts
        // those same blank lines) starts from a never-adjusted value and underestimates the line.
        for (int j = stmtStart; j < script.Length && char.IsWhiteSpace(script[j]); j++)
            if (script[j] == '\n') stmtStartLine++;

        for (int i = 0; i < script.Length; i++)
        {
            char c = script[i];
            char next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (c == '\n') { line++; inLineComment = false; }
            if (inLineComment) continue;
            if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
            if (inString) { if (c == stringChar) inString = false; continue; }
            if (inBracket) { if (c == ']') inBracket = false; continue; }

            switch (c)
            {
                case '/' when next == '/':
                case '-' when next == '-':
                    inLineComment = true; i++; continue;
                case '/' when next == '*':
                    inBlockComment = true; i++; continue;
                case '"' or '\'': inString = true; stringChar = c; continue;
                case '[': inBracket = true; continue;
            }

            // Tracking of SCOPE / END SCOPE (whole words, outside strings/comments)
            if (char.IsLetter(c) && (i == 0 || !char.IsLetterOrDigit(script[i - 1])))
            {
                if (IsWordAt(script, i, "SCOPE") && !IsWordAt(script, PrevWordStart(script, i), "END"))
                    scopeDepth++;
                else if (IsWordAt(script, i, "END") && IsNextWord(script, i + 3, "SCOPE"))
                    scopeDepth--;
            }

            if (c == ';' && scopeDepth == 0)
            {
                results.Add((script[stmtStart..i], stmtStartLine));
                stmtStart = i + 1;
                stmtStartLine = line;
                // The real start of the next statement: skip the following line breaks
                for (int j = stmtStart; j < script.Length && char.IsWhiteSpace(script[j]); j++)
                    if (script[j] == '\n') stmtStartLine++;
            }
        }
        if (stmtStart < script.Length && script[stmtStart..].Trim().Length > 0)
            results.Add((script[stmtStart..], stmtStartLine));
        return results;
    }

    private static bool IsWordAt(string s, int i, string word)
    {
        if (i < 0 || i + word.Length > s.Length) return false;
        if (!s.AsSpan(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)) return false;
        int end = i + word.Length;
        return end >= s.Length || !char.IsLetterOrDigit(s[end]);
    }

    private static bool IsNextWord(string s, int from, string word)
    {
        int i = from;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return IsWordAt(s, i, word);
    }

    private static int PrevWordStart(string s, int i)
    {
        int j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
        while (j >= 0 && char.IsLetterOrDigit(s[j])) j--;
        return j + 1;
    }

    /// <summary>
    /// Section (path of nested regions) of each line, index 0 = line 1.
    /// The #region line opens the region; the #endregion line is no longer part of it.
    /// An #endregion without #region is ignored; an unclosed region runs to the end.
    /// </summary>
    internal static IReadOnlyList<string?> SectionsPerLine(string script)
    {
        var lines = script.Split('\n');
        var stack = new Stack<string>();
        var result = new string?[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            var start = RegionStart().Match(lines[i]);
            if (start.Success)
            {
                string name = start.Groups["name"].Value.Trim();
                stack.Push(name.Length == 0 ? "#region" : name);
            }
            else if (RegionEnd().IsMatch(lines[i]) && stack.Count > 0)
            {
                stack.Pop();
            }
            result[i] = stack.Count == 0 ? null : string.Join(" / ", stack.Reverse());
        }
        return result;
    }

    /// <summary>
    /// Line of the first real content of a statement (the leading comments/blank lines,
    /// including region markers, belong to the statement but not to its content).
    /// </summary>
    /// <remarks>
    /// <paramref name="startLine"/> already points to the first non-blank line of <paramref name="raw"/>
    /// (<see cref="SplitStatements"/> skips the leading blank lines when computing it, without
    /// removing those characters from <paramref name="raw"/> itself). <see cref="StripLeadingComments"/>
    /// only guarantees to return a suffix of <paramref name="raw"/> as is (not of its trimmed
    /// version: with no leading comment it returns <paramref name="raw"/> intact, with its
    /// possible leading whitespace). So we first recompute the real line of the very first
    /// character of <paramref name="raw"/> by subtracting its own leading blank line breaks,
    /// then count again from there over the untrimmed <paramref name="raw"/>.
    /// </remarks>
    private static int ContentLine(string raw, string afterComments, int startLine)
    {
        int leadingBlankNewlines = 0;
        foreach (char c in raw)
        {
            if (!char.IsWhiteSpace(c)) break;
            if (c == '\n') leadingBlankNewlines++;
        }
        int line = startLine - leadingBlankNewlines;
        foreach (char c in raw.AsSpan(0, raw.Length - afterComments.Length))
            if (c == '\n') line++;
        foreach (char c in afterComments)
        {
            if (c == '\n') line++;
            else if (!char.IsWhiteSpace(c)) break;
        }
        return line;
    }
}
