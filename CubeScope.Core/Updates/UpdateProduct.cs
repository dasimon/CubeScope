namespace CubeScope.Core.Updates;

/// <summary>
/// What an application downloads when it updates itself: the GitHub repository its releases come
/// from, the asset that is the deliverable and the checksum listing published beside it. Fixed in
/// code per application — nothing on the user's machine can point a copy at another repository.
/// </summary>
/// <param name="Name">Sent as the User-Agent product (GitHub's API refuses requests without one).</param>
/// <param name="Repository">"owner/name".</param>
/// <param name="AssetName">The deliverable, matched exactly (case included) among the release assets.</param>
/// <param name="SumsName">The <c>sha256sum</c> listing that holds the deliverable's hash.</param>
public sealed record UpdateProduct(string Name, string Repository, string AssetName, string SumsName)
{
    /// <summary>The single-file executable: replaced in place, then restarted.</summary>
    public static readonly UpdateProduct CubeScope =
        new("CubeScope", "dasimon/CubeScope", "cubescope.exe", "cubescope.exe.sha256");

    /// <summary>The add-in zip: Excel locks the .xll, so it is downloaded and verified, then the user
    /// replaces the files once Excel is closed.</summary>
    public static readonly UpdateProduct PivotScope =
        new("PivotScope", "dasimon/PivotScope", "PivotScope.zip", "PivotScope.zip.sha256");
}
