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
  description: string
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

export interface FileEntry {
  name: string
  path: string
  isDirectory: boolean
}
export interface DirectoryListing {
  path: string
  parent: string | null
  drives: string[]
  directories: FileEntry[]
  cubeFiles: FileEntry[]
}

export interface CalculationProp {
  reference: string
  formatString: string | null
  displayFolder: string | null
  description: string | null
}

export interface Snippet {
  id: number
  name: string
  mdx: string
  createdUtc: string
}

export interface RegressionCase {
  id: number
  name: string
  mdx: string
  createdUtc: string
}
export interface RegressionDiff {
  row: number
  column: string
  expected: string | null
  actual: string | null
}
export interface RegressionRunResult {
  id: number
  name: string
  match: boolean
  summary: string | null
  diffCount: number
  diffs: RegressionDiff[]
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
  drillthrough: (mdx: string, maxRows: number, signal: AbortSignal) =>
    request<QueryResult>('POST', '/api/drillthrough', { mdx, maxRows }, signal),
  history: (limit = 100) => request<HistoryEntry[]>('GET', `/api/history?limit=${limit}`),
  cubes: () => request<string[]>('GET', '/api/metadata/cubes'),
  cubeMeta: (cube: string, refresh = false) =>
    request<CubeMeta>('GET', `/api/metadata/cube/${encodeURIComponent(cube)}?refresh=${refresh}`),
  members: (cube: string, hierarchy: string) =>
    request<MemberMeta[]>(
      'GET',
      `/api/metadata/members?cube=${encodeURIComponent(cube)}&hierarchy=${encodeURIComponent(hierarchy)}`,
    ),
  memberCaption: (cube: string, name: string) =>
    request<{ caption: string | null }>(
      'GET',
      `/api/metadata/member?cube=${encodeURIComponent(cube)}&name=${encodeURIComponent(name)}`,
    ),
  memberCaptions: (cube: string, names: string[]) =>
    request<Record<string, string | null>>('POST', '/api/metadata/captions', { cube, names }),
  refreshCaptions: (cube: string) => request<void>('POST', '/api/metadata/captions/refresh', { cube }),
  statsStatus: () => request<StatsStatus>('GET', '/api/stats/status'),
  profilerStatus: () => request<StatsStatus>('GET', '/api/profiler/status'),
  profilerHistory: () => request<ProfileRun[]>('GET', '/api/profiler/history'),
  clearCache: () => request<{ databaseId: string; durationMs: number }>('POST', '/api/cache/clear'),
  script: (cube: string, refresh = false) =>
    request<CubeScript>('GET', `/api/script/${encodeURIComponent(cube)}?refresh=${refresh}`),
  dependencies: (cube: string, name: string) =>
    request<DependencyGraph>(
      'GET',
      `/api/script/${encodeURIComponent(cube)}/dependencies?name=${encodeURIComponent(name)}`,
    ),
  docUrl: (cube: string) => `/api/doc/${encodeURIComponent(cube)}`,
  aiStatus: () => request<{ configured: boolean; model: string }>('GET', '/api/ai/status'),
  ai: (action: AiAction, mdx: string, lang: string, signal: AbortSignal) =>
    request<{ text: string; durationMs: number }>('POST', `/api/ai/${action}`, { mdx, lang }, signal),
  aiOptimizeProfile: (mdx: string, profile: QueryProfile, lang: string, signal: AbortSignal) =>
    request<{ text: string; durationMs: number }>(
      'POST',
      '/api/ai/optimize-profile',
      { mdx, profile, lang },
      signal,
    ),
  generateMdx: (cube: string, question: string, lang: string, signal: AbortSignal) =>
    request<{ text: string; durationMs: number }>(
      'POST',
      '/api/ai/generate-mdx',
      { cube, question, lang },
      signal,
    ),
  projectOpen: (path: string) => request<ProjectScript>('POST', '/api/project/open', { path }),
  projectSave: (path: string, fullText: string) =>
    request<{ warnings: string[] }>('POST', '/api/project/save', { path, fullText }),
  projectDeploy: (path: string, server: string, catalog: string, force: boolean) =>
    request<DeployScriptResult>('POST', '/api/project/deploy', { path, server, catalog, force }),
  projectRecent: () => request<RecentProject[]>('GET', '/api/project/recent'),
  deployLog: () => request<DeployLogEntry[]>('GET', '/api/project/deploylog'),
  calcProps: (path: string) =>
    request<CalculationProp[]>('GET', `/api/project/calcprops?path=${encodeURIComponent(path)}`),
  saveCalcProp: (
    path: string,
    reference: string,
    formatString: string | null,
    displayFolder: string | null,
    description: string | null,
  ) =>
    request<void>('POST', '/api/project/calcprops', {
      path,
      reference,
      formatString,
      displayFolder,
      description,
    }),
  fsList: (path?: string) =>
    request<DirectoryListing>('GET', `/api/fs/list${path ? `?path=${encodeURIComponent(path)}` : ''}`),
  snippets: () => request<Snippet[]>('GET', '/api/snippets'),
  addSnippet: (name: string, mdx: string) => request<{ id: number }>('POST', '/api/snippets', { name, mdx }),
  deleteSnippet: (id: number) => request<void>('DELETE', `/api/snippets/${id}`),
  regressionSave: (name: string, mdx: string, expected: QueryResult) =>
    request<{ id: number }>('POST', '/api/regression', { name, mdx, expected }),
  regressionList: () => request<RegressionCase[]>('GET', '/api/regression'),
  regressionRun: (signal?: AbortSignal) =>
    request<RegressionRunResult[]>('POST', '/api/regression/run', undefined, signal),
  regressionDelete: (id: number) => request<void>('DELETE', `/api/regression/${id}`),
  renameMember: (script: string, oldName: string, newName: string) =>
    request<RenameResult>('POST', '/api/script/rename', { script, oldName, newName }),
  explainCalc: (cube: string, name: string, lang: string, signal?: AbortSignal) =>
    request<{ text: string }>(
      'GET',
      `/api/script/${encodeURIComponent(cube)}/explain?name=${encodeURIComponent(name)}&lang=${lang}`,
      undefined,
      signal,
    ),
  impact: (oldScript: string, newScript: string) =>
    request<ImpactReport>('POST', '/api/script/impact', { oldScript, newScript }),
}

export type AiAction =
  | 'expliquer'
  | 'optimiser'
  | 'antipatterns'
  | 'formater'
  | 'optimize-profile'
  | 'generate-mdx'

export interface RenameResult {
  newScript: string
  occurrences: number
}
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
export interface DeployLogEntry {
  id: number
  server: string
  catalog: string | null
  cubeName: string
  projectPath: string
  scriptChars: number
  forced: boolean
  deployedUtc: string
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

export interface MemberChange {
  name: string
  kind: string
  change: 'Added' | 'Removed' | 'Changed'
  impactedDownstream: string[]
}
export interface ImpactReport {
  changes: MemberChange[]
}

export interface ProfileRun {
  id: number
  server: string
  catalog: string | null
  mdx: string
  totalMs: number
  storageEngineMs: number
  formulaEngineMs: number
  subcubeCount: number
  cacheHits: number
  aggregationHits: number
  executedUtc: string
}
