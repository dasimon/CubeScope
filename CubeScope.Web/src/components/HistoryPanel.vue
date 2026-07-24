<script setup lang="ts">
// Historique des requêtes (SQLite côté serveur). Filtre texte local (les entrées chargées),
// double-clic ou bouton crayon : recharge le MDX dans l'éditeur ; bouton copie : presse-papiers.
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import Button from 'primevue/button'
import type { HistoryEntry } from '../api'
import { actions, store } from '../store'

const { t } = useI18n()
const filter = ref('')

const filtered = computed(() => {
  const f = filter.value.trim().toLowerCase()
  if (!f) return store.history
  return store.history.filter(
    (h) =>
      h.mdx.toLowerCase().includes(f) ||
      (h.catalog ?? '').toLowerCase().includes(f) ||
      (h.error ?? '').toLowerCase().includes(f),
  )
})

function onRowDblClick(e: { data: HistoryEntry }) {
  actions.loadFromHistory(e.data)
}

async function copyMdx(entry: HistoryEntry) {
  await navigator.clipboard.writeText(entry.mdx)
}

function shortMdx(mdx: string): string {
  return mdx.replace(/\s+/g, ' ').trim().slice(0, 120)
}

function localTime(utc: string): string {
  return new Date(utc).toLocaleString()
}
</script>

<template>
  <div class="history-panel">
    <div class="history-bar">
      <InputText v-model="filter" :placeholder="t('history.filterPlaceholder')" size="small" class="history-filter" />
      <Button icon="pi pi-refresh" text size="small" :title="t('common.reload')" @click="actions.loadHistory()" />
    </div>
    <DataTable
      :value="filtered"
      scrollable
      scroll-height="flex"
      size="small"
      class="history-table"
      data-key="id"
      @row-dblclick="onRowDblClick"
    >
      <Column header="" style="width: 2rem">
        <template #body="{ data }">
          <i
            :class="data.success ? 'pi pi-check-circle history-ok' : 'pi pi-times-circle history-ko'"
            :title="data.error ?? ''"
          />
        </template>
      </Column>
      <Column :header="t('history.executed')">
        <template #body="{ data }">{{ localTime(data.executedUtc) }}</template>
      </Column>
      <Column field="catalog" :header="t('history.catalog')" />
      <Column header="MDX">
        <template #body="{ data }">
          <span class="history-mdx" :title="data.mdx">{{ shortMdx(data.mdx) }}</span>
        </template>
      </Column>
      <Column field="durationMs" :header="t('history.ms')" style="text-align: right" />
      <Column field="cellCount" :header="t('history.cells')" style="text-align: right" />
      <Column header="" style="width: 5rem">
        <template #body="{ data }">
          <Button
            icon="pi pi-pencil"
            text
            size="small"
            :title="t('history.loadInEditor')"
            @click="actions.loadFromHistory(data)"
          />
          <Button icon="pi pi-copy" text size="small" :title="t('history.copyMdx')" @click="copyMdx(data)" />
        </template>
      </Column>
    </DataTable>
  </div>
</template>

<style scoped>
.history-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.history-bar {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.3rem 0.5rem;
}
.history-filter {
  flex: 1;
  max-width: 24rem;
}
.history-table {
  flex: 1;
}
.history-ok {
  color: var(--p-green-400);
}
.history-ko {
  color: var(--p-red-400);
}
.history-mdx {
  font-family: monospace;
  font-size: 0.85em;
}
</style>
