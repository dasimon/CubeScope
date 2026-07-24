// Client API CubeScope — miroir des DTO du serveur (System.Text.Json → camelCase).
import { t, currentLocale } from './i18n'

export interface GridColumn {
  field: string
  header: string
  isRowHeader: boolean
}

export interface QueryResult {
  columns: GridColumn[]
  rows: Record<string, unknown>[]
  cellCount: number
  axesCount: number
  durationMs: number
}

export interface HistoryEntry {
  id: number
  server: string
  catalog: string | null
  mdx: string
  success: boolean
  durationMs: number
  cellCount: number
  error: string | null
  executedUtc: string
}

export interface RecentConnection {
  server: string
  catalog: string | null
  lastUsedUtc: string
}

export interface MeasureMeta {
  name: string
  uniqueName: string
}
export interface MeasureFolder {
  folder: string
  measures: MeasureMeta[]
}
export interface LevelMeta {
  name: string
  uniqueName: string
  number: number
}
export interface HierarchyMeta {
  name: string
  uniqueName: string
  levels: LevelMeta[]
}
export interface DimensionMeta {
  name: string
  uniqueName: string
  hierarchies: HierarchyMeta[]
}
export interface CubeMeta {
  cubeName: string
  measureFolders: MeasureFolder[]
  dimensions: DimensionMeta[]
}

async function request<T>(method: string, url: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  })
  if (!res.ok) {
    const payload = await res.json().catch(() => null)
    throw new Error(payload?.error ?? t('errors.http', { status: res.status }))
  }
  return res.status === 204 ? (undefined as T) : ((await res.json().catch(() => undefined)) as T)
}

export const api = {
  connect: (server: string) =>
    request<{ server: string; catalogs: string[] }>('POST', '/api/connection', { server, lang: currentLocale() }),
  setCatalog: (catalog: string) => request<void>('PUT', '/api/connection/catalog', { catalog }),
  recent: () => request<RecentConnection[]>('GET', '/api/connection/recent'),
  query: (mdx: string, signal: AbortSignal) => request<QueryResult>('POST', '/api/query', { mdx }, signal),
  history: (limit = 100) => request<HistoryEntry[]>('GET', `/api/history?limit=${limit}`),
  cubes: () => request<string[]>('GET', '/api/metadata/cubes'),
  cubeMeta: (cube: string, refresh = false) =>
    request<CubeMeta>('GET', `/api/metadata/cube/${encodeURIComponent(cube)}?refresh=${refresh}`),
  members: (cube: string, hierarchy: string) =>
    request<MemberMeta[]>(
      'GET',
      `/api/metadata/members?cube=${encodeURIComponent(cube)}&hierarchy=${encodeURIComponent(hierarchy)}`,
    ),
  statsStatus: () => request<StatsStatus>('GET', '/api/stats/status'),
  profilerStatus: () => request<StatsStatus>('GET', '/api/profiler/status'),
  clearCache: () => request<{ databaseId: string; durationMs: number }>('POST', '/api/cache/clear'),
  script: (cube: string, refresh = false) =>
    request<CubeScript>('GET', `/api/script/${encodeURIComponent(cube)}?refresh=${refresh}`),
  dependencies: (cube: string, name: string) =>
    request<DependencyGraph>(
      'GET',
      `/api/script/${encodeURIComponent(cube)}/dependencies?name=${encodeURIComponent(name)}`,
    ),
  docUrl: (cube: string) => `/api/doc/${encodeURIComponent(cube)}`,
  aiStatus: () => request<{ configured: boolean }>('GET', '/api/ai/status'),
  ai: (action: AiAction, mdx: string, lang: string, signal: AbortSignal) =>
    request<{ text: string; durationMs: number }>('POST', `/api/ai/${action}`, { mdx, lang }, signal),
  projectOpen: (path: string) => request<ProjectScript>('POST', '/api/project/open', { path }),
  projectSave: (path: string, fullText: string) =>
    request<{ warnings: string[] }>('POST', '/api/project/save', { path, fullText }),
  projectDeploy: (path: string, server: string, catalog: string, force: boolean) =>
    request<DeployScriptResult>('POST', '/api/project/deploy', { path, server, catalog, force }),
  projectRecent: () => request<RecentProject[]>('GET', '/api/project/recent'),
}

export type AiAction = 'expliquer' | 'optimiser' | 'antipatterns' | 'formater'

export interface ScriptCommand {
  kind: 'CalculatedMember' | 'NamedSet' | 'Scope' | 'Autre'
  name: string
  expression: string
  startLine: number
  section: string | null
}
export interface CubeScript {
  cubeName: string
  fullText: string
  commands: ScriptCommand[]
}
export interface ProjectScript {
  path: string
  cubeName: string
  fullText: string
  commands: ScriptCommand[]
  canEdit: boolean
  readOnlyReason: string | null
}
export interface RecentProject {
  path: string
  lastUsedUtc: string
}
export interface DeployScriptResult {
  deployed: boolean
  differs: boolean
  serverText: string | null
  durationMs: number
}
export interface DependencyNode {
  name: string
  kind: string
  dependencies: DependencyNode[]
}
export interface DependencyGraph {
  root: DependencyNode
  usedBy: string[]
}

export interface MemberMeta {
  caption: string
  uniqueName: string
}

export interface CounterDelta {
  category: string
  counter: string
  delta: number
}

export interface StatsStatus {
  status: 'NotInitialized' | 'Ready' | 'Unavailable'
  detail: string | null
}

export interface SubcubeInfo {
  durationMs: number
  text: string
}
export interface QueryProfile {
  totalMs: number
  storageEngineMs: number
  formulaEngineMs: number
  subcubeCount: number
  cacheHits: number
  aggregationHits: number
  subcubes: SubcubeInfo[]
}
