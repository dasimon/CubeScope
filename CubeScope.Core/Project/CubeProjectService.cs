using System.Security.Cryptography;
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
/// Every entry point validates its path first (LocalPathGuard: local .cube file only).
/// </summary>
public sealed class CubeProjectService
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/analysisservices/2003/engine";

    public ProjectScript Load(string path)
    {
        path = LocalPathGuard.EnsureLocalCubeFile(path);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Fichier .cube introuvable ou chemin pointant sur un dossier : {path}");

        var (doc, hash) = ReadDocument(path);
        var (cube, script) = FindScript(doc, path);
        string cubeName = cube.Element(Ns + "Name")?.Value
            ?? System.IO.Path.GetFileNameWithoutExtension(path);
        var texts = CommandTexts(script);
        string fullText = string.Join("\n\n", texts);
        bool canEdit = texts.Count == 1;
        return new ProjectScript(path, cubeName, fullText, ScriptParser.Parse(fullText), canEdit,
            canEdit ? null : $"MdxScript à {texts.Count} Command — édition non supportée (v1, cas SSDT standard = 1).",
            hash);
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

    /// <summary>
    /// Parses the file from the SAME bytes that are hashed: the hash handed to the UI
    /// describes exactly the content it was shown, never a later version.
    /// </summary>
    private static (XDocument Doc, string Hash) ReadDocument(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        return (XDocument.Load(stream, LoadOptions.PreserveWhitespace), HashOf(bytes));
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Writes next to the target then swaps: a crash or a full disk mid-write leaves the
    /// previous file intact instead of a truncated .cube (the SSDT project's source of truth).
    /// </summary>
    private static void WriteAtomically(string path, Action<string> write)
    {
        string tmp = path + ".tmp";
        try
        {
            write(tmp);
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // .bak backup: only once per session (spec §3) — the service is a singleton.
    // Only touched under _gate.
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);

    // Serializes every read-modify-write of a .cube (Save, SaveCalculationProperty): two
    // concurrent requests would otherwise each rewrite the file from the same old version,
    // and the second would silently drop the first one's change.
    private readonly object _gate = new();

    /// <summary>
    /// Rewrites the text of the single MdxScript Command in the .cube, exports the
    /// script as plain text (.mdxscript.mdx, readable Git diffs) and returns the
    /// CalculationProperties that became orphans (never deleted automatically), with the
    /// new ContentHash of the file.
    /// <paramref name="expectedHash"/> (optional) = the ContentHash returned by Load: if the
    /// file changed on disk since then (SSDT, Git checkout…), nothing is written and
    /// <see cref="ProjectConflictException"/> is thrown — the editor's text would otherwise
    /// overwrite that change without a word.
    /// </summary>
    public ProjectSaveResult Save(string path, string fullText, string? expectedHash = null)
    {
        path = LocalPathGuard.EnsureLocalCubeFile(path);
        lock (_gate)
        {
            var (doc, hash) = ReadDocument(path);
            if (expectedHash is not null && !string.Equals(expectedHash, hash, StringComparison.OrdinalIgnoreCase))
                throw new ProjectConflictException(
                    $"The .cube file was modified outside CubeScope since it was opened: {path}");

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
            WriteAtomically(path, doc.Save);

            string mdxPath = System.IO.Path.ChangeExtension(path, ".mdxscript.mdx");
            WriteAtomically(mdxPath, tmp => File.WriteAllText(tmp, fullText));

            return new ProjectSaveResult(
                OrphanCalculationProperties(script, fullText), HashOf(File.ReadAllBytes(path)));
        }
    }

    /// <summary>
    /// Reads the MdxScript CalculationProperty elements (FormatString/DisplayFolder/Description
    /// of a calculated member or set). Never touches the disk.
    /// </summary>
    public IReadOnlyList<CalculationProp> GetCalculationProperties(string path)
    {
        path = LocalPathGuard.EnsureLocalCubeFile(path);
        var (doc, _) = ReadDocument(path);
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
    /// Returns the new ContentHash of the file: the editor must adopt it, otherwise its next
    /// Save would take this very write for an external modification.
    /// </summary>
    public string SaveCalculationProperty(
        string path, string reference, string? formatString, string? displayFolder, string? description)
    {
        path = LocalPathGuard.EnsureLocalCubeFile(path);
        lock (_gate)
        {
            var (doc, _) = ReadDocument(path);
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

            WriteAtomically(path, doc.Save);
            return HashOf(File.ReadAllBytes(path));
        }
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

/// <summary>The .cube changed on disk since the editor loaded it (HTTP 409 on the API side).</summary>
public sealed class ProjectConflictException(string message) : InvalidOperationException(message);
