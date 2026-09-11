using System.Diagnostics;
using System.Windows;
using CubeScope.Core.State;
using CubeScope.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace CubeScope.Shell;

public partial class App : Application
{
    private WebApplication? _serveur;
    private bool _basculeNavigateur;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await DemarrerAsync(e);
        }
        catch (Exception ex)
        {
            // `OnStartup` est un `async void` : sans ce filet, la moindre défaillance du
            // démarrage (le cas banal étant un port déjà pris — `--port 5199` pendant que
            // la boucle de dev tourne) sort en exception non gérée et l'utilisateur reçoit
            // une boîte de dialogue .NET illisible.
            MessageBox.Show(
                "CubeScope n'a pas pu démarrer.\n\n"
                + ex.Message + "\n\n"
                + "Si le port demandé est déjà utilisé (autre instance de CubeScope, "
                + "serveur de développement), fermez-la puis relancez.",
                "CubeScope",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            if (_serveur is not null) await ArreterServeurAsync();
            else Shutdown();
        }
    }

    private async Task DemarrerAsync(StartupEventArgs e)
    {
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
            RelayerArretDeLHote(app);
            // Cas nominal du repli : pas de dialogue à fermer à chaque lancement — le
            // navigateur qui s'ouvre EST le signal que l'application tourne. Reste une
            // trace côté journal pour le diagnostic, l'exe étant un WinExe sans console.
            app.Logger.LogInformation("Repli navigateur : interface ouverte sur {Url}", url);
            if (!OuvrirNavigateur(url)) await ArreterServeurAsync();
            return;
        }

        var store = app.Services.GetRequiredService<StateStore>();
        var fenetre = new MainWindow(url, store);
        fenetre.EchecInitialisation += (_, args) => BasculerVersNavigateur(app, url, args.Raison);
        fenetre.Closed += async (_, _) =>
        {
            // Après bascule, le serveur sert le navigateur : la fenêtre disparaît, l'hôte
            // reste — c'est BrowserLifetime (armé par la bascule) qui l'arrêtera.
            if (_basculeNavigateur) return;
            await ArreterServeurAsync();
        };
        MainWindow = fenetre;
        fenetre.Show();
    }

    /// <summary>
    /// BrowserLifetime arrête l'HÔTE WEB, pas l'application WPF. Sans ce relais, l'exe
    /// survivrait sans fenêtre ni serveur, invisible et increvable.
    ///
    /// ApplicationStopping, PAS ApplicationStopped : BrowserLifetime appelle
    /// IHostApplicationLifetime.StopApplication(), qui ne déclenche QUE
    /// ApplicationStopping. Le pont vers StopAsync() — et donc vers ApplicationStopped —
    /// vit dans WaitForShutdownAsync(), que StartAsync n'appelle pas (c'est RunAsync, le
    /// chemin du Cli, qui l'utilise). S'abonner à ApplicationStopped attendrait donc un
    /// événement qui n'arrive jamais. C'est ArreterServeurAsync qui DÉCLENCHE l'arrêt de
    /// l'hôte, elle n'en attend pas la confirmation.
    /// </summary>
    private void RelayerArretDeLHote(WebApplication app) =>
        app.Lifetime.ApplicationStopping.Register(
            () => Dispatcher.Invoke(() => _ = ArreterServeurAsync()));

    /// <summary>
    /// Repli tardif : la fenêtre était le mode choisi, mais WebView2 a échoué à s'initialiser
    /// (dossier de données corrompu, disque plein, stratégie d'entreprise). Mourir sur cette
    /// exception priverait l'utilisateur d'un produit par ailleurs fonctionnel — on bascule
    /// donc sur le navigateur, comme si le runtime avait été absent au départ.
    /// </summary>
    private void BasculerVersNavigateur(WebApplication app, string url, string raison)
    {
        if (_basculeNavigateur) return;
        _basculeNavigateur = true;

        // Le lifetime navigateur était désarmé (on partait en mode fenêtre) : sans ça,
        // fermer l'onglet laisserait l'exe tourner sans rien à l'écran — exactement le
        // mode de panne que ce repli doit éviter.
        app.Services.GetRequiredService<BrowserLifetime>().Enabled = true;
        RelayerArretDeLHote(app);

        if (!OuvrirNavigateur(url))
        {
            _ = ArreterServeurAsync();
            return;
        }

        // Ici un dialogue se justifie : on n'est pas dans le cas nominal, et l'utilisateur
        // doit comprendre pourquoi il n'a pas la fenêtre native attendue.
        MessageBox.Show(
            "La fenêtre CubeScope n'a pas pu démarrer :\n\n"
            + raison + "\n\n"
            + "CubeScope continue dans votre navigateur :\n\n" + url,
            "CubeScope",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Ouvre l'URL dans le navigateur par défaut. En cas d'échec (association HTTP cassée ou
    /// verrouillée par stratégie — le cas des serveurs Windows / LTSC que ce repli vise),
    /// l'ancien `catch {}` vide laissait un process invisible tenir un port, sans fenêtre et
    /// sans console : il fallait le Gestionnaire des tâches. On affiche donc l'URL en clair.
    /// </summary>
    private static bool OuvrirNavigateur(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                url.Replace("127.0.0.1", "localhost")) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "CubeScope n'a pas pu ouvrir votre navigateur (" + ex.Message + ").\n\n"
                + "Ouvrez cette adresse à la main, SANS fermer ce message :\n\n"
                + url + "\n\n"
                + "CubeScope s'arrêtera à la fermeture de ce message.",
                "CubeScope",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
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
        catch
        {
            // C'est une SONDE : tout échec vaut « non ». Au-delà de l'absence de runtime
            // (WebView2RuntimeNotFoundException), le loader lui-même peut ne pas être
            // extractible — antivirus, %TEMP% plein, dossier d'extraction en lecture seule —
            // et lever DllNotFoundException ou BadImageFormatException. Laisser passer ces
            // cas ferait planter le démarrage au lieu de basculer sur le repli navigateur.
            return false;
        }
    }
}
