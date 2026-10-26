using System.Xml;
using System.Xml.Linq;

namespace CubeScope.Core.Ssas;

/// <summary>A command found in an XMLA script. <see cref="Detail"/>: the process type, if any.</summary>
public sealed record XmlaCommandInfo(string Name, string? Detail, bool OnDimension);

/// <summary>
/// The commands of an XMLA script, read by parsing the XML — never by searching the text: a
/// "Delete" in a comment or in an object description is not a command, a Delete in a Batch is.
/// The script that is sent is the parsed one (<see cref="Normalized"/>): what was checked is what runs.
/// </summary>
public sealed record XmlaAnalysis(IReadOnlyList<XmlaCommandInfo> Commands, string Normalized);

/// <summary>Pure analysis of an XMLA script before it is sent (console, production rule).</summary>
public static class XmlaScriptAnalyzer
{
    // XMLA engine commands (ASSL "Commands" reference) plus Statement (xml-analysis namespace).
    private static readonly HashSet<string> CommandNames = new(StringComparer.Ordinal)
    {
        "Alter", "Attach", "Backup", "BeginTransaction", "Cancel", "ClearCache", "CommitTransaction",
        "Create", "Delete", "DesignAggregations", "Detach", "ImageLoad", "ImageSave", "Insert", "Lock",
        "MergePartitions", "NotifyTableChange", "Process", "Restore", "RollbackTransaction",
        "SetPasswordEncryptionKey", "Statement", "Subscribe", "Synchronize", "Unlock", "Update",
        "UpdateCells",
    };

    // Elements that hold commands. Commands are looked for ONLY at these structural positions:
    // inside an ObjectDefinition, an element named like a command is data, not a command.
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal) { "Batch", "Parallel" };

    // Non-command children a Batch may carry (out-of-line bindings, error configuration).
    // Anything else inside a container is refused: fail-closed, an unknown element must not
    // slip past the production check (the server would execute what it knows).
    private static readonly HashSet<string> BatchSettings = new(StringComparer.Ordinal)
    {
        "Bindings", "DataSource", "DataSourceView", "ErrorConfiguration",
    };

    /// <summary>
    /// Refused on a production server whatever the confirmation. The owner's list (Delete,
    /// Restore, Detach, Synchronize), plus what destroys or replaces in the same way: ClearCache
    /// (dev-only rule of the State panel), MergePartitions (the source partitions are deleted),
    /// Attach and ImageLoad (restore-like). See also <see cref="ProductionRefusal"/>.
    /// </summary>
    private static readonly HashSet<string> ForbiddenInProduction = new(StringComparer.Ordinal)
    {
        "Delete", "Restore", "Detach", "Synchronize", "ClearCache", "MergePartitions", "Attach", "ImageLoad",
    };

    public static XmlaAnalysis Analyze(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new XmlaScriptException("Script XMLA vide.");
        XDocument doc;
        try { doc = XDocument.Parse(script); }
        catch (XmlException ex) { throw new XmlaScriptException($"XML invalide : {ex.Message}"); }

        var root = doc.Root!;
        string name = root.Name.LocalName;
        if (name == "Discover")
            throw new XmlaScriptException(
                "Discover n'est pas une commande : AMO n'exécute que des commandes (refusé par le serveur, observé). "
                + "Pour lire une DMV, utilisez <Statement>SELECT … FROM $SYSTEM.…</Statement>.");
        if (!Containers.Contains(name) && !CommandNames.Contains(name))
            throw new XmlaScriptException($"Élément racine non reconnu comme commande XMLA : <{name}>.");

        var commands = new List<XmlaCommandInfo>();
        Collect(root, commands);
        if (commands.Count == 0) throw new XmlaScriptException("Aucune commande dans le script.");
        return new XmlaAnalysis(commands, root.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>
    /// Names are matched by local name, whatever the namespace: a script without the engine
    /// namespace is refused by the server, but must not slip through the production check.
    /// </summary>
    private static void Collect(XElement element, List<XmlaCommandInfo> commands)
    {
        string name = element.Name.LocalName;
        if (Containers.Contains(name))
        {
            foreach (var child in element.Elements()) Collect(child, commands);
            return;
        }
        if (name == "Discover")
            throw new XmlaScriptException("Discover n'est pas une commande : il ne peut pas figurer dans un Batch.");
        if (BatchSettings.Contains(name)) return;
        if (!CommandNames.Contains(name))
            throw new XmlaScriptException($"Élément non reconnu dans un {element.Parent?.Name.LocalName} : <{name}>.");

        string? detail = null;
        bool onDimension = false;
        if (name == "Process")
        {
            detail = Child(element, "Type")?.Value.Trim();
            // Any object carrying a DimensionID counts as a dimension, cube dimensions included:
            // fail-closed (whether processing one cascades is not established).
            onDimension = Child(element, "Object") is { } obj && Child(obj, "DimensionID") is not null;
        }
        else if (name is "Create" or "Alter")
        {
            // A whole database redeployed or overwritten: the script-deployment rule's reason.
            detail = Child(element, "ObjectDefinition")?.Elements().FirstOrDefault()?.Name.LocalName;
        }
        commands.Add(new XmlaCommandInfo(name, detail, onDimension));
    }

    private static XElement? Child(XElement e, string localName)
        => e.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    /// <summary>
    /// What production refuses whatever the confirmation, or null: the commands of
    /// <see cref="ForbiddenInProduction"/>; Create or Alter of a whole Database; ProcessClear on any
    /// object (owner's decision); ProcessFull or ProcessData on a dimension (same rule as the State
    /// panel). Accepted limit: a &lt;Statement&gt; is opaque — an MDX/DMX statement that writes
    /// (UPDATE CUBE, DROP MINING STRUCTURE) only needs the confirmation.
    /// </summary>
    public static string? ProductionRefusal(XmlaAnalysis analysis)
    {
        foreach (var c in analysis.Commands)
        {
            if (ForbiddenInProduction.Contains(c.Name))
                return $"Refusé en production : la commande {c.Name} n'est autorisée que sur un serveur de développement.";
            if (c.Name is "Create" or "Alter" && c.Detail == "Database")
                return $"Refusé en production : {c.Name} d'une base entière la redéploie ou l'écrase.";
            if (c.Name == "Process" && c.Detail == "ProcessClear")
                return "Refusé en production : ProcessClear vide l'objet et le rend inaccessible.";
            if (c.Name == "Process" && c.OnDimension && c.Detail is "ProcessFull" or "ProcessData")
                return $"Refusé en production : {c.Detail} sur une dimension rend non traités les groupes de mesures qui l'utilisent.";
        }
        return null;
    }
}

/// <summary>The script cannot be sent (invalid XML, not a command): refused before any connection.</summary>
public sealed class XmlaScriptException(string message) : InvalidOperationException(message);
