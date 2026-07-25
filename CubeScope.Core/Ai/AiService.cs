using Anthropic;
using Anthropic.Models.Messages;
using CubeScope.Core.Ssas;

namespace CubeScope.Core.Ai;

public enum AiAction
{
    Expliquer,
    Optimiser,
    AntiPatterns,
    Formater,
    Tracer,
    OptimiserProfil,
    GenererMdx,
}

/// <summary>
/// Expert IA intégré (décision actée) : appelle l'API Anthropic avec un prompt système
/// par action et les métadonnées pertinentes du cube injectées dans le contexte.
/// Clé : variable d'environnement ANTHROPIC_API_KEY (validé 2026-07-23 — pas de stockage local).
/// Modèle : claude-opus-4-8, adaptive thinking.
/// </summary>
public sealed class AiService(MetadataService metadata, SsasSession session)
{
    private const string ModelId = "claude-opus-4-8";

    // Prompt système commun, stable (préfixe cacheable) — le contexte cube et le MDX
    // arrivent dans le message utilisateur.
    private const string SystemBase = """
        Tu es l'expert MDX intégré de CubeScope, un outil pour développeur SSAS
        Multidimensional. Tu réponds en Markdown, de façon précise et directement
        exploitable — pas de généralités. Le MDX fourni s'exécute sur un cube réel
        dont les métadonnées pertinentes te sont données. Ne réponds qu'à propos du
        MDX fourni ; n'invente jamais de membres, mesures ou hiérarchies qui ne sont
        ni dans la requête ni dans les métadonnées.
        """;

    private static readonly Dictionary<AiAction, string> ActionPrompts = new()
    {
        [AiAction.Expliquer] = """
            Tâche : EXPLIQUER la requête. Décris ce qu'elle retourne (axes, mesures,
            ensembles, filtres, calculs), dans l'ordre logique d'évaluation. Signale les
            subtilités (contexte de la clause WHERE, membres calculés, NON EMPTY…).
            Termine par un résumé d'une phrase en gras.
            """,
        [AiAction.Optimiser] = """
            Tâche : OPTIMISER la requête. Identifie les coûts probables (crossjoins non
            filtrés, Filter cellule-par-cellule vs NonEmpty, calculs non cachés, ensembles
            recalculés), puis propose UNE réécriture complète dans un bloc ```mdx, suivie
            de la justification point par point. Si la requête est déjà bien écrite, dis-le
            et n'invente pas d'optimisation.
            """,
        [AiAction.AntiPatterns] = """
            Tâche : DÉTECTER LES ANTI-PATTERNS. Passe en revue : CrossJoin non filtré sur
            grosses hiérarchies, Filter() là où NonEmpty/EXISTING suffirait, membres calculés
            dans la requête au lieu du script MDX, absence de NON EMPTY sur les axes,
            LookupCube, StrToMember/StrToSet sur des chaînes dynamiques, cellules calculées
            en cascade. Pour chaque anti-pattern trouvé : sévérité (haute/moyenne/faible),
            extrait de code concerné, correction proposée. Si rien à signaler, dis-le.
            """,
        [AiAction.Formater] = """
            Tâche : FORMATER la requête, sans changer sa sémantique. Règles : mots-clés en
            MAJUSCULES, un membre/tuple par ligne dans les ensembles, indentation de 4
            espaces par niveau d'imbrication, clauses WITH/SELECT/FROM/WHERE alignées à
            gauche, virgules en fin de ligne. Réponds UNIQUEMENT avec le MDX formaté dans
            un bloc ```mdx, sans aucune explication.
            """,
        [AiAction.OptimiserProfil] = """
            Tâche : OPTIMISER À PARTIR DU PROFIL D'EXÉCUTION. On te donne une requête MDX ET
            son profil d'exécution réel (découpage Formula Engine / Storage Engine, nombre de
            sous-cubes scannés, hits cache et agrégation, sous-cubes les plus coûteux). Propose
            des optimisations CONCRÈTES et spécifiques à CETTE requête, chaque suggestion
            JUSTIFIÉE par les chiffres du profil :
            - Storage Engine dominant + peu de hits cache/agrégation → agrégations à concevoir,
              ou NON_EMPTY/EXISTS/NonEmpty mal placés qui forcent des scans larges ;
            - Formula Engine dominant → calculs coûteux à revoir (IIF imbriqués, ensembles
              recalculés, SCOPE, cellules en cascade) ;
            - beaucoup de sous-cubes → granularité de requête trop fine / crossjoins à filtrer.
            Cite les chiffres du profil dans ta justification. Si utile, propose une réécriture
            dans un bloc ```mdx. NE DONNE PAS de conseils génériques déconnectés du profil.
            """,
        [AiAction.GenererMdx] = """
            Tâche : GÉNÉRER DU MDX à partir d'une demande en langage naturel. On te donne les
            métadonnées du cube (mesures, dimensions, hiérarchies, niveaux) et une demande en
            français. Écris UNE requête MDX qui y répond, en n'utilisant QUE les mesures /
            dimensions / hiérarchies listées (jamais de membre, mesure ou hiérarchie inventé).
            Conventions : mesures sur COLUMNS, la dimension d'analyse sur ROWS (souvent
            `.Members` ou `.Children` du bon niveau), `NON EMPTY` sur les axes, `FROM [cube]`,
            filtres dans la clause `WHERE`. Pour une date/période non déterminable précisément
            (ex. "aujourd'hui", "actuel"), prends la DERNIÈRE DATE AVEC DONNÉES pour la mesure de
            la requête via `Tail(NonEmpty(<Hiérarchie>.[Niveau feuille].Members, <mesure>), 1).Item(0)`
            — jamais `Tail(<Niveau>.Members).Item(0)` seul : le dernier membre calendaire d'une
            hiérarchie de dates (souvent chargée au-delà des données réelles — jours fériés,
            week-ends, dates futures) n'a généralement PAS de données, la requête renvoie alors un
            résultat vide. Et JAMAIS `.LastChild` sur un niveau (LastChild attend un membre, pas un
            niveau : erreur d'exécution garantie). SIGNALE cette hypothèse de date. Réponds avec la requête dans un bloc ```mdx, suivie d'une courte phrase
            expliquant les choix et les hypothèses. Si la demande est trop ambiguë pour choisir
            une mesure ou une dimension, demande la précision manquante au lieu de deviner.
            """,
        [AiAction.Tracer] = """
            Tâche : TRACER LE CALCUL. On te donne un membre calculé (ou un named set), son
            expression, et les expressions des membres calculés/sets dont il dépend
            (directement ou transitivement). Explique en français, étape par étape, COMMENT
            sa valeur est construite : la chaîne de calcul, ce que chaque sous-membre apporte
            au résultat final, et l'ordre logique d'évaluation. Sois concret et concis. Ne
            réécris pas le MDX, explique-le. N'invente aucun membre ou dépendance qui ne
            figure pas dans le contexte fourni.
            """,
    };

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    // Actions qui opèrent sur une VRAIE requête MDX existante : on peut auto-extraire les
    // références [Dim].[Hiér] qu'elle contient et n'injecter que les métadonnées pertinentes
    // (MdxContextBuilder). Les autres actions (GenererMdx, Tracer, OptimiserProfil) reçoivent
    // déjà un contexte complet et auto-descriptif construit par leur appelant (Program.cs) —
    // pour elles, ni auto-extraction (qui interrogerait le mauvais cube via cubes[0]), ni
    // enveloppe "Requête MDX" trompeuse : le texte fourni est envoyé tel quel.
    private static readonly HashSet<AiAction> RawMdxActions =
        [AiAction.Expliquer, AiAction.Optimiser, AiAction.AntiPatterns, AiAction.Formater];

    public async Task<string> RunAsync(AiAction action, string mdx, string lang = "fr", CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Clé API absente : définir la variable d'environnement ANTHROPIC_API_KEY puis relancer CubeScope.");
        if (string.IsNullOrWhiteSpace(mdx))
            throw new InvalidOperationException("Aucune requête MDX à analyser.");

        // Langue de réponse (l'UI envoie la locale courante) — le reste du prompt est stable.
        string langInstruction = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "Respond in English."
            : "Réponds en français.";

        string userContent;
        if (RawMdxActions.Contains(action))
        {
            // Contexte cube : métadonnées du cube courant si disponibles (sinon on continue sans)
            string cubeContext = "";
            try
            {
                var cubes = await metadata.GetCubesAsync(ct);
                if (cubes.Count > 0 && session.Catalog is not null)
                {
                    var meta = await metadata.GetCubeMetaAsync(cubes[0], ct: ct);
                    cubeContext = MdxContextBuilder.Build(meta, mdx);
                }
            }
            catch
            {
                // Pas de connexion/cube : l'IA travaille sur le MDX seul, dégradé assumé
            }

            userContent = $"""
                {ActionPrompts[action]}

                Métadonnées du cube :
                {(cubeContext.Length > 0 ? cubeContext : "(non connecté — analyse le MDX seul)")}

                Requête MDX :
                ```mdx
                {mdx}
                ```
                """;
        }
        else
        {
            userContent = $"""
                {ActionPrompts[action]}

                {mdx}
                """;
        }

        AnthropicClient client = new();
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = 16000,
            Thinking = new ThinkingConfigAdaptive(),
            System = new List<TextBlockParam>
            {
                new() { Text = SystemBase, CacheControl = new CacheControlEphemeral() },
                new() { Text = langInstruction },
            },
            Messages =
            [
                new() { Role = Role.User, Content = userContent },
            ],
        }, cancellationToken: ct);

        var parts = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(t => t.Text);
        return string.Concat(parts);
    }
}
