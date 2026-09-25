using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

public class SsasSessionTests
{
    [Fact]
    public void Cancellable_ExceptionAfterCancellation_BecomesOperationCanceled()
    {
        // What ADOMD does when AdomdCommand.Cancel lands: its own exception, not an OCE.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var boom = new InvalidOperationException("server said: cancelled");

        var ex = Assert.Throws<OperationCanceledException>(
            () => SsasSession.Cancellable<int>(() => throw boom, cts.Token));
        Assert.Same(boom, ex.InnerException);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public void Cancellable_ExceptionWithoutCancellation_IsKept()
    {
        var boom = new InvalidOperationException("syntax error");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SsasSession.Cancellable<int>(() => throw boom, CancellationToken.None));
        Assert.Same(boom, ex);
    }

    [Fact]
    public void Cancellable_CancelledAfterSuccess_StillReportsCancellation()
    {
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(
            () => SsasSession.Cancellable(() => { cts.Cancel(); return 1; }, cts.Token));
    }

    [Fact]
    public void Cancellable_Success_ReturnsTheResult()
        => Assert.Equal(42, SsasSession.Cancellable(() => 42, CancellationToken.None));

    [Fact]
    public async Task ConnectAsync_Failure_LeavesNoHalfReplacedConnection()
    {
        // Nothing listens on port 1: Open is refused locally, no SSAS server is contacted.
        using var session = new SsasSession();
        await Assert.ThrowsAnyAsync<Exception>(() => session.ConnectAsync("localhost:1"));

        Assert.Null(session.Server);
        Assert.Null(session.SessionId);
        // Before the fix, the failed connection was already installed: the next call tried
        // to reopen it (on the new server) instead of reporting that nothing is connected.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WithConnectionAsync(_ => 0));
        Assert.Equal("Aucune connexion ouverte.", ex.Message);
    }
}
