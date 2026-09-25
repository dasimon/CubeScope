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
import ConnectDialog from './components/ConnectDialog.vue'
import SnippetsMenu from './components/SnippetsMenu.vue'
import MemberScaffoldDialog from './components/MemberScaffoldDialog.vue'
import RegressionDialog from './components/RegressionDialog.vue'
import { startStatsHub } from './stats'
import { actions, isDevServer, store } from './store'

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
}

// (panel id, title translation key) — to re-title panels when the language changes.
const PANELS = [
  ['editor', 'panel.mdx'],
  ['explorer', 'panel.explorer'],
  ['results', 'panel.results'],
  ['history', 'panel.history'],
  ['stats', 'panel.stats'],
  ['profiler', 'panel.profiler'],
  ['ai', 'panel.ai'],
  ['script', 'panel.script'],
  ['sessions', 'panel.sessions'],
] as const

const dvApi = shallowRef<DockviewApi>()

type PanelId = (typeof PANELS)[number][0]
type Anchor = [referencePanel: PanelId, direction: 'left' | 'right' | 'below' | 'within']

// Where each panel goes: the first anchor panel still present wins. Used for the initial
// layout AND to reopen a closed panel (its usual neighbours may be closed too).
const BOTTOM_GROUP: PanelId[] = ['results', 'history', 'stats', 'profiler', 'sessions']
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
  results: { anchors: bottomAnchors('results') },
  history: { anchors: bottomAnchors('history') },
  stats: { anchors: bottomAnchors('stats') },
  profiler: { anchors: bottomAnchors('profiler') },
  sessions: { anchors: bottomAnchors('sessions') },
  ai: { anchors: [['editor', 'right'], ['script', 'right']], initialWidth: 420 },
  script: { anchors: [['editor', 'within']] },
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

// "Go to definition": the layout belongs to this shell, navigation within the script
// belongs to ScriptPanel — each one reacts to the same signal on its own side.
watch(
  () => store.gotoDefinitionRevision,
  () => dvApi.value?.getPanel('script')?.api.setActive(),
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
    void actions.run()
  }
}
onMounted(() => {
  window.addEventListener('keydown', onKeydown)
  void actions.loadHistory()
  startStatsHub()
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <div class="app-shell">
    <header class="toolbar">
      <span class="app-title">CubeScope</span>
      <Button
        :label="store.connected ? store.server : t('toolbar.connecting')"
        :icon="store.connected ? 'pi pi-server' : 'pi pi-link'"
        size="small"
        :severity="store.connected ? 'secondary' : 'primary'"
        @click="store.showConnect = true"
      />
      <Select
        v-if="store.connected"
        :model-value="store.catalog"
        :options="store.catalogs"
        size="small"
        :placeholder="t('toolbar.catalog')"
        :disabled="store.running"
        @update:model-value="onCatalogChange"
      />
      <Button
        v-if="store.connected && store.catalog"
        icon="pi pi-eraser"
        size="small"
        severity="secondary"
        :title="t('toolbar.clearCacheTitle', { catalog: store.catalog })"
        @click="confirmClear = true"
      />
      <SnippetsMenu />
      <MemberScaffoldDialog />
      <RegressionDialog />
      <Button
        icon="pi pi-th-large"
        size="small"
        severity="secondary"
        :label="t('panels.menu')"
        :title="t('panels.menuTitle')"
        @click="(e: Event) => panelsMenu?.toggle(e)"
      />
      <Menu ref="panelsMenu" :model="panelMenuItems" popup />
      <span class="toolbar-spacer" />
      <Select
        :model-value="locale"
        :options="LANGS"
        option-label="label"
        option-value="value"
        size="small"
        class="lang-select"
        @update:model-value="(l: Locale) => setLocale(l)"
      />
      <Button
        v-if="!store.running"
        :label="t('toolbar.execute')"
        icon="pi pi-play"
        size="small"
        :disabled="!store.connected || !store.catalog"
        :title="t('toolbar.executeTitle')"
        @click="actions.run()"
      />
      <Button v-else :label="t('common.cancel')" icon="pi pi-stop" size="small" severity="danger" @click="actions.cancel()" />
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
        :label="t('results.drillthrough')"
        icon="pi pi-arrow-down-right"
        size="small"
        severity="secondary"
        :disabled="!store.connected || !store.catalog || store.running"
        :title="t('results.drillthroughHint')"
        @click="actions.runDrillthrough(drillthroughMaxRows ?? 1000)"
      />
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
      <span v-if="store.connected">{{ store.server }} · {{ store.catalog ?? '—' }}</span>
      <span v-else>{{ t('status.notConnected') }}</span>
      <span class="toolbar-spacer" />
      <template v-if="store.result">
        <span>{{ t('status.cells', { cells: store.result.cellCount, axes: store.result.axesCount }) }}</span>
        <span class="status-duration">{{ store.result.durationMs }} ms</span>
      </template>
    </footer>

    <ConnectDialog />
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
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 0.75rem;
  border-bottom: 1px solid var(--p-surface-700);
}
.app-title {
  font-weight: 700;
  margin-right: 0.75rem;
}
.toolbar-spacer {
  flex: 1;
}
.lang-select {
  width: 5rem;
}
.max-rows-input {
  width: 5rem;
}
.dock-host {
  flex: 1;
  min-height: 0;
  position: relative;
}
.statusbar {
  display: flex;
  align-items: center;
  gap: 1rem;
  padding: 0.25rem 0.75rem;
  font-size: 0.85rem;
  border-top: 1px solid var(--p-surface-700);
  color: var(--p-text-muted-color);
}
.status-duration {
  font-weight: 600;
  color: var(--p-primary-color);
}
.clear-result {
  font-size: 0.85rem;
  color: var(--p-text-muted-color);
}
</style>
