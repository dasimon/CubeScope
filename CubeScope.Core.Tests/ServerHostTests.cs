using CubeScope.Core.Profiler;
using CubeScope.Core.Ssas;
using CubeScope.Server;
using Microsoft.Extensions.DependencyInjection;

namespace CubeScope.Core.Tests;

/// <summary>
/// Le refactor de Program.cs en ServerHost doit garder deux propriétés : l'URL réelle
/// (port libre attribué par l'OS) est lisible APRÈS démarrage, et le conteneur DI se
/// construit entièrement — c'est ce second point qui attrape une dépendance cassée,
/// invisible au build.
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
            // Port 0 = « choisis-en un » : après démarrage il doit être résolu.
            Assert.DoesNotContain(":0", url);

            // Force l'instanciation de chaque singleton : une dépendance non résolvable
            // lève ici, alors que le build reste vert.
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

            // C'est ce que fait BrowserLifetime quand la dernière page est partie.
            app.Lifetime.StopApplication();

            // StartAsync n'appelle pas WaitForShutdownAsync : le pont vers StopAsync()
            // n'existe pas ici. Un Shell abonné à ApplicationStopped n'entendrait donc
            // jamais rien, et l'exe survivrait sans fenêtre ni serveur.
            Assert.True(stopping, "ApplicationStopping doit se déclencher");
            Assert.False(stopped, "ApplicationStopped ne se déclenche PAS sans WaitForShutdownAsync");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
