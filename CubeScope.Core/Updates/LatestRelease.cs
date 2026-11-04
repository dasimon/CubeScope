using System.Text.Json;

namespace CubeScope.Core.Updates;

/// <summary>
/// What the update check needs from GitHub's "latest release" answer. <see cref="Parse"/> accepts
/// only a complete, published, plainly versioned release carrying the product's deliverable and its
/// checksum listing, and answers null to anything else (a web page from a captive portal or a
/// proxy, an error object, a half-built release): the caller then simply has nothing to offer.
/// </summary>
/// <param name="Version">The release tag, read as three numbers.</param>
/// <param name="PageUrl">The release page, for "what's new".</param>
/// <param name="AssetUrl">Where the deliverable is downloaded from (redirected by GitHub).</param>
/// <param name="AssetSize">The size in bytes GitHub reports for the deliverable; always above zero.</param>
/// <param name="AssetDigest">The SHA-256 GitHub reports for the deliverable, 64 lower-case
/// hexadecimal characters without the "sha256:" prefix, or null when absent or unreadable.</param>
/// <param name="SumsUrl">Where the checksum listing is downloaded from.</param>
public sealed record LatestRelease(
    ReleaseVersion Version,
    Uri PageUrl,
    Uri AssetUrl,
    long AssetSize,
    string? AssetDigest,
    Uri SumsUrl)
{
    private const string DigestPrefix = "sha256:";
    private const int DigestLength = 64;

    /// <summary>Reads the answer of <c>GET /repos/{owner}/{repo}/releases/latest</c>; never throws.</summary>
    public static LatestRelease? Parse(string json, UpdateProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement, product);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static LatestRelease? Read(JsonElement root, UpdateProduct product)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Both flags must be present and false: a missing one is not taken for a published release.
        if (!IsFalse(root, "prerelease") || !IsFalse(root, "draft")) return null;
        if (GetString(root, "tag_name") is not { } tag || !ReleaseVersion.TryParseTag(tag, out var version)) return null;
        if (GetHttpsUrl(root, "html_url") is not { } pageUrl) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        JsonElement? deliverable = null, sums = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(asset, "name");
            if (name == product.AssetName) deliverable ??= asset;
            else if (name == product.SumsName) sums ??= asset;
        }

        if (deliverable is not { } assetElement || sums is not { } sumsElement) return null;
        if (GetHttpsUrl(assetElement, "browser_download_url") is not { } assetUrl) return null;
        if (GetHttpsUrl(sumsElement, "browser_download_url") is not { } sumsUrl) return null;
        // TryGetInt64 throws on anything but a number, so the kind is checked first.
        if (!assetElement.TryGetProperty("size", out var size)
            || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var assetSize)
            || assetSize <= 0)
            return null;

        return new LatestRelease(version, pageUrl, assetUrl, assetSize, ReadDigest(assetElement), sumsUrl);
    }

    /// <summary>"sha256:" and 64 hexadecimal characters, else null (an unknown algorithm or a damaged
    /// value is not used; the deliverable is still checked against the checksum listing).</summary>
    private static string? ReadDigest(JsonElement asset)
    {
        if (GetString(asset, "digest") is not { } digest
            || !digest.StartsWith(DigestPrefix, StringComparison.Ordinal))
            return null;

        var hex = digest.AsSpan(DigestPrefix.Length);
        if (hex.Length != DigestLength) return null;
        foreach (var c in hex)
            if (!char.IsAsciiHexDigit(c)) return null;
        return hex.ToString().ToLowerInvariant();
    }

    private static bool IsFalse(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.False;

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? GetHttpsUrl(JsonElement element, string property) =>
        GetString(element, property) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
}
