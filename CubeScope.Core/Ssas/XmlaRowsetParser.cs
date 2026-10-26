using System.Xml;
using System.Xml.Linq;
using CubeScope.Core.Models;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Turns the value of an XMLA result into a grid. Observed on SSAS 17.0.25.218 (dev): a
/// <c>&lt;Statement&gt;</c> with a DMV query returns an XSD schema followed by <c>&lt;row&gt;</c>
/// elements in the <c>…:xml-analysis:rowset</c> namespace (a fragment, no single root); an MDX
/// statement returns an <c>…:mddataset</c> cellset, which belongs to the MDX editor.
/// </summary>
public static class XmlaRowsetParser
{
    private const string RowsetNs = "urn:schemas-microsoft-com:xml-analysis:rowset";
    private const string MddatasetNs = "urn:schemas-microsoft-com:xml-analysis:mddataset";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Rows kept for the grid. A DMV such as DISCOVER_OBJECT_MEMORY_USAGE can return a huge
    /// rowset; like the member lists elsewhere, the grid shows a capped part and says so.
    /// </summary>
    public const int MaxRows = 10_000;

    public static bool IsMultidimensional(string? value) => value?.Contains(MddatasetNs, StringComparison.Ordinal) == true;

    public static bool IsRowset(string? value) => value?.Contains(RowsetNs, StringComparison.Ordinal) == true;

    /// <summary>
    /// The rowset as a grid, or null when the value is not a rowset — or is one that cannot be
    /// read (the caller checks <see cref="IsRowset"/> to tell the user rather than show nothing).
    /// </summary>
    public static QueryResult? Parse(string? value, out bool truncated, long durationMs = 0)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(value) || !IsRowset(value)) return null;

        XElement wrapper;
        try { wrapper = XElement.Parse($"<r>{value}</r>"); }
        catch (XmlException) { return null; }

        var allRows = wrapper.Elements().Where(e => e.Name.LocalName == "row" && e.Name.NamespaceName == RowsetNs);
        var rows = allRows.Take(MaxRows + 1).ToList();
        if (rows.Count > MaxRows)
        {
            truncated = true;
            rows.RemoveAt(MaxRows);
        }

        // Columns in schema order (they exist even when no row came back), then any extra name.
        var names = new List<string>();
        var rowType = wrapper.Descendants(Xsd + "complexType").FirstOrDefault(c => (string?)c.Attribute("name") == "row");
        if (rowType is not null)
            names.AddRange(rowType.Descendants(Xsd + "element").Select(e => (string?)e.Attribute("name")).OfType<string>().Distinct());
        foreach (var row in rows)
            foreach (var cell in row.Elements())
                if (!names.Contains(cell.Name.LocalName)) names.Add(cell.Name.LocalName);

        // Field = c{i}: header names are XML-encoded (_x0020_…) and may clash with grid internals.
        var columns = names.Select((n, i) => new GridColumn($"c{i}", XmlConvert.DecodeName(n), i == 0)).ToList();
        var index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var grid = rows.Select(row =>
        {
            var d = new Dictionary<string, object?>();
            foreach (var cell in row.Elements())
            {
                string field = $"c{index[cell.Name.LocalName]}";
                if (d.ContainsKey(field)) continue; // a repeated element: the first one is shown
                d[field] = (string?)cell.Attribute(Xsi + "nil") == "true" ? null
                    : cell.HasElements ? cell.ToString(SaveOptions.DisableFormatting) : cell.Value;
            }
            return d;
        }).ToList();

        return new QueryResult(columns, grid, grid.Count * columns.Count, 2, durationMs);
    }
}
