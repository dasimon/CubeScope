using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

/// <summary>
/// ClearCache had no server-side guard: only the UI's confirmation dialog stood between a
/// direct API call and the cache of a production catalog. Same rule as the script deployment
/// (see ScriptDeployGuardTests): explicit dev server list, fail-closed, checked before any
/// contact with the server. No SSAS needed.
/// </summary>
public class ClearCacheGuardTests
{
    [Fact]
    public void Refuses_a_server_absent_from_the_list_and_names_it()
    {
        var ex = Assert.Throws<ClearCacheRefusedException>(() =>
            CacheService.EnsureDevServer("SRV-PROD", ["SRV-DEV"]));
        Assert.Contains("SRV-PROD", ex.Message);
    }

    [Fact]
    public void Refuses_when_the_list_is_empty()
        => Assert.Throws<ClearCacheRefusedException>(() => CacheService.EnsureDevServer("SRV-DEV", []));

    [Fact]
    public void A_declared_server_passes()
        => Assert.Null(Record.Exception(() => CacheService.EnsureDevServer("srv-dev ", ["SRV-DEV"])));

    [Fact]
    public async Task ClearCache_without_a_connection_fails_before_the_guard_and_any_contact()
    {
        // No server in the session: the "no connection" error, not the guard's — the two
        // messages must stay distinct so the user knows which one spoke.
        using var session = new SsasSession();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CacheService(session).ClearCacheAsync(["SRV-DEV"]));
        Assert.IsNotType<ClearCacheRefusedException>(ex);
    }
}
