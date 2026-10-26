<script setup lang="ts">
// "Process…" from the State panel: mode, the XMLA AMO will send (copyable — what is shown is
// exactly what runs), the measure groups a dimension process leaves unprocessed, then the run.
// Production rule (server-side, ProductionActionGuard): on a server not declared dev, the server
// name must be typed again, and Full/Data on a dimension is refused whatever is typed.
// No ProcessAdd nor ProcessClear (owner's decisions): the XMLA console covers them.
// No progress streaming: elapsed time only; a long process is cancelled from the Sessions panel.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { useToast } from 'primevue/usetoast'
import { api, type ProcessMode, type ProcessPreview, type ProcessResult, type ProcessTarget } from '../api'
import { actions, store } from '../store'

const props = defineProps<{ target: (ProcessTarget & { label: string }) | null }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const toast = useToast()

const MODES: ProcessMode[] = ['Default', 'Full', 'Data', 'Update']
const modes = computed(() =>
  MODES.filter((m) => m !== 'Update' || props.target?.kind === 'Dimension').map((m) => ({
    value: m,
    label: `Process${m}`,
  })),
)

const mode = ref<ProcessMode>('Default')
const preview = ref<ProcessPreview | null>(null)
const previewError = ref('')
const loadingPreview = ref(false)
const confirmText = ref('')
const running = ref(false)
const elapsed = ref(0)
const result = ref<ProcessResult | null>(null)
const runError = ref('')
let timer: number | undefined
let previewSeq = 0

async function loadPreview() {
  if (!props.target) return
  const mine = ++previewSeq
  loadingPreview.value = true
  previewError.value = ''
  preview.value = null
  try {
    const { label: _label, ...target } = props.target
    const p = await api.processPreview({ target, mode: mode.value })
    if (mine === previewSeq) preview.value = p
  } catch (e) {
    if (mine === previewSeq) previewError.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (mine === previewSeq) loadingPreview.value = false
  }
}

// A run in flight keeps its state (and its own label) across closing / reopening the dialog:
// only a new preview is loaded for the object now shown.
watch(
  () => props.target,
  (tg) => {
    if (!tg) return
    confirmText.value = ''
    if (!running.value) {
      result.value = null
      runError.value = ''
    }
    if (mode.value !== 'Default') mode.value = 'Default' // the mode watch loads the preview
    else void loadPreview()
  },
)
watch(mode, () => {
  // A confirmation typed for one mode is not one for another (Default → Full).
  confirmText.value = ''
  if (!running.value) {
    result.value = null
    runError.value = ''
  }
  void loadPreview()
})

/** Same rule as the server (trimmed, case-insensitive); the server checks again anyway. */
const confirmed = computed(() =>
  !preview.value?.isProduction || confirmText.value.trim().toLowerCase() === store.server.trim().toLowerCase())

const canRun = computed(() =>
  !!preview.value && !preview.value.refusal && !!preview.value.xmla && confirmed.value && !running.value)

/** Object of the last run: the dialog may show another object by the time it ends. */
const runLabel = ref('')

async function run() {
  if (!props.target || !canRun.value) return
  // Copied before the await: closing or reopening the dialog changes props.target.
  const { label, ...target } = props.target
  const request = {
    target,
    mode: mode.value,
    confirmServer: preview.value?.isProduction ? confirmText.value : undefined,
  }
  runLabel.value = label
  running.value = true
  result.value = null
  runError.value = ''
  elapsed.value = 0
  const started = Date.now()
  timer = window.setInterval(() => (elapsed.value = Math.floor((Date.now() - started) / 1000)), 1000)
  try {
    result.value = await api.processRun(request)
    toast.add({
      severity: result.value.success ? 'success' : 'error',
      summary: t(result.value.success ? 'process.succeeded' : 'process.failed', { object: label }),
      life: 5000,
    })
  } catch (e) {
    // A dropped request (TypeError from fetch) says nothing about the server, which may still
    // be processing: point to the log and the state rather than claiming a failure.
    runError.value = e instanceof TypeError ? t('process.maybeRunning') : e instanceof Error ? e.message : String(e)
    toast.add({ severity: 'error', summary: t('process.failed', { object: label }), detail: runError.value, life: 8000 })
  } finally {
    window.clearInterval(timer)
    running.value = false
    // The state may have changed on the server: read it again (one AMO read, not a loop).
    void actions.loadProcessing()
  }
}

onBeforeUnmount(() => window.clearInterval(timer))

async function copyXmla() {
  try {
    await navigator.clipboard.writeText(preview.value?.xmla ?? '')
    toast.add({ severity: 'success', summary: t('process.xmlaCopied'), life: 3000 })
  } catch {
    /* clipboard unavailable */
  }
}

function close() {
  // Closing does not stop a process already sent: the server keeps going (Sessions panel to cancel).
  emit('close')
}
</script>

<template>
  <!-- Not modal while running: the Sessions panel (to cancel) must stay reachable. -->
  <Dialog
    :visible="target != null"
    :modal="!running"
    :header="t('process.title', { object: target?.label ?? '' })"
    :style="{ width: '44rem' }"
    @update:visible="(v: boolean) => !v && close()"
  >
    <div class="process">
      <div class="process-row">
        <label for="process-mode">{{ t('process.mode') }}</label>
        <Select id="process-mode" v-model="mode" :options="modes" option-label="label" option-value="value"
          size="small" :disabled="running" />
        <span class="process-server" :class="preview?.isProduction ? 'prod' : 'dev'">
          {{ store.server }} · {{ preview ? (preview.isProduction ? t('process.production') : t('process.development')) : '…' }}
        </span>
      </div>

      <Message v-if="preview?.isProduction && !preview.refusal" severity="warn" :closable="false">
        {{ t('process.productionBanner', { server: store.server }) }}
      </Message>
      <Message v-if="preview?.refusal" severity="error" :closable="false">{{ preview.refusal }}</Message>
      <Message v-if="previewError" severity="error" :closable="false">{{ previewError }}</Message>

      <Message v-if="preview && !preview.refusal && target?.kind === 'Dimension' && ['Full', 'Data'].includes(mode)"
        severity="warn" :closable="false">
        <div>{{ t('process.dimensionWarning') }}</div>
        <ul v-if="preview.affectedMeasureGroups.length" class="process-affected">
          <li v-for="g in preview.affectedMeasureGroups" :key="g">{{ g }}</li>
        </ul>
        <div v-else>{{ t('process.noAffected') }}</div>
      </Message>
      <Message v-if="preview && target?.kind === 'Dimension' && mode === 'Update'" severity="info" :closable="false">
        <div>{{ t('process.updateWarning') }}</div>
        <ul v-if="preview.affectedMeasureGroups.length" class="process-affected">
          <li v-for="g in preview.affectedMeasureGroups" :key="g">{{ g }}</li>
        </ul>
      </Message>

      <div class="process-xmla-head">
        <span>{{ t('process.xmla') }}</span>
        <span>
          <Button icon="pi pi-copy" size="small" text :disabled="!preview?.xmla" :aria-label="t('process.copyXmla')"
            :title="t('process.copyXmla')" @click="copyXmla" />
          <Button icon="pi pi-external-link" size="small" text :disabled="!preview?.xmla"
            :aria-label="t('process.openInConsole')" :title="t('process.openInConsole')"
            @click="actions.openInXmlaConsole(preview!.xmla); close()" />
        </span>
      </div>
      <pre class="process-xmla">{{ loadingPreview ? '…' : preview?.xmla || '—' }}</pre>

      <div v-if="preview?.isProduction && !preview.refusal" class="process-confirm">
        <label for="process-confirm">{{ t('process.typeServer', { server: store.server }) }}</label>
        <InputText id="process-confirm" v-model="confirmText" size="small" autocomplete="off" :disabled="running" />
      </div>

      <div v-if="running" class="process-running">
        <i class="pi pi-spin pi-spinner" />
        {{ runLabel }} — {{ t('process.running', { s: elapsed }) }}
        <Button :label="t('process.openSessions')" size="small" link @click="actions.requestPanel('sessions')" />
      </div>
      <Message v-if="runError" severity="error" :closable="false">{{ runError }}</Message>
      <div v-if="result" class="process-result">
        <Message :severity="result.success ? 'success' : 'error'" :closable="false">
          {{ runLabel }} — {{ t(result.success ? 'process.doneOk' : 'process.doneKo', { ms: result.durationMs }) }}
        </Message>
        <ul v-if="result.messages.length" class="process-messages">
          <li v-for="(m, i) in result.messages" :key="i" :class="m.severity.toLowerCase()">{{ m.severity }} — {{ m.text }}</li>
        </ul>
      </div>
    </div>
    <template #footer>
      <Button :label="t('common.close')" severity="secondary" text @click="close" />
      <Button
        :label="preview?.isProduction ? t('process.runProd') : t('process.run')"
        icon="pi pi-play"
        :severity="preview?.isProduction ? 'danger' : undefined"
        :loading="running"
        :disabled="!canRun"
        @click="run"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.process {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}
.process-row {
  display: flex;
  align-items: center;
  gap: 0.6rem;
}
.process-server {
  margin-left: auto;
  font-size: 0.8rem;
  font-weight: 600;
}
.process-server.prod {
  color: var(--p-red-400);
}
.process-server.dev {
  color: var(--p-green-400);
}
.process-affected {
  margin: 0.3rem 0 0;
  padding-left: 1.2rem;
  max-height: 8rem;
  overflow: auto;
}
.process-xmla-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--p-text-muted-color);
}
.process-xmla {
  margin: 0;
  max-height: 14rem;
  overflow: auto;
  font-size: 0.78rem;
  background: var(--p-surface-900);
  padding: 0.4rem 0.6rem;
  border-radius: 0.3rem;
}
.process-confirm {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  font-size: 0.85rem;
}
.process-running {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
.process-messages {
  margin: 0;
  padding-left: 1.2rem;
  max-height: 10rem;
  overflow: auto;
  font-size: 0.8rem;
}
.process-messages .error {
  color: var(--p-red-400);
}
.process-messages .warning {
  color: var(--p-orange-400);
}
</style>
