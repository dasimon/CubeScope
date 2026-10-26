using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using Xunit.Abstractions;

namespace CubeScope.Core.Tests;

/// <summary>
/// XMLA console against the DEV server: read-only statements, plus a Process aimed at an object
/// that does not exist (nothing can be processed) to see how a server error comes back.
/// </summary>
[Trait("Category", "Integration")]
public class XmlaConsoleIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly StateStore _store = new(Path.Combine(Path.GetTempPath(), $"cs-xmla-it-{Guid.NewGuid():N}.db"));

    private Task<XmlaExecutionResult> RunAsync(string script)
    {
        TestTarget.AssertDevServerDistinct();
        return new XmlaConsoleService(new SsasSession(), _store).ExecuteAsync(
            TestTarget.ServerDev, TestTarget.CatalogDev, script, null, [TestTarget.ServerDev]);
    }

    [Fact]
    public async Task DmvStatement_ComesBackAsARowset()
    {
        var r = await RunAsync("<Statement xmlns='urn:schemas-microsoft-com:xml-analysis'>SELECT [CATALOG_NAME], [DATE_MODIFIED] FROM $SYSTEM.DBSCHEMA_CATALOGS</Statement>");
        Assert.True(r.Success);
        Assert.NotNull(r.Rowset);
        Assert.Equal(["CATALOG_NAME", "DATE_MODIFIED"], r.Rowset!.Columns.Select(c => c.Header));
        Assert.Contains(r.Rowset.Rows, row => (string?)row["c0"] == TestTarget.CatalogDev);
    }

    [Fact]
    public async Task MdxStatement_RunsOnTheCurrentCatalog_AsACellset()
    {
        var r = await RunAsync($"<Statement xmlns='urn:schemas-microsoft-com:xml-analysis'>SELECT {{[Measures].DefaultMember}} ON 0 FROM [{TestTarget.Cube}]</Statement>");
        Assert.True(r.Success);
        Assert.Null(r.Rowset);
        Assert.True(r.MultidimensionalResult);
    }

    [Fact]
    public async Task ServerError_IsAFailedResult_NotAnException()
    {
        var r = await RunAsync($"""
            <Process xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
              <Type>ProcessDefault</Type>
              <Object><DatabaseID>{TestTarget.CatalogDev}</DatabaseID><DimensionID>__CubeScope_no_such_dimension</DimensionID></Object>
            </Process>
            """);
        foreach (var m in r.Messages) output.WriteLine($"{m.Severity}: {m.Text}");
        Assert.False(r.Success);
        Assert.Contains(r.Messages, m => m.Severity == "Error");
        var log = Assert.Single(_store.GetXmlaLog());
        Assert.False(log.Success);
        Assert.False(log.IsProduction);
    }

    public void Dispose() => _store.Dispose();
}
