using CubeScope.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeScope.Core.Tests;

/// <summary>
/// Logic that stops the exe when the browser closes. The "tab closed → hub disconnection" link
/// belongs to SignalR; what is tested here is what we do with it: count the tabs, absorb a
/// reload, never shut down prematurely.
/// </summary>
public class BrowserLifetimeTests
{
    /// <summary>Delay for "the page announced it was leaving" (close or F5).</summary>
    private static readonly TimeSpan Close = TimeSpan.FromMilliseconds(50);

    /// <summary>Delay for "the transport dropped without notice" — clearly longer, so that
    /// the tests can tell the two apart without depending on the scheduler.</summary>
    private static readonly TimeSpan Drop = TimeSpan.FromSeconds(30);

    private static (BrowserLifetime Sut, FakeLifetime Host) Create(bool enabled = true)
    {
        var host = new FakeLifetime();
        var sut = new BrowserLifetime(host, NullLogger<BrowserLifetime>.Instance, enabled, Close, Drop);
        return (sut, host);
    }

    /// <summary>
    /// Waits for the shutdown instead of sleeping for a fixed duration: under a saturated thread
    /// pool (CI), the grace-delay continuation can be rescheduled well after its due time — a
    /// plain sleep made the test flaky without any bug being involved.
    /// </summary>
    private static async Task AssertStopsAsync(FakeLifetime host)
    {
        var stopped = await Task.WhenAny(host.Stopping, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(stopped == host.Stopping, "Shutdown was not triggered within 10 s.");
    }

    /// <summary>
    /// Lets the grace delay pass by a wide margin and checks that nothing fired.
    /// This direction does not suffer from slowness: a cancellation is set before any wait,
    /// so late scheduling cannot make a shutdown appear.
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

        sut.NoticeClientLeaving(); // pagehide beacon, then the WebSocket closes
        sut.ClientDisconnected();

        await AssertStopsAsync(host);
    }

    [Fact]
    public async Task NoticeArrivingAfterTheDisconnect_ShortensThePendingShutdown()
    {
        // The beacon and the socket close race each other: in this order, the shutdown is
        // already armed with the LONG delay and must be brought back to the short delay.
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.NoticeClientLeaving();

        await AssertStopsAsync(host);
    }

    [Fact]
    public async Task UnannouncedDrop_WaitsForTheClientToComeBack()
    {
        // The heart of the fix: a transport that drops is NOT a close. The client uses
        // withAutomaticReconnect (0/2/10/30 s) — shutting down after the short delay would
        // kill it under a page that is still open.
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected(); // no beacon

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task ReconnectAfterADrop_DoesNotStop()
    {
        var (sut, host) = Create();
        sut.ClientConnected();

        sut.ClientDisconnected();
        sut.ClientConnected(); // the automatic reconnect succeeded

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task Reload_WithinGrace_DoesNotStop()
    {
        // An F5 gives notice (pagehide) then reopens immediately: the server must survive.
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
        // A beacon consumed by a reload must not make a later drop, without notice, pass
        // for a deliberate close.
        var (sut, host) = Create();
        sut.ClientConnected();
        sut.NoticeClientLeaving();
        sut.ClientDisconnected();
        sut.ClientConnected(); // the F5 succeeded

        sut.ClientDisconnected(); // later: network drop, no beacon

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
        // --no-browser case: the dev loop and the tests must not stop on their own.
        var (sut, host) = Create(enabled: false);
        sut.ClientConnected();

        sut.NoticeClientLeaving();
        sut.ClientDisconnected();

        await AssertDoesNotStopAsync(host);
    }

    [Fact]
    public async Task NoClientEverConnected_NeverStops()
    {
        // Safeguard: nothing must arm before a tab has connected, otherwise the exe would
        // shut down while the browser is opening.
        var (_, host) = Create();

        await AssertDoesNotStopAsync(host);
    }

    /// <summary>
    /// The shutdown is triggered from the thread pool: it is exposed as a task rather than a
    /// boolean, so the test can await it without polling or memory-visibility concerns.
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
