<script setup lang="ts">
// Sessions ouvertes sur l'instance SSAS, et annulation d'une session par son SPID.
// ⚠️ La liste ne contient PAS que les sessions de CubeScope : les jobs de production et les
// autres utilisateurs y figurent. D'où la confirmation détaillée avant toute annulation —
// annuler la mauvaise ligne fait échouer une alimentation.
// Lecture réservée aux admins SSAS : sans droits, le serveur refuse et on affiche le message.
import { ref, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import Message from 'primevue/message'
import Dialog from 'primevue/dialog'
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
    // Rafraîchir dans tous les cas : après un échec, la liste affichée est justement
    // celle dont on vient de constater qu'elle n'était plus à jour.
    await load()
  }
}

/** Millisecondes → durée lisible ; les sessions vivent parfois depuis des heures. */
function duration(ms: number): string {
  if (ms < 1000) return `${ms} ms`
  const s = Math.floor(ms / 1000)
  if (s < 60) return `${s} s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} min ${s % 60} s`
  return `${Math.floor(m / 60)} h ${m % 60} min`
}

/** Texte le plus parlant : la commande en cours, sinon la dernière connue. */
function commandOf(s: SsasSessionInfo): string {
  return (s.commandText || s.lastCommand || '').replace(/\s+/g, ' ').trim()
}

// Le panneau peut être monté avant la connexion (dockview crée tous les panneaux au
// démarrage) : sans ce watch, il resterait vide jusqu'à un rafraîchissement manuel.
watch(() => store.connected, (c) => { if (c) void load() })
onMounted(() => { if (store.connected) void load() })
</script>

<template>
  <div class="sessions-panel">
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
          <span class="cmd" :title="commandOf(data)">{{ commandOf(data) }}</span>
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
.sessions-count,
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
.cmd {
  display: inline-block;
  max-width: 34rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: bottom;
  font-family: var(--font-mono, monospace);
  font-size: 0.78rem;
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
