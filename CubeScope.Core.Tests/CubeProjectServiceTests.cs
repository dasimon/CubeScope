using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

public class CubeProjectServiceTests : IDisposable
{
    // Minimal but faithful skeleton of an SSDT .cube: ASSL 2003/engine namespace,
    // designer Annotations, an MdxScript with 1 Command + CalculationProperties.
    private const string SampleCube = """
        <?xml version="1.0" encoding="utf-8"?>
        <Cube xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:ddl2="http://schemas.microsoft.com/analysisservices/2003/engine/2" xmlns:ddl2_2="http://schemas.microsoft.com/analysisservices/2003/engine/2/2" xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
          <ID>CubeDemo</ID>
          <Name>CubeDemo</Name>
          <Annotations>
            <Annotation>
              <Name>http://schemas.microsoft.com/DataWarehouse/Designer/1.0:DiagramLayout</Name>
            </Annotation>
          </Annotations>
          <MdxScripts>
            <MdxScript>
              <ID>MdxScript</ID>
              <Name>MdxScript</Name>
              <Commands>
                <Command>
                  <Text>CALCULATE;

        // #region Rentabilité
        CREATE MEMBER CURRENTCUBE.[Measures].[Marge]
         AS [Measures].[CA] - [Measures].[Coûts],
        VISIBLE = 1;
        // #endregion</Text>
                </Command>
              </Commands>
              <CalculationProperties>
                <CalculationProperty>
                  <CalculationReference>[Measures].[Marge]</CalculationReference>
                  <CalculationType>Member</CalculationType>
                  <FormatString>'#,##0.00'</FormatString>
                </CalculationProperty>
                <CalculationProperty>
                  <CalculationReference>[Measures].[Disparu]</CalculationReference>
                  <CalculationType>Member</CalculationType>
                </CalculationProperty>
              </CalculationProperties>
            </MdxScript>
          </MdxScripts>
        </Cube>
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("cubescope-test-").FullName;
    private string WriteFixture(string content, string name = "CubeDemo.cube")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_ReadsNameScriptAndSections()
    {
        var svc = new CubeProjectService();
        var p = svc.Load(WriteFixture(SampleCube));
        Assert.Equal("CubeDemo", p.CubeName);
        Assert.True(p.CanEdit);
        Assert.Null(p.ReadOnlyReason);
        Assert.Contains("CREATE MEMBER CURRENTCUBE.[Measures].[Marge]", p.FullText);
        var marge = p.Commands.Single(c => c.Name == "[Measures].[Marge]");
        Assert.Equal("Rentabilité", marge.Section);
    }

    [Fact]
    public void Load_TwoCommands_IsReadOnly()
    {
        string twoCommands = SampleCube.Replace("</Commands>", """
                <Command>
                  <Text>CREATE SET CURRENTCUBE.[Deuxième] AS [D].[H].Members;</Text>
                </Command>
              </Commands>
            """);
        var p = new CubeProjectService().Load(WriteFixture(twoCommands));
        Assert.False(p.CanEdit);
        Assert.NotNull(p.ReadOnlyReason);
        Assert.Contains("[Deuxième]", p.FullText); // everything is visible, even read-only
    }

    [Fact]
    public void Load_NoMdxScript_Throws()
    {
        string noScript = SampleCube[..SampleCube.IndexOf("<MdxScripts>", StringComparison.Ordinal)] + "</Cube>";
        Assert.Throws<InvalidOperationException>(() => new CubeProjectService().Load(WriteFixture(noScript)));
    }

    private const string NewScript = """
        CALCULATE;

        // #region Rentabilité
        CREATE MEMBER CURRENTCUBE.[Measures].[Marge]
         AS [Measures].[CA] - [Measures].[Coûts] - [Measures].[Frais],
        VISIBLE = 1;
        // #endregion
        """;

    // XDocument normalizes line endings to LF when parsing (XML 1.0 spec). The contract under
    // test is preserving the CONTENT, not the CRLFs: compare EOL-normalized — otherwise the
    // test breaks depending on the machine's core.autocrlf (CRLF source vs LF round-trip).
    private static string NoCrlf(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void Save_RoundTrip_PreservesRestOfDocument()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        svc.Save(path, NewScript);

        var reloaded = svc.Load(path);
        Assert.Equal(NoCrlf(NewScript), NoCrlf(reloaded.FullText));
        // The rest of the document is intact (designer annotations, calculation properties)
        string xml = File.ReadAllText(path);
        Assert.Contains("DiagramLayout", xml);
        Assert.Contains("<FormatString>'#,##0.00'</FormatString>", xml);
    }

    [Fact]
    public void Save_CreatesBackupOncePerSession_AndExportsMdx()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        svc.Save(path, NewScript);

        string bak = path + ".bak";
        Assert.True(File.Exists(bak));
        Assert.Contains("[Measures].[CA] - [Measures].[Coûts],", File.ReadAllText(bak)); // original text

        svc.Save(path, NewScript + "\n-- v2");
        Assert.Contains("[Measures].[CA] - [Measures].[Coûts],", File.ReadAllText(bak)); // .bak NOT overwritten

        string mdx = Path.Combine(_dir, "CubeDemo.mdxscript.mdx");
        Assert.True(File.Exists(mdx));
        Assert.EndsWith("-- v2", File.ReadAllText(mdx).TrimEnd());
    }

    [Fact]
    public void Save_ReportsOrphanCalculationProperties()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        // NewScript no longer defines [Measures].[Disparu] (which has a CalculationProperty)
        var warnings = svc.Save(path, NewScript).Warnings;
        Assert.Contains(warnings, w => w.Contains("[Measures].[Disparu]"));
        Assert.DoesNotContain(warnings, w => w.Contains("[Measures].[Marge]"));
    }

    [Fact]
    public void Save_WhitespaceOnlySecondCommand_MatchesLoadCanEditAndSucceeds()
    {
        // A Command whose <Text> is present but blank must NOT count as a 2nd "real"
        // Command — Load.CanEdit and Save must agree (bug observed: Load.CanEdit=true
        // but Save still threw, see CommandTexts, which filters out blanks, vs the old
        // Save filter, which only tested whether <Text> was present).
        string withBlankCommand = SampleCube.Replace("</Commands>", """
                <Command>
                  <Text>   </Text>
                </Command>
              </Commands>
            """);
        string path = WriteFixture(withBlankCommand, "AvecCommandeVide.cube");
        var svc = new CubeProjectService();

        var loaded = svc.Load(path);
        Assert.True(loaded.CanEdit);

        svc.Save(path, NewScript); // must not throw

        var reloaded = svc.Load(path);
        Assert.Equal(NoCrlf(NewScript), NoCrlf(reloaded.FullText));
        string xml = File.ReadAllText(path);
        Assert.Contains("DiagramLayout", xml);
        Assert.Contains("<FormatString>'#,##0.00'</FormatString>", xml);
    }

    [Fact]
    public void Save_TwoCommands_Throws()
    {
        string twoCommands = SampleCube.Replace("</Commands>", """
                <Command>
                  <Text>CREATE SET CURRENTCUBE.[Deuxième] AS [D].[H].Members;</Text>
                </Command>
              </Commands>
            """);
        string path = WriteFixture(twoCommands, "Deux.cube");
        Assert.Throws<InvalidOperationException>(() => new CubeProjectService().Save(path, "CALCULATE;"));
    }

    [Fact]
    public void Load_Directory_ThrowsClearMessage()
    {
        // A folder named like a .cube file: passes the path guard, then must fail with a clear message.
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "Folder.cube")).FullName;
        var ex = Assert.Throws<InvalidOperationException>(() => new CubeProjectService().Load(folder));
        Assert.Contains("introuvable", ex.Message);
    }

    [Fact]
    public void Load_RefusesANonCubeFile()
        => Assert.Throws<InvalidOperationException>(() =>
            new CubeProjectService().Load(WriteFixture(SampleCube, "CubeDemo.xml")));

    [Fact]
    public void Load_ReturnsAContentHash_StableForTheSameContent()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        string hash = svc.Load(path).ContentHash;
        Assert.False(string.IsNullOrEmpty(hash));
        Assert.Equal(hash, svc.Load(path).ContentHash);
    }

    [Fact]
    public void Save_WithTheLoadedHash_Succeeds_AndReturnsTheNewHash()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        string loaded = svc.Load(path).ContentHash;

        var saved = svc.Save(path, NewScript, loaded);

        Assert.NotEqual(loaded, saved.ContentHash);
        Assert.Equal(svc.Load(path).ContentHash, saved.ContentHash);
        // The returned hash chains into the next save.
        svc.Save(path, NewScript + "\n-- v2", saved.ContentHash);
    }

    [Fact]
    public void Save_AfterAnExternalModification_ThrowsConflict_AndWritesNothing()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        string loaded = svc.Load(path).ContentHash;

        // Edited in SSDT (or a Git checkout) while the editor had the file open.
        string external = SampleCube.Replace("[Measures].[CA] - [Measures].[Coûts],", "[Measures].[CA] * 2,");
        File.WriteAllText(path, external);

        Assert.Throws<ProjectConflictException>(() => svc.Save(path, NewScript, loaded));
        Assert.Equal(external, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(Path.Combine(_dir, "CubeDemo.mdxscript.mdx")));
    }

    [Fact]
    public void Save_WithoutExpectedHash_StillOverwrites()
    {
        // expectedHash is optional: an explicit "overwrite anyway" after a conflict.
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        svc.Load(path);
        File.WriteAllText(path, SampleCube.Replace("<Name>CubeDemo</Name>", "<Name>Autre</Name>"));

        svc.Save(path, NewScript);

        Assert.Equal(NoCrlf(NewScript), NoCrlf(svc.Load(path).FullText));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        svc.Save(path, NewScript);
        svc.SaveCalculationProperty(path, "[Measures].[Marge]", "'0.0'", null, null);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void SaveCalculationProperty_ReturnsTheHashTheEditorMustAdopt()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        string loaded = svc.Load(path).ContentHash;

        string afterProps = svc.SaveCalculationProperty(path, "[Measures].[Marge]", "'0.0'", null, null);

        Assert.Equal(svc.Load(path).ContentHash, afterProps);
        // Its own write must not read as an external change for the next script save…
        svc.Save(path, NewScript, afterProps);
        // …whereas the hash from before it does.
        Assert.Throws<ProjectConflictException>(() => svc.Save(path, NewScript, loaded));
    }

    [Fact]
    public void Concurrent_writes_are_serialized_and_none_is_lost()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        var refs = Enumerable.Range(0, 12).Select(i => $"[Measures].[M{i}]").ToList();

        Parallel.ForEach(refs, r => svc.SaveCalculationProperty(path, r, "'0'", null, null));

        var saved = svc.GetCalculationProperties(path).Select(p => p.Reference).ToHashSet();
        Assert.All(refs, r => Assert.Contains(r, saved));
    }
}
