using CubeScope.Shell;

namespace CubeScope.Core.Tests;

/// <summary>
/// Le choix fenêtre / navigateur est la seule logique branchante du Shell, et c'est
/// celle qui doit survivre à une machine sans runtime WebView2 (Windows Server, LTSC).
/// </summary>
public class LaunchModeTests
{
    [Fact]
    public void Runtime_present_et_pas_de_drapeau_donne_la_fenetre()
        => Assert.Equal(LaunchMode.Window,
            LaunchModeDecider.Decide(runtimeDisponible: true, forceBrowser: false));

    [Fact]
    public void Runtime_absent_donne_le_navigateur()
        => Assert.Equal(LaunchMode.Browser,
            LaunchModeDecider.Decide(runtimeDisponible: false, forceBrowser: false));

    [Fact]
    public void Force_browser_gagne_meme_avec_le_runtime()
        => Assert.Equal(LaunchMode.Browser,
            LaunchModeDecider.Decide(runtimeDisponible: true, forceBrowser: true));
}
