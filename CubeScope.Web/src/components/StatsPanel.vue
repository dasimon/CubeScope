<script setup lang="ts">
// Stats perfmon de la dernière requête (deltas de compteurs cumulés, poussés par SignalR).
// Compteurs GLOBAUX au serveur (assumé MVP) : une activité concurrente pollue les deltas.
import { onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Message from 'primevue/message'
import Button from 'primevue/button'
import { actions, store } from '../store'

const { t } = useI18n()
onMounted(() => void actions.loadStatsStatus())
</script>

<template>
  <div class="stats-panel">
    <Message v-if="store.statsStatus?.status === 'Unavailable'" severity="warn" class="stats-msg">
      {{ t('stats.unavailable', { detail: store.statsStatus.detail }) }}
      <Button :label="t('common.retry')" link size="small" @click="actions.loadStatsStatus()" />
    </Message>
    <template v-else-if="store.stats.length">
      <div class="stats-header">
        {{ t('stats.header', { ms: store.statsQueryDurationMs }) }}
      </div>
      <DataTable :value="store.stats" scrollable scroll-height="flex" size="small" class="stats-table">
        <Column field="category" :header="t('stats.category')" />
        <Column field="counter" :header="t('stats.counter')" />
        <Column field="delta" :header="t('stats.delta')" class="stats-delta" />
      </DataTable>
    </template>
    <div v-else class="stats-empty">
      <span v-if="store.statsStatus?.status === 'Ready'">{{ t('stats.ready') }}</span>
      <span v-else>{{ t('stats.waiting') }}</span>
    </div>
  </div>
</template>

<style scoped>
.stats-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.stats-msg {
  margin: 0.75rem;
}
.stats-header {
  padding: 0.4rem 0.75rem;
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
}
.stats-table {
  flex: 1;
  font-variant-numeric: tabular-nums;
}
:deep(.stats-delta) {
  text-align: right;
  font-weight: 600;
}
.stats-empty {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--p-text-muted-color);
}
</style>
