using System.Xml.Linq;
using CubeScope.Core.Models;
using CubeScope.Core.Script;

namespace CubeScope.Core.Project;

/// <summary>
/// Reads/writes the MDX Script in an SSDT project .cube file (settled
/// decision: the project is the source of truth, never a diverging live edit).
/// Minimal round-trip: only the text of the MdxScript Command is rewritten, all
/// the rest of the XML document is preserved (LoadOptions.PreserveWhitespace).
/// v1: editing is supported only if the MdxScript has exactly one Command
/// (standard SSDT case); otherwise read-only.
/// </summary>
public sealed class CubeProjectService
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/analysisservices/2003/engine";

    public ProjectScript Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Fichier .cube introuvable ou chemin pointant sur un dossier : {path}");

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

    // .bak backup: only once per session (spec §3) — the service is a singleton.
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites the text of the single MdxScript Command in the .cube, exports the
    /// script as plain text (.mdxscript.mdx, readable Git diffs) and returns the
    /// CalculationProperties that became orphans (never deleted automatically).
    /// </summary>
    public IReadOnlyList<string> Save(string path, string fullText)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var (_, script) = FindScript(doc, path);
        // Same notion of "editable Command" as Load.CanEdit (CommandTexts): a
        // <Text> that is present but blank does not count as a real Command.
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
    /// Reads the MdxScript CalculationProperty elements (FormatString/DisplayFolder/Description
    /// of a calculated member or set). Never touches the disk.
    /// </summary>
    public IReadOnlyList<CalculationProp> GetCalculationProperties(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var (_, script) = FindScript(doc, path);
        return script.Element(Ns + "CalculationProperties")?
            .Elements(Ns + "CalculationProperty")
            .Select(p => new CalculationProp(
                p.Element(Ns + "CalculationReference")?.Value ?? "",
                p.Element(Ns + "FormatString")?.Value,
                p.Element(Ns + "DisplayFolder")?.Value,
                p.Element(Ns + "Description")?.Value))
            .ToList() ?? [];
    }

    /// <summary>
    /// Creates or updates the CalculationProperty of a calculated member/set (FormatString,
    /// DisplayFolder, Description). A null or empty value removes the matching child
    /// element if it exists; a non-empty value creates or updates it. Touches no
    /// other CalculationProperty nor the MdxScript Command — the
    /// rest of the document is preserved (LoadOptions.PreserveWhitespace).
    /// </summary>
    public void SaveCalculationProperty(
        string path, string reference, string? formatString, string? displayFolder, string? description)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var (_, script) = FindScript(doc, path);

        var container = script.Element(Ns + "CalculationProperties");
        if (container is null)
        {
            container = new XElement(Ns + "CalculationProperties");
            script.Add(container);
        }

        var prop = container.Elements(Ns + "CalculationProperty")
            .FirstOrDefault(p => p.Element(Ns + "CalculationReference")?.Value == reference);
        if (prop is null)
        {
            prop = new XElement(Ns + "CalculationProperty",
                new XElement(Ns + "CalculationReference", reference),
                new XElement(Ns + "CalculationType", "Member"));
            container.Add(prop);
        }

        SetOrRemoveChild(prop, Ns + "FormatString", formatString);
        SetOrRemoveChild(prop, Ns + "DisplayFolder", displayFolder);
        SetOrRemoveChild(prop, Ns + "Description", description);

        doc.Save(path);
    }

    /// <summary>Named child element: non-empty value → created/updated (appended at the end of
    /// the parent if missing, since SSAS does not validate the order of these elements); null or
    /// empty → removed if it exists.</summary>
    private static void SetOrRemoveChild(XElement parent, XName name, string? value)
    {
        var existing = parent.Element(name);
        if (string.IsNullOrEmpty(value))
        {
            existing?.Remove();
        }
        else if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            parent.Add(new XElement(name, value));
        }
    }

    /// <summary>
    /// CalculationReference with no matching CREATE MEMBER/SET in the script.
    /// Best effort: comparison on the normalized name, warning only.
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
