using CubeScope.Core.Profiler;
using CubeScope.Core.Ssas;
using CubeScope.Server;
using Microsoft.Extensions.DependencyInjection;

namespace CubeScope.Core.Tests;

/// <summary>
/// Refactoring Program.cs into ServerHost must keep two properties: the actual URL
/// (free port assigned by the OS) is readable AFTER startup, and the DI container
/// builds completely — it is this second point that catches a broken dependency,
/// invisible at build time.
/// </summary>
public class ServerHostTests
{
    [Fact]
    public async Task StartAsync_expose_l_url_reelle_et_construit_le_conteneur()
    {
        var (app, url) = await ServerHost.StartAsync(
            ["--no-browser"], browserLifetime: false);

        try
        {
            Assert.StartsWith("http://127.0.0.1:", url);
            // Port 0 = "pick one": after startup it must be resolved.
            Assert.DoesNotContain(":0", url);

            // Forces every singleton to be instantiated: an unresolvable dependency
            // throws here, while the build stays green.
            using var scope = app.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SsasSession>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProfilerService>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopApplication_declenche_Stopping_mais_pas_Stopped()
    {
        var (app, _) = await ServerHost.StartAsync(["--no-browser"], browserLifetime: false);
        try
        {
            bool stopping = false, stopped = false;
            app.Lifetime.ApplicationStopping.Register(() => stopping = true);
            app.Lifetime.ApplicationStopped.Register(() => stopped = true);

            // This is what BrowserLifetime does when the last page has gone.
            app.Lifetime.StopApplication();

            // StartAsync does not call WaitForShutdownAsync: the bridge to StopAsync()
            // does not exist here. A Shell subscribed to ApplicationStopped would therefore
            // never hear anything, and the exe would survive with neither window nor server.
            Assert.True(stopping, "ApplicationStopping must fire");
            Assert.False(stopped, "ApplicationStopped does NOT fire without WaitForShutdownAsync");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_libere_les_singletons_IDisposable()
    {
        var (app, _) = await ServerHost.StartAsync(["--no-browser"], browserLifetime: false);
        var profiler = app.Services.GetRequiredService<ProfilerService>();

        await app.StopAsync();
        await app.DisposeAsync();

        // ObjectDisposedException = the container did dispose its singletons; it is
        // this path that runs the Stop() + Drop() of the SSAS trace. Without the
        // DisposeAsync, the CubeScope_Profiler_<pid> trace outlives the process.
        Assert.Throws<ObjectDisposedException>(() => profiler.EnsureNotDisposed());
    }
}
