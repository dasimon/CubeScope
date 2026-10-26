using CubeScope.Core.Project;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;

namespace CubeScope.Core.Tests;

public class XmlaScriptAnalyzerTests
{
    private const string Ns = "http://schemas.microsoft.com/analysisservices/2003/engine";

    // As captured by AMO for the State panel (observed on the dev server).
    private const string ProcessPartition = $"""
        <Batch xmlns='{Ns}' Transaction='true'>
        <Process xmlns="{Ns}">
          <Type>ProcessFull</Type>
          <Object>
            <DatabaseID>Ratios</DatabaseID>
            <CubeID>Portefeuilles</CubeID>
            <MeasureGroupID>Fait Flux</MeasureGroupID>
            <PartitionID>Fait Flux</PartitionID>
          </Object>
        </Process>
        </Batch>
        """;

    private static string Refusal(string script) => XmlaScriptAnalyzer.ProductionRefusal(XmlaScriptAnalyzer.Analyze(script)) ?? "";

    [Fact]
    public void Traitement_de_partition_capture_par_amo()
    {
        var a = XmlaScriptAnalyzer.Analyze(ProcessPartition);
        var c = Assert.Single(a.Commands);
        Assert.Equal(("Process", "ProcessFull", false), (c.Name, c.Detail, c.OnDimension));
        Assert.Null(XmlaScriptAnalyzer.ProductionRefusal(a));
    }

    [Theory]
    [InlineData("Delete")]
    [InlineData("Restore")]
    [InlineData("Detach")]
    [InlineData("Synchronize")]
    [InlineData("ClearCache")]
    [InlineData("MergePartitions")]
    [InlineData("Attach")]
    [InlineData("ImageLoad")]
    public void Commandes_interdites_en_prod_meme_dans_des_batch_imbriques(string command)
    {
        string script = $"""
            <Batch xmlns="{Ns}">
              <Parallel>
                <Batch>
                  <{command}><Object><DatabaseID>Ratios</DatabaseID></Object></{command}>
                </Batch>
              </Parallel>
            </Batch>
            """;
        Assert.Contains(command, Refusal(script));
    }

    [Fact]
    public void Commande_interdite_sans_namespace_detectee_quand_meme()
    {
        Assert.Contains("Delete", Refusal("<Delete><Object><DatabaseID>Ratios</DatabaseID></Object></Delete>"));
    }

    [Fact]
    public void Mot_cle_en_commentaire_ou_dans_une_definition_n_est_pas_une_commande()
    {
        // A comment, a description and an element named like a command inside an ObjectDefinition
        // are data: text search would refuse this script, parsing does not.
        string script = $"""
            <Alter xmlns="{Ns}" AllowCreate="true" ObjectExpansion="ObjectProperties">
              <!-- <Delete> la partition plus tard -->
              <Object><DatabaseID>Ratios</DatabaseID><RoleID>Lecteurs</RoleID></Object>
              <ObjectDefinition>
                <Role><ID>Lecteurs</ID><Name>Lecteurs</Name><Description>Delete, Restore, Detach</Description>
                  <Restore>pas une commande</Restore>
                </Role>
              </ObjectDefinition>
            </Alter>
            """;
        var a = XmlaScriptAnalyzer.Analyze(script);
        Assert.Equal(["Alter"], a.Commands.Select(c => c.Name));
        Assert.Null(XmlaScriptAnalyzer.ProductionRefusal(a));
    }

    [Theory]
    [InlineData("ProcessFull", true)]
    [InlineData("ProcessData", true)]
    [InlineData("ProcessUpdate", false)]
    [InlineData("ProcessDefault", false)]
    public void Traitement_de_dimension_en_prod(string type, bool refused)
    {
        string script = $"<Process xmlns=\"{Ns}\"><Type>{type}</Type><Object><DatabaseID>Ratios</DatabaseID><DimensionID>AIFM</DimensionID></Object></Process>";
        Assert.Equal(refused, Refusal(script).Length > 0);
    }

    [Fact]
    public void Dimension_de_cube_traitee_comptee_comme_dimension()
    {
        // Fail-closed: a DimensionID next to a CubeID still counts as a dimension process.
        Assert.Contains("dimension", Refusal($"<Process xmlns=\"{Ns}\"><Type>ProcessFull</Type><Object><DatabaseID>Ratios</DatabaseID><CubeID>Portefeuilles</CubeID><DimensionID>AIFM</DimensionID></Object></Process>"));
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Alter")]
    public void Base_entiere_creee_ou_ecrasee_refusee_en_prod(string command)
    {
        string script = $"<{command} xmlns=\"{Ns}\" AllowOverwrite=\"true\"><ObjectDefinition><Database><ID>Ratios</ID><Name>Ratios</Name></Database></ObjectDefinition></{command}>";
        Assert.Contains("base entière", Refusal(script));
    }

    [Fact]
    public void Creation_de_partition_autorisee_avec_confirmation()
    {
        string script = $"<Create xmlns=\"{Ns}\"><ParentObject><DatabaseID>Ratios</DatabaseID><CubeID>C</CubeID><MeasureGroupID>MG</MeasureGroupID></ParentObject><ObjectDefinition><Partition><ID>P2026</ID><Name>P2026</Name></Partition></ObjectDefinition></Create>";
        Assert.Equal("", Refusal(script));
    }

    [Fact]
    public void Element_inconnu_dans_un_batch_refuse()
    {
        // Fail-closed: an element the analyzer does not know must not slip through inside a Batch.
        Assert.Throws<XmlaScriptException>(() => XmlaScriptAnalyzer.Analyze(
            $"<Batch xmlns='{Ns}'><Process><Type>ProcessDefault</Type></Process><Frobnicate/></Batch>"));
        // Known Batch settings are fine.
        Assert.Single(XmlaScriptAnalyzer.Analyze(
            $"<Batch xmlns='{Ns}'><ErrorConfiguration/><Process><Type>ProcessDefault</Type></Process></Batch>").Commands);
    }

    [Fact]
    public void ProcessClear_refuse_en_prod_sur_tout_objet()
    {
        Assert.Contains("ProcessClear", Refusal(ProcessPartition.Replace("ProcessFull", "ProcessClear")));
    }

    [Fact]
    public void ProcessAdd_autorise_avec_confirmation()
    {
        // The reason the console exists for processing: an Add with its own source query.
        Assert.Equal("", Refusal(ProcessPartition.Replace("ProcessFull", "ProcessAdd")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<Batch>")]
    [InlineData("<Batch></Batch><Batch></Batch>")]
    [InlineData("pas du xml")]
    public void Xml_invalide_ou_vide_refuse(string script)
    {
        Assert.Throws<XmlaScriptException>(() => XmlaScriptAnalyzer.Analyze(script));
    }

    [Fact]
    public void Discover_refuse_avec_explication()
    {
        var ex = Assert.Throws<XmlaScriptException>(() => XmlaScriptAnalyzer.Analyze(
            "<Discover xmlns='urn:schemas-microsoft-com:xml-analysis'><RequestType>DBSCHEMA_CATALOGS</RequestType></Discover>"));
        Assert.Contains("Statement", ex.Message);
    }

    [Fact]
    public void Racine_inconnue_ou_batch_vide_refuses()
    {
        Assert.Throws<XmlaScriptException>(() => XmlaScriptAnalyzer.Analyze("<Foo/>"));
        Assert.Throws<XmlaScriptException>(() => XmlaScriptAnalyzer.Analyze($"<Batch xmlns='{Ns}'><!-- rien --></Batch>"));
    }

    [Fact]
    public void Statement_dmv_reconnu_et_texte_envoye_normalise()
    {
        var a = XmlaScriptAnalyzer.Analyze("<?xml version='1.0'?>\n<!-- lire -->\n<Statement xmlns='urn:schemas-microsoft-com:xml-analysis'>SELECT * FROM $SYSTEM.DISCOVER_SESSIONS</Statement>");
        Assert.Equal(["Statement"], a.Commands.Select(c => c.Name));
        // No XML declaration nor top-level comment in what is sent: only the parsed command.
        Assert.StartsWith("<Statement", a.Normalized);
    }

    [Fact]
    public async Task Console_refuse_avant_toute_connexion_et_journalise()
    {
        using var store = new StateStore(Path.Combine(Path.GetTempPath(), $"cs-xmla-{Guid.NewGuid():N}.db"));
        var svc = new XmlaConsoleService(new SsasSession(), store);

        // Unreachable server, not dev, right confirmation: refused by the analysis, not by a connection error.
        await Assert.ThrowsAsync<ProductionActionRefusedException>(() => svc.ExecuteAsync(
            "no-such-host.cubescope.invalid", "Db",
            $"<Delete xmlns='{Ns}'><Object><DatabaseID>Db</DatabaseID></Object></Delete>",
            "no-such-host.cubescope.invalid", []));
        // Right script, wrong confirmation.
        await Assert.ThrowsAsync<ProductionActionRefusedException>(() => svc.ExecuteAsync(
            "no-such-host.cubescope.invalid", "Db", ProcessPartition, "autre", []));
        // Invalid XML.
        await Assert.ThrowsAsync<XmlaScriptException>(() => svc.ExecuteAsync(
            "no-such-host.cubescope.invalid", "Db", "<Batch>", null, []));

        var log = store.GetXmlaLog();
        Assert.Equal(3, log.Count);
        Assert.All(log, l => Assert.False(l.Success));
        Assert.All(log, l => Assert.True(l.IsProduction));
    }
}
