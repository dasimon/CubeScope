<script setup lang="ts">
// Processing state of the current database (AMO, read-only): cubes → measure groups →
// partitions, plus the dimensions. Answers "did the processing run?" and "why is this
// catalog empty?" (an unprocessed cube is missing from MDSCHEMA_CUBES, AMO still sees it).
// No automatic refresh: each read is an AMO connection to the server, possibly production.
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TreeTable from 'primevue/treetable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import type { TreeNode } from 'primevue/treenode'
import { useToast } from 'primevue/usetoast'
import { api, type FreshnessStatus, type ObjectState } from '../api'
import { actions, store } from '../store'
import { age, bytes, dateTime, severity, stateLabel } from '../processing'

const { t } = useI18n()
const toast = useToast()

interface Row {
  kind: 'folder' | 'cube' | 'measureGroup' | 'partition' | 'dimension'
  name: string
  state?: ObjectState
  status?: FreshnessStatus
  lastProcessedUtc?: string | null
  oldestUtc?: string | null
  newestUtc?: string | null
  staleCount?: number
  unprocessedCount?: number
  storage?: string
  size?: number
  slice?: string | null
  aggregations?: string | null
  rows?: number
}

const nodes = computed<TreeNode[]>(() => {
  const s = store.processing
  if (!s) return []
  const cubes: TreeNode[] = s.cubes.map((c) => ({
    key: `c:${c.id}`,
    data: {
      kind: 'cube', name: c.name, state: c.state, status: c.status, lastProcessedUtc: c.lastProcessedUtc,
      oldestUtc: c.oldestPartitionUtc, newestUtc: c.newestPartitionUtc,
      staleCount: c.staleCount, unprocessedCount: c.unprocessedCount,
    } satisfies Row,
    children: c.measureGroups.map((g) => ({
      key: `g:${c.id}:${g.id}`,
      data: {
        kind: 'measureGroup', name: g.name, state: g.state, status: g.status, lastProcessedUtc: g.lastProcessedUtc,
        oldestUtc: g.oldestPartitionUtc, newestUtc: g.newestPartitionUtc,
        staleCount: g.staleCount, unprocessedCount: g.unprocessedCount, size: g.estimatedSize,
      } satisfies Row,
      children: g.partitions.map((p) => ({
        key: `p:${c.id}:${g.id}:${p.id}`,
        data: {
          kind: 'partition', name: p.name, state: p.state, status: p.status, lastProcessedUtc: p.lastProcessedUtc,
          storage: p.storageMode, size: p.estimatedSize, slice: p.slice, aggregations: p.aggregationDesign,
          rows: p.estimatedRows,
        } satisfies Row,
      })),
    })),
  }))
  const dimensions: TreeNode[] = s.dimensions.map((d) => ({
    key: `d:${d.id}`,
    data: {
      kind: 'dimension', name: d.name, state: d.state, status: d.status, lastProcessedUtc: d.lastProcessedUtc,
    } satisfies Row,
  }))
  const dimStatus: FreshnessStatus = s.dimensions.some((d) => d.status === 'Unprocessed')
    ? 'Unprocessed'
    : s.dimensions.some((d) => d.status === 'Stale') ? 'Stale' : 'Ok'
  return [
    { key: 'cubes', data: { kind: 'folder', name: t('processing.cubes') } satisfies Row, children: cubes },
    {
      key: 'dims',
      data: {
        kind: 'folder', name: t('processing.dimensions'), status: dimStatus,
        staleCount: s.dimensions.filter((d) => d.status === 'Stale').length,
        unprocessedCount: s.dimensions.filter((d) => d.status === 'Unprocessed').length,
      } satisfies Row,
      children: dimensions,
    },
  ]
})

// Cubes open by default, measure groups and dimensions folded. Kept across refreshes and cube
// changes; forgotten only when the state itself is (server or catalog change).
const expandedKeys = ref<Record<string, boolean>>({})
watch(
  () => store.processing,
  (s) => {
    if (!s) {
      expandedKeys.value = {}
      return
    }
    if (Object.keys(expandedKeys.value).length > 0) return
    expandedKeys.value = { cubes: true, ...Object.fromEntries(s.cubes.map((c) => [`c:${c.id}`, true])) }
  },
  { immediate: true },
)

// Threshold: the server holds it (SQLite) and returns it with each state.
const threshold = ref<number | null>(store.processing?.thresholdHours ?? null)
watch(() => store.processing?.thresholdHours, (h) => { if (h != null) threshold.value = h })
const savingThreshold = ref(false)

// PrimeVue InputNumber only updates v-model on blur (observed): without this, the save button
// stays disabled while typing and Enter would save the previous value.
function onThresholdInput(e: { value: string | number | undefined | null }) {
  threshold.value = typeof e.value === 'number' ? e.value : null
}

async function saveThreshold() {
  if (threshold.value == null) return
  savingThreshold.value = true
  try {
    const r = await api.setProcessingThreshold(threshold.value)
    toast.add({ severity: 'success', summary: t('processing.thresholdSaved', { hours: r.hours }), life: 3000 })
    // The verdicts are computed server-side: read again with the new threshold.
    await actions.loadProcessing()
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 8000 })
  } finally {
    savingThreshold.value = false
  }
}

const readAt = computed(() =>
  store.processing ? new Date(store.processing.readAtUtc).toLocaleTimeString() : '')

/** Ages are measured from the read time, like the server's verdict. */
function ago(utc: string | null | undefined): string {
  if (!utc || !store.processing) return '—'
  return t('processing.ago', { age: age(utc, store.processing.readAtUtc) })
}

const SLICE_PREVIEW = 80
function slicePreview(s: string | null | undefined): string {
  if (!s) return ''
  return s.length > SLICE_PREVIEW ? s.slice(0, SLICE_PREVIEW) + '…' : s
}

function stateTitle(r: Row): string {
  if (r.kind === 'partition' && r.rows) return t('processing.rowsHint', { rows: r.rows.toLocaleString() })
  return ''
}

function lastProcessedTitle(r: Row): string {
  // Parents: their own server date is not trusted to follow their partitions (see CLAUDE.md).
  if (r.kind === 'cube' || r.kind === 'measureGroup')
    return t('processing.parentDateHint', { date: dateTime(r.lastProcessedUtc ?? null) })
  return dateTime(r.lastProcessedUtc ?? null)
}

/** Parents show the newest partition (did it run?), leaves their own date. */
function lastProcessedOf(r: Row): string | null | undefined {
  return r.kind === 'cube' || r.kind === 'measureGroup' ? r.newestUtc : r.lastProcessedUtc
}
</script>

<template>
  <div class="processing-panel">
    <div class="processing-bar">
      <Button
        :label="t('processing.refresh')"
        icon="pi pi-refresh"
        size="small"
        text
        :loading="store.processingLoading"
        :disabled="!store.connected || !store.catalog"
        @click="actions.loadProcessing()"
      />
      <label class="processing-threshold">
        {{ t('processing.threshold') }}
        <InputNumber
          v-model="threshold"
          :min="1"
          :max="8760"
          :use-grouping="false"
          size="small"
          input-class="threshold-input"
          @input="onThresholdInput"
          @keydown.enter="saveThreshold"
        />
      </label>
      <Button
        icon="pi pi-check"
        size="small"
        text
        :title="t('processing.thresholdSave')"
        :aria-label="t('processing.thresholdSave')"
        :loading="savingThreshold"
        :disabled="threshold == null || threshold === store.processing?.thresholdHours"
        @click="saveThreshold"
      />
      <span v-if="readAt" class="processing-meta">{{ t('processing.readAt', { time: readAt }) }}</span>
    </div>

    <Message v-if="store.processingUnavailable" severity="warn" class="processing-msg">
      {{ t('processing.unavailable', { detail: store.processingUnavailable }) }}
    </Message>
    <Message v-else-if="store.processingError" severity="error" class="processing-msg">
      {{ store.processingError }}
    </Message>
    <p v-else-if="!store.catalog" class="processing-meta processing-msg">{{ t('processing.noCatalog') }}</p>

    <TreeTable
      v-if="store.processing"
      v-model:expanded-keys="expandedKeys"
      :value="nodes"
      scrollable
      scroll-height="flex"
      size="small"
      class="processing-table"
    >
      <Column field="name" :header="t('processing.name')" expander style="min-width: 18rem">
        <template #body="{ node }">
          <span :class="{ folder: node.data.kind === 'folder' }">{{ node.data.name }}</span>
        </template>
      </Column>
      <Column :header="t('processing.state')" style="min-width: 11rem">
        <template #body="{ node }">
          <span class="state-cell" :title="stateTitle(node.data)">
            <Tag
              v-if="node.data.state && node.data.state !== 'Processed'"
              :value="stateLabel(node.data.state)"
              severity="danger"
            />
            <Tag
              v-else-if="node.data.kind !== 'folder' && node.data.status === 'Stale' && !node.data.staleCount"
              :value="t('processing.stale')"
              severity="warn"
            />
            <Tag v-else-if="node.data.state" :value="stateLabel(node.data.state)" severity="success" />
            <Tag
              v-if="node.data.unprocessedCount"
              :value="t('processing.unprocessedCount', { n: node.data.unprocessedCount })"
              :severity="severity('Unprocessed')"
            />
            <Tag
              v-if="node.data.staleCount"
              :value="t('processing.staleCount', { n: node.data.staleCount })"
              :severity="severity('Stale')"
            />
          </span>
        </template>
      </Column>
      <Column :header="t('processing.lastProcessed')" style="min-width: 9rem">
        <template #body="{ node }">
          <span v-if="node.data.kind !== 'folder'" :title="lastProcessedTitle(node.data)">
            {{ ago(lastProcessedOf(node.data)) }}
          </span>
        </template>
      </Column>
      <Column :header="t('processing.freshness')" style="min-width: 16rem">
        <template #body="{ node }">
          <span v-if="node.data.oldestUtc" class="num">
            {{ dateTime(node.data.oldestUtc) }} → {{ dateTime(node.data.newestUtc) }}
          </span>
        </template>
      </Column>
      <Column :header="t('processing.storage')">
        <template #body="{ node }">{{ node.data.storage ?? '' }}</template>
      </Column>
      <Column :header="t('processing.size')" class="col-num">
        <template #body="{ node }">
          <span v-if="node.data.size != null" class="num">{{ bytes(node.data.size) }}</span>
        </template>
      </Column>
      <Column :header="t('processing.slice')" style="min-width: 14rem">
        <template #body="{ node }">
          <span v-if="node.data.slice" class="slice" :title="node.data.slice">{{ slicePreview(node.data.slice) }}</span>
        </template>
      </Column>
      <Column :header="t('processing.aggregations')">
        <template #body="{ node }">{{ node.data.aggregations ?? '' }}</template>
      </Column>
    </TreeTable>
  </div>
</template>

<style scoped>
.processing-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.processing-bar {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.25rem 0.5rem;
}
.processing-threshold {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin-left: 0.5rem;
}
.processing-threshold,
.processing-meta {
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
}
.processing-meta {
  margin-left: auto;
}
.processing-msg {
  margin: 0.75rem;
}
.processing-table {
  flex: 1;
  min-height: 0;
}
:deep(.threshold-input) {
  width: 4.5rem;
}
:deep(.col-num) {
  text-align: right;
  white-space: nowrap;
}
.num {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}
.folder {
  font-weight: 600;
}
.state-cell {
  display: inline-flex;
  gap: 0.3rem;
  flex-wrap: wrap;
}
.slice {
  font-family: var(--font-mono, monospace);
  font-size: 0.75rem;
  white-space: nowrap;
}
</style>
