<script setup lang="ts">
// Application shell: toolbar, dockview layout (editor / results / history),
// status bar, connection dialog.
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { DockviewVue, type DockviewApi, type DockviewReadyEvent, type VueComponent } from 'dockview-vue'
import Button from 'primevue/button'
import Select from 'primevue/select'
import InputNumber from 'primevue/inputnumber'
import Dialog from 'primevue/dialog'
import Toast from 'primevue/toast'
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
import { actions, store } from './store'

const { t, locale } = useI18n()
const toast = useToast()

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

function onReady(event: DockviewReadyEvent) {
  dvApi.value = event.api
  event.api.addPanel({ id: 'editor', component: 'editor', title: t('panel.mdx') })
  event.api.addPanel({
    id: 'explorer',
    component: 'explorer',
    title: t('panel.explorer'),
    position: { referencePanel: 'editor', direction: 'left' },
    initialWidth: 300,
  })
  event.api.addPanel({
    id: 'results',
    component: 'results',
    title: t('panel.results'),
    position: { referencePanel: 'editor', direction: 'below' },
  })
  event.api.addPanel({
    id: 'history',
    component: 'history',
    title: t('panel.history'),
    position: { referencePanel: 'results', direction: 'within' },
  })
  event.api.addPanel({
    id: 'stats',
    component: 'stats',
    title: t('panel.stats'),
    position: { referencePanel: 'results', direction: 'within' },
  })
  event.api.addPanel({
    id: 'profiler',
    component: 'profiler',
    title: t('panel.profiler'),
    position: { referencePanel: 'results', direction: 'within' },
  })
  event.api.addPanel({
    id: 'sessions',
    component: 'sessions',
    title: t('panel.sessions'),
    position: { referencePanel: 'results', direction: 'within' },
  })
  event.api.addPanel({
    id: 'ai',
    component: 'ai',
    title: t('panel.ai'),
    position: { referencePanel: 'editor', direction: 'right' },
    initialWidth: 420,
  })
  event.api.addPanel({
    id: 'script',
    component: 'script',
    title: t('panel.script'),
    position: { referencePanel: 'editor', direction: 'within' },
  })
  event.api.getPanel('editor')?.api.setActive()
  event.api.getPanel('results')?.api.setActive()
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
  await actions.setCatalog(catalog)
}

// ClearCache: explicit confirmation required (on a prod catalog, this clears the
// cache for every user of the cube). Success → toast + dialog closes.
const confirmClear = ref(false)
const clearing = ref(false)
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
    toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  } finally {
    clearing.value = false
  }
}

// Global F5 = execute (no browser reload in a local tool)
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'F5') {
    e.preventDefault()
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

    <Dialog v-model:visible="confirmClear" modal :header="t('clearCache.title')" :style="{ width: '26rem' }">
      <p>{{ t('clearCache.body', { catalog: store.catalog, server: store.server }) }}</p>
      <template #footer>
        <Button :label="t('common.cancel')" severity="secondary" text @click="confirmClear = false" />
        <Button
          :label="t('clearCache.confirm')"
          icon="pi pi-eraser"
          severity="danger"
          :loading="clearing"
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
