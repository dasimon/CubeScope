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
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

    private static (BrowserLifetime Sut, FakeLifetime Host) Create(bool enabled = true)
    {
        var host = new FakeLifetime();
        var sut = new BrowserLifetime(host, NullLogger<BrowserLifetime>.Instance, enabled, Grace);
        return (sut, host);
    }

    /// <summary>
    /// Attend l'arrêt au lieu de dormir une durée fixe : sous un pool de threads saturé (CI),
    /// la continuation du délai de grâce peut être replanifiée bien après son échéance — un
    /// simple sleep rendait le test instable sans qu'aucun bug ne soit en cause.
    /// </summary>
    private static async Task AssertStopsAsync(FakeLifetime host)
    {
        var stopped = await Task.WhenAny(host.Stopping, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(stopped == host.Stopping, "L'arrêt n'a pas été déclenché dans les 10 s.");
    }

    /// <summary>
    /// Laisse largement passer le délai de grâce et vérifie que rien ne s'est déclenché.
    /// Ce sens-là ne souffre pas de la lenteur : une annulation est posée avant toute attente,
    /// un ordonnancement tardif ne peut donc pas faire apparaître un arrêt.
    /// </summary>
    private static async Task AssertDoesNotStopAsync(FakeLifetime host)
    {
        await Task.Delay(Grace * 10);
        Assert.False(host.Stopped);
    }

    [Fact]
    public async Task LastTabClosed_StopsTheApplication()
    {
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();

        await AssertStopsAsync(host);
    }

    [Fact]
    public async Task Reload_WithinGrace_DoesNotStop()
    {
        // Un F5 ferme la connexion puis la rouvre aussitôt : le serveur doit survivre.
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.ClientConnected();

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task OneOfTwoTabsClosed_DoesNotStop()
    {
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.ClientConnected();

        sut.ClientDisconnected();

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task SecondTabClosed_AfterTheFirst_StopsTheApplication()
    {
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.ClientDisconnected();

        await AssertStopsAsync(host);
    }

    [Fact]
    public async Task Disabled_NeverStops()
    {
        // Cas --no-browser : boucle de dev et tests ne doivent pas s'arrêter tout seuls.
        var (sut, host) = Create(enabled: false);
        sut.ClientConnected();

        sut.ClientDisconnected();

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task NoClientEverConnected_NeverStops()
    {
        // Garde-fou : rien ne doit s'armer avant qu'un onglet se soit connecté, sinon l'exe
        // se couperait pendant l'ouverture du navigateur.
        var (_, host) = Create();

        await AssertDoesNotStopAsync(host);
    }

    /// <summary>
    /// L'arrêt est déclenché depuis le pool de threads : on l'expose en tâche plutôt qu'en
    /// booléen, pour que le test l'attende sans sondage ni question de visibilité mémoire.
    /// </summary>
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Stopping => _stopped.Task;
        public bool Stopped => _stopped.Task.IsCompleted;

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopped.TrySetResult();
    }
}
