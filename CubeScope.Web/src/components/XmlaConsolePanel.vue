<script setup lang="ts">
// XMLA console (replaces the SSMS "XMLA query" window): backup, roles, partitions, any command.
// Everything is checked server-side before being sent (XmlaConsoleService): invalid XML and
// Discover refused; on a server not declared dev the server name must be typed again, and
// Delete/Restore/Detach/Synchronize/ClearCache/ProcessClear/dimension Full|Data are refused.
// A DMV <Statement> comes back in the grid; MDX queries belong to the MDX editor.
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { useToast } from 'primevue/usetoast'
import { monaco } from '../monaco-mdx'
import { api, ApiError, type Snippet, type XmlaExecutionResult, type XmlaLogEntry } from '../api'
import { isDevServer, store } from '../store'
import { dateTime } from '../processing'
import ResultsGrid from './ResultsGrid.vue'

const { t } = useI18n()
const toast = useToast()

const host = ref<HTMLElement | null>(null)
let editor: monaco.editor.IStandaloneCodeEditor | null = null

// The server's verdict wins: if it answers "production" while the local dev list said otherwise,
// the confirmation field appears instead of a refusal the user cannot get past.
const serverSaidProduction = ref(false)
const production = computed(() => store.connected && (serverSaidProduction.value || !isDevServer(store.server)))
const confirmText = ref('')
const running = ref(false)
const elapsed = ref(0)
const result = ref<XmlaExecutionResult | null>(null)
const error = ref('')
let timer: number | undefined

onMounted(() => {
  editor = monaco.editor.create(host.value!, {
    value: store.xmlaScript,
    language: 'xml',
    theme: 'cubescope-dark',
    automaticLayout: true,
    minimap: { enabled: false },
    fontSize: 13,
    scrollBeyondLastLine: false,
    fixedOverflowWidgets: true,
  })
  editor.onDidChangeModelContent(() => (store.xmlaScript = editor!.getValue()))
  // addAction, not addCommand: addCommand is global to every Monaco editor (see EditorPanel.vue).
  editor.addAction({
    id: 'xmla.run',
    label: t('xmla.run'),
    keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, monaco.KeyCode.F5],
    run: () => void run(),
  })
  void loadSnippets()
})
onBeforeUnmount(() => {
  editor?.dispose()
  window.clearInterval(timer)
})

/** Replaces the whole script, undoable (Ctrl+Z), like the MDX editor does. */
function replaceScript(text: string) {
  const model = editor?.getModel()
  if (!editor || !model || editor.getValue() === text) return
  editor.pushUndoStop()
  editor.executeEdits('cubescope', [{ range: model.getFullModelRange(), text }])
  editor.pushUndoStop()
}

// "Open in the console" from the processing dialog.
watch(() => store.xmlaRevision, () => replaceScript(store.xmlaScript))

// A confirmation is typed for one server: forget it when the server changes.
watch(() => store.server, () => {
  confirmText.value = ''
  serverSaidProduction.value = false
})

const canRun = computed(() =>
  store.connected && !running.value && store.xmlaScript.trim().length > 0
  && (!production.value || confirmText.value.trim().toLowerCase() === store.server.trim().toLowerCase()))

async function run() {
  if (!canRun.value) return
  running.value = true
  result.value = null
  error.value = ''
  elapsed.value = 0
  const started = Date.now()
  timer = window.setInterval(() => (elapsed.value = Math.floor((Date.now() - started) / 1000)), 1000)
  try {
    result.value = await api.xmlaExecute(store.xmlaScript, production.value ? confirmText.value : undefined)
  } catch (e) {
    if (e instanceof ApiError && e.code === 'productionRefused') serverSaidProduction.value = true
    error.value = e instanceof TypeError ? t('process.maybeRunning') : e instanceof Error ? e.message : String(e)
  } finally {
    window.clearInterval(timer)
    running.value = false
  }
}

// --- XMLA snippets (same SQLite library as the MDX ones, language = xmla) ---------------
const snippets = ref<Snippet[]>([])
const chosenSnippet = ref<Snippet | null>(null)
const snippetName = ref('')

async function loadSnippets() {
  try {
    snippets.value = (await api.snippets()).filter((s) => s.language === 'xmla')
  } catch {
    snippets.value = []
  }
}

async function saveSnippet() {
  const name = snippetName.value.trim()
  if (!name || !store.xmlaScript.trim()) return
  try {
    await api.addSnippet(name, store.xmlaScript, 'xmla')
    snippetName.value = ''
    toast.add({ severity: 'success', summary: t('snippets.saved'), life: 3000 })
    await loadSnippets()
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}

async function deleteSnippet() {
  const s = chosenSnippet.value
  if (!s) return
  try {
    await api.deleteSnippet(s.id)
    chosenSnippet.value = null
    await loadSnippets()
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}

watch(chosenSnippet, (s) => s && replaceScript(s.mdx))

// --- Log -------------------------------------------------------------------------------
const showLog = ref(false)
const log = ref<XmlaLogEntry[]>([])
async function openLog() {
  try {
    log.value = await api.xmlaLog()
    showLog.value = true
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}
async function reload(entry: XmlaLogEntry) {
  try {
    replaceScript((await api.xmlaLogScript(entry.id)).script)
    showLog.value = false
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}
function preview(script: string): string {
  const s = script.replace(/\s+/g, ' ').trim()
  return s.length > 120 ? s.slice(0, 120) + '…' : s
}
</script>

<template>
  <div class="xmla">
    <div class="xmla-bar">
      <Button
        :label="t('xmla.run')"
        icon="pi pi-play"
        size="small"
        :severity="production ? 'danger' : undefined"
        :loading="running"
        :disabled="!canRun"
        :title="t('xmla.runHint')"
        @click="run"
      />
      <span v-if="store.connected" class="xmla-server" :class="production ? 'prod' : 'dev'">
        {{ store.server }} · {{ production ? t('process.production') : t('process.development') }}
      </span>
      <template v-if="production">
        <label class="xmla-confirm">
          {{ t('xmla.typeServer') }}
          <InputText v-model="confirmText" size="small" autocomplete="off" :placeholder="store.server" />
        </label>
      </template>
      <span class="xmla-spacer" />
      <Select
        v-model="chosenSnippet"
        :options="snippets"
        option-label="name"
        size="small"
        class="xmla-snippets"
        :placeholder="t('xmla.snippets')"
        :empty-message="t('xmla.noSnippet')"
      />
      <Button icon="pi pi-trash" size="small" text :disabled="!chosenSnippet" :title="t('snippets.deleteTitle')"
        :aria-label="t('snippets.deleteTitle')" @click="deleteSnippet" />
      <InputText v-model="snippetName" size="small" class="xmla-name" :placeholder="t('xmla.snippetName')" />
      <Button icon="pi pi-save" size="small" text :disabled="!snippetName.trim()" :title="t('xmla.saveSnippet')"
        :aria-label="t('xmla.saveSnippet')" @click="saveSnippet" />
      <Button :label="t('process.log')" icon="pi pi-history" size="small" text @click="openLog" />
    </div>

    <div ref="host" class="xmla-editor" />

    <div class="xmla-output">
      <p v-if="!store.connected" class="xmla-hint">{{ t('xmla.notConnected') }}</p>
      <div v-if="running" class="xmla-running"><i class="pi pi-spin pi-spinner" /> {{ t('process.running', { s: elapsed }) }}</div>
      <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
      <template v-if="result">
        <Message :severity="result.success ? 'success' : 'error'" :closable="false">
          {{ t(result.success ? 'xmla.ok' : 'xmla.ko', { ms: result.durationMs }) }} — {{ result.commands.join(', ') }}
        </Message>
        <ul v-if="result.messages.length" class="xmla-messages">
          <li v-for="(m, i) in result.messages" :key="i" :class="m.severity.toLowerCase()">{{ m.severity }} — {{ m.text }}</li>
        </ul>
        <Message v-if="result.multidimensionalResult" severity="info" :closable="false">{{ t('xmla.cellset') }}</Message>
        <Message v-if="result.rowsetTruncated" severity="warn" :closable="false">{{ t('xmla.truncated') }}</Message>
        <div v-if="result.rowset" class="xmla-grid">
          <ResultsGrid :columns="result.rowset.columns" :rows="result.rowset.rows" />
        </div>
      </template>
    </div>

    <Dialog v-model:visible="showLog" modal :header="t('xmla.logTitle')" :style="{ width: '60rem' }">
      <DataTable :value="log" size="small" scrollable scroll-height="26rem">
        <Column :header="t('process.logWhen')">
          <template #body="{ data }">{{ dateTime(data.executedUtc) }}</template>
        </Column>
        <Column field="server" :header="t('process.logServer')" />
        <Column :header="t('process.logEnv')">
          <template #body="{ data }">
            <Tag :value="data.isProduction ? t('process.production') : t('process.development')"
              :severity="data.isProduction ? 'danger' : 'success'" />
          </template>
        </Column>
        <Column :header="t('xmla.script')">
          <template #body="{ data }"><code class="log-script">{{ preview(data.script) }}</code></template>
        </Column>
        <Column :header="t('process.logResult')">
          <template #body="{ data }">
            <span :class="data.success ? 'log-ok' : 'log-ko'" :title="data.error ?? ''">
              {{ data.success ? `${data.durationMs} ms` : (data.error ?? t('process.failedShort')) }}
            </span>
          </template>
        </Column>
        <Column>
          <template #body="{ data }">
            <Button icon="pi pi-upload" size="small" text :title="t('xmla.reload')" :aria-label="t('xmla.reload')"
              @click="reload(data)" />
          </template>
        </Column>
      </DataTable>
    </Dialog>
  </div>
</template>

<style scoped>
.xmla {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.xmla-bar {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.3rem 0.5rem;
  flex-wrap: wrap;
}
.xmla-server {
  font-size: 0.8rem;
  font-weight: 600;
}
.xmla-server.prod {
  color: var(--p-red-400);
}
.xmla-server.dev {
  color: var(--p-green-400);
}
.xmla-confirm {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.8rem;
  color: var(--p-red-400);
}
.xmla-spacer {
  flex: 1;
}
.xmla-snippets {
  width: 12rem;
}
.xmla-name {
  width: 10rem;
}
.xmla-editor {
  flex: 1 1 55%;
  min-height: 8rem;
}
.xmla-output {
  flex: 1 1 45%;
  min-height: 0;
  overflow: auto;
  padding: 0.4rem 0.5rem;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  border-top: 1px solid var(--p-surface-700);
}
.xmla-grid {
  flex: 1;
  min-height: 12rem;
}
.xmla-hint,
.xmla-running {
  font-size: 0.85rem;
  color: var(--p-text-muted-color);
}
.xmla-messages {
  margin: 0;
  padding-left: 1.2rem;
  font-size: 0.8rem;
}
.xmla-messages .error {
  color: var(--p-red-400);
}
.xmla-messages .warning {
  color: var(--p-orange-400);
}
.log-script {
  font-size: 0.75rem;
}
.log-ok {
  color: var(--p-green-400);
}
.log-ko {
  color: var(--p-red-400);
}
</style>
