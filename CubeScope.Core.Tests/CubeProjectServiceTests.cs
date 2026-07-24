using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

public class CubeProjectServiceTests : IDisposable
{
    // Squelette minimal mais fidèle d'un .cube SSDT : namespace ASSL 2003/engine,
    // Annotations du designer, un MdxScript avec 1 Command + CalculationProperties.
    private const string SampleCube = """
        <?xml version="1.0" encoding="utf-8"?>
        <Cube xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:ddl2="http://schemas.microsoft.com/analysisservices/2003/engine/2" xmlns:ddl2_2="http://schemas.microsoft.com/analysisservices/2003/engine/2/2" xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
          <ID>Portefeuilles</ID>
          <Name>Portefeuilles</Name>
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
    private string WriteFixture(string content, string name = "Portefeuilles.cube")
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
        Assert.Equal("Portefeuilles", p.CubeName);
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
        Assert.Contains("[Deuxième]", p.FullText); // tout est visible, même en lecture seule
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

    // XDocument normalise les fins de ligne en LF à l'analyse (spec XML 1.0). Le contrat
    // testé est la préservation du CONTENU, pas des CRLF : comparer EOL-normalisé — sinon
    // le test casse selon core.autocrlf du poste (source en CRLF vs round-trip en LF).
    private static string NoCrlf(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void Save_RoundTrip_PreservesRestOfDocument()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        svc.Save(path, NewScript);

        var reloaded = svc.Load(path);
        Assert.Equal(NoCrlf(NewScript), NoCrlf(reloaded.FullText));
        // Le reste du document est intact (annotations designer, propriétés de calcul)
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
        Assert.Contains("[Measures].[CA] - [Measures].[Coûts],", File.ReadAllText(bak)); // texte d'origine

        svc.Save(path, NewScript + "\n-- v2");
        Assert.Contains("[Measures].[CA] - [Measures].[Coûts],", File.ReadAllText(bak)); // .bak PAS écrasé

        string mdx = Path.Combine(_dir, "Portefeuilles.mdxscript.mdx");
        Assert.True(File.Exists(mdx));
        Assert.EndsWith("-- v2", File.ReadAllText(mdx).TrimEnd());
    }

    [Fact]
    public void Save_ReportsOrphanCalculationProperties()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);
        // NewScript ne définit plus [Measures].[Disparu] (qui a une CalculationProperty)
        var warnings = svc.Save(path, NewScript);
        Assert.Contains(warnings, w => w.Contains("[Measures].[Disparu]"));
        Assert.DoesNotContain(warnings, w => w.Contains("[Measures].[Marge]"));
    }

    [Fact]
    public void Save_WhitespaceOnlySecondCommand_MatchesLoadCanEditAndSucceeds()
    {
        // Un Command dont le <Text> est présent mais blanc ne doit PAS compter comme
        // une 2e Command "réelle" — Load.CanEdit et Save doivent être d'accord (bug
        // constaté : Load.CanEdit=true mais Save levait quand même, cf. CommandTexts
        // qui filtre le blanc vs l'ancien filtre de Save qui ne testait que la présence
        // du <Text>).
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

        svc.Save(path, NewScript); // ne doit pas lever

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
}
