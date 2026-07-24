using System.Xml.Linq;
using CubeScope.Core.Models;
using CubeScope.Core.Script;

namespace CubeScope.Core.Project;

/// <summary>
/// Lecture/écriture du MDX Script dans un fichier .cube de projet SSDT (décision
/// actée : la source de vérité est le projet, jamais d'édition live divergente).
/// Round-trip minimal : seul le texte de la Command du MdxScript est réécrit, tout
/// le reste du document XML est préservé (LoadOptions.PreserveWhitespace).
/// v1 : édition supportée uniquement si le MdxScript a exactement une Command
/// (cas SSDT standard) ; sinon lecture seule.
/// </summary>
public sealed class CubeProjectService
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/analysisservices/2003/engine";

    public ProjectScript Load(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var (cube, script) = FindScript(doc, path);
        string cubeName = cube.Element(Ns + "Name")?.Value
            ?? System.IO.Path.GetFileNameWithoutExtension(path);
        var texts = CommandTexts(script);
        string fullText = string.Join("\n\n", texts);
        bool canEdit = texts.Count == 1;
        return new ProjectScript(path, cubeName, fullText, ScriptParser.Parse(fullText), canEdit,
            canEdit ? null : $"MdxScript à {texts.Count} Command — édition non supportée (v1, cas SSDT standard = 1).");
    }

    internal static (XElement Cube, XElement Script) FindScript(XDocument doc, string path)
    {
        var cube = doc.Root ?? throw new InvalidOperationException($"Fichier .cube vide : {path}");
        var script = cube.Element(Ns + "MdxScripts")?.Element(Ns + "MdxScript")
            ?? throw new InvalidOperationException($"Pas de MdxScript dans ce .cube : {path}");
        return (cube, script);
    }

    internal static List<string> CommandTexts(XElement script) =>
        script.Element(Ns + "Commands")?.Elements(Ns + "Command")
            .Select(c => c.Element(Ns + "Text")?.Value)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList() ?? [];

    // Backup .bak : une seule fois par session (spec §3) — le service est un singleton.
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Réécrit le texte de la Command unique du MdxScript dans le .cube, exporte le
    /// script en clair (.mdxscript.mdx, diffs Git lisibles) et retourne les
    /// CalculationProperties devenues orphelines (jamais supprimées automatiquement).
    /// </summary>
    public IReadOnlyList<string> Save(string path, string fullText)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var (_, script) = FindScript(doc, path);
        // Même notion de "Command éditable" que Load.CanEdit (CommandTexts) : un
        // <Text> présent mais blanc ne compte pas comme une Command réelle.
        var commands = script.Element(Ns + "Commands")?.Elements(Ns + "Command")
            .Where(c => !string.IsNullOrWhiteSpace(c.Element(Ns + "Text")?.Value)).ToList() ?? [];
        if (commands.Count != 1)
            throw new InvalidOperationException(
                $"Édition non supportée : le MdxScript a {commands.Count} Command (v1 = exactement 1).");

        if (_backedUp.Add(path))
            File.Copy(path, path + ".bak", overwrite: true);

        commands[0].Element(Ns + "Text")!.Value = fullText;
        doc.Save(path);

        string mdxPath = System.IO.Path.ChangeExtension(path, ".mdxscript.mdx");
        File.WriteAllText(mdxPath, fullText);

        return OrphanCalculationProperties(script, fullText);
    }

    /// <summary>
    /// CalculationReference sans CREATE MEMBER/SET correspondant dans le script.
    /// Best effort : comparaison sur le nom normalisé, avertissement seulement.
    /// </summary>
    internal static IReadOnlyList<string> OrphanCalculationProperties(XElement script, string fullText)
    {
        var names = ScriptParser.Parse(fullText)
            .Where(c => c.Kind is "CalculatedMember" or "NamedSet")
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return script.Element(Ns + "CalculationProperties")?
            .Elements(Ns + "CalculationProperty")
            .Select(p => p.Element(Ns + "CalculationReference")?.Value)
            .OfType<string>()
            .Where(r => !names.Contains(r))
            .Select(r => $"CalculationProperty orpheline (membre absent du script) : {r}")
            .ToList() ?? [];
    }
}
