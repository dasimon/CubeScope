using System.Text.RegularExpressions;
using CubeScope.Core.Models;

namespace CubeScope.Core.Script;

/// <summary>
/// Découpage pragmatique du MDX Script (décision actée : tokenizer, pas d'AST).
/// Statements séparés par ';' hors chaînes/commentaires/parenthèses ; blocs
/// SCOPE…END SCOPE regroupés (imbrication gérée par comptage). ~95 % assumé.
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

    // Propriétés qui terminent l'expression d'un CREATE MEMBER (virgule niveau 0 + mot-clé)
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
            if (trimmed.StartsWith("CALCULATE", StringComparison.OrdinalIgnoreCase)) continue; // le CALCULATE; racine
            commands.Add(new ScriptCommand("Autre", trimmed.Split('\n')[0].Trim(), trimmed, startLine, section));
        }
        return commands;
    }

    /// <summary>"[Measures] . [X]" → "[Measures].[X]" (espaces autour des points).</summary>
    private static string Normalize(string name) =>
        Regex.Replace(name, @"\]\s*\.\s*\[", "].[");

    /// <summary>Retire les commentaires (lignes -- ou //, blocs /* */) en tête de statement.</summary>
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
    /// L'expression d'un CREATE MEMBER va jusqu'à la première virgule de niveau 0
    /// suivie d'une propriété connue (FORMAT_STRING = …), sinon tout le reste.
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
    /// Découpe en statements au ';' de niveau 0, en regroupant SCOPE…END SCOPE
    /// (imbrication comptée). Retourne aussi la ligne de départ (1-based).
    /// </summary>
    internal static IEnumerable<(string Text, int StartLine)> SplitStatements(string script)
    {
        var results = new List<(string, int)>();
        int stmtStart = 0, line = 1, stmtStartLine = 1, scopeDepth = 0;
        bool inString = false, inBracket = false, inLineComment = false, inBlockComment = false;
        char stringChar = '"';

        // Même peek-forward que celui utilisé après chaque ';' (voir plus bas) : le tout
        // premier statement doit lui aussi voir son stmtStartLine avancer au-delà des
        // éventuelles lignes blanches de tête du script, sinon ContentLine (qui soustrait
        // ces mêmes lignes blanches) part d'une valeur jamais ajustée et sous-évalue la ligne.
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

            // Suivi des SCOPE / END SCOPE (mots entiers, hors chaînes/commentaires)
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
                // Le début réel du prochain statement : sauter les sauts de ligne suivants
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
    /// Section (chemin de régions imbriquées) de chaque ligne, index 0 = ligne 1.
    /// La ligne #region ouvre la région ; la ligne #endregion n'en fait plus partie.
    /// Un #endregion sans #region est ignoré ; une région non fermée court jusqu'au bout.
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
    /// Ligne du premier contenu réel d'un statement (les commentaires/blancs de tête,
    /// dont les marqueurs de région, appartiennent au statement mais pas à son contenu).
    /// </summary>
    /// <remarks>
    /// <paramref name="startLine"/> pointe déjà sur la 1ʳᵉ ligne non blanche de <paramref name="raw"/>
    /// (<see cref="SplitStatements"/> saute les lignes blanches de tête en le calculant, sans pour
    /// autant retirer ces caractères de <paramref name="raw"/> lui-même). <see cref="StripLeadingComments"/>
    /// ne garantit d'être un suffixe que de <paramref name="raw"/> tel quel (pas de sa version
    /// tronquée : sans commentaire de tête elle renvoie <paramref name="raw"/> intact, avec ses
    /// éventuels blancs de tête). On recalcule donc d'abord la ligne réelle du tout premier
    /// caractère de <paramref name="raw"/> en soustrayant ses propres sauts de ligne blancs de tête,
    /// puis on recompte à partir de là sur <paramref name="raw"/> non tronqué.
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
