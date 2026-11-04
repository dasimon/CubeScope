using CubeScope.Server;

namespace CubeScope.Shell;

/// <summary>The Shell side of the built-in update: called from the update service's background
/// task, so the window work is posted to the UI thread (BeginInvoke: the caller must not wait on
/// a close that may ask the user a question).</summary>
internal sealed class ShellRestarter(App app) : IAppRestarter
{
    public bool CanRestart => true;

    public void RequestRestart() => app.Dispatcher.BeginInvoke(app.RedemarrerApresMiseAJour);
}
