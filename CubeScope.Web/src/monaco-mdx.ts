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
  'create', 'alter', 'session', 'static', 'dynamic', 'null', 'visible', 'hidden', 'solve_order', 'format_string',
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

// Repliement structurel MDX : la config `folding.markers` ne replie QUE les régions
// #region. Ce fournisseur ajoute le repliement des blocs multi-lignes `{ }` / `( )` et
// des `SCOPE … END SCOPE` — indispensable pour les gros ensembles (CREATE STATIC SET
// … AS { … }). Scan caractère à caractère qui saute chaînes, commentaires et
// [identifiants crochetés] (même logique que le tokenizer serveur), + régions par ligne.
const isWordChar = (c: string) => /[A-Za-z0-9_]/.test(c)
function wordAt(text: string, i: number, w: string): boolean {
  if (text.slice(i, i + w.length).toUpperCase() !== w) return false
  if (i > 0 && isWordChar(text[i - 1])) return false
  const end = i + w.length
  return end >= text.length || !isWordChar(text[end])
}
function nextWordIs(text: string, from: number, w: string): boolean {
  let i = from
  while (i < text.length && /\s/.test(text[i])) i++
  return wordAt(text, i, w)
}
function prevWordIsEnd(text: string, i: number): boolean {
  let j = i - 1
  while (j >= 0 && /\s/.test(text[j])) j--
  let e = j
  while (e >= 0 && isWordChar(text[e])) e--
  return wordAt(text, e + 1, 'END')
}

monaco.languages.registerFoldingRangeProvider('mdx', {
  provideFoldingRanges(model) {
    const text = model.getValue()
    const ranges: monaco.languages.FoldingRange[] = []
    const braces: number[] = []
    const parens: number[] = []
    const scopes: number[] = []
    let line = 1
    let inLineComment = false
    let inBlockComment = false
    let inString = false
    let inBracket = false
    let stringChar = ''

    for (let i = 0; i < text.length; i++) {
      const c = text[i]
      const next = i + 1 < text.length ? text[i + 1] : ''
      if (c === '\n') {
        line++
        inLineComment = false
        continue
      }
      if (inLineComment) continue
      if (inBlockComment) {
        if (c === '*' && next === '/') {
          inBlockComment = false
          i++
        }
        continue
      }
      if (inString) {
        if (c === stringChar) inString = false
        continue
      }
      if (inBracket) {
        if (c === ']') inBracket = false
        continue
      }
      if ((c === '/' && next === '/') || (c === '-' && next === '-')) {
        inLineComment = true
        i++
        continue
      }
      if (c === '/' && next === '*') {
        inBlockComment = true
        i++
        continue
      }
      if (c === '"' || c === "'") {
        inString = true
        stringChar = c
        continue
      }
      if (c === '[') {
        inBracket = true
        continue
      }
      if (c === '{') braces.push(line)
      else if (c === '}') {
        const s = braces.pop()
        if (s !== undefined && s < line) ranges.push({ start: s, end: line })
      } else if (c === '(') parens.push(line)
      else if (c === ')') {
        const s = parens.pop()
        if (s !== undefined && s < line) ranges.push({ start: s, end: line })
      } else if (isWordChar(c) && (i === 0 || !isWordChar(text[i - 1]))) {
        if (wordAt(text, i, 'SCOPE') && !prevWordIsEnd(text, i)) scopes.push(line)
        else if (wordAt(text, i, 'END') && nextWordIs(text, i + 3, 'SCOPE')) {
          const s = scopes.pop()
          if (s !== undefined && s < line) ranges.push({ start: s, end: line })
        }
      }
    }

    // Régions #region / #endregion (repliables et repliées par défaut par Monaco)
    const regions: number[] = []
    const lines = text.split('\n')
    for (let ln = 0; ln < lines.length; ln++) {
      if (/^\s*(?:\/\/|--)\s*#region\b/i.test(lines[ln])) regions.push(ln + 1)
      else if (/^\s*(?:\/\/|--)\s*#endregion\b/i.test(lines[ln])) {
        const s = regions.pop()
        if (s !== undefined) ranges.push({ start: s, end: ln + 1, kind: monaco.languages.FoldingRangeKind.Region })
      }
    }
    return ranges
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
