<script setup lang="ts">
// Application shell: toolbar, dockview layout (editor / results / history),
// status bar, connection dialog.
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { DockviewVue, type DockviewApi, type DockviewReadyEvent, type VueComponent } from 'dockview-vue'
import Button from 'primevue/button'
import Select from 'primevue/select'
import InputNumber from 'primevue/inputnumber'
import Dialog from 'primevue/dialog'
import ConfirmDialog from 'primevue/confirmdialog'
import Menu from 'primevue/menu'
import Message from 'primevue/message'
import Toast from 'primevue/toast'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import { api } from './api'
import { setLocale, type Locale } from './i18n'
import EditorPanel from './components/EditorPanel.vue'
import ResultsPanel from './components/ResultsPanel.vue'
import HistoryPanel from './components/HistoryPanel.vue'
import ExplorerPanel from './components/ExplorerPanel.vue'
import StatsPanel from './components/StatsPanel.vue'
import AiPanel from './components/AiPanel.vue'
import ScriptPanel from './components/ScriptPanel.vue'
import ProfilerPanel from './components/ProfilerPanel.vue'
import SessionsPanel from './components/SessionsPanel.vue'
import ProcessingPanel from './components/ProcessingPanel.vue'
import BuilderPanel from './components/BuilderPanel.vue'
import XmlaConsolePanel from './components/XmlaConsolePanel.vue'
import AboutDialog from './components/AboutDialog.vue'
import ConnectDialog from './components/ConnectDialog.vue'
import ScopeMark from './components/ScopeMark.vue'
import SnippetsMenu from './components/SnippetsMenu.vue'
import MemberScaffoldDialog from './components/MemberScaffoldDialog.vue'
import RegressionDialog from './components/RegressionDialog.vue'
import UpdateDialog from './components/UpdateDialog.vue'
import { startUpdatePolling, update, updateActions } from './update'
import { startStatsHub } from './stats'
import { actions, isDevServer, store } from './store'
import { age, nowUtc } from './processing'

const { t, locale } = useI18n()
const toast = useToast()
const confirm = useConfirm()

function errorToast(e: unknown) {
  toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
}

// Query / AI errors → toast (in addition to the inline display in the panel).
// Deliberate cancellations by the user are ignored.
watch(
  () => store.queryError,
  (e) => {
    if (e && e !== t('errors.queryCanceled'))
      toast.add({ severity: 'error', summary: t('toast.error'), detail: e, life: 6000 })
  },
)
watch(
  () => store.aiError,
  (e) => {
    if (e && e !== t('errors.aiCanceled'))
      toast.add({ severity: 'error', summary: t('toast.error'), detail: e, life: 6000 })
  },
)

const LANGS = [
  { label: 'FR', value: 'fr' as Locale },
  { label: 'EN', value: 'en' as Locale },
]

const drillthroughMaxRows = ref(1000)

// The target capsule: where the next query goes, and whether that server counts as
// production (same rule as ProductionActionGuard — a server not declared as dev is prod).
const markState = computed<'cube' | 'prod' | 'off'>(() =>
  !store.connected ? 'off' : isDevServer(store.server) ? 'cube' : 'prod',
)
const envTitle = computed(() =>
  markState.value === 'off'
    ? t('toolbar.envOffTitle')
    : markState.value === 'cube'
      ? t('toolbar.envDevTitle')
      : t('toolbar.envProdTitle'),
)

// Cast required: typed SFCs do not satisfy the generic VueComponent index (TS variance)
const panelComponents: Record<string, VueComponent> = {
  editor: EditorPanel as VueComponent,
  results: ResultsPanel as VueComponent,
  history: HistoryPanel as VueComponent,
  explorer: ExplorerPanel as VueComponent,
  stats: StatsPanel as VueComponent,
  ai: AiPanel as VueComponent,
  script: ScriptPanel as VueComponent,
  profiler: ProfilerPanel as VueComponent,
  sessions: SessionsPanel as VueComponent,
  processing: ProcessingPanel as VueComponent,
  builder: BuilderPanel as VueComponent,
  xmla: XmlaConsolePanel as VueComponent,
}

// (panel id, title translation key) — to re-title panels when the language changes.
const PANELS = [
  ['editor', 'panel.mdx'],
  ['explorer', 'panel.explorer'],
  ['builder', 'panel.builder'],
  ['results', 'panel.results'],
  ['history', 'panel.history'],
  ['stats', 'panel.stats'],
  ['profiler', 'panel.profiler'],
  ['ai', 'panel.ai'],
  ['script', 'panel.script'],
  ['xmla', 'panel.xmla'],
  ['sessions', 'panel.sessions'],
  ['processing', 'panel.processing'],
] as const

const dvApi = shallowRef<DockviewApi>()

type PanelId = (typeof PANELS)[number][0]
type Anchor = [referencePanel: PanelId, direction: 'left' | 'right' | 'below' | 'within']

// Where each panel goes: the first anchor panel still present wins. Used for the initial
// layout AND to reopen a closed panel (its usual neighbours may be closed too).
const BOTTOM_GROUP: PanelId[] = ['results', 'history', 'stats', 'profiler', 'sessions', 'processing']
function bottomAnchors(id: PanelId): Anchor[] {
  return [
    ...BOTTOM_GROUP.filter((p) => p !== id).map((p): Anchor => [p, 'within']),
    ['editor', 'below'],
    ['script', 'below'],
  ]
}
const PLACEMENT: Record<PanelId, { anchors: Anchor[]; initialWidth?: number }> = {
  editor: { anchors: [['script', 'within']] },
  explorer: { anchors: [['editor', 'left'], ['script', 'left']], initialWidth: 300 },
  // Below the explorer, never in its group: items are dragged from one to the other.
  builder: { anchors: [['explorer', 'below'], ['editor', 'left'], ['script', 'left']], initialWidth: 300 },
  results: { anchors: bottomAnchors('results') },
  history: { anchors: bottomAnchors('history') },
  stats: { anchors: bottomAnchors('stats') },
  profiler: { anchors: bottomAnchors('profiler') },
  sessions: { anchors: bottomAnchors('sessions') },
  processing: { anchors: bottomAnchors('processing') },
  ai: { anchors: [['editor', 'right'], ['script', 'right']], initialWidth: 420 },
  script: { anchors: [['editor', 'within']] },
  xmla: { anchors: [['editor', 'within'], ['script', 'within']] },
}

function addPanel(api: DockviewApi, id: PanelId, title: string) {
  const anchor = PLACEMENT[id].anchors.find(([ref]) => api.getPanel(ref))
  api.addPanel({
    id,
    component: id,
    title,
    ...(anchor ? { position: { referencePanel: anchor[0], direction: anchor[1] } } : {}),
    initialWidth: PLACEMENT[id].initialWidth,
  })
}

// Panels currently in the layout (dockview closes a panel for good: the Panels menu reopens it).
const openPanels = ref(new Set<string>())

function isScriptDirty(): boolean {
  return (window as Window & { __cubescopeDirty?: boolean }).__cubescopeDirty === true
}

/** Closing the Script tab would destroy unsaved project edits: ask first. Wraps the panel
 *  api's close(), which every close path goes through (tab cross, Delete key on the tab). */
const guardedPanels = new WeakSet<object>() // a panel moved by drag may be "added" again
function guardScriptClose(api: DockviewApi) {
  const panel = api.getPanel('script')
  if (!panel || guardedPanels.has(panel.api)) return
  guardedPanels.add(panel.api)
  const close = panel.api.close.bind(panel.api)
  panel.api.close = () => {
    if (!isScriptDirty()) return close()
    confirm.require({
      header: t('panels.closeScriptHeader'),
      message: t('project.discardConfirm'),
      icon: 'pi pi-exclamation-triangle',
      rejectLabel: t('common.cancel'),
      rejectProps: { severity: 'secondary', text: true },
      acceptLabel: t('panels.closeAnyway'),
      acceptProps: { severity: 'danger' },
      accept: close,
    })
  }
}

function onReady(event: DockviewReadyEvent) {
  dvApi.value = event.api
  event.api.onDidAddPanel((p) => {
    openPanels.value = new Set(openPanels.value).add(p.id)
    if (p.id === 'script') guardScriptClose(event.api)
  })
  event.api.onDidRemovePanel((p) => {
    const s = new Set(openPanels.value)
    s.delete(p.id)
    openPanels.value = s
  })
  for (const [id, key] of PANELS) addPanel(event.api, id, t(key))
  event.api.getPanel('editor')?.api.setActive()
  event.api.getPanel('results')?.api.setActive()
}

const panelsMenu = ref<InstanceType<typeof Menu>>()
const panelMenuItems = computed(() =>
  PANELS.map(([id, key]) => ({
    label: t(key),
    icon: openPanels.value.has(id) ? 'pi pi-check' : 'pi pi-plus',
    command: () => showPanel(id),
  })),
)

function showPanel(id: PanelId) {
  const api = dvApi.value
  if (!api) return
  if (!api.getPanel(id)) addPanel(api, id, t(PANELS.find(([p]) => p === id)![1]))
  api.getPanel(id)?.api.setActive()
}

// Re-title the dockview tabs when the language changes (titles are not reactive)
watch(locale, () => {
  for (const [id, key] of PANELS) dvApi.value?.getPanel(id)?.api.setTitle(t(key))
})

// Processing summary in the status bar: problems first (red / orange), then the age of the
// newest partition — "did last night's processing run?". Click = open the State panel.
const processingParts = computed(() => {
  const s = store.processing
  if (!s) return []
  const m = s.summary
  const parts: { text: string; level: 'danger' | 'warn' | '' }[] = []
  if (m.unprocessedCubes) parts.push({ text: t('status.unprocessedCubes', { n: m.unprocessedCubes }), level: 'danger' })
  if (m.unprocessedPartitions)
    parts.push({ text: t('status.unprocessedPartitions', { n: m.unprocessedPartitions }), level: 'danger' })
  if (m.unprocessedDimensions)
    parts.push({ text: t('status.unprocessedDimensions', { n: m.unprocessedDimensions }), level: 'danger' })
  if (m.staleObjects) parts.push({ text: t('status.staleObjects', { n: m.staleObjects }), level: 'warn' })
  // The verdict (colours) stays the server's, as of the read; only the age follows the clock.
  if (m.newestProcessedUtc)
    parts.push({ text: t('status.lastProcessed', { age: age(m.newestProcessedUtc, nowUtc.value) }), level: '' })
  return parts
})
const processingLevel = computed(() =>
  processingParts.value.some((p) => p.level === 'danger')
    ? 'danger'
    : processingParts.value.some((p) => p.level === 'warn')
      ? 'warn'
      : '',
)
const processingTitle = computed(() =>
  store.processing
    ? `${t('status.openProcessing')} — ${t('processing.readAt', { time: new Date(store.processing.readAtUtc).toLocaleString() })}`
    : '',
)

// "Go to definition": the layout belongs to this shell, navigation within the script
// belongs to ScriptPanel — each one reacts to the same signal on its own side.
watch(
  () => store.gotoDefinitionRevision,
  () => dvApi.value?.getPanel('script')?.api.setActive(),
)

// A panel asked for another one (processing dialog → Sessions to cancel a long process).
watch(
  () => store.panelRequestRevision,
  () => {
    const id = PANELS.find(([p]) => p === store.panelRequest)?.[0]
    if (id) showPanel(id)
  },
)

async function onCatalogChange(catalog: string) {
  try {
    await actions.setCatalog(catalog)
  } catch (e) {
    errorToast(e)
  }
}

// ClearCache: explicit confirmation required. The server only accepts it on a declared
// development server (same list as the script deployment): say so before the click.
const confirmClear = ref(false)
const clearing = ref(false)
const clearAllowed = computed(() => isDevServer(store.server))
async function clearCache() {
  clearing.value = true
  try {
    const r = await api.clearCache()
    confirmClear.value = false
    toast.add({
      severity: 'success',
      summary: t('toast.cacheCleared'),
      detail: t('clearCache.result', { id: r.databaseId, ms: r.durationMs }),
      life: 4000,
    })
  } catch (e) {
    errorToast(e)
  } finally {
    clearing.value = false
  }
}

// Global F5 = execute (no browser reload in a local tool) — but not behind a modal dialog.
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'F5') {
    e.preventDefault()
    if (document.querySelector('.p-dialog-mask')) return
    // Inside the XMLA console, F5 belongs to the console (its editor action); in its fields
    // (server name, snippet name) it must not run the MDX query.
    if (e.target instanceof Element && e.target.closest('.xmla')) return
    void actions.run()
  }
}
onMounted(() => {
  window.addEventListener('keydown', onKeydown)
  void actions.loadHistory()
  startStatsHub()
  startUpdatePolling()
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <div class="app-shell">
    <header class="toolbar">
      <span class="wordmark"><ScopeMark :state="markState" /><b>CubeScope</b></span>
      <span class="toolbar-sep" />
      <span class="target" :class="markState">
        <button type="button" class="target-env" :title="envTitle" @click="store.showConnect = true">
          {{ markState === 'cube' ? t('toolbar.envDev') : t('toolbar.envProd') }}
        </button>
        <button type="button" class="target-seg" :title="t('connect.title')" @click="store.showConnect = true">
          <i :class="store.connected ? 'pi pi-server' : 'pi pi-link'" />
          {{ store.connected ? store.server : t('toolbar.connecting') }}
        </button>
        <Select
          v-if="store.connected"
          :model-value="store.catalog"
          :options="store.catalogs"
          size="small"
          class="target-select"
          :placeholder="t('toolbar.catalog')"
          :disabled="store.running"
          @update:model-value="onCatalogChange"
        />
        <span v-if="store.connected && store.cube" class="target-seg target-cube" :title="t('toolbar.cubeTitle')">{{ store.cube }}</span>
      </span>
      <Button
        v-if="store.connected && store.catalog"
        icon="pi pi-eraser"
        size="small"
        severity="secondary"
        text
        :title="t('toolbar.clearCacheTitle', { catalog: store.catalog })"
        :aria-label="t('toolbar.clearCacheTitle', { catalog: store.catalog })"
        @click="confirmClear = true"
      />
      <span class="toolbar-spacer" />
      <span class="toolbar-tools">
        <SnippetsMenu />
        <MemberScaffoldDialog />
        <RegressionDialog />
        <Button
          icon="pi pi-th-large"
          size="small"
          severity="secondary"
          text
          :title="t('panels.menuTitle')"
          :aria-label="t('panels.menu')"
          @click="(e: Event) => panelsMenu?.toggle(e)"
        />
        <Menu ref="panelsMenu" :model="panelMenuItems" popup />
        <AboutDialog />
      </span>
      <Select
        :model-value="locale"
        :options="LANGS"
        option-label="label"
        option-value="value"
        size="small"
        class="lang-select"
        @update:model-value="(l: Locale) => setLocale(l)"
      />
      <span class="toolbar-sep" />
      <span class="drill-cluster" :title="t('results.drillthroughHint')">
        <span class="drill-label">{{ t('results.drillthrough') }}</span>
        <InputNumber
          v-model="drillthroughMaxRows"
          :min="1"
          :max="100000"
          :use-grouping="false"
          size="small"
          class="max-rows-input"
          :title="t('results.maxRows')"
        />
        <Button
          icon="pi pi-arrow-down-right"
          size="small"
          severity="secondary"
          text
          :disabled="!store.connected || !store.catalog || store.running"
          :aria-label="t('results.drillthrough')"
          @click="actions.runDrillthrough(drillthroughMaxRows ?? 1000)"
        />
      </span>
      <Button
        v-if="!store.running"
        size="small"
        class="run-btn"
        :disabled="!store.connected || !store.catalog"
        :title="t('toolbar.executeTitle')"
        @click="actions.run()"
      >
        <i class="pi pi-play" />
        <span>{{ t('toolbar.execute') }}</span>
        <kbd>F5</kbd>
      </Button>
      <Button v-else :label="t('common.cancel')" icon="pi pi-stop" size="small" severity="danger" class="run-btn" @click="actions.cancel()" />
    </header>

    <!-- Wrapper required: DockviewVue is multi-root (portals), scoped CSS does not
         reach it — dimensions are passed as inline style. -->
    <div class="dock-host">
      <DockviewVue
        class="dockview-theme-dark"
        style="width: 100%; height: 100%"
        :components="panelComponents"
        @ready="onReady"
      />
    </div>

    <footer class="statusbar">
      <span v-if="store.connected" class="mono">{{ store.server }} · {{ store.catalog ?? '—' }}</span>
      <span v-else>{{ t('status.notConnected') }}</span>
      <button
        v-if="processingParts.length"
        type="button"
        class="status-processing"
        :class="processingLevel && `status-${processingLevel}`"
        :title="processingTitle"
        @click="showPanel('processing')"
      >
        <span v-for="(p, i) in processingParts" :key="i" :class="p.level && `status-${p.level}`">{{ p.text }}</span>
      </button>
      <span class="toolbar-spacer" />
      <button
        v-if="update.view?.status.available && update.view.lastOutcome?.kind !== 'restarting'"
        type="button"
        class="status-update"
        :title="t('update.pillTitle')"
        @click="updateActions.open()"
      >
        <i class="pi pi-arrow-circle-up" />
        {{ t('update.pill', { version: update.view.status.available.version }) }}
      </button>
      <template v-if="store.result">
        <span>{{ t('status.cells', { cells: store.result.cellCount, axes: store.result.axesCount }) }}</span>
        <span class="status-duration mono">{{ store.result.durationMs }} ms</span>
      </template>
    </footer>

    <ConnectDialog />
    <UpdateDialog />
    <Toast position="bottom-right" />
    <ConfirmDialog :style="{ width: '30rem' }" />

    <Dialog v-model:visible="confirmClear" modal :header="t('clearCache.title')" :style="{ width: '26rem' }">
      <p>{{ t('clearCache.body', { catalog: store.catalog, server: store.server }) }}</p>
      <Message v-if="!clearAllowed" severity="warn">{{ t('clearCache.notDev', { server: store.server }) }}</Message>
      <template #footer>
        <Button :label="t('common.cancel')" severity="secondary" text @click="confirmClear = false" />
        <Button
          :label="t('clearCache.confirm')"
          icon="pi pi-eraser"
          severity="danger"
          :loading="clearing"
          :disabled="!clearAllowed"
          @click="clearCache"
        />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.app-shell {
  height: 100vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.toolbar {
  height: 44px;
  flex: none;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 12px;
  background: var(--scope-bg-1);
  border-bottom: 1px solid var(--scope-line);
}
.wordmark {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 14.5px;
  letter-spacing: -0.01em;
  font-variation-settings:
    'CASL' 0.35,
    'wght' 560;
}
.wordmark b {
  font-weight: inherit;
}
.toolbar-sep {
  width: 1px;
  height: 18px;
  background: var(--scope-line-2);
  margin: 0 2px;
}
.toolbar-spacer {
  flex: 1;
}
.toolbar-tools {
  display: inline-flex;
  gap: 2px;
}

/* Target capsule: environment tag › server › catalog › cube. Amber all over when the
   target counts as production. */
.target {
  display: inline-flex;
  align-items: stretch;
  height: 28px;
  border: 1px solid var(--scope-line-2);
  border-radius: 6px;
  background: var(--scope-bg);
  overflow: hidden;
  font-size: 12.5px;
}
.target.prod {
  border-color: rgba(240, 179, 62, 0.55);
  background: var(--scope-prod-dim);
}
.target-env,
.target-seg {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 0 10px;
  border: 0;
  background: none;
  font: inherit;
  color: var(--scope-text);
  white-space: nowrap;
}
.target-env {
  font-size: 11px;
  font-weight: 600;
  color: var(--scope-face-top);
  padding: 0 8px;
  cursor: pointer;
}
.target.prod .target-env {
  color: var(--scope-prod);
}
.target.off .target-env {
  display: none;
}
.target-seg {
  border-left: 1px solid var(--scope-line);
}
.target.off .target-seg {
  border-left: 0;
}
button.target-seg {
  cursor: pointer;
}
button.target-seg:hover,
.target-env:hover {
  background: var(--scope-bg-2);
}
.target-seg .pi {
  font-size: 12px;
  color: var(--scope-text-3);
}
.target-cube {
  font-weight: 560;
}
.target :deep(.target-select.p-select) {
  border: 0;
  border-left: 1px solid var(--scope-line);
  border-radius: 0;
  background: transparent;
  box-shadow: none;
}
.target :deep(.target-select .p-select-label) {
  padding: 0 4px 0 10px;
  font-size: 12.5px;
  display: flex;
  align-items: center;
}
.target :deep(.target-select .p-select-dropdown) {
  width: 1.6rem;
  color: var(--scope-text-3);
}

.lang-select {
  width: 4.9rem;
}
.lang-select :deep(.p-select-label) {
  font-size: 12px;
}

/* Drillthrough: the row cap and its button, one object. */
.drill-cluster {
  display: inline-flex;
  align-items: stretch;
  height: 28px;
  border: 1px solid var(--scope-line-2);
  border-radius: 6px;
  background: var(--scope-bg);
  overflow: hidden;
}
.drill-label {
  display: inline-flex;
  align-items: center;
  padding: 0 8px 0 10px;
  font-size: 11px;
  color: var(--scope-text-3);
  white-space: nowrap;
}
.drill-cluster :deep(.p-inputnumber-input) {
  width: 4.4rem;
  height: 100%;
  border: 0;
  border-left: 1px solid var(--scope-line);
  border-right: 1px solid var(--scope-line);
  border-radius: 0;
  background: transparent;
  box-shadow: none;
  text-align: right;
  font-variation-settings: 'MONO' 1;
}
.drill-cluster :deep(.p-button) {
  border: 0;
  border-radius: 0;
  height: 100%;
}

.run-btn {
  height: 28px;
  gap: 6px;
  font-weight: 600;
}
.run-btn kbd {
  background: rgba(0, 0, 0, 0.18);
  border-color: transparent;
  color: inherit;
}

.dock-host {
  flex: 1;
  min-height: 0;
  position: relative;
}

.statusbar {
  height: 26px;
  flex: none;
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 0 12px;
  font-size: 11.5px;
  background: var(--scope-bg-1);
  border-top: 1px solid var(--scope-line);
  color: var(--scope-text-2);
}
.status-duration {
  font-weight: 600;
  color: var(--scope-face-top);
}
.status-processing {
  display: inline-flex;
  align-items: center;
  gap: 0;
  padding: 1px 9px;
  border: 0;
  border-radius: 10px;
  background: var(--scope-bg-3);
  font: inherit;
  color: inherit;
  cursor: pointer;
}
.status-processing > span + span::before {
  content: '·';
  margin: 0 5px;
}
.status-processing.status-warn {
  background: var(--scope-prod-dim);
  color: var(--scope-prod);
}
.status-processing.status-danger {
  background: var(--scope-danger-dim);
  color: var(--scope-danger);
}
.status-processing.status-danger .status-warn {
  color: var(--scope-prod);
}
.status-processing:hover,
.status-processing:focus-visible {
  text-decoration: underline dotted;
}
/* A new release: the only emerald element of the status bar besides the duration. */
.status-update {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 1px 9px;
  border: 1px solid var(--scope-face-right);
  border-radius: 10px;
  background: var(--scope-face-glow);
  color: var(--scope-face-top);
  font: inherit;
  cursor: pointer;
}
.status-update .pi {
  font-size: 11px;
}
.status-update:hover,
.status-update:focus-visible {
  background: rgba(52, 211, 153, 0.24);
}
.clear-result {
  font-size: 0.85rem;
  color: var(--p-text-muted-color);
}
</style>
