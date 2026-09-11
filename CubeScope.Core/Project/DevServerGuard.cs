namespace CubeScope.Core.Project;

/// <summary>
/// Décide si un serveur SSAS est un serveur de développement, à partir d'une liste EXPLICITE.
///
/// POURQUOI PAS UNE CONVENTION DE NOMMAGE : la règle précédente regardait le nom du catalogue
/// (« contient dev »), ce qui tenait tant que le dev vivait sur le serveur de production sous
/// un autre nom. Depuis que le dev a son propre serveur, prod et dev ont le MÊME nom de
/// catalogue — le nom ne discrimine plus rien, et une règle par sous-chaîne rangerait
/// « SRV-DEV-PROD » du côté dev.
///
/// La liste vide ne déclare aucun serveur de dev : une configuration absente doit gêner
/// (avertissement de production partout), jamais autoriser.
/// </summary>
public static class DevServerGuard
{
    public static bool IsDev(IEnumerable<string> devServers, string? server)
    {
        string cible = (server ?? "").Trim();
        if (cible.Length == 0) return false;

        // Égalité stricte après normalisation : un nom de serveur Windows est insensible à la
        // casse, et un espace venu d'un copier-coller ne doit pas changer le verdict.
        return devServers.Any(s =>
            s.Trim().Length > 0 && string.Equals(s.Trim(), cible, StringComparison.OrdinalIgnoreCase));
    }
}
