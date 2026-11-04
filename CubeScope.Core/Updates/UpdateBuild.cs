using System.Reflection;

namespace CubeScope.Core.Updates;

/// <summary>
/// What a build knows about itself and may do about updates, read from its assembly.
/// </summary>
/// <remarks>
/// The update channel is assembly metadata, <c>AssemblyMetadata("UpdateChannel", "release")</c>,
/// stamped only by the release workflow (<c>-p:UpdateChannel=release</c>). It is never a setting:
/// a build without the stamp — a source checkout, the dev loop, CI — neither checks nor updates,
/// so a developer's binary is never overwritten by a release.
/// </remarks>
/// <param name="Version">The running version, or null when it is not plain MAJOR.MINOR.PATCH.</param>
/// <param name="Channel">The stamped channel, or null for a plain build.</param>
public sealed record UpdateBuild(ReleaseVersion? Version, string? Channel)
{
    public const string ChannelKey = "UpdateChannel";
    public const string ReleaseChannel = "release";

    /// <summary>True when the build is on the release channel and knows its version. The
    /// comparison is ordinal, case included: only the exact stamp the workflow writes counts.</summary>
    public bool CanUpdate => Version is not null && string.Equals(Channel, ReleaseChannel, StringComparison.Ordinal);

    /// <summary>Reads the version and the channel of <paramref name="assembly"/>. Pass the entry
    /// assembly for an executable, the add-in's own assembly in a host process (Excel has none).</summary>
    public static UpdateBuild Read(Assembly? assembly)
    {
        if (assembly is null) return new UpdateBuild(null, null);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var channel = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, ChannelKey, StringComparison.Ordinal))?.Value;
        return new UpdateBuild(
            ReleaseVersion.TryParseInformational(informational, out var version) ? version : null,
            channel);
    }
}
