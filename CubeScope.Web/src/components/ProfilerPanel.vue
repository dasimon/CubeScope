<script setup lang="ts">
// Panneau Profiler : découpage Formula Engine / Storage Engine de la dernière requête,
// via trace SSAS (par requête, scopé à la session — pas les compteurs globaux du panneau Stats).
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Message from 'primevue/message'
import Button from 'primevue/button'
import type { ProfileRun } from '../api'
import { actions, store } from '../store'

const { t } = useI18n()
onMounted(() => {
  void actions.loadProfilerStatus()
  void actions.loadProfilerHistory()
})

const p = computed(() => store.profile)

// Largeurs relatives FE/SE pour la barre
const sePct = computed(() => {
  const t = p.value?.totalMs ?? 0
  return t > 0 ? Math.round(((p.value?.storageEngineMs ?? 0) / t) * 100) : 0
})
const fePct = computed(() => 100 - sePct.value)

// --- Historique & comparaison ---
function loadHistory(): void {
  void actions.loadProfilerHistory()
}

function localTime(utc: string): string {
  return new Date(utc).toLocaleString()
}

function shortMdx(mdx: string): string {
  return mdx.replace(/\s+/g, ' ').trim().slice(0, 80)
}

const pickA = ref<number | null>(null)
const pickB = ref<number | null>(null)

const runA = computed<ProfileRun | null>(
  () => store.profilerHistory.find((r) => r.id === pickA.value) ?? null,
)
const runB = computed<ProfileRun | null>(
  () => store.profilerHistory.find((r) => r.id === pickB.value) ?? null,
)

interface CompareRow {
  label: string
  a: number
  b: number
  delta: number
  deltaText: string
  deltaClass: string
}

// lowerIsBetter : durées + nb sous-cubes (moins = mieux) ; sinon hits cache/agrégation (plus = mieux).
function compareRow(label: string, a: number, b: number, lowerIsBetter: boolean): CompareRow {
  const delta = b - a
  const better = delta === 0 ? null : lowerIsBetter ? delta < 0 : delta > 0
  const deltaClass = better === null ? 'prof-delta-neutral' : better ? 'prof-delta-better' : 'prof-delta-worse'
  const deltaText = delta > 0 ? `+${delta}` : `${delta}`
  return { label, a, b, delta, deltaText, deltaClass }
}

const compareRows = computed<CompareRow[]>(() => {
  const a = runA.value
  const b = runB.value
  if (!a || !b) return []
  return [
    compareRow('Total (ms)', a.totalMs, b.totalMs, true),
    compareRow('Formula Engine (ms)', a.formulaEngineMs, b.formulaEngineMs, true),
    compareRow('Storage Engine (ms)', a.storageEngineMs, b.storageEngineMs, true),
    compareRow('Subcubes', a.subcubeCount, b.subcubeCount, true),
    compareRow('Cache hits', a.cacheHits, b.cacheHits, false),
    compareRow('Aggregation hits', a.aggregationHits, b.aggregationHits, false),
  ]
})
</script>

<template>
  <div class="prof-panel">
    <Message v-if="store.profilerStatus?.status === 'Unavailable'" severity="warn" class="prof-msg">
      {{ t('profiler.unavailable', { detail: store.profilerStatus.detail }) }}
      <Button :label="t('common.retry')" link size="small" @click="actions.loadProfilerStatus()" />
    </Message>

    <template v-else-if="p">
      <div class="prof-summary">
        <div class="prof-total">{{ t('profiler.total') }} <strong>{{ p.totalMs }} ms</strong></div>
        <div class="prof-bar" :title="t('profiler.barTitle', { fe: p.formulaEngineMs, se: p.storageEngineMs })">
          <div class="prof-fe" :style="{ width: fePct + '%' }">FE {{ p.formulaEngineMs }} ms</div>
          <div class="prof-se" :style="{ width: sePct + '%' }">SE {{ p.storageEngineMs }} ms</div>
        </div>
        <div class="prof-counters">
          <span>{{ t('profiler.subcubes', { n: p.subcubeCount }) }}</span>
          <span>{{ t('profiler.cacheHits', { n: p.cacheHits }) }}</span>
          <span>{{ t('profiler.aggHits', { n: p.aggregationHits }) }}</span>
        </div>
        <div class="prof-hint">{{ t('profiler.hint') }}</div>
      </div>

      <div v-if="p.subcubes.length" class="prof-subcubes">
        <div class="prof-subcubes-title">{{ t('profiler.subcubesTitle') }}</div>
        <DataTable :value="p.subcubes" scrollable scroll-height="flex" size="small" class="prof-table">
          <Column field="durationMs" :header="t('history.ms')" class="prof-dur" style="width: 5rem" />
          <Column field="text" :header="t('profiler.grid')">
            <template #body="{ data }">
              <span class="prof-subcube-text" :title="data.text">{{ data.text }}</span>
            </template>
          </Column>
        </DataTable>
      </div>
      <div v-else class="prof-hint prof-nosub">{{ t('profiler.noSubcube') }}</div>
    </template>

    <div v-else class="prof-empty">
      <span v-if="store.profilerStatus?.status === 'Ready'">{{ t('profiler.ready') }}</span>
      <span v-else>{{ t('profiler.waiting') }}</span>
    </div>

    <div class="prof-history">
      <div class="prof-history-header">
        <span class="prof-history-title">{{ t('profiler.history') }}</span>
        <Button icon="pi pi-refresh" text size="small" :title="t('profiler.refresh')" @click="loadHistory()" />
      </div>

      <div v-if="!store.profilerHistory.length" class="prof-hint prof-nosub">{{ t('profiler.noHistory') }}</div>

      <template v-else>
        <DataTable
          :value="store.profilerHistory"
          scrollable
          scroll-height="180px"
          size="small"
          class="prof-history-table"
          data-key="id"
        >
          <Column header="A" style="width: 2.5rem">
            <template #body="{ data }">
              <input v-model="pickA" type="radio" name="prof-pick-a" :value="data.id" />
            </template>
          </Column>
          <Column header="B" style="width: 2.5rem">
            <template #body="{ data }">
              <input v-model="pickB" type="radio" name="prof-pick-b" :value="data.id" />
            </template>
          </Column>
          <Column :header="t('history.executed')">
            <template #body="{ data }">{{ localTime(data.executedUtc) }}</template>
          </Column>
          <Column field="totalMs" header="Total (ms)" class="prof-dur" style="width: 5rem" />
          <Column field="formulaEngineMs" header="FE (ms)" class="prof-dur" style="width: 5rem" />
          <Column field="storageEngineMs" header="SE (ms)" class="prof-dur" style="width: 5rem" />
          <Column header="MDX">
            <template #body="{ data }">
              <span class="prof-subcube-text" :title="data.mdx">{{ shortMdx(data.mdx) }}</span>
            </template>
          </Column>
        </DataTable>

        <div v-if="runA && runB" class="prof-compare">
          <div class="prof-history-title">{{ t('profiler.compare') }}</div>
          <table class="prof-compare-table">
            <thead>
              <tr>
                <th></th>
                <th>{{ t('profiler.runA') }}</th>
                <th>{{ t('profiler.runB') }}</th>
                <th>{{ t('profiler.delta') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in compareRows" :key="row.label">
                <td>{{ row.label }}</td>
                <td>{{ row.a }}</td>
                <td>{{ row.b }}</td>
                <td :class="row.deltaClass">{{ row.deltaText }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <div v-else class="prof-hint prof-nosub">{{ t('profiler.pickTwo') }}</div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.prof-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow-y: auto;
}
.prof-msg {
  margin: 0.75rem;
}
.prof-summary {
  padding: 0.6rem 0.9rem;
}
.prof-total {
  font-size: 0.95rem;
  margin-bottom: 0.4rem;
}
.prof-bar {
  display: flex;
  height: 26px;
  border-radius: 5px;
  overflow: hidden;
  font-size: 0.78rem;
  font-weight: 600;
}
.prof-fe {
  background: var(--p-primary-color);
  color: var(--p-primary-contrast-color, #000);
  display: flex;
  align-items: center;
  justify-content: center;
  min-width: 2rem;
  white-space: nowrap;
}
.prof-se {
  background: var(--p-orange-500, #f97316);
  color: #000;
  display: flex;
  align-items: center;
  justify-content: center;
  min-width: 2rem;
  white-space: nowrap;
}
.prof-counters {
  display: flex;
  gap: 1rem;
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: var(--p-text-muted-color);
}
.prof-hint {
  margin-top: 0.5rem;
  font-size: 0.78rem;
  color: var(--p-text-muted-color);
}
.prof-subcubes {
  flex: 0 0 260px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  border-top: 1px solid var(--p-surface-700);
}
.prof-subcubes-title {
  padding: 0.4rem 0.9rem;
  font-size: 0.82rem;
  font-weight: 600;
}
.prof-table {
  flex: 1;
  font-variant-numeric: tabular-nums;
}
:deep(.prof-dur) {
  text-align: right;
  font-weight: 600;
}
.prof-subcube-text {
  font-family: Consolas, monospace;
  font-size: 0.78rem;
}
.prof-nosub {
  padding: 0.75rem 0.9rem;
}
.prof-empty {
  min-height: 120px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--p-text-muted-color);
  text-align: center;
  padding: 1rem;
}
.prof-history {
  flex: 0 0 auto;
  border-top: 1px solid var(--p-surface-700);
  padding: 0.6rem 0.9rem;
}
.prof-history-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.prof-history-title {
  font-size: 0.82rem;
  font-weight: 600;
}
.prof-history-table {
  margin-top: 0.4rem;
  font-variant-numeric: tabular-nums;
}
.prof-compare {
  margin-top: 0.75rem;
}
.prof-compare-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.82rem;
  margin-top: 0.3rem;
}
.prof-compare-table th,
.prof-compare-table td {
  text-align: right;
  padding: 0.25rem 0.5rem;
  border-bottom: 1px solid var(--p-surface-700);
}
.prof-compare-table th:first-child,
.prof-compare-table td:first-child {
  text-align: left;
}
.prof-delta-better {
  color: var(--p-green-500, #22c55e);
  font-weight: 600;
}
.prof-delta-worse {
  color: var(--p-red-500, #ef4444);
  font-weight: 600;
}
.prof-delta-neutral {
  color: var(--p-text-muted-color);
}
</style>
