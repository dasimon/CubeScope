namespace CubeScope.Server;

/// <summary>
/// Arrête l'exe quand la dernière fenêtre du navigateur se ferme : sans ça, fermer l'onglet
/// laisse `cubescope.exe` tourner en fond, sa console orpheline gardant le port.
///
/// Signal de vie = les connexions du <see cref="StatsHub"/> : chaque onglet en ouvre une au
/// démarrage de la SPA, et le navigateur la ferme qu'on le veuille ou non. Le compteur gère
/// donc naturellement plusieurs onglets.
///
/// Trois garde-fous :
/// - l'arrêt ne peut partir que d'une déconnexion, donc jamais avant qu'un client se soit
///   connecté (l'exe ne peut pas se suicider pendant l'ouverture du navigateur) ;
/// - un délai de grâce absorbe les déconnexions transitoires — un F5 ferme puis rouvre la
///   connexion, il ne doit pas emporter le serveur ;
/// - désactivable (<c>--no-browser</c>), sinon la boucle de dev et les tests s'arrêteraient
///   dès la fermeture de la page.
///
/// Limite connue : en WebSocket la fermeture est vue tout de suite ; si le transport retombe
/// en long-polling, SignalR ne déclare le client mort qu'au bout de son ClientTimeoutInterval
/// (~30 s par défaut). Et si aucun client ne se connecte jamais (navigateur qui n'ouvre pas),
/// rien ne s'arme : on retombe sur le comportement d'avant, jamais sur un arrêt surprise.
/// </summary>
public sealed class BrowserLifetime(
    IHostApplicationLifetime lifetime,
    ILogger<BrowserLifetime> logger,
    bool enabled,
    TimeSpan? grace = null)
{
    /// <summary>Marge après la fermeture du dernier onglet. Couvre largement un rechargement.</summary>
    private readonly TimeSpan _grace = grace ?? TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private int _clients;
    private CancellationTokenSource? _pendingShutdown;

    public void ClientConnected()
    {
        lock (_gate)
        {
            _clients++;
            // Un onglet est revenu (rechargement, reconnexion auto) : on désarme.
            _pendingShutdown?.Cancel();
            _pendingShutdown?.Dispose();
            _pendingShutdown = null;
        }
    }

    public void ClientDisconnected()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (--_clients > 0 || !enabled) return;
            _clients = 0;
            _pendingShutdown?.Dispose();
            _pendingShutdown = new CancellationTokenSource();
            token = _pendingShutdown.Token;
        }

        _ = ShutdownAfterGraceAsync(token);
    }

    private async Task ShutdownAfterGraceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_grace, token);
        }
        catch (OperationCanceledException)
        {
            return; // un onglet est revenu entre-temps
        }

        logger.LogInformation(
            "Dernière fenêtre fermée depuis {Grace}s : arrêt de CubeScope.", _grace.TotalSeconds);
        lifetime.StopApplication();
    }
}
