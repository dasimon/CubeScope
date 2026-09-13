using System.Net.Http.Json;
using System.Text.Json.Serialization;
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
/// Built-in AI expert (settled decision): a system prompt per action + the relevant cube
/// metadata injected into the context.
/// Default transport: Anthropic API (ANTHROPIC_API_KEY, claude-opus-4-8, adaptive
/// thinking). Alternative: any OpenAI-compatible endpoint (/v1/chat/completions) if
/// CUBESCOPE_LLM_BASEURL + CUBESCOPE_LLM_MODEL are set — covers Ollama/LM Studio running
/// locally (confidentiality) and OpenAI/Mistral/OpenRouter/Groq. Keys in env vars only,
/// never stored locally (validated 2026-07-23).
/// </summary>
public sealed class AiService(MetadataService metadata, SsasSession session)
{
    private const string DefaultAnthropicModel = "claude-opus-4-8";

    // Anthropic model: CUBESCOPE_ANTHROPIC_MODEL if set (e.g. another Claude model),
    // otherwise the default. Adaptive thinking stays on whatever the model.
    private static string ModelId =>
        Env("CUBESCOPE_ANTHROPIC_MODEL") is { } m && !string.IsNullOrWhiteSpace(m) ? m.Trim() : DefaultAnthropicModel;

    // Shared, stable system prompt (cacheable prefix) — the cube context and the MDX
    // come in the user message.
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

    // Alternative OpenAI-compatible provider (/v1/chat/completions): covers local Ollama
    // (data that does not leave the network), OpenAI, Mistral, OpenRouter, Groq, LM Studio…
    // Enabled as soon as base URL + model are set; otherwise default = Anthropic (unchanged).
    private static string? LlmBaseUrl => Env("CUBESCOPE_LLM_BASEURL");
    private static string? LlmModel => Env("CUBESCOPE_LLM_MODEL");
    private static string? LlmKey => Env("CUBESCOPE_LLM_KEY");
    private static bool UseOpenAiCompat =>
        !string.IsNullOrWhiteSpace(LlmBaseUrl) && !string.IsNullOrWhiteSpace(LlmModel);
    private static string? AnthropicKey => Env("ANTHROPIC_API_KEY");
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static bool IsConfigured =>
        UseOpenAiCompat || !string.IsNullOrWhiteSpace(AnthropicKey);

    /// <summary>Active model (for display in the UI): the configured OpenAI-compatible model, otherwise Anthropic.</summary>
    public static string ActiveModel => UseOpenAiCompat ? LlmModel! : ModelId;

    // Actions that work on a REAL existing MDX query: we can auto-extract the
    // [Dim].[Hier] references it contains and inject only the relevant metadata
    // (MdxContextBuilder). The other actions (GenererMdx, Tracer, OptimiserProfil) already
    // receive a complete, self-describing context built by their caller (Program.cs) —
    // for them, no auto-extraction (which would query the wrong cube via cubes[0]) and no
    // misleading "Requête MDX" wrapper: the supplied text is sent as is.
    private static readonly HashSet<AiAction> RawMdxActions =
        [AiAction.Expliquer, AiAction.Optimiser, AiAction.AntiPatterns, AiAction.Formater];

    public async Task<string> RunAsync(AiAction action, string mdx, string lang = "fr", CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "IA non configurée : définir ANTHROPIC_API_KEY, ou CUBESCOPE_LLM_BASEURL + CUBESCOPE_LLM_MODEL (fournisseur compatible OpenAI), puis relancer CubeScope.");
        if (string.IsNullOrWhiteSpace(mdx))
            throw new InvalidOperationException("Aucune requête MDX à analyser.");

        // Response language (the UI sends the current locale) — the rest of the prompt is stable.
        string langInstruction = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "Respond in English."
            : "Réponds en français.";

        string userContent;
        if (RawMdxActions.Contains(action))
        {
            // Cube context: metadata of the current cube if available (otherwise carry on without it)
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
                // No connection/cube: the AI works on the MDX alone, accepted degraded mode
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

        // OpenAI-compatible provider configured → HTTP /chat/completions; otherwise Anthropic (default).
        if (UseOpenAiCompat)
            return await RunOpenAiCompatAsync($"{SystemBase}\n\n{langInstruction}", userContent, ct);

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

    // Call to an OpenAI-compatible endpoint (/v1/chat/completions, Bearer). Covers Ollama (local,
    // data that does not leave the network), OpenAI, Mistral, OpenRouter, Groq, LM Studio, etc.
    // Not covered (accepted limitation): Azure OpenAI (deployment URL + non-standard api-key header).
    private static async Task<string> RunOpenAiCompatAsync(string system, string user, CancellationToken ct)
    {
        string url = LlmBaseUrl!.TrimEnd('/') + "/chat/completions";
        var payload = new OpenAiChatRequest
        {
            Model = LlmModel!,
            MaxTokens = 16000,
            Messages =
            [
                new() { Role = "system", Content = system },
                new() { Role = "user", Content = user },
            ],
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        if (!string.IsNullOrWhiteSpace(LlmKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LlmKey);

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Erreur LLM ({(int)resp.StatusCode}) : {Truncate(body, 500)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<OpenAiChatResponse>(ct);
        string? text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Réponse LLM vide ou illisible (champ choices[0].message.content absent).");
        return text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class OpenAiChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<OpenAiMessage> Messages { get; set; } = [];
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
    }

    private sealed class OpenAiMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class OpenAiChatResponse
    {
        [JsonPropertyName("choices")] public List<OpenAiChoice>? Choices { get; set; }
    }

    private sealed class OpenAiChoice
    {
        [JsonPropertyName("message")] public OpenAiMessage? Message { get; set; }
    }
}
