using System.Net;
using System.Security.Cryptography;
using System.Text;
using CubeScope.Core.Updates;

namespace CubeScope.Core.Tests.Updates;

/// <summary>What the update client accepts from the network, with a fake handler standing in for
/// GitHub: the host list on every hop, the redirection limit, the size caps and the hash checks, in
/// the order that keeps a wrong file off the disk.</summary>
public sealed class ReleaseClientTests : IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/dasimon/CubeScope/releases/latest";
    private const string ExeUrl = "https://github.com/dasimon/CubeScope/releases/download/v0.19.0/cubescope.exe";
    private const string SumsUrl = "https://github.com/dasimon/CubeScope/releases/download/v0.19.0/cubescope.exe.sha256";
    private const string AssetUrl = "https://release-assets.githubusercontent.com/asset/cubescope.exe?sig=secret";

    private static readonly byte[] Exe = Encoding.ASCII.GetBytes("MZ pretend executable, long enough to be split in a few reads");
    private static readonly string ExeHash = Convert.ToHexStringLower(SHA256.HashData(Exe));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CubeScopeRelease-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();

    public ReleaseClientTests()
    {
        // The text-mode line that `sha256sum cubescope.exe` writes in release.yml.
        _github.Bytes(SumsUrl, Encoding.ASCII.GetBytes($"{ExeHash}  cubescope.exe\n"));
        // As on GitHub: the asset link redirects to the file host.
        _github.Redirect(ExeUrl, AssetUrl);
        _github.Bytes(AssetUrl, Exe);
    }

    public void Dispose()
    {
        _github.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static LatestRelease Release(string? digest = null, long? size = null, string sums = SumsUrl, string exe = ExeUrl) =>
        new(new ReleaseVersion(0, 19, 0), new Uri("https://github.com/dasimon/CubeScope/releases/tag/v0.19.0"),
            new Uri(exe), size ?? Exe.Length, digest, new Uri(sums));

    internal static string ReleaseJson(long size = 1000, string exeUrl = ExeUrl, string tag = "v0.19.0") =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":false,\"draft\":false," +
        "\"html_url\":\"https://github.com/dasimon/CubeScope/releases/tag/" + tag + "\",\"assets\":[" +
        $"{{\"name\":\"cubescope.exe\",\"size\":{size},\"browser_download_url\":\"{exeUrl}\"}}," +
        $"{{\"name\":\"cubescope.exe.sha256\",\"size\":80,\"browser_download_url\":\"{SumsUrl}\"}}]}}";

    private ReleaseClient Client() => new(_github, UpdateProduct.CubeScope, "0.18.0");

    private async Task<LatestRelease> GetLatestAsync()
    {
        using var client = Client();
        return await client.GetLatestAsync(CancellationToken.None);
    }

    private async Task<VerifiedDownload> DownloadAsync(LatestRelease release)
    {
        using var client = Client();
        return await client.DownloadAsync(release, _directory, new Progress<double>(), CancellationToken.None);
    }

    private string Target => Path.Combine(_directory, "cubescope.exe");

    private void AssertNothingOnDisk()
    {
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".partial"));
    }

    // --- Handler -------------------------------------------------------------------------------

    [Fact]
    public void A_handler_that_follows_redirections_itself_is_refused()
    {
        // Both default to following redirections, which would take each hop past the host check.
        using var sockets = new SocketsHttpHandler();
        using var classic = new HttpClientHandler();

        Assert.Throws<ArgumentException>("handler", () => new ReleaseClient(sockets, UpdateProduct.CubeScope, "0.18.0"));
        Assert.Throws<ArgumentException>("handler", () => new ReleaseClient(classic, UpdateProduct.CubeScope, "0.18.0"));
    }

    [Fact]
    public void The_default_handler_does_not_follow_redirections()
    {
        using var client = new ReleaseClient(ReleaseClient.CreateHandler(), UpdateProduct.CubeScope, "0.18.0");
    }

    // --- Check ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_latest_release_is_read_with_the_headers_GitHub_requires()
    {
        _github.Text(ApiUrl, ReleaseJson());

        var release = await GetLatestAsync();

        Assert.Equal(new ReleaseVersion(0, 19, 0), release.Version);
        var request = Assert.Single(_github.Requests);
        Assert.Equal("CubeScope/0.18.0", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
    }

    [Fact]
    public async Task The_repository_asked_is_the_products()
    {
        using var client = new ReleaseClient(_github, UpdateProduct.PivotScope, "0.7.0");

        await Assert.ThrowsAsync<UpdateException>(() => client.GetLatestAsync(CancellationToken.None));

        Assert.Equal(new Uri("https://api.github.com/repos/dasimon/PivotScope/releases/latest"), Assert.Single(_github.Requests).Uri);
    }

    [Fact]
    public async Task A_web_page_instead_of_the_API_answer_is_an_update_failure()
    {
        _github.Text(ApiUrl, "<html><body>Please sign in to the Wi-Fi</body></html>");

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task A_release_larger_than_the_cap_is_not_offered()
    {
        _github.Text(ApiUrl, ReleaseJson(size: ReleaseClient.MaxAssetBytes + 1));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task A_release_whose_files_are_elsewhere_is_not_offered()
    {
        _github.Text(ApiUrl, ReleaseJson(exeUrl: "https://example.com/cubescope.exe"));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task An_API_answer_one_byte_over_the_cap_is_refused()
    {
        var json = ReleaseJson();
        _github.Text(ApiUrl, json + new string(' ', (int)ReleaseClient.MaxApiBytes - json.Length + 1));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task An_HTTP_error_is_an_update_failure()
    {
        // Nothing registered for the API address: the fake answers 404.
        var failure = await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Contains("404", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_failure_reports_its_innermost_cause()
    {
        _github.Fail(ApiUrl, new HttpRequestException("outer", new IOException("certificate rejected")));

        var failure = await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Contains("certificate rejected", failure.Message, StringComparison.Ordinal);
    }

    // --- Redirections ----------------------------------------------------------------------------

    [Fact]
    public async Task A_relative_redirection_is_resolved_against_the_address_that_sent_it()
    {
        _github.Redirect(ApiUrl, "/repos/dasimon/CubeScope/releases/123");
        _github.Text("https://api.github.com/repos/dasimon/CubeScope/releases/123", ReleaseJson());

        await GetLatestAsync();

        Assert.Equal(
            [new Uri(ApiUrl), new Uri("https://api.github.com/repos/dasimon/CubeScope/releases/123")],
            _github.Requests.Select(r => r.Uri));
    }

    [Theory]
    [InlineData("http://api.github.com/repos/dasimon/CubeScope/releases/123")]
    [InlineData("https://example.com/releases/123")]
    [InlineData("https://github.com.evil.example/releases/123")]
    [InlineData("https://github.com:8443/releases/123")]
    [InlineData("//example.com/releases/123")]
    public async Task A_redirection_off_the_allowed_hosts_is_refused_without_being_followed(string location)
    {
        _github.Redirect(ApiUrl, location);

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Equal([new Uri(ApiUrl)], _github.Requests.Select(r => r.Uri));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task At_most_five_redirections_are_followed(int redirections, bool followed)
    {
        var from = ApiUrl;
        for (var i = 1; i <= redirections; i++)
        {
            var to = $"{ApiUrl}/{i}";
            _github.Redirect(from, to);
            from = to;
        }
        _github.Text(from, ReleaseJson());

        if (followed)
            await GetLatestAsync();
        else
            await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Equal(Math.Min(redirections, ReleaseClient.MaxRedirects) + 1, _github.Requests.Count);
    }

    [Fact]
    public async Task A_redirection_without_a_target_is_refused()
    {
        _github.Respond(ApiUrl, () => new HttpResponseMessage(HttpStatusCode.Found));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    // --- Download ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("sums")]
    public async Task The_verified_deliverable_gets_its_final_name(string? digest)
    {
        var download = await DownloadAsync(Release(digest: digest is null ? null : ExeHash));

        Assert.Equal(Target, download.Path);
        Assert.Equal(ExeHash, download.Sha256);
        Assert.Equal(Exe, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".partial"));
        // The checksum listing first, then the deliverable through its redirection.
        Assert.Equal([new Uri(SumsUrl), new Uri(ExeUrl), new Uri(AssetUrl)], _github.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task Progress_ends_at_one()
    {
        var reports = new List<double>();
        using var client = Client();

        await client.DownloadAsync(Release(), _directory, new SynchronousProgress(reports.Add), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
    }

    [Theory]
    [InlineData("https://example.com/cubescope.exe.sha256", "sums")]
    [InlineData("http://github.com/dasimon/CubeScope/releases/download/v0.19.0/cubescope.exe", "exe")]
    public async Task A_first_address_off_the_allowed_hosts_is_never_requested(string url, string which)
    {
        var release = which == "sums" ? Release(sums: url) : Release(exe: url);

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(release));

        Assert.DoesNotContain(_github.Requests, r => r.Uri == new Uri(url));
        if (which == "sums") Assert.Empty(_github.Requests);
        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_redirection_of_the_deliverable_off_the_allowed_hosts_is_refused()
    {
        _github.Redirect(ExeUrl, "https://codeload.github.com/cubescope.exe");

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_listing_without_a_line_for_the_deliverable_is_refused_before_the_download()
    {
        _github.Bytes(SumsUrl, Encoding.ASCII.GetBytes($"{ExeHash}  cubescope.exe.bak\n"));

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        Assert.Equal([new Uri(SumsUrl)], _github.Requests.Select(r => r.Uri));
        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_listing_one_byte_over_the_cap_is_refused()
    {
        var line = $"{ExeHash}  cubescope.exe\n";
        _github.Bytes(SumsUrl, Encoding.ASCII.GetBytes(line + new string('\n', (int)ReleaseClient.MaxSumsBytes - line.Length + 1)));

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        Assert.Equal([new Uri(SumsUrl)], _github.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task An_API_digest_that_differs_from_the_listing_is_refused_before_the_download()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(digest: new string('0', 64))));

        Assert.Equal([new Uri(SumsUrl)], _github.Requests.Select(r => r.Uri));
        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_deliverable_one_byte_over_its_announced_size_is_refused_and_deleted()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(size: Exe.Length - 1)));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_deliverable_shorter_than_its_announced_size_is_refused_and_deleted()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(size: Exe.Length + 1)));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_deliverable_whose_bytes_do_not_match_is_refused_and_deleted()
    {
        var tampered = (byte[])Exe.Clone();
        tampered[^1] ^= 1;
        _github.Bytes(AssetUrl, tampered);

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_release_larger_than_the_cap_is_not_downloaded()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(size: ReleaseClient.MaxAssetBytes + 1)));

        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task A_cancelled_download_stays_a_cancellation_and_leaves_nothing()
    {
        using var cts = new CancellationTokenSource();
        using var client = Client();
        var progress = new SynchronousProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadAsync(Release(), _directory, progress, cts.Token));

        AssertNothingOnDisk();
    }
}
