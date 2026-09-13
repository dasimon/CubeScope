// Moved off Microsoft.NET.Sdk.Web (task 1, step 5): these usings, implicit under the Web
// SDK, must be explicit with the standard SDK.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeScope.Server;

/// <summary>
/// Stops the exe when the last browser window closes: without this, closing the tab
/// leaves `cubescope.exe` running in the background, its orphaned console holding the port.
///
/// Heartbeat = the <see cref="StatsHub"/> connections: each tab opens one when the
/// SPA starts. The counter therefore handles several tabs naturally.
///
/// CENTRAL PITFALL — a hub disconnection does NOT mean the page is gone. The client
/// uses `withAutomaticReconnect()`, which retries after 0, 2, 10 then 30 s: a transient
/// WebSocket drop is recovered, but well after a short grace period. Shutting down after 10 s
/// in that case kills the server under a page that is still open ("Failed to fetch", and since the exe
/// takes a free port on launch, the tab left open then points at a dead port).
/// Hence two delays, chosen according to what the client announced:
/// - the page gave notice that it was leaving (<see cref="NoticeClientLeaving"/>, `pagehide` beacon) —
///   close or reload: SHORT delay, the exe goes away quickly;
/// - nobody gave notice: it is the transport that dropped — LONG delay, beyond the reconnection
///   window, to let the client come back.
///
/// Other safeguards:
/// - shutdown can only start from a disconnection, so never before a client has
///   connected (the exe cannot kill itself while the browser is opening);
/// - can be disabled (<c>--no-browser</c>), otherwise the dev loop and the tests would stop
///   as soon as the page is closed.
///
/// If no client ever connects (browser that does not open), nothing is armed: we
/// fall back to the previous behaviour, never to a surprise shutdown.
/// </summary>
public sealed class BrowserLifetime(
    IHostApplicationLifetime lifetime,
    ILogger<BrowserLifetime> logger,
    bool enabled,
    TimeSpan? closeGrace = null,
    TimeSpan? dropGrace = null)
{
    /// <summary>
    /// Can be armed afterwards. The Shell starts disarmed when it launches in native window mode,
    /// but it switches to the browser fallback if WebView2 fails to initialize: the exe
    /// must then stop as in browser mode, otherwise it survives with nothing on screen.
    /// </summary>
    public bool Enabled { get; set; } = enabled;

    /// <summary>Page left on purpose (close / reload): covers the time of an F5.</summary>
    private readonly TimeSpan _closeGrace = closeGrace ?? TimeSpan.FromSeconds(10);

    /// <summary>Transport dropped without notice: must exceed the SignalR reconnection (0/2/10/30 s).</summary>
    private readonly TimeSpan _dropGrace = dropGrace ?? TimeSpan.FromSeconds(45);

    private readonly object _gate = new();
    private int _clients;
    private bool _leaving; // a page announced it was leaving (beacon received)
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
    /// The page announces it is leaving (`pagehide`). The beacon and the WebSocket close race
    /// each other: both arrival orders are handled — either the intent is remembered
    /// for the upcoming disconnection, or a shutdown already armed with the long delay is shortened.
    /// </summary>
    public void NoticeClientLeaving()
    {
        lock (_gate)
        {
            _leaving = true;
            if (_pendingShutdown is null) return; // the disconnection has not arrived yet
            CancelPending();
            Arm(_closeGrace);
            _leaving = false;
        }
    }

    public void ClientDisconnected()
    {
        lock (_gate)
        {
            if (--_clients > 0 || !Enabled) return;
            _clients = 0;
            CancelPending();
            Arm(_leaving ? _closeGrace : _dropGrace);
            _leaving = false;
        }
    }

    /// <summary>Arms the delayed shutdown. Call while holding <see cref="_gate"/>.</summary>
    private void Arm(TimeSpan grace)
    {
        _pendingShutdown = new CancellationTokenSource();
        // Runs synchronously up to the first await (Task.Delay): does not take the lock again.
        _ = ShutdownAfterGraceAsync(grace, _pendingShutdown.Token);
    }

    /// <summary>Disarms the pending shutdown. Call while holding <see cref="_gate"/>.</summary>
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
            return; // a tab came back, or the delay was revised
        }

        logger.LogInformation(
            "Plus aucune fenêtre depuis {Grace}s : arrêt de CubeScope.", grace.TotalSeconds);
        lifetime.StopApplication();
    }
}
