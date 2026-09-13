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
            // `OnStartup` is an `async void`: without this safety net, the slightest startup
            // failure (the common case being a port already taken — `--port 5199` while
            // the dev loop is running) escapes as an unhandled exception and the user gets
            // an unreadable .NET dialog box.
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
        // The runtime presence check is static: it does not depend on the URL, so
        // it runs BEFORE starting Kestrel — which allows passing the right browserLifetime
        // value the first time, without mutating it afterwards.
        var mode = LaunchModeDecider.Decide(
            runtimeDisponible: RuntimeWebView2Disponible(),
            forceBrowser: e.Args.Contains("--force-browser"));

        var (app, url) = await ServerHost.StartAsync(
            e.Args, browserLifetime: mode == LaunchMode.Browser);
        _serveur = app;

        if (mode == LaunchMode.Browser)
        {
            RelayerArretDeLHote(app);
            // Nominal fallback case: no dialog to close on every launch — the
            // browser opening IS the signal that the application is running. A
            // log entry remains for diagnostics, since the exe is a WinExe without a console.
            app.Logger.LogInformation("Browser fallback: interface opened at {Url}", url);
            if (!OuvrirNavigateur(url)) await ArreterServeurAsync();
            return;
        }

        var store = app.Services.GetRequiredService<StateStore>();
        var fenetre = new MainWindow(url, store);
        fenetre.EchecInitialisation += (_, args) => BasculerVersNavigateur(app, url, args.Raison);
        fenetre.Closed += async (_, _) =>
        {
            // After the switch, the server serves the browser: the window goes away, the host
            // stays — BrowserLifetime (armed by the switch) is what will stop it.
            if (_basculeNavigateur) return;
            await ArreterServeurAsync();
        };
        MainWindow = fenetre;
        fenetre.Show();
    }

    /// <summary>
    /// BrowserLifetime stops the WEB HOST, not the WPF application. Without this relay, the exe
    /// would survive with neither window nor server, invisible and unkillable.
    ///
    /// ApplicationStopping, NOT ApplicationStopped: BrowserLifetime calls
    /// IHostApplicationLifetime.StopApplication(), which ONLY triggers
    /// ApplicationStopping. The bridge to StopAsync() — and therefore to ApplicationStopped —
    /// lives in WaitForShutdownAsync(), which StartAsync does not call (RunAsync, the
    /// Cli path, is what uses it). Subscribing to ApplicationStopped would therefore wait for an
    /// event that never comes. ArreterServeurAsync is what TRIGGERS the host
    /// shutdown, it does not wait for its confirmation.
    /// </summary>
    private void RelayerArretDeLHote(WebApplication app) =>
        app.Lifetime.ApplicationStopping.Register(
            () => Dispatcher.Invoke(() => _ = ArreterServeurAsync()));

    /// <summary>
    /// Late fallback: the window was the chosen mode, but WebView2 failed to initialize
    /// (corrupted data folder, full disk, enterprise policy). Dying on this
    /// exception would deprive the user of an otherwise working product — so we switch
    /// to the browser, as if the runtime had been missing from the start.
    /// </summary>
    private void BasculerVersNavigateur(WebApplication app, string url, string raison)
    {
        if (_basculeNavigateur) return;
        _basculeNavigateur = true;

        // The browser lifetime was disarmed (we were starting in window mode): without this,
        // closing the tab would leave the exe running with nothing on screen — exactly the
        // failure mode this fallback must avoid.
        app.Services.GetRequiredService<BrowserLifetime>().Enabled = true;
        RelayerArretDeLHote(app);

        if (!OuvrirNavigateur(url))
        {
            _ = ArreterServeurAsync();
            return;
        }

        // Here a dialog is justified: this is not the nominal case, and the user
        // must understand why they do not get the expected native window.
        MessageBox.Show(
            "La fenêtre CubeScope n'a pas pu démarrer :\n\n"
            + raison + "\n\n"
            + "CubeScope continue dans votre navigateur :\n\n" + url,
            "CubeScope",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Opens the URL in the default browser. On failure (HTTP association broken or
    /// locked by policy — the case of the Windows Server / LTSC machines this fallback targets),
    /// the former empty `catch {}` left an invisible process holding a port, with no window and
    /// no console: Task Manager was needed. So the URL is shown in plain text.
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
    /// Full shutdown: web host then application. `StopAsync` IS NOT ENOUGH — the
    /// SSAS trace's `Stop()` + `Drop()` lives in the `Dispose()` of `ProfilerService`,
    /// a container singleton, and only `DisposeAsync` on the host destroys that container.
    /// `app.Run()` took care of it in its own `finally`; by driving the lifecycle
    /// ourselves, we take over the obligation. Without it, every close leaves an
    /// orphaned `CubeScope_Profiler_&lt;pid&gt;` trace on a shared SSAS server.
    /// Reentrant: can be called from the window closing as well as from the host
    /// shutdown, without doing the work twice.
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
        catch { /* we are stopping anyway */ }
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
            // This is a PROBE: any failure means "no". Beyond a missing runtime
            // (WebView2RuntimeNotFoundException), the loader itself may not be
            // extractable — antivirus, full %TEMP%, read-only extraction folder —
            // and throw DllNotFoundException or BadImageFormatException. Letting these
            // cases through would crash startup instead of switching to the browser fallback.
            return false;
        }
    }
}
