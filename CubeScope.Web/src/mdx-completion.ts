// MDX autocompletion: keywords + functions (static), measures/dimensions/hierarchies/
// levels (current cube metadata), members lazily after ".&" or "." (server cache
// + client cache). Pragmatic regex-based approach, aligned with the tokenizer (~95%).
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

// Client cache of members per hierarchy (the server caches too — deliberate double safety net)
const memberCache = new Map<string, MemberMeta[]>()
const captionCache = new Map<string, string | null>() // unique name → caption (hover)

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

/** Clears the caches (catalog/cube change). */
export function resetCompletionCache(): void {
  memberCache.clear()
  captionCache.clear()
}

/** Clears only the client caption cache (manual refresh of captions). */
export function clearCaptionCache(): void {
  captionCache.clear()
}

function suggestion(
  label: string,
  insertText: string,
  kind: monaco.languages.CompletionItemKind,
  range: monaco.IRange,
  detail?: string,
  documentation?: string,
): monaco.languages.CompletionItem {
  // filterText covers the bracketed name AND the unique name: typing "[Sales" must match
  // the measure "Sales Amount" (inserted as [Measures].[Sales Amount])
  return { label, insertText, kind, range, detail, documentation, filterText: `[${label}] ${insertText}` }
}

/** MDX function suggestion: signature (detail) + short doc (documentation) when known. */
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

    // Case 1: ".&[" or "." after a bracketed unique name → actual members + member functions
    const afterDot = line.match(/((?:\[(?:[^\]]|\]\])+\])(?:\.\[(?:[^\]]|\]\])+\])*)\.(?:&?\[?)?([\w]*)$/)
    if (afterDot && meta) {
      const uniqueName = afterDot[1]
      // The target can be a hierarchy, or a level (→ members of its hierarchy)
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
        // Inserting the member suffix: "[Dates].[Année].&[2026]" → insert "&[2026]" after the "."
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

    // Case 2: general context — range = from the open "[" if any, otherwise the current word
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

// Hover: first a cube measure/member reference ([Dim].[Hier].[…]) → caption +
// description (metadata); otherwise fallback to a known MDX function (signature + doc).
interface RefEntry {
  caption: string
  description?: string
  kind: string
}

/** "[Measures] . [X]" → "[Measures].[X]"; also handles the key "] . & [" → "].&[". */
export function normalizeRef(s: string): string {
  return s.replace(/\]\s*\.\s*(&?)\s*\[/g, (_m, amp) => '].' + amp + '[')
}

/**
 * Resolves a referenced member (e.g. [Dim].[Hier].[Level].&[Key] or [Dim].[Hier].[Name]) to its
 * caption via a targeted server-side lookup (MDSCHEMA_MEMBERS filtered on MEMBER_UNIQUE_NAME):
 * works for ANY member, regardless of the dimension size (unlike the load capped at 1000).
 * Result cached by unique name. Null if not resolved.
 */
async function resolveMemberCaption(normRef: string): Promise<string | null> {
  if (!store.cube) return null
  if (captionCache.has(normRef)) return captionCache.get(normRef) ?? null
  let caption: string | null = null
  try {
    caption = (await api.memberCaption(store.cube, normRef)).caption
  } catch {
    caption = null
  }
  captionCache.set(normRef, caption)
  return caption
}

/** Map of normalized uniqueName → caption/description, built from the current cube. */
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

// Pattern for a reference chain: [..] segments joined by "." / ".&" / adjacent "&" (composite
// key &[k1]&[k2]), with escaped ]]. New instance on each use (g flag = stateful).
const REF_PATTERN =
  String.raw`\[(?:[^\]]|\]\])*\](?:\s*\.\s*&?\s*\[(?:[^\]]|\]\])*\]|&\s*\[(?:[^\]]|\]\])*\])*`

/** Bracketed reference chain containing the column (1-based), or null. Handles escaped ]],
 *  key qualifiers `.&[key]` and composite keys `&[k1]&[k2]`. */
export function refAtColumn(line: string, column: number): { text: string; start: number; end: number } | null {
  const re = new RegExp(REF_PATTERN, 'g')
  const col0 = column - 1
  let m: RegExpExecArray | null
  while ((m = re.exec(line)) !== null) {
    if (m.index <= col0 && col0 <= m.index + m[0].length)
      return { text: m[0], start: m.index + 1, end: m.index + m[0].length + 1 }
  }
  return null
}

monaco.languages.registerHoverProvider('mdx', {
  async provideHover(model, position) {
    const ref = refAtColumn(model.getLineContent(position.lineNumber), position.column)
    if (ref) {
      const normRef = normalizeRef(ref.text)
      const range = new monaco.Range(position.lineNumber, ref.start, position.lineNumber, ref.end)
      // 1) Measure / dimension / hierarchy / level (synchronous metadata) → caption + description
      const entry = buildRefLookup().get(normRef)
      if (entry) {
        const contents: { value: string }[] = [
          { value: '**' + entry.caption + '**' + (entry.kind === 'measure' ? '' : ' _(' + entry.kind + ')_') },
        ]
        if (entry.description) contents.push({ value: entry.description })
        contents.push({ value: '`' + normRef + '`' })
        return { range, contents }
      }
      // 2) Member (key &[…] or name) → caption loaded on the fly from the hierarchy
      const caption = await resolveMemberCaption(normRef)
      if (caption)
        return { range, contents: [{ value: '**' + caption + '** _(membre)_' }, { value: '`' + normRef + '`' }] }
    }
    // 3) Fallback: known MDX function → signature + short doc
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

/**
 * Preloads in the background the captions of members referenced in a script (throttled,
 * non-blocking) for instant hovers. Extracts "member" references (key &[…] or
 * ≥3 bracketed segments, excluding metadata already resolved), deduplicated and capped, then
 * a targeted lookup in parallel (limited concurrency). Best effort: failures are ignored.
 */
export async function prefetchMemberCaptions(
  scriptText: string,
  onProgress?: (done: number, total: number) => void,
): Promise<void> {
  if (!store.cube || !store.cubeMeta) {
    onProgress?.(0, 0)
    return
  }
  const lookup = buildRefLookup()
  const re = new RegExp(REF_PATTERN, 'g')
  const seen = new Set<string>()
  const refs: string[] = []
  const MAX = 400 // beyond that, on-demand resolution on hover (still works)
  let m: RegExpExecArray | null
  while ((m = re.exec(scriptText)) !== null) {
    const norm = normalizeRef(m[0])
    if (seen.has(norm)) continue
    seen.add(norm)
    if (lookup.has(norm) || captionCache.has(norm)) continue
    // Only members with a &[…] key: those are the ones we want (portfolios, securities,
    // ratings…). Name/level refs ([Dim].[Hier].[Level]) are often levels or
    // expression fragments that make StrToMember fail — they get resolved on hover.
    if (!norm.includes('&[')) continue
    refs.push(norm)
    if (refs.length >= MAX) break
  }
  const total = refs.length
  onProgress?.(0, total)
  if (total === 0) return
  // BATCHED lookup: instead of ~400 single HTTP calls, split into chunks of 150 and
  // resolve each chunk in a single POST. Low concurrency (2): leave connections
  // to the UI and do not hammer SSAS. Best effort, failures are ignored.
  const CHUNK = 50
  const CONCURRENCY = 2
  const cube = store.cube
  const chunks: string[][] = []
  for (let c = 0; c < refs.length; c += CHUNK) chunks.push(refs.slice(c, c + CHUNK))
  let ci = 0
  let done = 0
  const worker = async () => {
    while (ci < chunks.length) {
      const chunk = chunks[ci++]
      try {
        const res = await api.memberCaptions(cube, chunk)
        for (const name of chunk) captionCache.set(name, res[name] ?? null)
      } catch {
        // chunk failed: cache nothing, on-demand resolution on hover
      }
      done += chunk.length
      onProgress?.(done, total)
    }
  }
  await Promise.all(Array.from({ length: CONCURRENCY }, () => worker()))
}
