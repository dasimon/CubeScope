using CubeScope.Core.Models;

namespace CubeScope.Core.Project;

/// <summary>
/// Rule for actions that modify a server (processing, then the XMLA console). Distinct from
/// <see cref="DevServerGuard"/>, which keeps its meaning (where deployment and ClearCache are
/// allowed at all); this one says how an action is allowed on a server that is NOT dev:
/// - declared dev server: allowed (the UI asks for a simple confirmation);
/// - any other server counts as production: the request must carry <c>confirmServer</c> equal to
///   the session's server (trimmed, case-insensitive) — the user types the name again;
/// - in production, Full or Data on a DIMENSION is refused whatever the confirmation:
///   it leaves the dependent partitions unprocessed and the cube unqueryable until they are
///   reprocessed. Full: documented; Data: presumed, not observed (owner's decision). Clear is
///   not offered at all (owner's decision).
/// Checked in Core, BEFORE any connection to the target server — a guard that touches the
/// target first guards nothing, and the API may be called without the UI.
/// </summary>
public static class ProductionActionGuard
{
    /// <summary>Modes each object accepts (Update is a dimension mode only).</summary>
    public static bool IsModeValid(ProcessObjectKind kind, ProcessMode mode)
        => mode != ProcessMode.Update || kind == ProcessObjectKind.Dimension;

    /// <summary>Leaves the measure groups using the dimension unprocessed.</summary>
    public static bool UnprocessesDependents(ProcessObjectKind kind, ProcessMode mode)
        => kind == ProcessObjectKind.Dimension && mode is ProcessMode.Full or ProcessMode.Data;

    /// <summary>
    /// Refusal that no confirmation can lift (invalid mode, destructive dimension mode in
    /// production), or null. Used by the preview, before anything is typed.
    /// </summary>
    public static string? HardRefusal(bool isProduction, ProcessObjectKind kind, ProcessMode mode)
    {
        if (!IsModeValid(kind, mode))
            return $"Le mode {mode} ne s'applique pas à un objet {kind}.";
        if (isProduction && UnprocessesDependents(kind, mode))
            return $"Refusé en production : Process{mode} sur une dimension rend non traités les groupes de mesures "
                 + "qui l'utilisent, et le cube inaccessible jusqu'à leur retraitement. À faire sur un serveur de développement.";
        return null;
    }

    /// <summary>Full verdict for a run: null = allowed, otherwise the refusal message.</summary>
    public static string? Check(string server, IReadOnlyList<string> devServers, string? confirmServer,
        ProcessObjectKind kind, ProcessMode mode)
    {
        bool production = !DevServerGuard.IsDev(devServers, server);
        if (HardRefusal(production, kind, mode) is { } refusal) return refusal;
        return ConfirmationRefusal(server, devServers, confirmServer);
    }

    /// <summary>
    /// The confirmation part alone, shared with the XMLA console: null on a dev server, otherwise
    /// null only if the server name was typed again (trimmed, case-insensitive).
    /// </summary>
    public static string? ConfirmationRefusal(string server, IReadOnlyList<string> devServers, string? confirmServer)
    {
        if (DevServerGuard.IsDev(devServers, server)) return null;
        string expected = (server ?? "").Trim();
        string typed = (confirmServer ?? "").Trim();
        return expected.Length > 0 && string.Equals(expected, typed, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Serveur de production « {expected} » : retapez son nom exact pour confirmer.";
    }

    public static void EnsureAllowed(string server, IReadOnlyList<string> devServers, string? confirmServer,
        ProcessObjectKind kind, ProcessMode mode)
    {
        if (Check(server, devServers, confirmServer, kind, mode) is { } refusal)
            throw new ProductionActionRefusedException(refusal);
    }
}

/// <summary>Action refused by the production rule (distinct from a failed connection or process).</summary>
public sealed class ProductionActionRefusedException(string message) : InvalidOperationException(message);
