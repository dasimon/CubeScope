using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CubeScope.Core.Updates;

namespace CubeScope.Core.Tests.Updates;

/// <summary>The shared update state: what a build may ask, what it offers, and that one operation
/// runs at a time.</summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/dasimon/CubeScope/releases/latest";
    private const string ExeUrl = "https://github.com/dasimon/CubeScope/releases/download/v0.19.0/cubescope.exe";
    private const string SumsUrl = "https://github.com/dasimon/CubeScope/releases/download/v0.19.0/cubescope.exe.sha256";

    private static readonly byte[] Exe = Encoding.ASCII.GetBytes("MZ the next CubeScope");
    private static readonly UpdateBuild Released = new(new ReleaseVersion(0, 18, 0), UpdateBuild.ReleaseChannel);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CubeScopeChecker-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();

    public UpdateCheckerTests()
    {
        _github.Bytes(SumsUrl, Encoding.ASCII.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(Exe))}  cubescope.exe\n"));
        _github.Bytes(ExeUrl, Exe);
    }

    public void Dispose()
    {
        _github.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Json(string tag, long size) =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":false,\"draft\":false," +
        "\"html_url\":\"https://github.com/dasimon/CubeScope/releases/tag/" + tag + "\",\"assets\":[" +
        $"{{\"name\":\"cubescope.exe\",\"size\":{size},\"browser_download_url\":\"{ExeUrl}\"}}," +
        $"{{\"name\":\"cubescope.exe.sha256\",\"size\":80,\"browser_download_url\":\"{SumsUrl}\"}}]}}";

    // --- The build stamp -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("Release")]
    [InlineData("test")]
    [InlineData("")]
    public void Only_the_exact_release_stamp_allows_updates(string? channel)
    {
        Assert.False(new UpdateBuild(new ReleaseVersion(0, 18, 0), channel).CanUpdate);
        Assert.True(new UpdateBuild(new ReleaseVersion(0, 18, 0), "release").CanUpdate);
    }

    [Fact]
    public void A_build_without_a_plain_version_cannot_update()
    {
        Assert.False(new UpdateBuild(null, UpdateBuild.ReleaseChannel).CanUpdate);
    }

    [Fact]
    public void The_test_assembly_is_a_plain_build()
    {
        // Neither the tests nor CI stamp a channel: a source build never asks GitHub.
        var build = UpdateBuild.Read(typeof(UpdateCheckerTests).Assembly);

        Assert.Null(build.Channel);
        Assert.False(build.CanUpdate);
        Assert.False(UpdateBuild.Read(null).CanUpdate);
    }

    // --- Checking ------------------------------------------------------------------------------

    [Fact]
    public async Task A_plain_build_never_asks_anything()
    {
        using var checker = new UpdateChecker(UpdateProduct.CubeScope, new UpdateBuild(new ReleaseVersion(0, 18, 0), null), _github);

        var status = await checker.CheckAsync(CancellationToken.None);

        Assert.False(status.CanUpdate);
        Assert.Null(status.LastCheckUtc);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        _github.Text(ApiUrl, Json("v0.19.0", Exe.Length));
        using var checker = new UpdateChecker(UpdateProduct.CubeScope, Released, _github);

        var status = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal("0.18.0", status.CurrentVersion);
        Assert.Equal("0.19.0", status.Available?.Version);
        Assert.Equal(new Uri("https://github.com/dasimon/CubeScope/releases/tag/v0.19.0"), status.Available?.PageUrl);
        Assert.Null(status.LastError);
        Assert.NotNull(status.LastCheckUtc);
        Assert.Equal(UpdatePhase.Idle, status.Phase);
    }

    [Theory]
    [InlineData("v0.18.0")]
    [InlineData("v0.17.9")]
    public async Task The_same_or_an_older_release_is_not_offered(string tag)
    {
        _github.Text(ApiUrl, Json(tag, Exe.Length));
        using var checker = new UpdateChecker(UpdateProduct.CubeScope, Released, _github);

        var status = await checker.CheckAsync(CancellationToken.None);

        Assert.Null(status.Available);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task A_failed_check_keeps_the_reason_and_the_release_already_known()
    {
        _github.Text(ApiUrl, Json("v0.19.0", Exe.Length));
        using var checker = new UpdateChecker(UpdateProduct.CubeScope, Released, _github);
        await checker.CheckAsync(CancellationToken.None);

        _github.Text(ApiUrl, "<html>proxy</html>");
        var status = await checker.CheckAsync(CancellationToken.None);

        Assert.NotNull(status.LastError);
        Assert.Equal("0.19.0", status.Available?.Version);
    }

    // --- Downloading ---------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_downloaded_when_nothing_is_available()
    {
        _github.Text(ApiUrl, Json("v0.18.0", Exe.Length));
        using var checker = new UpdateChecker(UpdateProduct.CubeScope, Released, _github);
        await checker.CheckAsync(CancellationToken.None);

        Assert.Null(await checker.DownloadAsync(_directory, CancellationToken.None));
    }

    [Fact]
    public async Task A_download_stays_busy_until_the_install_ends()
    {
        _github.Text(ApiUrl, Json("v0.19.0", Exe.Length));
        using var checker = new UpdateChecker(UpdateProduct.CubeScope, Released, _github);
        await checker.CheckAsync(CancellationToken.None);

        var download = await checker.DownloadAsync(_directory, CancellationToken.None);

        Assert.NotNull(download);
        Assert.Equal(Exe, File.ReadAllBytes(download.Value.File.Path));
        Assert.Equal(UpdatePhase.Installing, checker.Status.Phase);

        // Busy: neither a check nor a second download goes through.
        var requests = _github.Requests.Count;
        Assert.Equal(UpdatePhase.Installing, (await checker.CheckAsync(CancellationToken.None)).Phase);
        Assert.Null(await checker.DownloadAsync(_directory, CancellationToken.None));
        Assert.Equal(requests, _github.Requests.Count);

        checker.EndInstall(error: "the folder is protected");
        Assert.Equal(UpdatePhase.Idle, checker.Status.Phase);
        Assert.Equal("the folder is protected", checker.Status.LastError);
    }

    [Fact]
    public async Task A_failed_download_frees_the_checker_and_says_why()
    {
        _github.Text(ApiUrl, Json("v0.19.0", Exe.Length + 1));
        using var checker = new UpdateChecker(UpdateProduct.CubeScope, Released, _github);
        await checker.CheckAsync(CancellationToken.None);

        await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(_directory, CancellationToken.None));

        Assert.Equal(UpdatePhase.Idle, checker.Status.Phase);
        Assert.NotNull(checker.Status.LastError);
    }

    // --- Release workflow ----------------------------------------------------------------------

    [Fact]
    public void The_release_workflow_stamps_the_release_channel_and_nothing_else()
    {
        // Without the stamp the shipped exe never looks for an update, and nothing would say so.
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var shell = File.ReadAllText(Path.Combine(root, "CubeScope.Shell", "CubeScope.Shell.csproj"));

        Assert.Contains("-p:UpdateChannel=release", workflow, StringComparison.Ordinal);
        Assert.Contains("cubescope.exe.sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("<AssemblyMetadata Include=\"UpdateChannel\" Value=\"$(UpdateChannel)\"", shell, StringComparison.Ordinal);
        // The CI workflow builds plain binaries: a channel there would let a CI artefact update itself.
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.DoesNotContain("UpdateChannel", ci, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
