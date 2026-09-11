using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace CubeScope.Shell;

public partial class MainWindow : Window
{
    private readonly string _url;

    public MainWindow(string url)
    {
        _url = url;
        InitializeComponent();
        Loaded += async (_, _) => await InitialiserVueAsync();
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
