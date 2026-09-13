<script setup lang="ts">
// Sessions open on the SSAS instance, and cancelling a session by its SPID.
// ⚠️ The list does NOT contain only CubeScope's sessions: production jobs and other
// users show up in it too. Hence the detailed confirmation before any cancellation —
// cancelling the wrong row makes a data load fail.
// Reading is restricted to SSAS admins: without permissions, the server refuses and the message is shown.
import { ref, onMounted, onBeforeUnmount, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import Message from 'primevue/message'
import Dialog from 'primevue/dialog'
import Checkbox from 'primevue/checkbox'
import { useToast } from 'primevue/usetoast'
import { api, type SsasSessionInfo } from '../api'
import { store } from '../store'

const { t } = useI18n()
const toast = useToast()

const sessions = ref<SsasSessionInfo[]>([])
const loading = ref(false)
const error = ref('')
const target = ref<SsasSessionInfo | null>(null)
const cancelling = ref(false)

async function load() {
  loading.value = true
  error.value = ''
  try {
    sessions.value = await api.sessions()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
    sessions.value = []
  } finally {
    loading.value = false
  }
}

async function confirmCancel() {
  const s = target.value
  if (!s) return
  cancelling.value = true
  try {
    const r = await api.cancelSession(s.spid)
    toast.add({
      severity: r.cancelled ? 'success' : 'info',
      summary: r.cancelled
        ? t('sessions.cancelled', { spid: s.spid })
        : t('sessions.alreadyGone', { spid: s.spid }),
      life: 4000,
    })
    target.value = null
  } catch (e) {
    toast.add({
      severity: 'error',
      summary: e instanceof Error ? e.message : String(e),
      life: 8000,
    })
  } finally {
    cancelling.value = false
    // Refresh in every case: after a failure, the displayed list is precisely
    // the one that was just found to be out of date.
    await load()
  }
}

/** Milliseconds → readable duration; sessions sometimes live for hours. */
function duration(ms: number): string {
  if (ms < 1000) return `${ms} ms`
  const s = Math.floor(ms / 1000)
  if (s < 60) return `${s} s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} min ${s % 60} s`
  return `${Math.floor(m / 60)} h ${m % 60} min`
}

/** Most meaningful text: the running command, otherwise the last known one. */
function commandOf(s: SsasSessionInfo): string {
  return (s.commandText || s.lastCommand || '').replace(/\s+/g, ' ').trim()
}

/**
 * Bounded preview. An MDX command is commonly several thousand characters long: leaving
 * it whole would make the horizontal scrollbar unusable. Enough is shown to recognize
 * the query; the full text is one click away.
 */
const PREVIEW = 300
function preview(s: SsasSessionInfo): string {
  const c = commandOf(s)
  return c.length > PREVIEW ? c.slice(0, PREVIEW) + '…' : c
}

/** Command shown in full (click on the cell). */
const full = ref<SsasSessionInfo | null>(null)

async function copyCommand() {
  if (!full.value) return
  try {
    await navigator.clipboard.writeText(commandOf(full.value))
    toast.add({ severity: 'success', summary: t('sessions.commandCopied'), life: 3000 })
  } catch {
    /* clipboard unavailable */
  }
}

// The panel can be mounted before connecting (dockview creates every panel at
// startup): without this watch, it would stay empty until a manual refresh.
watch(() => store.connected, (c) => { if (c) void load() })

// --- Automatic refresh ----------------------------------------------------------------
// Each cycle costs TWO DMV queries on the SSAS server. So it only runs when the panel
// is actually visible: dockview keeps inactive tabs mounted but hidden, and without
// this guard prod would be queried continuously with nobody looking.
// Same when the browser tab goes to the background.
const AUTO_KEY = 'cubescope.sessions.auto'
const PERIOD_MS = 10_000

const auto = ref(localStorage.getItem(AUTO_KEY) !== 'off')
const root = ref<HTMLElement | null>(null)
let timer: number | undefined

watch(auto, (on) => localStorage.setItem(AUTO_KEY, on ? 'on' : 'off'))

/** Visible = active dockview tab (non-null offsetParent) AND browser tab in the foreground. */
function isVisible(): boolean {
  return document.visibilityState === 'visible' && root.value?.offsetParent != null
}

function tick() {
  // `loading` avoids stacking calls if the server responds more slowly than the period.
  if (auto.value && store.connected && !loading.value && isVisible()) void load()
}

onMounted(() => {
  if (store.connected) void load()
  timer = window.setInterval(tick, PERIOD_MS)
})
onBeforeUnmount(() => window.clearInterval(timer))
</script>

<template>
  <div ref="root" class="sessions-panel">
    <div class="sessions-bar">
      <Button
        :label="t('sessions.refresh')"
        icon="pi pi-refresh"
        size="small"
        text
        :loading="loading"
        @click="load()"
      />
      <span v-if="sessions.length" class="sessions-count">
        {{ t('sessions.count', { n: sessions.length }) }}
      </span>
      <label class="sessions-auto" :title="t('sessions.autoHint')">
        <Checkbox v-model="auto" binary size="small" />
        {{ t('sessions.auto') }}
      </label>
    </div>

    <Message v-if="error" severity="warn" class="sessions-msg">
      {{ t('sessions.unavailable', { detail: error }) }}
    </Message>

    <DataTable
      v-else
      :value="sessions"
      scrollable
      scroll-height="flex"
      size="small"
      class="sessions-table"
      :row-class="(s: SsasSessionInfo) => (s.isMine ? 'row-mine' : '')"
    >
      <Column field="spid" :header="t('sessions.spid')" class="col-num" />
      <Column :header="t('sessions.user')">
        <template #body="{ data }">
          {{ data.user }}
          <span v-if="data.isMine" class="tag-mine">{{ t('sessions.mine') }}</span>
        </template>
      </Column>
      <Column field="database" :header="t('sessions.database')" />
      <Column :header="t('sessions.cpu')" class="col-num">
        <template #body="{ data }">{{ duration(data.cpuMs) }}</template>
      </Column>
      <Column :header="t('sessions.idle')" class="col-num">
        <template #body="{ data }">{{ duration(data.idleMs) }}</template>
      </Column>
      <Column :header="t('sessions.command')">
        <template #body="{ data }">
          <span class="cmd" :title="t('sessions.commandFull')" @click="full = data">
            {{ preview(data) }}
          </span>
        </template>
      </Column>
      <Column class="col-action">
        <template #body="{ data }">
          <Button
            icon="pi pi-times-circle"
            severity="danger"
            text
            size="small"
            :title="t('sessions.cancel')"
            @click="target = data"
          />
        </template>
      </Column>
    </DataTable>

    <Dialog
      :visible="full !== null"
      modal
      :header="t('sessions.commandHeader')"
      :style="{ width: '58rem' }"
      @update:visible="full = null"
    >
      <template v-if="full">
        <div class="full-meta">
          {{ t('sessions.spid') }} {{ full.spid }} — {{ full.user }} — {{ full.database ?? '—' }}
        </div>
        <pre class="full-cmd">{{ commandOf(full) || '—' }}</pre>
      </template>
      <template #footer>
        <Button :label="t('results.copy')" icon="pi pi-copy" text @click="copyCommand()" />
        <Button :label="t('common.cancel')" text @click="full = null" />
      </template>
    </Dialog>

    <Dialog
      :visible="target !== null"
      modal
      :header="t('sessions.confirmHeader')"
      :style="{ width: '46rem' }"
      @update:visible="target = null"
    >
      <template v-if="target">
        <Message
          :severity="target.isMine ? 'info' : 'warn'"
          :closable="false"
          class="confirm-msg"
        >
          {{ target.isMine ? t('sessions.confirmMine') : t('sessions.confirmOther') }}
        </Message>
        <dl class="confirm-details">
          <dt>{{ t('sessions.spid') }}</dt>
          <dd>{{ target.spid }}</dd>
          <dt>{{ t('sessions.user') }}</dt>
          <dd>{{ target.user }}</dd>
          <dt>{{ t('sessions.database') }}</dt>
          <dd>{{ target.database ?? '—' }}</dd>
          <dt>{{ t('sessions.cpu') }}</dt>
          <dd>{{ duration(target.cpuMs) }}</dd>
          <dt>{{ t('sessions.command') }}</dt>
          <dd><pre class="confirm-cmd">{{ commandOf(target) || '—' }}</pre></dd>
        </dl>
      </template>
      <template #footer>
        <Button :label="t('common.cancel')" text @click="target = null" />
        <Button
          :label="t('sessions.cancel')"
          severity="danger"
          :loading="cancelling"
          @click="confirmCancel()"
        />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.sessions-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.sessions-bar {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.25rem 0.5rem;
}
.sessions-auto {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  margin-left: auto;
  cursor: pointer;
}
.sessions-count,
.sessions-auto,
.sessions-msg {
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
}
.sessions-msg {
  margin: 0.75rem;
}
.sessions-table {
  flex: 1;
  font-variant-numeric: tabular-nums;
}
:deep(.col-num) {
  text-align: right;
  white-space: nowrap;
}
:deep(.col-action) {
  width: 3rem;
  text-align: center;
}
:deep(.row-mine) {
  background: color-mix(in srgb, var(--p-primary-color) 12%, transparent);
}
.tag-mine {
  margin-left: 0.4rem;
  font-size: 0.7rem;
  padding: 0.05rem 0.35rem;
  border-radius: 0.25rem;
  background: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
}
/* No CSS truncation: the text forces the table width, which brings up the horizontal
   scrollbar. The preview is bounded in JS so that the scrollbar stays usable. */
.cmd {
  display: inline-block;
  white-space: nowrap;
  vertical-align: bottom;
  font-family: var(--font-mono, monospace);
  font-size: 0.78rem;
  cursor: pointer;
}
.cmd:hover,
.cmd:focus-visible {
  text-decoration: underline dotted;
}
.full-meta {
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
  margin-bottom: 0.5rem;
}
.full-cmd {
  margin: 0;
  max-height: 26rem;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 0.8rem;
}
.confirm-msg {
  margin-bottom: 0.75rem;
}
.confirm-details {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0.35rem 1rem;
  margin: 0;
}
.confirm-details dt {
  font-weight: 600;
  color: var(--p-text-muted-color);
}
.confirm-details dd {
  margin: 0;
}
.confirm-cmd {
  margin: 0;
  max-height: 11rem;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 0.78rem;
}
</style>
