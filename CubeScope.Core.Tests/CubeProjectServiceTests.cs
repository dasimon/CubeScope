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
}
