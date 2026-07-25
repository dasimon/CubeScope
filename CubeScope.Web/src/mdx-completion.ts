// Autocomplétion MDX : mots-clés + fonctions (statique), mesures/dimensions/hiérarchies/
// niveaux (métadonnées du cube courant), membres en lazy après ".&" ou "." (cache serveur
// + cache client). Approche pragmatique par regex, alignée sur le tokenizer (~95 %).
import { monaco } from './monaco-mdx'
import { api, type MemberMeta } from './api'
import { store } from './store'
import { mdxFunctions } from './mdxFunctions'

const KEYWORD_SUGGESTIONS = [
  'SELECT', 'FROM', 'WHERE', 'ON COLUMNS', 'ON ROWS', 'NON EMPTY', 'WITH MEMBER', 'WITH SET',
  'AS', 'CELL PROPERTIES', 'PROPERTIES', 'HAVING', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END',
  'AND', 'OR', 'NOT', 'IS', 'EXISTING',
]

const FUNCTION_SUGGESTIONS = [
  'Members', 'Children', 'AllMembers', 'CurrentMember', 'DefaultMember', 'Parent', 'FirstChild',
  'LastChild', 'PrevMember', 'NextMember', 'Lag(', 'Lead(', 'Head(', 'Tail(', 'Filter(',
  'Order(', 'TopCount(', 'BottomCount(', 'CrossJoin(', 'NonEmpty(', 'Except(', 'Descendants(',
  'Ancestors(', 'Hierarchize(', 'Sum(', 'Avg(', 'Count(', 'Min(', 'Max(', 'Aggregate(', 'IIf(',
  'CoalesceEmpty(', 'IsEmpty(', 'ParallelPeriod(', 'PeriodsToDate(', 'Ytd(', 'Qtd(', 'Mtd(',
]

// Cache client des membres par hiérarchie (le serveur cache aussi — double filet assumé)
const memberCache = new Map<string, MemberMeta[]>()

async function membersOf(hierarchy: string): Promise<MemberMeta[]> {
  if (!store.cube) return []
  const cached = memberCache.get(hierarchy)
  if (cached) return cached
  try {
    const m = await api.members(store.cube, hierarchy)
    memberCache.set(hierarchy, m)
    return m
  } catch {
    return []
  }
}

/** Vide le cache membres (changement de catalogue/cube). */
export function resetCompletionCache(): void {
  memberCache.clear()
}

function suggestion(
  label: string,
  insertText: string,
  kind: monaco.languages.CompletionItemKind,
  range: monaco.IRange,
  detail?: string,
  documentation?: string,
): monaco.languages.CompletionItem {
  // filterText couvre le nom crocheté ET l'unique name : taper "[Sales" doit matcher
  // la mesure "Sales Amount" (insérée comme [Measures].[Sales Amount])
  return { label, insertText, kind, range, detail, documentation, filterText: `[${label}] ${insertText}` }
}

/** Suggestion de fonction MDX : signature (detail) + doc courte (documentation) si connue. */
function functionSuggestion(f: string, range: monaco.IRange): monaco.languages.CompletionItem {
  const name = f.replace(/\($/, '').toUpperCase()
  const sig = mdxFunctions[name]
  return suggestion(f, f, monaco.languages.CompletionItemKind.Function, range, sig?.signature, sig?.doc)
}

monaco.languages.registerCompletionItemProvider('mdx', {
  triggerCharacters: ['[', '.', '&'],

  async provideCompletionItems(model, position) {
    const meta = store.cubeMeta
    const line = model.getLineContent(position.lineNumber).slice(0, position.column - 1)
    const word = model.getWordUntilPosition(position)

    // Cas 1 : ".&[" ou "." après un unique name crocheté → membres réels + fonctions membres
    const afterDot = line.match(/((?:\[(?:[^\]]|\]\])+\])(?:\.\[(?:[^\]]|\]\])+\])*)\.(?:&?\[?)?([\w]*)$/)
    if (afterDot && meta) {
      const uniqueName = afterDot[1]
      // La cible peut être une hiérarchie, ou un niveau (→ membres de sa hiérarchie)
      const hierarchies = meta.dimensions.flatMap((d) => d.hierarchies)
      const hier =
        hierarchies.find((h) => h.uniqueName === uniqueName) ??
        hierarchies.find((h) => h.levels.some((l) => l.uniqueName === uniqueName))
      const start = position.column - (line.length - line.lastIndexOf('.') - 1)
      const range: monaco.IRange = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: start,
        endColumn: position.column,
      }
      const items: monaco.languages.CompletionItem[] = FUNCTION_SUGGESTIONS.map((f) =>
        functionSuggestion(f, range),
      )
      if (hier) {
        const members = await membersOf(hier.uniqueName)
        // Insertion du suffixe membre : "[Dates].[Année].&[2026]" → on insère "&[2026]" après le "."
        for (const m of members) {
          const suffix = m.uniqueName.startsWith(uniqueName + '.')
            ? m.uniqueName.slice(uniqueName.length + 1)
            : m.uniqueName
          items.push(
            suggestion(m.caption, suffix, monaco.languages.CompletionItemKind.EnumMember, range, m.uniqueName),
          )
        }
      }
      return { suggestions: items }
    }

    // Cas 2 : contexte général — plage = depuis le "[" ouvert éventuel, sinon le mot courant
    const openBracket = line.match(/\[[^\]]*$/)
    const startColumn = openBracket ? position.column - openBracket[0].length : word.startColumn
    const range: monaco.IRange = {
      startLineNumber: position.lineNumber,
      endLineNumber: position.lineNumber,
      startColumn,
      endColumn: position.column,
    }

    const items: monaco.languages.CompletionItem[] = []
    if (meta) {
      for (const f of meta.measureFolders)
        for (const m of f.measures)
          items.push(
            suggestion(
              m.name,
              m.uniqueName,
              monaco.languages.CompletionItemKind.Value,
              range,
              m.uniqueName,
              m.description || undefined,
            ),
          )
      for (const d of meta.dimensions) {
        items.push(
          suggestion(d.name, d.uniqueName, monaco.languages.CompletionItemKind.Class, range, d.uniqueName),
        )
        for (const h of d.hierarchies)
          items.push(
            suggestion(`${d.name}.${h.name}`, h.uniqueName, monaco.languages.CompletionItemKind.Struct, range, h.uniqueName),
          )
      }
    }
    if (!openBracket) {
      for (const k of KEYWORD_SUGGESTIONS)
        items.push(suggestion(k, k, monaco.languages.CompletionItemKind.Keyword, range))
      for (const f of FUNCTION_SUGGESTIONS)
        items.push(functionSuggestion(f, range))
    }
    return { suggestions: items }
  },
})

// Survol : d'abord une référence de mesure/membre du cube ([Dim].[Hier].[…]) → caption +
// description (métadonnées) ; sinon repli sur une fonction MDX connue (signature + doc).
interface RefEntry {
  caption: string
  description?: string
  kind: string
}

/** "[Measures] . [X]" → "[Measures].[X]" (espaces autour des points, comme le tokenizer). */
function normalizeRef(s: string): string {
  return s.replace(/\]\s*\.\s*\[/g, '].[')
}

/** Table uniqueName normalisé → caption/description, construite depuis le cube courant. */
function buildRefLookup(): Map<string, RefEntry> {
  const map = new Map<string, RefEntry>()
  const meta = store.cubeMeta
  if (!meta) return map
  for (const f of meta.measureFolders)
    for (const m of f.measures)
      map.set(normalizeRef(m.uniqueName), {
        caption: m.name,
        description: m.description || undefined,
        kind: 'measure',
      })
  for (const d of meta.dimensions) {
    map.set(normalizeRef(d.uniqueName), { caption: d.name, kind: 'dimension' })
    for (const h of d.hierarchies) {
      map.set(normalizeRef(h.uniqueName), { caption: h.name, kind: 'hierarchy' })
      for (const l of h.levels) map.set(normalizeRef(l.uniqueName), { caption: l.name, kind: 'level' })
    }
  }
  return map
}

/** Chaîne de référence crochetée contenant la colonne (1-based), ou null. Gère le ]] échappé. */
function refAtColumn(line: string, column: number): { text: string; start: number; end: number } | null {
  const re = /\[(?:[^\]]|\]\])*\](?:\s*\.\s*\[(?:[^\]]|\]\])*\])*/g
  const col0 = column - 1
  let m: RegExpExecArray | null
  while ((m = re.exec(line)) !== null) {
    if (m.index <= col0 && col0 <= m.index + m[0].length)
      return { text: m[0], start: m.index + 1, end: m.index + m[0].length + 1 }
  }
  return null
}

monaco.languages.registerHoverProvider('mdx', {
  provideHover(model, position) {
    // 1) Référence de mesure/membre du cube → caption + description
    const ref = refAtColumn(model.getLineContent(position.lineNumber), position.column)
    if (ref) {
      const entry = buildRefLookup().get(normalizeRef(ref.text))
      if (entry) {
        const contents: { value: string }[] = [
          { value: '**' + entry.caption + '**' + (entry.kind === 'measure' ? '' : ' _(' + entry.kind + ')_') },
        ]
        if (entry.description) contents.push({ value: entry.description })
        contents.push({ value: '`' + normalizeRef(ref.text) + '`' })
        return {
          range: new monaco.Range(position.lineNumber, ref.start, position.lineNumber, ref.end),
          contents,
        }
      }
    }
    // 2) Repli : fonction MDX connue → signature + doc courte
    const word = model.getWordAtPosition(position)
    if (!word) return null
    const fn = mdxFunctions[word.word.toUpperCase()]
    if (!fn) return null
    return {
      range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
      contents: [{ value: '```mdx\n' + fn.signature + '\n```' }, { value: fn.doc }],
    }
  },
})
