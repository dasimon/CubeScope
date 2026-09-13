namespace CubeScope.Core.Project;

/// <summary>
/// Decides whether an SSAS server is a development server, based on an EXPLICIT list.
///
/// WHY NOT A NAMING CONVENTION: the previous rule looked at the catalog name
/// ("contains dev"), which held as long as dev lived on the production server under
/// another name. Since dev got its own server, prod and dev have the SAME catalog
/// name — the name no longer tells them apart, and a substring rule would put
/// "SRV-DEV-PROD" on the dev side.
///
/// An empty list declares no dev server: a missing configuration must get in the way
/// (production warning everywhere), never grant permission.
/// </summary>
public static class DevServerGuard
{
    public static bool IsDev(IEnumerable<string> devServers, string? server)
    {
        string cible = (server ?? "").Trim();
        if (cible.Length == 0) return false;

        // Strict equality after normalization: a Windows server name is case-insensitive,
        // and a space coming from a copy-paste must not change the verdict.
        return devServers.Any(s =>
            s.Trim().Length > 0 && string.Equals(s.Trim(), cible, StringComparison.OrdinalIgnoreCase));
    }
}
