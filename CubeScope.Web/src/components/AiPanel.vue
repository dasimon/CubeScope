<script setup lang="ts">
// Panneau IA : expliquer / optimiser / anti-patterns / formater le MDX courant,
// avec les métadonnées du cube injectées côté serveur. Rendu Markdown (marked).
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { marked } from 'marked'
import type { AiAction } from '../api'
import { actions, store } from '../store'

const { t } = useI18n()

const ACTIONS: { action: AiAction; labelKey: string; icon: string }[] = [
  { action: 'expliquer', labelKey: 'ai.explain', icon: 'pi pi-question-circle' },
  { action: 'optimiser', labelKey: 'ai.optimize', icon: 'pi pi-bolt' },
  { action: 'antipatterns', labelKey: 'ai.antipatterns', icon: 'pi pi-exclamation-triangle' },
  { action: 'formater', labelKey: 'ai.format', icon: 'pi pi-align-left' },
]

onMounted(() => void actions.loadAiStatus())

const resultHtml = computed(() => (store.aiResult ? marked.parse(store.aiResult) : ''))

// Un bloc ```mdx dans la réponse → proposable à l'éditeur
const hasApplicableMdx = computed(() => /```(mdx)?\s*\n[\s\S]*?```/i.test(store.aiResult))

const runningLabel = computed(() => {
  const a = ACTIONS.find((x) => x.action === store.aiAction)
  return a ? t('ai.running', { action: t(a.labelKey) }) : t('ai.analyzing')
})
</script>

<template>
  <div class="ai-panel">
    <Message v-if="store.aiConfigured === false" severity="warn" class="ai-msg">
      {{ t('ai.keyMissing', { var: 'ANTHROPIC_API_KEY' }) }}
    </Message>

    <div class="ai-toolbar">
      <Button
        v-for="a in ACTIONS"
        :key="a.action"
        :label="t(a.labelKey)"
        :icon="a.icon"
        size="small"
        severity="secondary"
        :disabled="store.aiRunning || store.aiConfigured === false"
        @click="actions.runAi(a.action)"
      />
      <span class="ai-spacer" />
      <Button
        v-if="store.aiRunning"
        :label="t('common.cancel')"
        icon="pi pi-stop"
        size="small"
        severity="danger"
        text
        @click="actions.cancelAi()"
      />
      <Button
        v-else-if="hasApplicableMdx"
        :label="t('ai.apply')"
        icon="pi pi-file-import"
        size="small"
        @click="actions.applyAiMdx()"
      />
    </div>

    <div class="ai-body">
      <div v-if="store.aiRunning" class="ai-center">
        <ProgressSpinner style="width: 40px; height: 40px" />
        <span>{{ runningLabel }}</span>
      </div>
      <Message v-else-if="store.aiError" severity="error" class="ai-msg">{{ store.aiError }}</Message>
      <template v-else-if="store.aiResult">
        <div class="ai-result" v-html="resultHtml" />
        <div class="ai-footer">{{ t('ai.footer', { s: (store.aiDurationMs / 1000).toFixed(1) }) }}</div>
      </template>
      <div v-else class="ai-center ai-hint">
        {{ t('ai.hint') }}
      </div>
    </div>
  </div>
</template>

<style scoped>
.ai-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.ai-toolbar {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.4rem 0.5rem;
  flex-wrap: wrap;
}
.ai-spacer {
  flex: 1;
}
.ai-msg {
  margin: 0.5rem 0.75rem 0;
}
.ai-body {
  flex: 1;
  overflow: auto;
  display: flex;
  flex-direction: column;
}
.ai-center {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
  text-align: center;
  padding: 1rem;
}
.ai-hint {
  color: var(--p-text-muted-color);
}
.ai-result {
  padding: 0.5rem 1rem 1rem;
  font-size: 0.9rem;
  line-height: 1.55;
}
.ai-result :deep(pre) {
  background: var(--p-surface-800);
  border: 1px solid var(--p-surface-700);
  border-radius: 6px;
  padding: 0.6rem 0.8rem;
  overflow-x: auto;
  font-size: 0.85rem;
}
.ai-result :deep(code) {
  font-family: Consolas, monospace;
}
.ai-result :deep(h1),
.ai-result :deep(h2),
.ai-result :deep(h3) {
  font-size: 1rem;
  margin: 0.9rem 0 0.4rem;
}
.ai-result :deep(table) {
  border-collapse: collapse;
}
.ai-result :deep(td),
.ai-result :deep(th) {
  border: 1px solid var(--p-surface-700);
  padding: 0.25rem 0.5rem;
}
.ai-footer {
  padding: 0.25rem 1rem 0.5rem;
  font-size: 0.75rem;
  color: var(--p-text-muted-color);
}
</style>
