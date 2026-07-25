# CubeScope

Successeur moderne de MDX Studio : environnement de travail pour développeur
SSAS **Multidimensional** travaillant seul. Écrire, comprendre, mesurer et
maintenir du MDX sur des cubes existants, avec un expert IA intégré.
Projet open source (MIT) à vocation communautaire, mais conçu d'abord pour
l'usage quotidien de son auteur. **Hors périmètre définitif : Tabular, Power BI,
DAX.** Ne jamais introduire d'abstraction multi-moteurs "au cas où".

## Décisions d'architecture (actées — ne pas rouvrir sans raison forte)

- **Un seul exécutable** `cubescope.exe` : ASP.NET Core 10 (Kestrel, port libre
  sur localhost) servant une SPA Vue 3. Au lancement, ouverture du navigateur
  par défaut. Pas de WebView2 pour l'instant.
- **Solution** : `CubeScope.Core` (services métier, aucune dépendance web),
  `CubeScope.Server` (minimal API + SignalR + hébergement SPA),
  `CubeScope.Web` (Vue 3 + TypeScript + Vite).
- **Connectivité SSAS** : package NuGet
  `Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64` (jamais la
  variante .NET Framework). AMO (`Microsoft.AnalysisServices.NetCore.retail.amd64`)
  uniquement pour lire le MDX Script et résoudre les ID d'objets.
- **Métadonnées** : DMV `$SYSTEM.MDSCHEMA_*` en voie principale (mapping JSON
  simple), schema rowsets typés en complément si besoin. Chargement paresseux
  des membres (autocomplétion) avec cache mémoire.
- **Stats d'exécution** : deltas de compteurs perfmon (catégories `MSAS<ver>:*`
  ou `MSOLAP$<instance>:*`, découverte dynamique par préfixe). Compteurs
  globaux au serveur, pas par session : assumé pour le MVP.
- **État local** : SQLite unique (historique de requêtes, connexions récentes,
  layouts, snippets). Pas de fichiers de config éparpillés.
- **Frontend** : Monaco Editor (grammaire Monarch MDX maison), dockview pour
  le layout, PrimeVue comme kit UI unique (dialogues, arbre, menus) dont le
  `DataTable` virtualisé sert de grille de résultats v1 — encapsulé dans
  `ResultsGrid.vue` (interface `columns`/`rows`) pour basculer sur AG Grid
  Community si les crossjoins larges rament (constaté, pas supposé). SignalR
  pour tout ce qui streame (progression, futures traces) — introduit en
  Phase 2 avec les stats, pas avant.
- **Parseur MDX = tokenizer pragmatique**, pas d'AST complet. Sert la
  coloration, la détection de références `[Dim].[Hier]` / `[Measures].[X]` et
  le graphe de dépendances par matching de tokens (~95 % de précision, assumé).
- **IA** : service appelant l'API Anthropic (expliquer / optimiser / détecter
  les anti-patterns / formater), avec injection des métadonnées pertinentes du
  cube dans le contexte. Le formatage MDX passe par l'IA : **ne pas écrire de
  formateur déterministe** (piège à effort identifié).

## Reporté / interdit pour le MVP

Formateur déterministe, cartographie graphique interactive (la vue arbre
suffit), viewer Extended Events (perfmon d'abord), impact analysis croisée
(SSRS/Excel), refactoring, système de plugins.

## Environnement de dev

- Windows uniquement. Cible un SSAS **Multidimensional** réel (testé sur
  SSAS 2022, instance par défaut port 2383, plus une instance nommée sur port
  fixe — la syntaxe `hôte\instance` ne marche pas si SQL Browser (UDP 2382) est
  fermé, utiliser `Data Source=hôte:port`). Pour tout ce qui vide le cache :
  cibler un catalogue **de dev** (ClearCache est scopé au `DatabaseID`, la prod
  n'est pas touchée), jamais un catalogue de prod.
- Sécurité intégrée Windows pour toutes les connexions SSAS. Aucun credential
  en clair nulle part.
- .NET 10 SDK, Node LTS, Vue 3 + TypeScript strict.

## Pièges connus (déjà identifiés, ne pas redécouvrir)

- Perfmon distant : nécessite l'appartenance au groupe "Performance Monitor
  Users" sur le serveur SSAS et le service Remote Registry démarré. En cas
  d'échec, tester sur le serveur pour distinguer droits vs noms de compteurs.
  Symptôme droits : `Win32Exception "Accès refusé"` dès
  `PerformanceCounterCategory.GetCategories` (SID du groupe = `S-1-5-32-558`).
- Catégories perfmon SSAS **localisées** (si l'OS serveur est en français) :
  séparateur `" : "` AVEC espaces et libellés traduits — `MSAS16 : MDX`,
  `MSAS16 : cache`, `MSAS16 : mémoire`, `MSAS16 : connexion`, `MSAS16 : requête
  du moteur de stockage`, `MSAS16 : verrous`, `MSAS16 : threads`,
  `MSAS16 : traitement`, `MSAS16 : traitement des agrégations`, `MSAS16 :
  traitement des index`, `MSAS16 : mise en cache proactive`, etc. Deux
  catégories restent en anglais sans espaces : `MSAS16:Database Auto Image
  Load`, `MSAS16:Reliability Metrics`. Instance nommée = préfixe
  `MSOLAP$<instance>` (mêmes libellés). ⇒ Matcher le libellé après le premier
  `:` (trim) en FR ET EN ; ne jamais filtrer les compteurs par nom (`/sec`
  devient `/s`) mais par `CounterType` (`NumberOfItems32/64` = cumulatifs à
  delta).
- Un cube **non processé disparaît de `MDSCHEMA_CUBES`** (plus aucune ligne
  `CUBE_SOURCE=1` ; les dimensions `$` restent listées) — symptôme : « base
  vide » alors que le catalogue existe. Vérifier `LAST_DATA_UPDATE` via le mode
  `--discover` du spike (constaté sur un catalogue redéployé non processé).
- Rowsets ADOMD → `DataTable.Load` : les rowsets (DMV comme schema rowsets)
  déclarent des contraintes d'unicité que leurs propres données violent
  (`ConstraintException "Failed to enable constraints"`). Toujours charger la
  `DataTable` dans un `DataSet { EnforceConstraints = false }` avant `Load`.
- `CellSet` : une requête à un seul axe n'a pas d'`Axes[1]` ; toujours tester
  `Axes.Count`. (Confirmé au spike : 1 axe → `Axes.Count = 1`.)
- `ClearCache` XMLA : exige le `DatabaseID`, qui diffère du nom en cas de
  renommage — résoudre via AMO, pas par convention. (Au spike, `DatabaseID` =
  nom du catalogue a fonctionné sur une base jamais renommée ; ne pas en faire
  une règle.)
- Ne PAS mettre `Initial Catalog` inexistant dans la chaîne ADOMD : ouvrir sans
  catalogue puis `ChangeDatabase()` marche très bien et permet de lister
  `DBSCHEMA_CATALOGS` d'abord.
- Profiler (trace SSAS, `CubeScope.Spike --profile`) : l'AMO .NET Core 19.84.1
  supporte bien la souscription live `Trace.OnEvent` (événements poussés en
  temps réel). Nécessite des droits **admin SSAS** (création de trace).
  **PIÈGE MAJEUR** : chaque `TraceEventClass` a sa propre liste blanche de
  colonnes, validée **côté serveur au `Trace.Update()`** (pas au `Columns.Add`,
  client-side, qui ne lève rien). Couple invalide → `OperationException`
  « L'ID d'événement Id=X ne contient pas l'ID Id=Y » avec
  X=`(int)TraceEventClass`, Y=`(int)TraceColumn`. Solution retenue
  (`ProfileSpike.cs`) : boucle auto-corrective qui parse (X,Y), retire la
  colonne Y de l'événement X et réessaie `Update()`. Ne suivre QUE les
  événements « complétés » (`QueryEnd`, `QuerySubcube(Verbose)`,
  `GetDataFrom*`, `*End`) — les « Begin » n'ont pas de `Duration`. Découpage :
  `QueryEnd`.Duration = total, Σ `QuerySubcube`.Duration = Storage Engine,
  FE = total − SE, + hits cache/agg. Filtrer par `SessionID` (colonne trace =
  `AdomdConnection.SessionID`). Trace serveur = **globale** → toujours
  `Stop()`+`Drop()` en `finally` ; nettoyer les traces orphelines `CubeScope_*`
  en cas de crash.
- Package `Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64` 19.84.1 :
  tirait `Microsoft.Identity.Client` 4.56.0 en transitive, avec 2 vulnérabilités
  connues (NU1901/NU1902, gravité faible/moyenne). **Résolu** : pin direct de
  `Microsoft.Identity.Client` 4.86.1 dans `CubeScope.Core` et `CubeScope.Spike`
  (force la transitive vers la version patchée — audit `dotnet list --vulnerable`
  vide, build 0 warning). MSAL 4.86.1 compatible ADOMD 19.84.1 (auth Entra non
  utilisée de toute façon, on est en Integrated Security). Resynchroniser ce pin
  si ADOMD/AMO montent de version.
- Cibler `net10.0-windows` (pas `net10.0`) : `System.Diagnostics.PerformanceCounter`
  est Windows-only et génère ~30 warnings CA1416 sinon.
- DMV : **crocheter toutes les colonnes** (`SELECT [HIERARCHY_UNIQUE_NAME] …`) —
  `HIERARCHY` (entre autres) est un mot réservé MDX, la requête non crochetée
  échoue en syntaxe. `CUBE_NAME`/`MEASURE_NAME` passent nus par chance.
- **Caption d'un membre par sa clé** (survol des `…&[clé]` dans le script) : NE PAS
  passer par `$SYSTEM.MDSCHEMA_MEMBERS`. (a) Le DMV **ne supporte pas `IN (…)`**
  (« La syntaxe de "IN" est incorrecte »). (b) Filtrer par `MEMBER_UNIQUE_NAME`
  seul (même avec `HIERARCHY_UNIQUE_NAME`) fait **scanner toute la dimension** →
  gel sur une dimension titres (milliers d'ISIN). La bonne méthode = **MDX**
  `StrToMember('[Dim].[Hier].[Niveau].&[clé]').Properties("MEMBER_CAPTION")` :
  résolution directe par clé, zéro scan ; une seule requête résout tout un paquet
  (`WITH MEMBER [Measures].[__capN] AS … SELECT {…} ON 0 FROM [cube]`), repli
  membre par membre si une référence périmée fait échouer le paquet entier.
  Cache persistant SQLite (`MemberCaption`) invalidé sur l'empreinte
  `LAST_SCHEMA_UPDATE|LAST_DATA_UPDATE` du cube.
- `CELL PROPERTIES VALUE` (requêtes copiées d'Excel/SSMS) : le serveur ne renvoie
  QUE les propriétés listées → `Cell.FormattedValue` vaut **chaîne vide, pas
  null** (le `??` ne suffit pas). Toujours se replier sur `Cell.Value` quand
  FormattedValue est null OU vide (fait dans `CellSetMapper.CellValue`), sinon
  la grille affiche des colonnes vides alors que les données sont là.
- `CellSet` : `axis.Set.Hierarchies` déclenche une résolution paresseuse d'objets
  schéma qui peut échouer (`ArgumentException "Impossible de trouver l'objet
  [Dimension].[Membre]"`, constaté sur un cube réel) alors que positions/cellules
  sont déjà là. `CellSetMapper` a un repli : libellés déduits des `UniqueName`
  des membres (attention, pour une mesure `[Measures].[X]` le 2ᵉ segment est le
  membre, pas la hiérarchie).
- PrimeVue : **rester en v4.5.x + `@primeuix/themes`**. npm installe v5 par
  défaut, qui exige `@primeuix/styled` ^1.0 (incompatible `@primevue/themes` 4.x)
  et embarque un `license-manager` non audité. Mode sombre permanent : classe
  `p-dark` sur `<html>` + `darkModeSelector: '.p-dark'` (`':root'` ne marche pas).
- monaco-editor ≥ 0.56 : exports map `"./*" → "./esm/vs/*.js"` — importer
  `monaco-editor/editor/editor.worker?worker`, plus jamais le chemin `esm/vs/…`
  (Vite/Rolldown ne résout plus). Monaco utilise EditContext : plus de
  `textarea.inputarea` pour les tests E2E, cliquer `.view-lines` puis clavier.
- dockview-vue : `DockviewVue` est **multi-root** (portals) → le CSS scoped du
  parent ne l'atteint pas (hauteur 0 silencieuse). L'entourer d'un wrapper div
  dimensionné et lui passer `style="width:100%;height:100%"`.
- Monaco dégraissé : `src/monaco-core.ts` reproduit `editor.main.js` SANS les
  81 langages ni les 4 features à workers (dist 26 Mo → 6 Mo). Liste d'imports
  à **resynchroniser à chaque montée de version monaco** (générée depuis
  `esm/vs/editor/editor.main.js`).
- Perfmon en marche : catégories utiles par requête = `MDX`, `cache`,
  `requête du moteur de stockage` (~53 compteurs cumulatifs, constaté).
  Limite MVP assumée : préfixe `MSAS*` (instance par défaut) — pour une
  connexion à une instance nommée sur port fixe, le mapping port→instance n'est
  pas découvrable, les compteurs restent ceux de l'instance par défaut. La
  découverte (`Initialize`) prend ~2-4 s → lancée en arrière-plan à la
  connexion ; une requête partie avant la fin n'a simplement pas de stats.
- Exe single-file **autonome** : le publish laisse par défaut la SPA (`wwwroot`)
  ET les DLL natives (`e_sqlite3`, `msalruntime`…) en fichiers **libres à côté**
  de l'exe → déplacé seul, 404 sur `index.html` (`ContentRoot` = dossier de
  l'exe) et SQLite plante. Corrigé : `IncludeNativeLibrariesForSelfExtract=true`
  + SPA embarquée dans l'assembly (`EmbeddedResource` préfixe `spa/`, servie par
  `EmbeddedSpaFileProvider`). Pièges MSBuild : hooker la cible d'embarquement à
  `BeforeTargets="PrepareForBuild"` (à `CoreCompile` c'est trop tard, 0 ressource
  embarquée) ; le `LogicalName` avec `%(Filename)` sur un Include auto-référencé
  s'évalue vide (collision `CS1508`) → passer par un item intermédiaire qualifié ;
  glob `**\*.*` (pas `**\*`, qui matche les dossiers). Cible active seulement au
  publish (`_IsPublishing` OU `-p:EmbedSpa=true`).
- Serveur de dev qui redémarre : toujours vérifier qu'aucun process orphelin ne
  garde le port ni ne verrouille les DLL (`cubescope.exe` résiduel) — sinon
  binaire obsolète servi en silence (le fallback SPA renvoie index.html pour
  toute route API inconnue : un 200 HTML sur un endpoint attendu = symptôme de
  vieux binaire, pas de bug front).
- Nom "MDX" pollué par Markdown+JSX dans l'écosystème npm/GitHub : ne pas
  nommer de packages `mdx-*` côté frontend.
- Round-trip `.cube` (mode projet SSDT) : `XDocument.Load` doit utiliser
  `LoadOptions.PreserveWhitespace`, sinon l'indentation XML est reformatée en
  silence à l'enregistrement.
- `Save` et `Load` (`CubeProjectService`) doivent s'accorder sur la définition
  d'un `Command` éditable — un `<Text>` composé uniquement d'espaces ne compte
  pas comme du contenu, sinon `CanEdit` (calculé au `Load`) et la garde de
  sauvegarde (recomptée au `Save`) divergent.
- Pliage Monaco par régions (`// #region` / `// #endregion`) : se déclare via
  `folding.markers` (regex) dans la config de langage `monaco-mdx.ts`, la
  contribution folding elle-même est déjà importée par `monaco-core.ts`.
- `en.ts` typé `typeof fr` impose la complétude des clés i18n à la
  compilation : une clé manquante devient une erreur TypeScript, pas un texte
  vide silencieux en prod.

## Conventions de travail

- Chaque phase se termine par un binaire utilisable au quotidien ; pas de
  grand refactoring spéculatif.
- Toute proposition d'architecture nouvelle doit être justifiée contre :
  simplicité, robustesse, faible maintenance, rapidité de livraison.
- Tests : couvrir le tokenizer MDX et les services Core ; pas d'objectif de
  couverture sur l'UI.

## Statut

MVP livré (Phases 1–5) : connexion + éditeur Monaco + exécution + grille ;
explorateur de métadonnées, autocomplétion, stats perfmon, ClearCache,
historique ; panneau IA (API Anthropic, `claude-opus-4-8`) ; MDX Script +
graphe de dépendances + doc Markdown exportable ; publication GitHub (MIT,
CI + Release GitHub Actions). Extras post-MVP : **Profiler** (découpage
Formula/Storage Engine par requête via trace SSAS), **i18n FR/EN**.

**Mode projet SSDT : TERMINÉ (2026-07-24)** — ouverture/édition du fichier
`.cube` d'un projet SSDT Multidimensional (`CubeProjectService`,
`CubeScope.Core/Project/`) : lecture XML `PreserveWhitespace`, script
éditable seulement si exactement 1 `Command` non vide (`CanEdit`), régions
`// #region` / `// #endregion` (parsées par `ScriptParser`, pliage Monaco
assorti), sauvegarde round-trip dans le `.cube` + export texte
`<nom>.mdxscript.mdx` (diffs Git lisibles) + `.bak` une fois par session,
rapport des `CalculationProperty` orphelines (référence à un membre/set
calculé disparu — signalé, jamais supprimé automatiquement). Déploiement du
script seul vers un cube de dev via AMO façon BIDS Helper
(`ScriptDeployService`) avec garde de divergence (compare serveur vs projet,
refuse sans `force` si différent) et garde catalogue dev (nom contenant
« dev »). StateStore v2 (`RecentProject(Path, LastUsedUtc)`,
`PRAGMA user_version = 2`).

`cubescope.exe` est un single-file self-contained : SPA et DLL natives sont
embarquées dans l'assembly (voir `EmbeddedSpaFileProvider` + la cible `EmbedSpa`
du `.csproj`) → l'exe fonctionne seul, déplaçable.

Roadmap (US) : Phase 1 = connexion + éditeur + exécution + grille (US 1-4) ;
Phase 2 = explorateur, autocomplétion, stats, cache, historique (US 5-12) ;
Phase 3 = panneau IA (US 13-15) ; Phase 4 = script, dépendances, doc (US 16-20) ;
Phase 5 = publication GitHub. Le projet `CubeScope.Spike` reste dans la solution
comme harnais de non-régression serveur en lecture seule (`--discover`).
