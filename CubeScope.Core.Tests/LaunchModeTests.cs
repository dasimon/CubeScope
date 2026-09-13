using CubeScope.Shell;

namespace CubeScope.Core.Tests;

/// <summary>
/// The window / browser choice is the only branching logic in the Shell, and it is the one
/// that must survive a machine without the WebView2 runtime (Windows Server, LTSC).
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
