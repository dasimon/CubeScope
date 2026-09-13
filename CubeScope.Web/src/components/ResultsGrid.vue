<script setup lang="ts">
// Results grid v1: virtualized PrimeVue DataTable, behind a deliberately minimal
// columns/rows interface — if wide crossjoins ever get sluggish, AG Grid gets
// plugged in here without touching anything else (settled decision).
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import { useToast } from 'primevue/usetoast'
import { useI18n } from 'vue-i18n'
import type { GridColumn } from '../api'

defineProps<{
  columns: GridColumn[]
  rows: Record<string, unknown>[]
}>()

const toast = useToast()
const { t } = useI18n()

// A cell in error carries its message under a twin key "<field>__err"
// (set by CellSetMapper): the SSAS server returned an <Error><Description>.
const ERROR_SUFFIX = '__err'

function cellError(row: Record<string, unknown>, field: string): string | null {
  const message = row[field + ERROR_SUFFIX]
  return typeof message === 'string' && message.length > 0 ? message : null
}

async function showCellError(message: string) {
  let detail = message
  try {
    await navigator.clipboard.writeText(message)
    detail = `${message}\n\n(${t('results.cellErrorCopied')})`
  } catch {
    /* clipboard unavailable: show the message anyway */
  }
  toast.add({ severity: 'error', summary: t('results.cellErrorHint'), detail, life: 12000 })
}
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
    >
      <template #body="{ data }">
        <button
          v-if="cellError(data, col.field)"
          type="button"
          class="cell-error"
          :title="cellError(data, col.field) ?? ''"
          @click="showCellError(cellError(data, col.field) as string)"
        >
          {{ data[col.field] }}
        </button>
        <template v-else>{{ data[col.field] }}</template>
      </template>
    </Column>
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
.cell-error {
  all: unset;
  cursor: help;
  color: var(--p-red-400);
  font-weight: 600;
  text-decoration: underline dotted;
}
.cell-error:hover,
.cell-error:focus-visible {
  color: var(--p-red-300);
}
</style>
