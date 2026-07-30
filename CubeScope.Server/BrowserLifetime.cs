namespace CubeScope.Server;

/// <summary>
/// Arrête l'exe quand la dernière fenêtre du navigateur se ferme : sans ça, fermer l'onglet
/// laisse `cubescope.exe` tourner en fond, sa console orpheline gardant le port.
///
/// Signal de vie = les connexions du <see cref="StatsHub"/> : chaque onglet en ouvre une au
/// démarrage de la SPA. Le compteur gère donc naturellement plusieurs onglets.
///
/// PIÈGE CENTRAL — une déconnexion du hub ne veut PAS dire que la page est partie. Le client
/// est en `withAutomaticReconnect()`, qui réessaie à 0, 2, 10 puis 30 s : une coupure passagère
/// du WebSocket est rattrapée, mais bien après un délai de grâce court. Couper au bout de 10 s
/// dans ce cas tue le serveur sous une page encore ouverte (« Failed to fetch », et comme l'exe
/// prend un port libre au lancement, l'onglet resté ouvert vise ensuite un port mort).
/// D'où deux délais, choisis selon ce que le client a annoncé :
/// - la page a prévenu qu'elle partait (<see cref="NoticeClientLeaving"/>, balise `pagehide`) —
///   fermeture ou rechargement : délai COURT, l'exe disparaît vite ;
/// - personne n'a prévenu : c'est le transport qui a lâché — délai LONG, au-delà de la fenêtre
///   de reconnexion, pour laisser le client revenir.
///
/// Autres garde-fous :
/// - l'arrêt ne peut partir que d'une déconnexion, donc jamais avant qu'un client se soit
///   connecté (l'exe ne peut pas se suicider pendant l'ouverture du navigateur) ;
/// - désactivable (<c>--no-browser</c>), sinon la boucle de dev et les tests s'arrêteraient
///   dès la fermeture de la page.
///
/// Si aucun client ne se connecte jamais (navigateur qui n'ouvre pas), rien ne s'arme : on
/// retombe sur le comportement d'avant, jamais sur un arrêt surprise.
/// </summary>
public sealed class BrowserLifetime(
    IHostApplicationLifetime lifetime,
    ILogger<BrowserLifetime> logger,
    bool enabled,
    TimeSpan? closeGrace = null,
    TimeSpan? dropGrace = null)
{
    /// <summary>Page partie volontairement (fermeture / rechargement) : couvre le temps d'un F5.</summary>
    private readonly TimeSpan _closeGrace = closeGrace ?? TimeSpan.FromSeconds(10);

    /// <summary>Transport tombé sans préavis : doit dépasser la reconnexion SignalR (0/2/10/30 s).</summary>
    private readonly TimeSpan _dropGrace = dropGrace ?? TimeSpan.FromSeconds(45);

    private readonly object _gate = new();
    private int _clients;
    private bool _leaving; // une page a annoncé son départ (balise reçue)
    private CancellationTokenSource? _pendingShutdown;

    public void ClientConnected()
    {
        lock (_gate)
        {
            _clients++;
            _leaving = false;
            CancelPending();
        }
    }

    /// <summary>
    /// La page annonce son départ (`pagehide`). La balise et la fermeture du WebSocket courent
    /// l'une contre l'autre : on gère les deux ordres d'arrivée — soit on mémorise l'intention
    /// pour la déconnexion à venir, soit on raccourcit un arrêt déjà armé au délai long.
    /// </summary>
    public void NoticeClientLeaving()
    {
        lock (_gate)
        {
            _leaving = true;
            if (_pendingShutdown is null) return; // la déconnexion n'est pas encore arrivée
            CancelPending();
            Arm(_closeGrace);
            _leaving = false;
        }
    }

    public void ClientDisconnected()
    {
        lock (_gate)
        {
            if (--_clients > 0 || !enabled) return;
            _clients = 0;
            CancelPending();
            Arm(_leaving ? _closeGrace : _dropGrace);
            _leaving = false;
        }
    }

    /// <summary>Arme l'arrêt différé. À appeler sous <see cref="_gate"/>.</summary>
    private void Arm(TimeSpan grace)
    {
        _pendingShutdown = new CancellationTokenSource();
        // Démarre en synchrone jusqu'au premier await (Task.Delay) : ne reprend pas le verrou.
        _ = ShutdownAfterGraceAsync(grace, _pendingShutdown.Token);
    }

    /// <summary>Désarme l'arrêt en cours. À appeler sous <see cref="_gate"/>.</summary>
    private void CancelPending()
    {
        _pendingShutdown?.Cancel();
        _pendingShutdown?.Dispose();
        _pendingShutdown = null;
    }

    private async Task ShutdownAfterGraceAsync(TimeSpan grace, CancellationToken token)
    {
        try
        {
            await Task.Delay(grace, token);
        }
        catch (OperationCanceledException)
        {
            return; // un onglet est revenu, ou le délai a été revu
        }

        logger.LogInformation(
            "Plus aucune fenêtre depuis {Grace}s : arrêt de CubeScope.", grace.TotalSeconds);
        lifetime.StopApplication();
    }
}
