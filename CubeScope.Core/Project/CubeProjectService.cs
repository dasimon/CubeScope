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
}
