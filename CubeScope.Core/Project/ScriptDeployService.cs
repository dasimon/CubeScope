using System.Diagnostics;
using CubeScope.Core.Models;
using Microsoft.AnalysisServices;

namespace CubeScope.Core.Project;

/// <summary>
/// Déploiement du MDX Script SEUL vers un cube serveur (idée reprise de BIDS Helper
/// « Deploy MDX Script ») : remplace les Commands du MdxScript et Update(), sans
/// redéployer le projet ni toucher aux CalculationProperties du serveur. Aucun
/// process nécessaire : le script recalculé est actif immédiatement.
/// Garde-fou : si le script serveur diffère du texte projet et force=false, ne
/// déploie PAS et retourne le texte serveur (retouche live à ne pas écraser).
/// </summary>
public sealed class ScriptDeployService
{
    public DeployScriptResult Deploy(string server, string catalog, string cubeName, string projectText, bool force)
    {
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

    /// <summary>Égalité tolérante : CRLF→LF, espaces de fin de ligne et de texte ignorés.</summary>
    public static bool TextEquals(string a, string b) => Canonical(a) == Canonical(b);

    private static string Canonical(string s) =>
        string.Join('\n', s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}
