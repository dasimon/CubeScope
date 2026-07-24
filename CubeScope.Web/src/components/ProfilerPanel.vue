<script setup lang="ts">
// Panneau Profiler : découpage Formula Engine / Storage Engine de la dernière requête,
// via trace SSAS (par requête, scopé à la session — pas les compteurs globaux du panneau Stats).
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Message from 'primevue/message'
import Button from 'primevue/button'
import { actions, store } from '../store'

const { t } = useI18n()
onMounted(() => void actions.loadProfilerStatus())

const p = computed(() => store.profile)

// Largeurs relatives FE/SE pour la barre
const sePct = computed(() => {
  const t = p.value?.totalMs ?? 0
  return t > 0 ? Math.round(((p.value?.storageEngineMs ?? 0) / t) * 100) : 0
})
const fePct = computed(() => 100 - sePct.value)
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
  </div>
</template>

<style scoped>
.prof-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
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
  flex: 1;
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
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--p-text-muted-color);
  text-align: center;
  padding: 1rem;
}
</style>
