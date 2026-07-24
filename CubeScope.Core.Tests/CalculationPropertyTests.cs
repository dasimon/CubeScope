using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

public class CalculationPropertyTests : IDisposable
{
    // Squelette minimal fidèle d'un .cube SSDT, nom de cube NEUTRE (pas un vrai nom de
    // production) : un MdxScript à 1 Command (2 membres calculés) + CalculationProperties
    // avec une seule propriété déjà renseignée (FormatString + DisplayFolder).
    private const string SampleCube = """
        <?xml version="1.0" encoding="utf-8"?>
        <Cube xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
          <ID>DemoCube</ID>
          <Name>DemoCube</Name>
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

        CREATE MEMBER CURRENTCUBE.[Measures].[Marge]
         AS [Measures].[CA] - [Measures].[Coûts],
        VISIBLE = 1;
        CREATE MEMBER CURRENTCUBE.[Measures].[Brut]
         AS [Measures].[CA],
        VISIBLE = 1;</Text>
                </Command>
              </Commands>
              <CalculationProperties>
                <CalculationProperty>
                  <CalculationReference>[Measures].[Marge]</CalculationReference>
                  <CalculationType>Member</CalculationType>
                  <FormatString>'#,##0.00'</FormatString>
                  <DisplayFolder>Rentabilité</DisplayFolder>
                </CalculationProperty>
              </CalculationProperties>
            </MdxScript>
          </MdxScripts>
        </Cube>
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("cubescope-test-").FullName;

    private string WriteFixture(string content, string name = "DemoCube.cube")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Get_ReadsExistingProperties()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);

        var props = svc.GetCalculationProperties(path);

        var marge = Assert.Single(props, p => p.Reference == "[Measures].[Marge]");
        Assert.Equal("'#,##0.00'", marge.FormatString);
        Assert.Equal("Rentabilité", marge.DisplayFolder);
        Assert.Null(marge.Description);
    }

    [Fact]
    public void Save_UpdatesExistingFormatString_PreservesRest()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);

        svc.SaveCalculationProperty(path, "[Measures].[Marge]", "'#,##0'", "Rentabilité", null);

        var reloaded = svc.GetCalculationProperties(path).Single(p => p.Reference == "[Measures].[Marge]");
        Assert.Equal("'#,##0'", reloaded.FormatString);

        // Le reste du document (annotations designer) est intact
        string xml = File.ReadAllText(path);
        Assert.Contains("DiagramLayout", xml);

        // La Command du MdxScript n'a pas bougé
        var script = svc.Load(path);
        Assert.Contains("CREATE MEMBER CURRENTCUBE.[Measures].[Marge]", script.FullText);
        Assert.Contains("CREATE MEMBER CURRENTCUBE.[Measures].[Brut]", script.FullText);
    }

    [Fact]
    public void Save_CreatesPropertyWhenAbsent()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);

        svc.SaveCalculationProperty(
            path, "[Measures].[Brut]", "'#,##0.00'", "Volumes", "Chiffre d'affaires brut");

        var props = svc.GetCalculationProperties(path);
        var brut = Assert.Single(props, p => p.Reference == "[Measures].[Brut]");
        Assert.Equal("'#,##0.00'", brut.FormatString);
        Assert.Equal("Volumes", brut.DisplayFolder);
        Assert.Equal("Chiffre d'affaires brut", brut.Description);

        string xml = File.ReadAllText(path);
        Assert.Contains("<CalculationType>Member</CalculationType>", xml);
    }

    [Fact]
    public void Save_NullOrEmpty_RemovesChild()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);

        // DisplayFolder existant -> null : doit disparaître. FormatString repassé à sa
        // valeur d'origine : doit rester intact (pas de perte croisée entre champs).
        svc.SaveCalculationProperty(path, "[Measures].[Marge]", "'#,##0.00'", null, null);

        var marge = svc.GetCalculationProperties(path).Single(p => p.Reference == "[Measures].[Marge]");
        Assert.Null(marge.DisplayFolder);
        Assert.Equal("'#,##0.00'", marge.FormatString);

        string xml = File.ReadAllText(path);
        Assert.DoesNotContain("<DisplayFolder>", xml);
    }

    [Fact]
    public void Save_AddSecondProperty_DoesNotDisturbFirst()
    {
        var svc = new CubeProjectService();
        string path = WriteFixture(SampleCube);

        // [Measures].[Brut] n'a pas encore de CalculationProperty : on en crée une.
        svc.SaveCalculationProperty(path, "[Measures].[Brut]", "'#,##0'", null, null);

        var marge = svc.GetCalculationProperties(path).Single(p => p.Reference == "[Measures].[Marge]");
        Assert.Equal("'#,##0.00'", marge.FormatString);
        Assert.Equal("Rentabilité", marge.DisplayFolder);
    }
}
