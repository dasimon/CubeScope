namespace CubeScope.Shell;

/// <summary>Display surface chosen at startup.</summary>
public enum LaunchMode
{
    /// <summary>Native window with WebView2.</summary>
    Window,

    /// <summary>Default browser — CubeScope's historical behaviour.</summary>
    Browser,
}

public static class LaunchModeDecider
{
    /// <summary>
    /// The WebView2 runtime is preinstalled on Windows 11 and on up-to-date Windows 10 1803+,
    /// but may be missing on Windows Server and on LTSC editions: the browser fallback
    /// is not theoretical. `--force-browser` takes the same path on demand, which
    /// makes this fallback testable without tearing a machine apart.
    /// </summary>
    public static LaunchMode Decide(bool runtimeDisponible, bool forceBrowser)
        => runtimeDisponible && !forceBrowser ? LaunchMode.Window : LaunchMode.Browser;
}
