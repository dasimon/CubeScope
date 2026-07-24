// Signatures + doc courte des fonctions MDX les plus courantes, pour l'autocomplétion
// (detail/documentation) et le HoverProvider. Clé = nom de fonction en MAJUSCULES.
// Couverture volontairement partielle : mieux vaut omettre une fonction que deviner
// une signature fausse. Aligné sur (sous-ensemble de) FUNCTIONS dans monaco-mdx.ts.
export interface MdxFunctionDoc {
  signature: string
  doc: string
}

export const mdxFunctions: Record<string, MdxFunctionDoc> = {
  AGGREGATE: {
    signature: 'Aggregate(set [, numeric_expression])',
    doc: "Agrège un ensemble selon la fonction d'agrégation de la mesure courante (ou l'expression donnée).",
  },
  ANCESTOR: {
    signature: 'Ancestor(member, distance | level)',
    doc: "Renvoie l'ancêtre d'un membre à une distance donnée ou à un niveau donné.",
  },
  ANCESTORS: {
    signature: 'Ancestors(member, distance | level)',
    doc: "Renvoie l'ensemble des ancêtres d'un membre jusqu'à une distance ou un niveau donné.",
  },
  AVG: {
    signature: 'Avg(set [, numeric_expression])',
    doc: "Moyenne des valeurs non vides d'un ensemble.",
  },
  AXIS: {
    signature: 'Axis(index)',
    doc: "Renvoie l'ensemble des tuples de l'axe de requête numéro index.",
  },
  BOTTOMCOUNT: {
    signature: 'BottomCount(set, count [, numeric_expression])',
    doc: 'Les count tuples les plus bas d\'un ensemble, triés par expression.',
  },
  CHILDREN: {
    signature: 'member.Children',
    doc: "Ensemble des enfants directs d'un membre.",
  },
  CLOSINGPERIOD: {
    signature: 'ClosingPeriod([level] [, member])',
    doc: "Dernier membre descendant d'un membre, au niveau donné (typiquement temporel).",
  },
  COALESCEEMPTY: {
    signature: 'CoalesceEmpty(expression [, expression ...])',
    doc: 'Renvoie la première expression non vide parmi la liste.',
  },
  COUNT: {
    signature: 'Count(set [, EXCLUDEEMPTY | INCLUDEEMPTY])',
    doc: "Nombre de tuples d'un ensemble.",
  },
  COUSIN: {
    signature: 'Cousin(member, ancestor_member)',
    doc: "Membre de même position relative que member, sous ancestor_member.",
  },
  CROSSJOIN: {
    signature: 'CrossJoin(set1, set2 [, ...])',
    doc: 'Produit cartésien de plusieurs ensembles.',
  },
  CURRENTMEMBER: {
    signature: 'hierarchy.CurrentMember',
    doc: "Membre courant d'une hiérarchie dans le contexte d'itération.",
  },
  DESCENDANTS: {
    signature: 'Descendants(member [, level] [, desc_flag])',
    doc: "Ensemble des descendants d'un membre, jusqu'à un niveau et selon un mode donné (SELF, BEFORE, AFTER...).",
  },
  DISTINCT: {
    signature: 'Distinct(set)',
    doc: "Supprime les tuples en double d'un ensemble.",
  },
  DISTINCTCOUNT: {
    signature: 'DistinctCount(set)',
    doc: "Nombre de valeurs distinctes non vides d'un ensemble.",
  },
  EXCEPT: {
    signature: 'Except(set1, set2 [, ALL])',
    doc: 'Tuples de set1 absents de set2 (différence ensembliste).',
  },
  EXISTS: {
    signature: 'Exists(set1, set2 [, measure_group_name])',
    doc: 'Tuples de set1 qui existent avec au moins un tuple de set2.',
  },
  FILTER: {
    signature: 'Filter(set, logical_expression)',
    doc: 'Retourne le sous-ensemble des tuples pour lesquels la condition est vraie.',
  },
  FIRSTCHILD: {
    signature: 'member.FirstChild',
    doc: "Premier enfant d'un membre.",
  },
  GENERATE: {
    signature: 'Generate(set, string_expr | set_expr [, ALL])',
    doc: 'Applique une expression à chaque tuple et concatène les résultats (chaîne ou ensemble).',
  },
  HEAD: {
    signature: 'Head(set [, count])',
    doc: "Les count premiers tuples d'un ensemble (défaut 1).",
  },
  HIERARCHIZE: {
    signature: 'Hierarchize(set [, POST])',
    doc: "Réordonne un ensemble selon la hiérarchie naturelle (parents avant enfants, ou POST pour l'inverse).",
  },
  IIF: {
    signature: 'IIf(condition, expr_if_true, expr_if_false)',
    doc: "Expression conditionnelle : renvoie l'une des deux valeurs/membres selon la condition.",
  },
  INTERSECT: {
    signature: 'Intersect(set1, set2 [, ALL])',
    doc: 'Intersection de deux ensembles.',
  },
  ISEMPTY: {
    signature: 'IsEmpty(expression)',
    doc: 'Vrai si l\'expression évaluée renvoie la cellule vide.',
  },
  ISLEAF: {
    signature: 'IsLeaf(member)',
    doc: "Vrai si le membre est une feuille (n'a pas d'enfant).",
  },
  ITEM: {
    signature: 'set.Item(index) | tuple.Item(index)',
    doc: "Élément d'un ensemble ou d'un tuple à l'index donné (base 0).",
  },
  LAG: {
    signature: 'member.Lag(index)',
    doc: "Membre situé index positions avant, dans la même hiérarchie/niveau.",
  },
  LASTCHILD: {
    signature: 'member.LastChild',
    doc: "Dernier enfant d'un membre.",
  },
  LASTPERIODS: {
    signature: 'LastPeriods(index [, member])',
    doc: 'Ensemble des index dernières périodes se terminant au membre donné.',
  },
  LEAD: {
    signature: 'member.Lead(index)',
    doc: "Membre situé index positions après, dans la même hiérarchie/niveau.",
  },
  MAX: {
    signature: 'Max(set [, numeric_expression])',
    doc: "Valeur maximale non vide d'un ensemble.",
  },
  MEDIAN: {
    signature: 'Median(set [, numeric_expression])',
    doc: "Médiane des valeurs non vides d'un ensemble.",
  },
  MEMBERS: {
    signature: 'hierarchy.Members | level.Members',
    doc: "Ensemble de tous les membres d'une hiérarchie ou d'un niveau.",
  },
  MIN: {
    signature: 'Min(set [, numeric_expression])',
    doc: "Valeur minimale non vide d'un ensemble.",
  },
  MTD: {
    signature: 'Mtd([member])',
    doc: 'Raccourci PeriodsToDate au niveau Mois, depuis le début du mois courant.',
  },
  NAME: {
    signature: 'member.Name | hierarchy.Name | level.Name',
    doc: "Nom (non qualifié) d'un objet MDX.",
  },
  NEXTMEMBER: {
    signature: 'member.NextMember',
    doc: 'Membre suivant dans le même niveau (ordre naturel).',
  },
  OPENINGPERIOD: {
    signature: 'OpeningPeriod([level] [, member])',
    doc: "Premier membre descendant d'un membre, au niveau donné (typiquement temporel).",
  },
  ORDER: {
    signature: 'Order(set, expression [, ASC | DESC | BASC | BDESC])',
    doc: 'Trie un ensemble selon une expression, avec ou sans respect de la hiérarchie (B = break).',
  },
  PARALLELPERIOD: {
    signature: 'ParallelPeriod([level] [, index] [, member])',
    doc: 'Membre situé index périodes avant, au même niveau relatif (comparaison type N-1).',
  },
  PARENT: {
    signature: 'member.Parent',
    doc: "Membre parent d'un membre.",
  },
  PERIODSTODATE: {
    signature: 'PeriodsToDate([level] [, member])',
    doc: "Ensemble des périodes depuis le début de l'ancêtre au niveau donné jusqu'au membre.",
  },
  PREVMEMBER: {
    signature: 'member.PrevMember',
    doc: 'Membre précédent dans le même niveau (ordre naturel).',
  },
  PROPERTIES: {
    signature: "member.Properties(\"property_name\")",
    doc: "Valeur d'une propriété de membre.",
  },
  QTD: {
    signature: 'Qtd([member])',
    doc: 'Raccourci PeriodsToDate au niveau Trimestre, depuis le début du trimestre courant.',
  },
  RANK: {
    signature: 'Rank(tuple, set [, sort_expression])',
    doc: "Position (1-based) d'un tuple dans un ensemble, éventuellement trié.",
  },
  ROOT: {
    signature: 'hierarchy.Root | Root(hierarchy)',
    doc: "Membre racine d'une hiérarchie.",
  },
  SIBLINGS: {
    signature: 'member.Siblings',
    doc: "Ensemble des membres de même niveau et même parent (inclut le membre lui-même).",
  },
  STDDEV: {
    signature: 'Stddev(set [, numeric_expression])',
    doc: "Écart-type (échantillon) des valeurs non vides d'un ensemble.",
  },
  STRTOMEMBER: {
    signature: 'StrToMember(string_expression)',
    doc: 'Convertit une chaîne (unique name) en membre.',
  },
  STRTOSET: {
    signature: 'StrToSet(string_expression [, CONSTRAINED])',
    doc: 'Convertit une chaîne MDX en ensemble.',
  },
  SUM: {
    signature: 'Sum(set [, numeric_expression])',
    doc: "Somme des valeurs non vides d'un ensemble.",
  },
  TAIL: {
    signature: 'Tail(set [, count])',
    doc: "Les count derniers tuples d'un ensemble (défaut 1).",
  },
  TOPCOUNT: {
    signature: 'TopCount(set, count [, numeric_expression])',
    doc: 'Les count tuples les plus hauts d\'un ensemble, triés par expression.',
  },
  TOPPERCENT: {
    signature: 'TopPercent(set, percentage, numeric_expression)',
    doc: "Tuples les plus hauts dont le cumul atteint le pourcentage donné de l'ensemble.",
  },
  UNION: {
    signature: 'Union(set1, set2 [, ALL])',
    doc: 'Union de deux ensembles (doublons supprimés sauf ALL).',
  },
  YTD: {
    signature: 'Ytd([member])',
    doc: 'Raccourci PeriodsToDate au niveau Année, depuis le début de l\'année courante.',
  },
}
