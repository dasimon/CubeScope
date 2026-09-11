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
    /// L'initialisation de WebView2 a échoué : la fenêtre est inutilisable, l'application
    /// ne l'est pas. L'hôte (App) bascule sur le repli navigateur.
    /// </summary>
    internal event EventHandler<EchecInitialisationEventArgs>? EchecInitialisation;

    /// <summary>
    /// `Loaded` branche un `async void` : sans ce filet, une défaillance de
    /// `CoreWebView2Environment.CreateAsync` / `EnsureCoreWebView2Async` (dossier de données
    /// corrompu, disque plein, stratégie d'entreprise) devient une exception non gérée sur
    /// le thread du Dispatcher, donc un crash brut au démarrage.
    /// </summary>
    private async Task DemarrerVueAsync()
    {
        try
        {
            await InitialiserVueAsync();
        }
        catch (Exception ex)
        {
            // Se retirer de l'écran AVANT de prévenir : le dialogue du repli ne doit pas
            // s'afficher devant une fenêtre vide qui ne servira jamais.
            Hide();
            EchecInitialisation?.Invoke(this, new EchecInitialisationEventArgs(ex.Message));
            FermerSansDemander();
        }
    }

    /// <summary>
    /// Le travail non enregistré du panneau Script était protégé par un `beforeunload` côté
    /// page. Détruire un contrôle WebView2 ne passe PAS par le chemin de fermeture du
    /// navigateur : ce handler n'est jamais évalué ici, et la croix de la fenêtre perdrait
    /// le travail sans un mot. On repose donc la question nous-mêmes.
    ///
    /// La réponse est asynchrone (`ExecuteScriptAsync`) alors que `Closing` est synchrone :
    /// on annule cette fermeture-ci, on interroge la page, puis on rappelle `Close()` une
    /// fois la réponse connue. `_fermetureConfirmee` distingue les deux passages.
    /// </summary>
    private void AuMomentDeFermer(object? sender, CancelEventArgs e)
    {
        // Enregistrée à CHAQUE passage, pas seulement sur la fermeture confirmée : WPF ignore
        // `e.Cancel` quand la fermeture vient d'un `Application.Shutdown()` (fin de session
        // Windows), et la géométrie serait alors perdue. L'écriture est un upsert d'une seule
        // ligne : la rejouer ne coûte rien, et c'est toujours le dernier passage qui gagne.
        EnregistrerGeometrie();

        if (_fermetureConfirmee) return;

        e.Cancel = true;

        // Sans ce garde, chaque clic sur la croix pendant la vérification en relance une :
        // les continuations reprennent dans la pompe imbriquée de la première `MessageBox`
        // (dialogues empilés), et le `Close()` du perdant lève sur une fenêtre déjà fermée.
        // La fenêtre de tir s'élargit précisément quand la page est lente — donc quand
        // l'utilisateur reclique.
        if (_verificationEnCours) return;
        _verificationEnCours = true;
        _ = ConfirmerPuisFermerAsync();
    }

    private async Task ConfirmerPuisFermerAsync()
    {
        try
        {
            // Renvoie la chaîne JSON "true" ou "false" ; tout le reste (null, undefined,
            // page pas encore chargée) se lit comme « rien à perdre ».
            //
            // Borné dans le temps : `ExecuteScriptAsync` est posté sur le thread JS du
            // renderer, et tant qu'un traitement synchrone l'occupe la promesse ne se résout
            // JAMAIS — sur ce produit ce n'est pas théorique (le panneau Script manipule
            // des centaines de commandes). Sans délai, la croix deviendrait inerte : un
            // garde-fou qui empêche de quitter l'application est pire que pas de garde-fou.
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
                    // La fenêtre reste ouverte, et la question sera reposée au prochain clic.
                    _verificationEnCours = false;
                    return;
                }
            }
        }
        catch
        {
            // WebView2 pas initialisée, page pas chargée, script en erreur : un garde-fou
            // qui empêcherait de quitter l'application serait pire que pas de garde-fou.
        }

        FermerSansDemander();
    }

    /// <summary>Ferme en court-circuitant la question — chemin du repli navigateur et de la confirmation acquise.</summary>
    internal void FermerSansDemander()
    {
        _fermetureConfirmee = true;
        Close();
    }

    private void RestaurerGeometrie()
    {
        var etat = _store.GetWindowState();
        if (etat is null) return;

        // Un écran débranché depuis la dernière session laisserait la fenêtre hors champ.
        // VirtualScreen* couvre TOUS les moniteurs (WorkArea se limite au principal, ce qui
        // rejetterait à tort toute position sur un écran secondaire).
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
        // RestoreBounds donne la géométrie d'avant l'agrandissement : sans ça, une
        // fenêtre fermée maximisée rouvrirait plein écran puis, une fois restaurée,
        // occuperait tout l'écran.
        bool maximise = WindowState == System.Windows.WindowState.Maximized;
        var r = maximise ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _store.SaveWindowState(r.X, r.Y, r.Width, r.Height, maximise);
    }

    private async Task InitialiserVueAsync()
    {
        // PIÈGE : par défaut WebView2 crée son dossier de données À CÔTÉ de l'exe
        // ({nom}.exe.WebView2) et échoue si le dossier n'est pas accessible en écriture —
        // exactement le mode de panne de l'exe v0.1.0 une fois déplacé. Microsoft
        // recommande explicitement un emplacement personnalisé en WPF.
        string dossier = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CubeScope", "WebView2");
        Directory.CreateDirectory(dossier);

        var env = await CoreWebView2Environment.CreateAsync(null, dossier);
        await Vue.EnsureCoreWebView2Async(env);

        var config = Vue.CoreWebView2.Settings;

        // Supprime Ctrl+R / Ctrl+W / Ctrl+P : plus de rechargement accidentel qui fait
        // perdre l'éditeur. Effet de bord recherché : F12 cesse d'être confisqué par les
        // devtools et redevient disponible pour l'application.
        config.AreBrowserAcceleratorKeysEnabled = false;

        // Retire le menu natif Edge (« Précédent », « Enregistrer sous »). Les menus
        // contextuels de Monaco et de PrimeVue sont du HTML : ils ne sont pas touchés.
        config.AreDefaultContextMenusEnabled = false;

        // Les devtools restent accessibles, mais derrière un raccourci explicite.
        config.AreDevToolsEnabled = true;

        // Un lien externe ouvre le navigateur plutôt qu'une fenêtre WebView2 nue,
        // sans barre d'adresse ni retour possible.
        Vue.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
            }
            catch { /* lien mort ou pas de navigateur : rien de plus à tenter */ }
        };

        Vue.Source = new Uri(_url);

        // AreBrowserAcceleratorKeysEnabled = false a aussi désactivé Ctrl+Shift+I et le zoom :
        // on les rebranche nous-mêmes, ce sont les seuls raccourcis navigateur qu'on garde.
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
                // Le zoom est une fonction du navigateur embarqué, pas du contenu web.
                // AreBrowserAcceleratorKeysEnabled = false l'a coupé avec les autres raccourcis,
                // et aucun zoom applicatif ne le remplace côté Vue — on le rend donc à l'utilisateur.
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

/// <summary>Raison lisible de l'échec d'initialisation de WebView2, à montrer à l'utilisateur.</summary>
internal sealed class EchecInitialisationEventArgs(string raison) : EventArgs
{
    public string Raison { get; } = raison;
}
