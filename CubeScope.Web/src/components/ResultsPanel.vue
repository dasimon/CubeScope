<script setup lang="ts">
// Panneau résultats : grille, erreur ou état vide.
import { useI18n } from 'vue-i18n'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import ResultsGrid from './ResultsGrid.vue'
import { store } from '../store'

const { t } = useI18n()
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
    <ResultsGrid v-else-if="store.result" :columns="store.result.columns" :rows="store.result.rows" />
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
</style>
