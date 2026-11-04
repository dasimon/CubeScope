using System.Reflection;
using CubeScope.Core.State;
using CubeScope.Core.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeScope.Server;

/// <summary>Closes the application and starts the executable that is now on disk. The Shell
/// implements it; the dev loop (CubeScope.Server.Cli) has nothing to restart.</summary>
public interface IAppRestarter
{
    bool CanRestart { get; }

    /// <summary>Asks for the restart and returns at once. The close may still be cancelled by the
    /// user (unsaved MDX Script): the new version then starts when the window is finally closed.</summary>
    void RequestRestart();
}

internal sealed class NoAppRestarter : IAppRestarter
{
    public bool CanRestart => false;

    public void RequestRestart() { }
}

/// <summary>How the last install attempt ended.</summary>
/// <param name="Kind">restarting | savedToDownloads | failed.</param>
/// <param name="Detail">The saved file's path, or the reason of the failure (English).</param>
public sealed record UpdateOutcome(string Kind, string? Detail);

/// <summary>What the SPA shows: the checker's snapshot plus what only the application knows.</summary>
public sealed record UpdateView(UpdateStatus Status, bool AutoCheck, UpdateOutcome? LastOutcome);

/// <summary>
/// CubeScope's built-in update, after Lucarne's: 10 s after start, then once a day, it reads the
/// latest release of dasimon/CubeScope; a newer one is offered in the status bar. Installing
/// downloads cubescope.exe and its .sha256 from GitHub only, checks the hash (and GitHub's digest),
/// swaps the executable in place keeping cubescope.exe.old, then asks the Shell to restart. When
/// the folder cannot be written (Program Files), the verified file goes to Downloads instead.
/// Everything network-side stays here: the SPA never leaves the loopback (CSP, LocalRequestGuard).
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly UpdateChecker _checker;
    private readonly StateStore _store;
    private readonly IAppRestarter _restarter;
    private readonly ILogger<UpdateService> _log;
    private readonly string? _target;
    private readonly string _workDirectory;
    private volatile UpdateOutcome? _lastOutcome;

    /// <summary>The executable on disk is already the new one: nothing more is installed by this
    /// process (a second swap would try to overwrite the running file, now cubescope.exe.old).</summary>
    private volatile bool _installed;

    public UpdateService(StateStore store, IAppRestarter restarter, ILogger<UpdateService> log)
    {
        _store = store;
        _restarter = restarter;
        _log = log;
        _workDirectory = Path.Combine(Path.GetDirectoryName(StateStore.DefaultDbPath)!, "update");

        // Only the published single file replaces itself: under `dotnet run` the process is
        // dotnet.exe, and a stamped build launched from anywhere else is not swapped either.
        var process = Environment.ProcessPath;
        _target = process is not null
            && string.Equals(Path.GetFileName(process), UpdateProduct.CubeScope.AssetName, StringComparison.OrdinalIgnoreCase)
            ? process
            : null;

        var build = _target is null ? new UpdateBuild(null, null) : UpdateBuild.Read(Assembly.GetEntryAssembly());
        _checker = new UpdateChecker(UpdateProduct.CubeScope, build);
    }

    public UpdateView View => new(_checker.Status, _store.GetUpdateAutoCheck(), _lastOutcome);

    public async Task<UpdateView> CheckAsync(CancellationToken ct)
    {
        var status = await _checker.CheckAsync(ct);
        if (status.LastError is { } error) _log.LogInformation("Update check failed: {Reason}", error);
        return View;
    }

    public UpdateView SetAutoCheck(bool enabled)
    {
        _store.SetUpdateAutoCheck(enabled);
        return View;
    }

    /// <summary>Starts the download and install in the background. False when nothing is
    /// available or an operation already runs; the SPA follows the phase through <see cref="View"/>.</summary>
    public bool StartInstall()
    {
        var status = _checker.Status;
        if (_installed || !_restarter.CanRestart || _target is null || status.Available is null || status.Phase != UpdatePhase.Idle)
            return false;
        _lastOutcome = null;
        _ = Task.Run(InstallAsync);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_checker.CanUpdate || _target is null) return;

        // What the previous update left behind: cubescope.exe.old (now unlocked) and the download.
        UpdateSwap.CleanUp(_target, _workDirectory);

        try
        {
            await Task.Delay(FirstCheckDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_store.GetUpdateAutoCheck()) await CheckAsync(stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task InstallAsync()
    {
        (LatestRelease Release, VerifiedDownload File)? download;
        try
        {
            download = await _checker.DownloadAsync(_workDirectory, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _lastOutcome = new UpdateOutcome("failed", ex is UpdateException ? ex.Message : $"download failed: {ex.Message}");
            _log.LogWarning(ex, "Update download failed");
            return;
        }
        if (download is not { } d) return;

        string? error = null;
        try
        {
            switch (UpdateSwap.Apply(d.File.Path, d.File.Sha256, _target!))
            {
                case SwapResult.Done:
                    _log.LogInformation("Updated to {Version}; restarting", d.Release.Version);
                    _installed = true;
                    _lastOutcome = new UpdateOutcome("restarting", d.Release.Version.ToString());
                    _restarter.RequestRestart();
                    break;

                case SwapResult.HashMismatch:
                    error = "the copy beside the executable does not have the verified hash";
                    _lastOutcome = new UpdateOutcome("failed", error);
                    break;

                default:
                    // A protected folder, or cubescope.exe.old locked by another running CubeScope.
                    var saved = UpdateDownloads.Save(d.File, $"cubescope-{d.Release.Version}.exe");
                    UpdateDownloads.Reveal(saved);
                    _log.LogInformation("Executable folder not writable; {Version} saved to {Path}", d.Release.Version, saved);
                    _lastOutcome = new UpdateOutcome("savedToDownloads", saved);
                    break;
            }
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            _lastOutcome = new UpdateOutcome("failed", error);
            _log.LogWarning(ex, "Update install failed");
        }
        finally
        {
            _checker.EndInstall(error);
        }
    }

    public override void Dispose()
    {
        _checker.Dispose();
        base.Dispose();
    }
}
