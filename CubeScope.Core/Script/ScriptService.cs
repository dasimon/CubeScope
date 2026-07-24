using System.Collections.Concurrent;
using System.Text;
using CubeScope.Core.Models;
using CubeScope.Core.Ssas;

namespace CubeScope.Core.Script;

/// <summary>
/// Lecture du MDX Script du cube via AMO (décision actée : AMO uniquement pour le
/// MDX Script et les ID d'objets). Cache mémoire par (serveur, catalogue, cube).
/// </summary>
public sealed class ScriptService(SsasSession session)
{
    private readonly ConcurrentDictionary<string, CubeScript> _cache = new();

    public async Task<CubeScript> GetScriptAsync(string cube, bool refresh = false, CancellationToken ct = default)
    {
        string server = session.Server ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        string catalog = session.Catalog ?? throw new InvalidOperationException("Aucun catalogue sélectionné.");
        string key = $"{server}|{catalog}|{cube}";
        if (!refresh && _cache.TryGetValue(key, out var cached)) return cached;

        string fullText = await Task.Run(() =>
        {
            using var amo = new Microsoft.AnalysisServices.Server();
            amo.Connect($"Data Source={server};Integrated Security=SSPI;");
            try
            {
                var db = amo.Databases.GetByName(catalog);
                var amoCube = db.Cubes.FindByName(cube)
                    ?? throw new InvalidOperationException($"Cube introuvable : {cube}");
                var sb = new StringBuilder();
                foreach (Microsoft.AnalysisServices.MdxScript script in amoCube.MdxScripts)
                    foreach (Microsoft.AnalysisServices.Command command in script.Commands)
                        if (!string.IsNullOrWhiteSpace(command.Text))
                            sb.AppendLine(command.Text.Trim()).AppendLine();
                return sb.ToString();
            }
            finally
            {
                amo.Disconnect();
            }
        }, ct);

        var result = new CubeScript(cube, fullText, ScriptParser.Parse(fullText));
        _cache[key] = result;
        return result;
    }
}
