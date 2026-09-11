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

        // AreBrowserAcceleratorKeysEnabled = false a aussi désactivé Ctrl+Shift+I :
        // on le rebranche nous-mêmes, c'est le seul raccourci navigateur qu'on garde.
        Vue.KeyDown += (_, e) =>
        {
            if (e.Key == Key.I
                && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                Vue.CoreWebView2.OpenDevToolsWindow();
                e.Handled = true;
            }
        };
    }
}
