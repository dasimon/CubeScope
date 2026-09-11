using System.Diagnostics;
using System.Windows;
using CubeScope.Core.State;
using CubeScope.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Web.WebView2.Core;

namespace CubeScope.Shell;

public partial class App : Application
{
    private WebApplication? _serveur;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Le test de présence du runtime est statique : il ne dépend pas de l'URL, donc
        // il se fait AVANT de démarrer Kestrel — ce qui permet de passer la bonne valeur
        // de browserLifetime du premier coup, sans mutation après coup.
        var mode = LaunchModeDecider.Decide(
            runtimeDisponible: RuntimeWebView2Disponible(),
            forceBrowser: e.Args.Contains("--force-browser"));

        var (app, url) = await ServerHost.StartAsync(
            e.Args, browserLifetime: mode == LaunchMode.Browser);
        _serveur = app;

        if (mode == LaunchMode.Browser)
        {
            // Repli : comportement historique à l'identique. BrowserLifetime est armé —
            // mais il arrête l'HÔTE WEB, pas l'application WPF. Sans ce relais, l'exe
            // survivrait sans fenêtre ni serveur, invisible et increvable.
            //
            // ApplicationStopping, PAS ApplicationStopped : BrowserLifetime appelle
            // IHostApplicationLifetime.StopApplication(), qui ne déclenche QUE
            // ApplicationStopping. Le pont vers StopAsync() — et donc vers
            // ApplicationStopped — vit dans WaitForShutdownAsync(), que StartAsync
            // n'appelle pas (c'est RunAsync, le chemin du Cli, qui l'utilise).
            // S'abonner à ApplicationStopped attendrait donc un événement qui n'arrive
            // jamais. C'est ArreterServeurAsync qui DÉCLENCHE l'arrêt de l'hôte, elle
            // n'en attend pas la confirmation.
            app.Lifetime.ApplicationStopping.Register(
                () => Dispatcher.Invoke(() => _ = ArreterServeurAsync()));
            try
            {
                Process.Start(new ProcessStartInfo(
                    url.Replace("127.0.0.1", "localhost")) { UseShellExecute = true });
            }
            catch { /* pas de navigateur : rien de plus à tenter */ }
            return;
        }

        var store = app.Services.GetRequiredService<StateStore>();
        var fenetre = new MainWindow(url, store);
        fenetre.Closed += async (_, _) => await ArreterServeurAsync();
        MainWindow = fenetre;
        fenetre.Show();
    }

    /// <summary>
    /// Arrêt complet : hôte web puis application. `StopAsync` NE SUFFIT PAS — le
    /// `Stop()` + `Drop()` de la trace SSAS vit dans le `Dispose()` de `ProfilerService`,
    /// singleton du conteneur, et seul `DisposeAsync` sur l'hôte détruit ce conteneur.
    /// `app.Run()` s'en chargeait dans son propre `finally` ; en pilotant nous-mêmes le
    /// cycle de vie, on reprend l'obligation. Sans elle, chaque fermeture laisse une
    /// trace `CubeScope_Profiler_&lt;pid&gt;` orpheline sur un serveur SSAS partagé.
    /// Réentrant : appelable depuis la fermeture de la fenêtre comme depuis l'arrêt
    /// de l'hôte, sans doubler le travail.
    /// </summary>
    private async Task ArreterServeurAsync()
    {
        var serveur = _serveur;
        if (serveur is null) return;
        _serveur = null;
        try
        {
            await serveur.StopAsync(TimeSpan.FromSeconds(5));
            await serveur.DisposeAsync();
        }
        catch { /* on s'arrête de toute façon */ }
        finally
        {
            Shutdown();
        }
    }

    private static bool RuntimeWebView2Disponible()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }
}
