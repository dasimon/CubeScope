<script setup lang="ts">
// Grille de résultats v1 : PrimeVue DataTable virtualisé, derrière une interface
// columns/rows volontairement minimale — si les crossjoins larges rament un jour,
// on branche AG Grid ici sans toucher au reste (décision actée).
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

// Une cellule en erreur porte son message sous une clé jumelle "<field>__err"
// (posée par CellSetMapper) : le serveur SSAS a renvoyé un <Error><Description>.
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
    /* presse-papiers indisponible : on affiche quand même le message */
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
