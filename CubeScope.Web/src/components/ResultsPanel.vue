<script setup lang="ts">
// Panneau résultats : grille, erreur ou état vide.
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Button from 'primevue/button'
import ResultsGrid from './ResultsGrid.vue'
import { actions, store } from '../store'
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

function closeTab(e: MouseEvent, id: number) {
  e.stopPropagation()
  actions.closeResult(id)
}
</script>

<template>
  <div class="results-panel">
    <div v-if="store.results.length > 0" class="results-tabs">
      <div
        v-for="tab in store.results"
        :key="tab.id"
        class="results-tab"
        :class="{ active: tab.id === store.activeResultId }"
        @click="actions.selectResult(tab.id)"
      >
        <span class="results-tab-label">{{ tab.label }}</span>
        <button
          type="button"
          class="results-tab-close"
          :title="t('results.tabClose')"
          :aria-label="t('results.tabClose')"
          @click="closeTab($event, tab.id)"
        >
          <i class="pi pi-times" />
        </button>
      </div>
    </div>
    <div v-if="store.running" class="results-center">
      <ProgressSpinner style="width: 40px; height: 40px" />
      <span>{{ store.selectedMdx.trim() ? t('results.runningSelection') : t('results.running') }}</span>
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
.results-tabs {
  flex: 0 0 auto;
  display: flex;
  gap: 0.125rem;
  padding: 0.25rem 0.5rem 0;
  border-bottom: 1px solid var(--p-content-border-color);
  overflow-x: auto;
}
.results-tab {
  display: flex;
  align-items: center;
  gap: 0.375rem;
  padding: 0.25rem 0.5rem;
  border: 1px solid var(--p-content-border-color);
  border-bottom: none;
  border-radius: 4px 4px 0 0;
  cursor: pointer;
  color: var(--p-text-muted-color);
  white-space: nowrap;
  font-size: 0.85rem;
}
.results-tab.active {
  color: var(--p-text-color);
  background: var(--p-content-background);
  border-color: var(--p-content-border-color);
}
.results-tab-close {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: none;
  background: transparent;
  color: inherit;
  cursor: pointer;
  padding: 0;
  width: 1rem;
  height: 1rem;
  font-size: 0.7rem;
}
.results-tab-close:hover,
.results-tab-close:focus-visible {
  color: var(--p-red-500, #ef4444);
}
.results-grid-wrapper {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
</style>
