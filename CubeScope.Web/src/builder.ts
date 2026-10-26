// State of the graphical builder, shared by the explorer (drag source) and the Builder panel.
// One direction only: this model → MDX (generated server-side). Never read back from the editor.
import { reactive, watch } from 'vue'
import type { BuilderAxisItem, BuilderModel, HierarchyMeta } from './api'
import { store } from './store'

/** Drag payload of the explorer, next to text/plain (which the editor keeps using). */
export const ITEM_MIME = 'application/x-cubescope-item'

export interface DragItem {
  kind: 'measure' | 'hierarchy' | 'level' | 'member'
  uniqueName: string
  hierarchyUniqueName: string
  caption: string
}

export interface Picked {
  uniqueName: string
  caption: string
}

export interface AxisEntry {
  hierarchy: string
  level: string | null
  members: Picked[]
  rank: { measure: string; topCount: number | null; descending: boolean } | null
}

export interface FilterEntry {
  hierarchy: string
  members: Picked[]
}

export interface BuilderState {
  measures: Picked[]
  columns: AxisEntry[]
  rows: AxisEntry[]
  filters: FilterEntry[]
  nonEmptyColumns: boolean
  nonEmptyRows: boolean
}

export type AxisZone = 'columns' | 'rows'

function empty(): BuilderState {
  return { measures: [], columns: [], rows: [], filters: [], nonEmptyColumns: true, nonEmptyRows: true }
}

// --- Persistence: survives a restart like the editor content (per-machine convenience).
// The state belongs to a server + catalog + cube: prod and dev carry the same names, so the
// server is part of the key. It is dropped as soon as the context differs.
const STORAGE_KEY = 'cubescope.builder'

// Bump when the shape of BuilderState changes: an older saved state is dropped rather than
// rendered with missing fields (which would break the panel until storage is cleared by hand).
const VERSION = 1

interface Saved {
  v: number
  context: string
  state: BuilderState
}

function load(): Saved | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    const saved = raw ? (JSON.parse(raw) as Saved) : null
    return saved?.v === VERSION ? saved : null
  } catch {
    return null
  }
}

const saved = load()
let context = saved?.context ?? ''
export const builder = reactive<BuilderState>({ ...empty(), ...saved?.state })

function currentContext(): string | null {
  return store.server && store.catalog && store.cube
    ? `${store.server.toLowerCase()}|${store.catalog}|${store.cube}`
    : null
}

// Context change (server, catalog, cube): the hierarchies of the old cube mean nothing here.
// Checked against the SAVED context, not on every contextRevision: reconnecting to the same
// cube at startup must not wipe the state.
watch(
  () => currentContext(),
  (ctx) => {
    if (!ctx || ctx === context) return
    if (context) clearBuilder()
    context = ctx
    persist()
  },
)

let saveTimer: ReturnType<typeof setTimeout> | undefined
function persist() {
  clearTimeout(saveTimer)
  saveTimer = setTimeout(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ v: VERSION, context, state: builder } satisfies Saved))
    } catch {
      /* storage unavailable: the builder keeps working, it just will not survive a restart */
    }
  }, 300)
}
watch(builder, persist, { deep: true })

export function clearBuilder() {
  Object.assign(builder, empty())
}

export function isEmpty(s: BuilderState = builder): boolean {
  return !s.measures.length && !s.columns.length && !s.rows.length && !s.filters.length
}

/** Something to select: filters alone would only give `SELECT FROM [cube]`. */
export function hasQuery(s: BuilderState = builder): boolean {
  return s.measures.length > 0 || s.columns.length > 0 || s.rows.length > 0
}

// --- Metadata helpers

export function findHierarchy(uniqueName: string): HierarchyMeta | undefined {
  return store.cubeMeta?.dimensions.flatMap((d) => d.hierarchies).find((h) => h.uniqueName === uniqueName)
}

/**
 * Level used when a whole hierarchy is dropped: the first one that is not (All). Read from the
 * level type, not from its position: a non-aggregatable hierarchy has no (All) level, and its
 * level 0 is a real one. For a parent-child hierarchy this is the root level (changeable in the
 * item options).
 */
export function defaultLevel(hierarchy: string): string | null {
  const levels = [...(findHierarchy(hierarchy)?.levels ?? [])].sort((a, b) => a.number - b.number)
  return (levels.find((l) => !l.isAll) ?? levels[0])?.uniqueName ?? null
}

export function hierarchyCaption(uniqueName: string): string {
  const dim = store.cubeMeta?.dimensions.find((d) => d.hierarchies.some((h) => h.uniqueName === uniqueName))
  const h = findHierarchy(uniqueName)
  if (!h) return uniqueName
  return dim && dim.name !== h.name ? `${dim.name} › ${h.name}` : h.name
}

export function levelCaption(uniqueName: string | null): string {
  if (!uniqueName) return ''
  for (const d of store.cubeMeta?.dimensions ?? [])
    for (const h of d.hierarchies) {
      const l = h.levels.find((x) => x.uniqueName === uniqueName)
      if (l) return l.name
    }
  return uniqueName
}

// --- Drop: where a dragged item lands. One entry per hierarchy and per zone.

/** Returns the filter entry to complete when its members still have to be chosen. */
export function dropItem(zone: 'measures' | AxisZone | 'filters', item: DragItem): FilterEntry | null {
  if (item.kind === 'measure') {
    if (!builder.measures.some((m) => m.uniqueName === item.uniqueName))
      builder.measures.push({ uniqueName: item.uniqueName, caption: item.caption })
    return null
  }
  if (zone === 'measures') return null
  const picked = item.kind === 'member' ? [{ uniqueName: item.uniqueName, caption: item.caption }] : []

  if (zone === 'filters') {
    let f = builder.filters.find((x) => x.hierarchy === item.hierarchyUniqueName)
    if (!f) {
      f = { hierarchy: item.hierarchyUniqueName, members: [] }
      builder.filters.push(f)
      f = builder.filters[builder.filters.length - 1]
    }
    addMembers(f.members, picked)
    return f.members.length ? null : f
  }

  const axis = builder[zone]
  // A hierarchy can only be on one axis: dropping it on the other one moves it there.
  const otherZone: AxisZone = zone === 'columns' ? 'rows' : 'columns'
  const otherIndex = builder[otherZone].findIndex((x) => x.hierarchy === item.hierarchyUniqueName)
  if (otherIndex >= 0) axis.push(...builder[otherZone].splice(otherIndex, 1))
  const existing = axis.find((x) => x.hierarchy === item.hierarchyUniqueName)
  if (existing) {
    if (item.kind === 'level') {
      existing.level = item.uniqueName
      existing.members = []
    } else addMembers(existing.members, picked)
    return null
  }
  axis.push({
    hierarchy: item.hierarchyUniqueName,
    level: item.kind === 'level' ? item.uniqueName : defaultLevel(item.hierarchyUniqueName),
    members: picked,
    rank: null,
  })
  return null
}

/** Members of one hierarchy added in bulk (pasted list) to Rows or Filters. */
export function addMembersTo(zone: AxisZone | 'filters', hierarchy: string, members: Picked[]) {
  if (zone === 'filters') {
    let f = builder.filters.find((x) => x.hierarchy === hierarchy)
    if (!f) {
      builder.filters.push({ hierarchy, members: [] })
      f = builder.filters[builder.filters.length - 1]
    }
    addMembers(f.members, members)
    return
  }
  const otherZone: AxisZone = zone === 'columns' ? 'rows' : 'columns'
  const otherIndex = builder[otherZone].findIndex((x) => x.hierarchy === hierarchy)
  if (otherIndex >= 0) builder[zone].push(...builder[otherZone].splice(otherIndex, 1))
  const existing = builder[zone].find((x) => x.hierarchy === hierarchy)
  if (existing) addMembers(existing.members, members)
  else builder[zone].push({ hierarchy, level: defaultLevel(hierarchy), members: [...members], rank: null })
}

function addMembers(target: Picked[], add: Picked[]) {
  // A Set, not target.some() per member: pasted lists reach thousands of members.
  const present = new Set(target.map((x) => x.uniqueName))
  for (const m of add) if (!present.has(m.uniqueName)) {
    present.add(m.uniqueName)
    target.push(m)
  }
}

// --- Request model: names only, no MDX. Incomplete filters are left out (shown as such).

export function toModel(cube: string, s: BuilderState = builder): BuilderModel {
  const axis = (entries: AxisEntry[]): BuilderAxisItem[] =>
    entries.map((e) => ({
      hierarchy: e.hierarchy,
      level: e.level,
      members: e.members.length ? e.members.map((m) => m.uniqueName) : null,
      rank: e.rank ? { ...e.rank } : null,
    }))
  return {
    cube,
    measures: s.measures.map((m) => m.uniqueName),
    columns: axis(s.columns),
    rows: axis(s.rows),
    filters: s.filters.filter((f) => f.members.length).map((f) => ({
      hierarchy: f.hierarchy,
      members: f.members.map((m) => m.uniqueName),
    })),
    nonEmptyColumns: s.nonEmptyColumns,
    nonEmptyRows: s.nonEmptyRows,
  }
}
