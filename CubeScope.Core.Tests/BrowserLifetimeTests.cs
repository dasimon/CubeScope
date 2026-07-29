using CubeScope.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeScope.Core.Tests;

/// <summary>
/// Logique d'arrêt de l'exe à la fermeture du navigateur. Le lien « onglet fermé → déconnexion
/// du hub » relève de SignalR ; ce qui se teste ici, c'est ce qu'on en fait : compter les
/// onglets, absorber un rechargement, ne jamais couper prématurément.
/// </summary>
public class BrowserLifetimeTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(60);

    private static (BrowserLifetime Sut, FakeLifetime Host) Create(bool enabled = true)
    {
        var host = new FakeLifetime();
        var sut = new BrowserLifetime(host, NullLogger<BrowserLifetime>.Instance, enabled, Grace);
        return (sut, host);
    }

    /// <summary>Laisse passer le délai de grâce, avec de la marge pour l'ordonnanceur.</summary>
    private static Task WaitPastGrace() => Task.Delay(Grace * 6);

    [Fact]
    public async Task LastTabClosed_StopsTheApplication()
    {
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();
        await WaitPastGrace();

        Assert.True(host.Stopped);
    }

    [Fact]
    public async Task Reload_WithinGrace_DoesNotStop()
    {
        // Un F5 ferme la connexion puis la rouvre aussitôt : le serveur doit survivre.
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.ClientConnected();
        await WaitPastGrace();

        Assert.False(host.Stopped);
    }

    [Fact]
    public async Task OneOfTwoTabsClosed_DoesNotStop()
    {
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.ClientConnected();

        sut.ClientDisconnected();
        await WaitPastGrace();

        Assert.False(host.Stopped);
    }

    [Fact]
    public async Task SecondTabClosed_AfterTheFirst_StopsTheApplication()
    {
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.ClientDisconnected();
        await WaitPastGrace();

        Assert.True(host.Stopped);
    }

    [Fact]
    public async Task Disabled_NeverStops()
    {
        // Cas --no-browser : boucle de dev et tests ne doivent pas s'arrêter tout seuls.
        var (sut, host) = Create(enabled: false);
        sut.ClientConnected();

        sut.ClientDisconnected();
        await WaitPastGrace();

        Assert.False(host.Stopped);
    }

    [Fact]
    public async Task NoClientEverConnected_NeverStops()
    {
        // Garde-fou : rien ne doit s'armer avant qu'un onglet se soit connecté, sinon l'exe
        // se couperait pendant l'ouverture du navigateur.
        var (_, host) = Create();

        await WaitPastGrace();

        Assert.False(host.Stopped);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }
}
