using System.Diagnostics;
using CubeScope.Core.Models;
using Microsoft.AnalysisServices;

namespace CubeScope.Core.Project;

/// <summary>
/// Deploys the MDX Script ALONE to a server cube (idea taken from BIDS Helper
/// "Deploy MDX Script"): replaces the MdxScript Commands and calls Update(), without
/// redeploying the project or touching the server's CalculationProperties. No
/// processing needed: the recalculated script is active immediately.
/// Two independent guards:
/// - SERVER: we only deploy to a server explicitly declared as a development server.
///   `force` does NOT bypass it — it only means "overwrite a server script that has
///   diverged", never "deploy to production".
/// - DIVERGENCE: if the server script differs from the project text and force=false, does NOT
///   deploy and returns the server text (a live tweak not to be overwritten).
/// </summary>
public sealed class ScriptDeployService
{
    /// <param name="devServers">
    /// Servers where deployment is allowed (explicit list, see <see cref="DevServerGuard"/>).
    /// Empty = none: a missing configuration must get in the way, never grant permission.
    /// </param>
    public DeployScriptResult Deploy(string server, string catalog, string cubeName,
        string projectText, bool force, IReadOnlyList<string> devServers)
    {
        // BEFORE any connection: a guard that touches the target only to find out it
        // should not have done so guards nothing. Message distinct from a failed connection's.
        if (!DevServerGuard.IsDev(devServers, server))
            throw new InvalidOperationException(
                $"Déploiement refusé : le serveur « {server} » n'est pas déclaré comme serveur "
                + "de développement. Déclarez-le dans le dialogue de connexion si c'en est un.");

        var sw = Stopwatch.StartNew();
        using var amo = new Server();
        amo.Connect($"Data Source={server};Integrated Security=SSPI;");
        try
        {
            var db = amo.Databases.GetByName(catalog);
            var cube = db.Cubes.FindByName(cubeName)
                ?? throw new InvalidOperationException($"Cube introuvable sur {server}/{catalog} : {cubeName}");
            if (cube.MdxScripts.Count == 0)
                throw new InvalidOperationException($"Le cube serveur {cubeName} n'a pas de MdxScript.");
            var script = cube.MdxScripts[0];

            string serverText = string.Join("\n\n", script.Commands.Cast<Command>()
                .Select(c => c.Text?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (!force && !TextEquals(serverText, projectText))
                return new DeployScriptResult(false, true, serverText, sw.ElapsedMilliseconds);

            script.Commands.Clear();
            script.Commands.Add(new Command(projectText));
            script.Update();
            return new DeployScriptResult(true, false, null, sw.ElapsedMilliseconds);
        }
        finally
        {
            amo.Disconnect();
        }
    }

    /// <summary>Lenient equality: CRLF→LF, trailing whitespace on lines and text ignored.</summary>
    public static bool TextEquals(string a, string b) => Canonical(a) == Canonical(b);

    private static string Canonical(string s) =>
        string.Join('\n', s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}
