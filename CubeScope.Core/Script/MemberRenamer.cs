using System.Text;
using System.Text.RegularExpressions;

namespace CubeScope.Core.Script;

/// <summary>Résultat d'un renommage : script réécrit + nombre d'occurrences remplacées.</summary>
public sealed record RenameResult(string NewScript, int Occurrences);

/// <summary>
/// Renommage sûr d'un membre calculé / set nommé dans le MDX Script : réécrit la
/// définition ET toutes les références textuelles à son unique name. Réutilise le
/// motif de balayage caractère par caractère de <see cref="ScriptParser.SplitStatements"/>
/// ('/"' chaînes, '//'/'--' commentaires de ligne, '/* */' commentaires de bloc,
/// '[bracket ids]' avec échappement ']]') : chaînes et commentaires sont recopiés tels
/// quels sans y chercher de référence. Hors chaîne/commentaire, chaque '[' amorce la
/// lecture de la chaîne maximale de segments `[...]` reliés par des points (espaces
/// tolérées autour du point, comme <see cref="ScriptParser"/>.Normalize) — la comparaison
/// et le remplacement portent sur la chaîne ENTIÈRE, jamais un segment isolé : ainsi
/// [Measures].[Marge] ne matche jamais à l'intérieur de [Measures].[Marge Ratio] ni de
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

    /// <summary>"[Measures] . [X]" → "[Measures].[X]" (espaces autour des points).</summary>
    private static string Normalize(string name) =>
        Regex.Replace(name, @"\]\s*\.\s*\[", "].[");

    /// <summary>
    /// Lit la chaîne maximale de segments `[...]` reliés par des points (espaces tolérées
    /// autour du point) à partir de <paramref name="start"/> (qui pointe sur un '['). Gère
    /// l'échappement `]]` (un `]` littéral à l'intérieur d'un segment). Retourne l'index
    /// juste après le dernier `]` de la chaîne.
    /// </summary>
    private static int ReadChain(string s, int start)
    {
        int n = s.Length;
        int i = start;
        while (true)
        {
            i++; // saute le '[' d'ouverture
            while (i < n)
            {
                if (s[i] == ']')
                {
                    if (i + 1 < n && s[i + 1] == ']') { i += 2; continue; } // ']]' échappé
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
