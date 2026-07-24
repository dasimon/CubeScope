<script setup lang="ts">
// Grille de résultats v1 : PrimeVue DataTable virtualisé, derrière une interface
// columns/rows volontairement minimale — si les crossjoins larges rament un jour,
// on branche AG Grid ici sans toucher au reste (décision actée).
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import type { GridColumn } from '../api'

defineProps<{
  columns: GridColumn[]
  rows: Record<string, unknown>[]
}>()
</script>

<template>
  <DataTable
    :value="rows"
    scrollable
    scroll-height="flex"
    :virtual-scroller-options="{ itemSize: 33 }"
    show-gridlines
    size="small"
    class="results-grid"
  >
    <Column
      v-for="col in columns"
      :key="col.field"
      :field="col.field"
      :header="col.header"
      :class="col.isRowHeader ? 'col-row-header' : 'col-value'"
    />
  </DataTable>
</template>

<style scoped>
.results-grid {
  height: 100%;
  font-variant-numeric: tabular-nums;
}
:deep(.col-value) {
  text-align: right;
}
:deep(.col-row-header) {
  font-weight: 600;
  white-space: nowrap;
}
</style>
