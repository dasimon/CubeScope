// Grammaire Monarch MDX v1 (décision actée : tokenizer pragmatique, pas d'AST).
// Sert la coloration : mots-clés, fonctions, [identifiants crochetés], chaînes,
// commentaires, nombres. L'autocomplétion viendra en Phase 2.
// Import via monaco-core (contribs éditeur sans les 81 langages intégrés)
import * as monaco from './monaco-core'
// monaco 0.56 : exports map "./*" → "./esm/vs/*.js" — ne plus écrire le préfixe esm/vs
import editorWorker from 'monaco-editor/editor/editor.worker?worker'

self.MonacoEnvironment = {
  // MDX n'a pas de worker de langage dédié : le worker éditeur générique suffit.
  getWorker: () => new editorWorker(),
}

const KEYWORDS = [
  'select', 'from', 'where', 'on', 'columns', 'rows', 'pages', 'sections', 'chapters', 'axis',
  'with', 'member', 'set', 'measure', 'as', 'cell', 'calculation', 'properties', 'dimension',
  'non', 'empty', 'nonempty', 'having', 'subcube', 'case', 'when', 'then', 'else', 'end',
  'and', 'or', 'not', 'xor', 'is', 'in', 'existing', 'scope', 'this', 'calculate',
  'freeze', 'if', 'drillthrough', 'maxrows', 'firstrowset', 'return', 'refresh', 'cube',
  'create', 'alter', 'session', 'null', 'visible', 'hidden', 'solve_order', 'format_string',
]

const FUNCTIONS = [
  'aggregate', 'ancestor', 'ancestors', 'ascendants', 'avg', 'axis', 'bottomcount', 'bottomsum',
  'children', 'closingperiod', 'coalesceempty', 'count', 'cousin', 'crossjoin', 'currentmember',
  'defaultmember', 'descendants', 'distinct', 'distinctcount', 'except', 'exists', 'extract',
  'filter', 'firstchild', 'firstsibling', 'generate', 'head', 'hierarchize', 'iif', 'intersect',
  'isancestor', 'isempty', 'isgeneration', 'isleaf', 'issibling', 'item', 'lag', 'lastchild',
  'lastperiods', 'lastsibling', 'lead', 'leaves', 'linkmember', 'lookupcube', 'max', 'median',
  'members', 'min', 'mtd', 'name', 'nextmember', 'nonemptycrossjoin', 'openingperiod', 'order',
  'parallelperiod', 'parent', 'periodstodate', 'prevmember', 'properties', 'qtd', 'rank', 'root',
  'siblings', 'stddev', 'strtomember', 'strtoset', 'strtotuple', 'strtovalue', 'subset', 'sum',
  'tail', 'toggledrillstate', 'topcount', 'toppercent', 'topsum', 'union', 'unorder',
  'uniquename', 'validmeasure', 'value', 'visualtotals', 'wtd', 'ytd',
]

monaco.languages.register({ id: 'mdx' })

monaco.languages.setLanguageConfiguration('mdx', {
  comments: { lineComment: '--', blockComment: ['/*', '*/'] },
  brackets: [
    ['{', '}'],
    ['(', ')'],
  ],
  autoClosingPairs: [
    { open: '{', close: '}' },
    { open: '(', close: ')' },
    { open: '[', close: ']' },
    { open: "'", close: "'" },
    { open: '"', close: '"' },
  ],
  folding: {
    markers: {
      start: /^\s*(?:\/\/|--)\s*#region\b/,
      end: /^\s*(?:\/\/|--)\s*#endregion\b/,
    },
  },
})

monaco.languages.setMonarchTokensProvider('mdx', {
  ignoreCase: true,
  defaultToken: '',
  keywords: KEYWORDS,
  functions: FUNCTIONS,
  tokenizer: {
    root: [
      [/--.*$/, 'comment'],
      [/\/\/.*$/, 'comment'],
      [/\/\*/, 'comment', '@comment'],
      // Identifiant crocheté : [Dim].[Hier] — ]] = échappement d'un crochet fermant
      [/\[(?:[^\]]|\]\])*\]/, 'identifier.bracket'],
      [/"[^"]*"/, 'string'],
      [/'[^']*'/, 'string'],
      [/\d+(\.\d+)?/, 'number'],
      [/[a-zA-Z_][\w$]*/, { cases: { '@keywords': 'keyword', '@functions': 'support.function', '@default': '' } }],
      [/[{}()]/, '@brackets'],
      [/[,;.:&*@]/, 'delimiter'],
      [/[<>=+\-/]/, 'operator'],
    ],
    comment: [
      [/\*\//, 'comment', '@pop'],
      [/./, 'comment'],
    ],
  },
})

monaco.editor.defineTheme('cubescope-dark', {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'identifier.bracket', foreground: '4EC9B0' }, // [Dim].[Hier] en vert d'eau
    { token: 'support.function', foreground: 'DCDCAA' }, // fonctions MDX en jaune pâle
  ],
  colors: {},
})

export { monaco }
