using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using Microsoft.Data.Sqlite;

namespace CubeScope.Core.Tests;

public class XmlaRowsetParserTests
{
    // Shape observed on the dev server for <Statement>SELECT … FROM $SYSTEM.DBSCHEMA_CATALOGS</Statement>
    // (schema shortened): XSD schema then <row> elements, no single root.
    private const string Rowset = """
        <xsd:schema targetNamespace="urn:schemas-microsoft-com:xml-analysis:rowset" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns="urn:schemas-microsoft-com:xml-analysis:rowset" xmlns:sql="urn:schemas-microsoft-com:xml-sql" elementFormDefault="qualified"><xsd:element name="root"><xsd:complexType><xsd:sequence minOccurs="0" maxOccurs="unbounded"><xsd:element name="row" type="row" /></xsd:sequence></xsd:complexType></xsd:element><xsd:complexType name="row"><xsd:sequence><xsd:element sql:field="CATALOG_NAME" name="CATALOG_NAME" type="xsd:string" minOccurs="0" /><xsd:element sql:field="DATE_MODIFIED" name="DATE_MODIFIED" type="xsd:dateTime" minOccurs="0" /><xsd:element name="A_x0020_B" type="xsd:string" minOccurs="0" /></xsd:sequence></xsd:complexType></xsd:schema><row xmlns="urn:schemas-microsoft-com:xml-analysis:rowset"><CATALOG_NAME>Ratios</CATALOG_NAME><DATE_MODIFIED>2026-10-26T13:52:21.2</DATE_MODIFIED></row><row xmlns="urn:schemas-microsoft-com:xml-analysis:rowset"><CATALOG_NAME>Autre</CATALOG_NAME><A_x0020_B>x</A_x0020_B></row>
        """;

    [Fact]
    public void Rowset_dmv_en_grille()
    {
        var r = XmlaRowsetParser.Parse(Rowset, out bool truncated)!;
        Assert.False(truncated);
        Assert.Equal(["CATALOG_NAME", "DATE_MODIFIED", "A B"], r.Columns.Select(c => c.Header));
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal("Ratios", r.Rows[0]["c0"]);
        Assert.Equal("2026-10-26T13:52:21.2", r.Rows[0]["c1"]);
        Assert.False(r.Rows[0].ContainsKey("c2")); // absent column = empty cell
        Assert.Equal("x", r.Rows[1]["c2"]);
    }

    [Fact]
    public void Rowset_vide_garde_ses_colonnes()
    {
        var schemaOnly = Rowset[..(Rowset.IndexOf("</xsd:schema>", StringComparison.Ordinal) + "</xsd:schema>".Length)];
        var r = XmlaRowsetParser.Parse(schemaOnly, out _)!;
        Assert.Equal(3, r.Columns.Count);
        Assert.Empty(r.Rows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<root xmlns=\"urn:schemas-microsoft-com:xml-analysis:mddataset\"/>")]
    public void Pas_un_rowset(string? value) => Assert.Null(XmlaRowsetParser.Parse(value, out _));

    [Fact]
    public void Plafond_de_lignes_et_valeur_nil()
    {
        const string ns = "urn:schemas-microsoft-com:xml-analysis:rowset";
        var rows = string.Concat(Enumerable.Range(0, XmlaRowsetParser.MaxRows + 5)
            .Select(i => $"<row xmlns=\"{ns}\"><N>{i}</N><V xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/></row>"));
        var r = XmlaRowsetParser.Parse(rows, out bool truncated)!;
        Assert.True(truncated);
        Assert.Equal(XmlaRowsetParser.MaxRows, r.Rows.Count);
        Assert.Null(r.Rows[0]["c1"]); // xsi:nil is null, not ""
    }

    [Fact]
    public void Rowset_illisible_reconnu_mais_non_lu()
    {
        const string broken = "<row xmlns=\"urn:schemas-microsoft-com:xml-analysis:rowset\"><N>1</row>";
        Assert.True(XmlaRowsetParser.IsRowset(broken));
        Assert.Null(XmlaRowsetParser.Parse(broken, out _));
    }

    [Fact]
    public void Cellset_multidimensionnel_reconnu()
    {
        Assert.True(XmlaRowsetParser.IsMultidimensional("<xs:schema targetNamespace=\"urn:schemas-microsoft-com:xml-analysis:mddataset\"/>"));
        Assert.False(XmlaRowsetParser.IsMultidimensional(Rowset));
    }

    [Fact]
    public void Migration_v12_vers_v13_snippets_existants_en_mdx()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs-v13-{Guid.NewGuid():N}.db");
        using (var store = new StateStore(path)) store.AddSnippet("Ancien", "SELECT {} ON 0 FROM [C]");
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            // What a v12 database looks like: no XmlaLog, no Language column.
            cmd.CommandText = """
                DROP TABLE XmlaLog;
                CREATE TABLE Snippet2 (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Mdx TEXT NOT NULL, CreatedUtc TEXT NOT NULL);
                INSERT INTO Snippet2 (Id, Name, Mdx, CreatedUtc) SELECT Id, Name, Mdx, CreatedUtc FROM Snippet;
                DROP TABLE Snippet; ALTER TABLE Snippet2 RENAME TO Snippet;
                PRAGMA user_version = 12;
                """;
            cmd.ExecuteNonQuery();
        }
        using var migrated = new StateStore(path);
        Assert.Equal("mdx", Assert.Single(migrated.GetSnippets()).Language);
        migrated.AddSnippet("Sessions", "<Statement/>", "xmla");
        Assert.Equal(["mdx", "xmla"], migrated.GetSnippets().OrderBy(s => s.Id).Select(s => s.Language));
        Assert.Throws<ArgumentException>(() => migrated.AddSnippet("X", "x", "dax"));
        migrated.AddXmlaLog("SRV", null, "<Statement/>", false, 3, true, null);
        Assert.Single(migrated.GetXmlaLog());
    }
}
