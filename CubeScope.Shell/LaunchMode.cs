namespace CubeScope.Shell;

/// <summary>Surface d'affichage retenue au démarrage.</summary>
public enum LaunchMode
{
    /// <summary>Fenêtre native avec WebView2.</summary>
    Window,

    /// <summary>Navigateur par défaut — comportement historique de CubeScope.</summary>
    Browser,
}

public static class LaunchModeDecider
{
    /// <summary>
    /// Le runtime WebView2 est pré-installé sur Windows 11 et sur Windows 10 1803+ à jour,
    /// mais peut manquer sur Windows Server et sur les éditions LTSC : le repli navigateur
    /// n'est pas théorique. `--force-browser` emprunte le même chemin à la demande, ce qui
    /// rend ce repli testable sans démonter une machine.
    /// </summary>
    public static LaunchMode Decide(bool runtimeDisponible, bool forceBrowser)
        => runtimeDisponible && !forceBrowser ? LaunchMode.Window : LaunchMode.Browser;
}
