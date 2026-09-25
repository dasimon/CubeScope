using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CubeScope.Core.State;
using Microsoft.Web.WebView2.Core;

namespace CubeScope.Shell;

public partial class MainWindow : Window
{
    private readonly string _url;
    private readonly StateStore _store;
    private bool _fermetureConfirmee;
    private bool _verificationEnCours;

    public MainWindow(string url, StateStore store)
    {
        _url = url;
        _store = store;
        InitializeComponent();
        RestaurerGeometrie();
        Loaded += async (_, _) => await DemarrerVueAsync();
        Closing += AuMomentDeFermer;
    }

    /// <summary>
    /// WebView2 initialization failed: the window is unusable, the application
    /// is not. The host (App) switches to the browser fallback.
    /// </summary>
    internal event EventHandler<EchecInitialisationEventArgs>? EchecInitialisation;

    /// <summary>
    /// `Loaded` wires an `async void`: without this safety net, a failure of
    /// `CoreWebView2Environment.CreateAsync` / `EnsureCoreWebView2Async` (corrupted data
    /// folder, full disk, enterprise policy) becomes an unhandled exception on
    /// the Dispatcher thread, hence a raw crash at startup.
    /// </summary>
    private async Task DemarrerVueAsync()
    {
        try
        {
            await InitialiserVueAsync();
        }
        catch (Exception ex)
        {
            // Get off the screen BEFORE notifying: the fallback dialog must not
            // show up in front of an empty window that will never be used.
            Hide();
            EchecInitialisation?.Invoke(this, new EchecInitialisationEventArgs(ex.Message));
            FermerSansDemander();
        }
    }

    /// <summary>
    /// Unsaved work in the Script panel was protected by a page-side `beforeunload`.
    /// Destroying a WebView2 control does NOT go through the browser's closing
    /// path: that handler is never evaluated here, and the window's close button would lose
    /// the work without a word. So we ask the question again ourselves.
    ///
    /// The answer is asynchronous (`ExecuteScriptAsync`) whereas `Closing` is synchronous:
    /// this close is cancelled, the page is queried, then `Close()` is called again once
    /// the answer is known. `_fermetureConfirmee` tells the two passes apart.
    /// </summary>
    private void AuMomentDeFermer(object? sender, CancelEventArgs e)
    {
        // Saved on EVERY pass, not only on the confirmed close: WPF ignores
        // `e.Cancel` when the close comes from an `Application.Shutdown()` (end of Windows
        // session), and the geometry would then be lost. The write is a single-row
        // upsert: replaying it costs nothing, and the last pass always wins.
        EnregistrerGeometrie();

        if (_fermetureConfirmee) return;

        e.Cancel = true;

        // Without this guard, each click on the close button during the check starts another one:
        // the continuations resume inside the nested pump of the first `MessageBox`
        // (stacked dialogs), and the loser's `Close()` throws on an already closed window.
        // The window of opportunity widens precisely when the page is slow — that is, when
        // the user clicks again.
        if (_verificationEnCours) return;
        _verificationEnCours = true;
        _ = ConfirmerPuisFermerAsync();
    }

    private async Task ConfirmerPuisFermerAsync()
    {
        try
        {
            // Returns the JSON string "true" or "false"; anything else (null, undefined,
            // page not loaded yet) reads as "nothing to lose".
            //
            // Time-bounded: `ExecuteScriptAsync` is posted to the renderer's JS
            // thread, and while synchronous work keeps it busy the promise NEVER
            // resolves — on this product that is not theoretical (the Script panel handles
            // hundreds of commands). Without a timeout, the close button would become inert: a
            // safeguard that prevents quitting the application is worse than no safeguard.
            var interrogation = Vue.CoreWebView2.ExecuteScriptAsync("window.__cubescopeDirty === true");
            if (await Task.WhenAny(interrogation, Task.Delay(TimeSpan.FromSeconds(2))) != interrogation)
            {
                FermerSansDemander();
                return;
            }

            if (await interrogation == "true")
            {
                var choix = MessageBox.Show(
                    this,
                    "Le script MDX contient des modifications non enregistrées.\n\n"
                    + "Fermer CubeScope quand même ? Ces modifications seront perdues.",
                    "CubeScope",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (choix != MessageBoxResult.Yes)
                {
                    // The window stays open, and the question will be asked again on the next click.
                    _verificationEnCours = false;
                    return;
                }
            }
        }
        catch
        {
            // WebView2 not initialized, page not loaded, script error: a safeguard
            // that would prevent quitting the application would be worse than no safeguard.
        }

        FermerSansDemander();
    }

    /// <summary>Closes while skipping the question — path for the browser fallback and for an already obtained confirmation.</summary>
    internal void FermerSansDemander()
    {
        _fermetureConfirmee = true;
        Close();
    }

    private void RestaurerGeometrie()
    {
        var etat = _store.GetWindowState();
        if (etat is null) return;

        // A screen unplugged since the last session would leave the window off screen.
        // VirtualScreen* covers ALL monitors (WorkArea is limited to the primary one, which
        // would wrongly reject any position on a secondary screen).
        bool utilisable = GeometrieDecider.GeometrieUtilisable(
            etat.X, etat.Y, etat.Width, etat.Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!utilisable) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = etat.X;
        Top = etat.Y;
        Width = etat.Width;
        Height = etat.Height;
        if (etat.Maximized) WindowState = System.Windows.WindowState.Maximized;
    }

    private void EnregistrerGeometrie()
    {
        // RestoreBounds gives the geometry from before maximizing: without it, a
        // window closed while maximized would reopen full screen and then, once restored,
        // would still fill the whole screen.
        bool maximise = WindowState == System.Windows.WindowState.Maximized;
        var r = maximise ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _store.SaveWindowState(r.X, r.Y, r.Width, r.Height, maximise);
    }

    private static void OuvrirDansLeNavigateur(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* dead link or no browser: nothing more to try */ }
    }

    private async Task InitialiserVueAsync()
    {
        // PITFALL: by default WebView2 creates its data folder NEXT TO the exe
        // ({name}.exe.WebView2) and fails if the folder is not writable —
        // exactly the failure mode of the v0.1.0 exe once moved. Microsoft
        // explicitly recommends a custom location in WPF.
        string dossier = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CubeScope", "WebView2");
        Directory.CreateDirectory(dossier);

        var env = await CoreWebView2Environment.CreateAsync(null, dossier);
        await Vue.EnsureCoreWebView2Async(env);

        var config = Vue.CoreWebView2.Settings;

        // Removes Ctrl+R / Ctrl+W / Ctrl+P: no more accidental reload that loses
        // the editor. Intended side effect: F12 stops being grabbed by the
        // devtools and becomes available to the application again.
        config.AreBrowserAcceleratorKeysEnabled = false;

        // Removes the native Edge menu ("Back", "Save as"). The Monaco and PrimeVue
        // context menus are HTML: they are not affected.
        config.AreDefaultContextMenusEnabled = false;

        // The devtools remain accessible, but behind an explicit shortcut.
        config.AreDevToolsEnabled = true;

        // An external link opens the browser rather than a bare WebView2 window,
        // with no address bar and no way back. http/https only (see NavigationPolicy.IsWebUrl).
        Vue.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (NavigationPolicy.IsWebUrl(args.Uri)) OuvrirDansLeNavigateur(args.Uri);
        };

        // Same thing for a plain link (no target=_blank), e.g. in an AI answer: the WebView never
        // leaves the application's origin. Registered BEFORE Source, which it lets through.
        Vue.CoreWebView2.NavigationStarting += (_, args) =>
        {
            var decision = NavigationPolicy.Decide(args.Uri, _url);
            if (decision == NavigationDecision.Allow) return;
            args.Cancel = true;
            if (decision == NavigationDecision.OpenExternally) OuvrirDansLeNavigateur(args.Uri);
        };

        Vue.Source = new Uri(_url);

        // AreBrowserAcceleratorKeysEnabled = false also disabled Ctrl+Shift+I and zoom:
        // we wire them back ourselves, they are the only browser shortcuts we keep.
        Vue.KeyDown += (_, e) =>
        {
            if (e.Key == Key.I
                && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                Vue.CoreWebView2.OpenDevToolsWindow();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Zoom is a feature of the embedded browser, not of the web content.
                // AreBrowserAcceleratorKeysEnabled = false turned it off along with the other shortcuts,
                // and no application-level zoom replaces it on the Vue side — so we give it back to the user.
                const double zoomStep = 0.1;
                const double minZoom = 0.5;
                const double maxZoom = 3.0;

                if (e.Key == Key.OemPlus || e.Key == Key.Add)
                {
                    Vue.ZoomFactor = Math.Min(Vue.ZoomFactor + zoomStep, maxZoom);
                    e.Handled = true;
                }
                else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
                {
                    Vue.ZoomFactor = Math.Max(Vue.ZoomFactor - zoomStep, minZoom);
                    e.Handled = true;
                }
                else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
                {
                    Vue.ZoomFactor = 1.0;
                    e.Handled = true;
                }
            }
        };
    }
}

/// <summary>Readable reason for the WebView2 initialization failure, to show to the user.</summary>
internal sealed class EchecInitialisationEventArgs(string raison) : EventArgs
{
    public string Raison { get; } = raison;
}
