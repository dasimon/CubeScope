<script setup lang="ts">
// Panneau résultats : grille, erreur ou état vide.
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Button from 'primevue/button'
import ResultsGrid from './ResultsGrid.vue'
import { store } from '../store'
import { toCsv, toTsv, downloadCsv, copyToClipboard } from '../exportResults'

const { t } = useI18n()
const toast = useToast()

function exportCsv() {
  if (!store.result) return
  downloadCsv('cubescope-resultats.csv', toCsv(store.result.columns, store.result.rows))
}

async function copyResults() {
  if (!store.result) return
  try {
    await copyToClipboard(toTsv(store.result.columns, store.result.rows))
    toast.add({ severity: 'success', summary: t('results.copied'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: t('results.copyFailed'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}
</script>

<template>
  <div class="results-panel">
    <div v-if="store.running" class="results-center">
      <ProgressSpinner style="width: 40px; height: 40px" />
      <span>{{ t('results.running') }}</span>
    </div>
    <Message v-else-if="store.queryError" severity="error" class="results-error">
      {{ store.queryError }}
    </Message>
    <template v-else-if="store.result">
      <div class="results-toolbar">
        <Button
          :label="t('results.exportCsv')"
          icon="pi pi-download"
          text
          size="small"
          @click="exportCsv"
        />
        <Button
          :label="t('results.copy')"
          icon="pi pi-copy"
          text
          size="small"
          @click="copyResults"
        />
      </div>
      <div class="results-grid-wrapper">
        <ResultsGrid :columns="store.result.columns" :rows="store.result.rows" />
      </div>
    </template>
    <div v-else class="results-center results-hint">
      {{ t('results.hint') }}
    </div>
  </div>
</template>

<style scoped>
.results-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.results-center {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
}
.results-hint {
  color: var(--p-text-muted-color);
}
.results-error {
  margin: 1rem;
  white-space: pre-wrap;
}
.results-toolbar {
  flex: 0 0 auto;
  display: flex;
  gap: 0.25rem;
  padding: 0.25rem 0.5rem;
  border-bottom: 1px solid var(--p-content-border-color);
}
.results-grid-wrapper {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
</style>
