using System.Text.Json.Serialization;

namespace CubeScope.Core.Updates;

/// <summary>What the checker is doing; anything but <see cref="Idle"/> makes it busy. Serialised
/// by name: the SPA and the PivotScope bridge compare strings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdatePhase>))]
public enum UpdatePhase
{
    Idle,
    Checking,
    Downloading,

    /// <summary>The deliverable is verified and the application is putting it in place.</summary>
    Installing,
}

/// <summary>A newer release than the running build.</summary>
/// <param name="Version">"0.19.0".</param>
/// <param name="PageUrl">The release page (release notes).</param>
/// <param name="Size">The deliverable's size in bytes.</param>
public sealed record AvailableUpdate(string Version, Uri PageUrl, long Size);

/// <summary>A snapshot of the checker, safe to serialise to a UI.</summary>
/// <param name="CanUpdate">False for a build that is not on the release channel: nothing is ever asked.</param>
/// <param name="CurrentVersion">The running version, or null when it is not plain MAJOR.MINOR.PATCH.</param>
/// <param name="Available">A newer release, or null (up to date, or not checked yet).</param>
/// <param name="LastCheckUtc">When the last check ended, successful or not.</param>
/// <param name="LastError">Why the last check or download failed, in English; null after a success.</param>
/// <param name="Phase">What is running now.</param>
/// <param name="Progress">Fraction of the deliverable received while <see cref="UpdatePhase.Downloading"/>.</param>
public sealed record UpdateStatus(
    bool CanUpdate,
    string? CurrentVersion,
    AvailableUpdate? Available,
    DateTime? LastCheckUtc,
    string? LastError,
    UpdatePhase Phase,
    double Progress);

/// <summary>
/// One application's update state: checks GitHub for a newer release and downloads it verified.
/// One operation at a time — a check or a download asked while another runs returns the current
/// status untouched. What happens to the download (swap in place, copy to Downloads) belongs to the
/// application: a successful <see cref="DownloadAsync"/> leaves the checker in
/// <see cref="UpdatePhase.Installing"/> until the application calls <see cref="EndInstall"/>, so that
/// no check or second download slips in between.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly object _gate = new();
    private readonly UpdateBuild _build;
    private readonly ReleaseClient? _client;
    private readonly TimeProvider _clock;

    private LatestRelease? _latest;
    private DateTime? _lastCheckUtc;
    private string? _lastError;
    private UpdatePhase _phase = UpdatePhase.Idle;
    private double _progress;

    /// <param name="handler">The network handler (tests stand in for GitHub); by default
    /// <see cref="ReleaseClient.CreateHandler"/>. Unused, and never created, for a build that cannot update.</param>
    public UpdateChecker(UpdateProduct product, UpdateBuild build, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
        _clock = clock ?? TimeProvider.System;
        if (build.CanUpdate)
            _client = new ReleaseClient(handler ?? ReleaseClient.CreateHandler(), product, build.Version!.Value.ToString());
        else
            handler?.Dispose();
    }

    public bool CanUpdate => _client is not null;

    public UpdateStatus Status
    {
        get
        {
            lock (_gate) return Snapshot();
        }
    }

    /// <summary>Asks GitHub for the latest release. Never throws for a network or content problem:
    /// the reason goes to <see cref="UpdateStatus.LastError"/>, and a release seen before stays
    /// offered (a failed check does not forget a known update).</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        if (_client is null) return Status;
        lock (_gate)
        {
            if (_phase != UpdatePhase.Idle) return Snapshot();
            _phase = UpdatePhase.Checking;
        }

        try
        {
            var release = await _client.GetLatestAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                _latest = release.Version > _build.Version!.Value ? release : null;
                _lastError = null;
            }
        }
        catch (UpdateException ex)
        {
            lock (_gate) _lastError = ex.Message;
        }
        finally
        {
            lock (_gate)
            {
                _lastCheckUtc = _clock.GetUtcNow().UtcDateTime;
                _phase = UpdatePhase.Idle;
            }
        }

        return Status;
    }

    /// <summary>Downloads the available release into <paramref name="directory"/> and verifies it.
    /// On success the checker stays in <see cref="UpdatePhase.Installing"/>: call
    /// <see cref="EndInstall"/> once the file has been dealt with.</summary>
    /// <returns>The verified file and its release, or null when nothing is available or another
    /// operation runs.</returns>
    /// <exception cref="UpdateException">The download or its verification failed (also kept in
    /// <see cref="UpdateStatus.LastError"/>); nothing is left on disk.</exception>
    public async Task<(LatestRelease Release, VerifiedDownload File)?> DownloadAsync(string directory, CancellationToken ct)
    {
        LatestRelease release;
        lock (_gate)
        {
            if (_client is null || _latest is null || _phase != UpdatePhase.Idle) return null;
            release = _latest;
            _phase = UpdatePhase.Downloading;
            _progress = 0;
        }

        try
        {
            var progress = new InlineProgress(p =>
            {
                lock (_gate) _progress = p;
            });
            var file = await _client!.DownloadAsync(release, directory, progress, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _lastError = null;
                _progress = 1;
                _phase = UpdatePhase.Installing;
            }
            return (release, file);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ex is UpdateException) _lastError = ex.Message;
                _phase = UpdatePhase.Idle;
                _progress = 0;
            }
            throw;
        }
    }

    /// <summary>Ends the <see cref="UpdatePhase.Installing"/> phase a successful download started.</summary>
    /// <param name="error">Why the install failed, or null.</param>
    public void EndInstall(string? error)
    {
        lock (_gate)
        {
            if (_phase == UpdatePhase.Installing) _phase = UpdatePhase.Idle;
            _progress = 0;
            if (error is not null) _lastError = error;
        }
    }

    public void Dispose() => _client?.Dispose();

    private UpdateStatus Snapshot() => new(
        CanUpdate,
        _build.Version?.ToString(),
        _latest is { } r ? new AvailableUpdate(r.Version.ToString(), r.PageUrl, r.AssetSize) : null,
        _lastCheckUtc,
        _lastError,
        _phase,
        _progress);

    /// <summary>Reports on the reporting thread: <see cref="Progress{T}"/> would post to a captured
    /// context, and a UI-less host has none to post to.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
