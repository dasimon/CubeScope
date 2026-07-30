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
    /// <summary>Délai « la page a prévenu qu'elle partait » (fermeture ou F5).</summary>
    private static readonly TimeSpan Close = TimeSpan.FromMilliseconds(50);

    /// <summary>Délai « le transport a lâché sans préavis » — franchement plus long, pour
    /// que les tests puissent distinguer les deux sans dépendre de l'ordonnanceur.</summary>
    private static readonly TimeSpan Drop = TimeSpan.FromSeconds(30);

    private static (BrowserLifetime Sut, FakeLifetime Host) Create(bool enabled = true)
    {
        var host = new FakeLifetime();
        var sut = new BrowserLifetime(host, NullLogger<BrowserLifetime>.Instance, enabled, Close, Drop);
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
        await Task.Delay(Close * 10);
        Assert.False(host.Stopped);
    }

    [Fact]
    public async Task AnnouncedClose_StopsQuickly()
    {
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.NoticeClientLeaving(); // balise pagehide, puis fermeture du WebSocket
        sut.ClientDisconnected();

        await AssertStopsAsync(host);
    }

    [Fact]
    public async Task NoticeArrivingAfterTheDisconnect_ShortensThePendingShutdown()
    {
        // La balise et la fermeture du socket courent l'une contre l'autre : dans cet ordre,
        // l'arrêt est déjà armé au délai LONG et doit être ramené au délai court.
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.NoticeClientLeaving();

        await AssertStopsAsync(host);
    }

    [Fact]
    public async Task UnannouncedDrop_WaitsForTheClientToComeBack()
    {
        // Le cœur du correctif : un transport qui lâche n'est PAS une fermeture. Le client
        // est en withAutomaticReconnect (0/2/10/30 s) — couper au délai court le tuerait
        // sous une page encore ouverte.
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected(); // aucune balise

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task ReconnectAfterADrop_DoesNotStop()
    {
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.ClientConnected(); // la reconnexion automatique a abouti

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task Reload_WithinGrace_DoesNotStop()
    {
        // Un F5 prévient (pagehide) puis rouvre aussitôt : le serveur doit survivre.
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.NoticeClientLeaving();
        sut.ClientDisconnected();
        sut.ClientConnected();

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task ReconnectClearsTheLeavingFlag()
    {
        // Une balise consommée par un rechargement ne doit pas faire passer une coupure
        // ultérieure, sans préavis, pour une fermeture volontaire.
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.NoticeClientLeaving();
        sut.ClientDisconnected();
        sut.ClientConnected(); // le F5 a abouti

        sut.ClientDisconnected(); // plus tard : coupure réseau, sans balise

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task OneOfTwoTabsClosed_DoesNotStop()
    {
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.ClientConnected();

        sut.NoticeClientLeaving();
        sut.ClientDisconnected();

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task SecondTabClosed_AfterTheFirst_StopsTheApplication()
    {
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.ClientConnected();

        sut.NoticeClientLeaving();
        sut.ClientDisconnected();
        sut.NoticeClientLeaving();
        sut.ClientDisconnected();

        await AssertStopsAsync(host);
    }

    [Fact]
    public async Task Disabled_NeverStops()
    {
        // Cas --no-browser : boucle de dev et tests ne doivent pas s'arrêter tout seuls.
        var (sut, host) = Create(enabled: false);
        sut.ClientConnected();

        sut.NoticeClientLeaving();
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
