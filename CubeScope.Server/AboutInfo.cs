using System.Reflection;
using System.Runtime.InteropServices;
using CubeScope.Core.State;

namespace CubeScope.Server;

/// <summary>What the About dialog shows. Nothing is hard-coded: the version comes from the
/// assembly, which the release workflow stamps from the tag (-p:Version).</summary>
public sealed record AboutInfo(
    string Version,
    string? Commit,
    string Runtime,
    string DataFolder,
    string DatabasePath,
    string? SsasServer,
    string? SsasVersion)
{
    public const string RepositoryUrl = "https://github.com/dasimon/CubeScope";

    public static AboutInfo Build(string? ssasServer, string? ssasVersion)
    {
        string? informational = typeof(AboutInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var (version, commit) = SplitVersion(informational);
        string db = StateStore.DefaultDbPath;
        return new AboutInfo(version, commit, RuntimeInformation.FrameworkDescription,
            Path.GetDirectoryName(db)!, db, ssasServer, ssasVersion);
    }

    /// <summary>"0.15.0+1c98c30…" → ("0.15.0", "1c98c30"). The SDK appends the full commit
    /// hash after '+'; seven characters are enough to find it on GitHub.</summary>
    public static (string Version, string? Commit) SplitVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return ("?", null);
        int plus = informational.IndexOf('+');
        if (plus < 0) return (informational, null);
        string hash = informational[(plus + 1)..];
        return (informational[..plus], hash.Length == 0 ? null : hash[..Math.Min(7, hash.Length)]);
    }
}
