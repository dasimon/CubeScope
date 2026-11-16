<script setup lang="ts">
// AI panel: explain / optimize / anti-patterns / format the current MDX,
// with the cube metadata injected on the server side. Markdown rendering (marked + DOMPurify).
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { renderMarkdown } from '../markdown'
import type { AiAction } from '../api'
import { actions, store } from '../store'

const { t } = useI18n()
const question = ref('')

const ACTIONS: { action: AiAction; labelKey: string; icon: string }[] = [
  { action: 'expliquer', labelKey: 'ai.explain', icon: 'pi pi-question-circle' },
  { action: 'optimiser', labelKey: 'ai.optimize', icon: 'pi pi-bolt' },
  { action: 'antipatterns', labelKey: 'ai.antipatterns', icon: 'pi pi-exclamation-triangle' },
  { action: 'formater', labelKey: 'ai.format', icon: 'pi pi-align-left' },
]

onMounted(() => void actions.loadAiStatus())

const resultHtml = computed(() => (store.aiResult ? renderMarkdown(store.aiResult) : ''))

// A ```mdx block in the response → can be offered to the editor
const hasApplicableMdx = computed(() => /```(mdx)?\s*\n[\s\S]*?```/i.test(store.aiResult))

// Name of the environment variable read for the Anthropic key (the key itself is never sent
// here nor stored). Open by itself while the key is missing, otherwise behind the key button.
const DEFAULT_KEY_VARIABLE = 'ANTHROPIC_API_KEY'
const showKey = ref(false)
const keyName = ref('')
const keyError = ref('')
const keyNote = ref('')
const keySaving = ref(false)
watch(
  () => store.aiKeyVariable,
  (v) => (keyName.value = v),
  { immediate: true },
)
const keyOpen = computed(() => showKey.value || store.aiConfigured === false)
const keyChanged = computed(() => (keyName.value.trim() || DEFAULT_KEY_VARIABLE) !== store.aiKeyVariable)

function toggleKey(): void {
  showKey.value = !showKey.value
  keyName.value = store.aiKeyVariable
  keyError.value = ''
  keyNote.value = ''
}

async function saveKey(): Promise<void> {
  if (!keyChanged.value || keySaving.value) return
  // Stay open once saved: a key now found would otherwise close the setting before its note shows.
  showKey.value = true
  keySaving.value = true
  keyError.value = ''
  keyNote.value = ''
  try {
    await actions.setAiKeyVariable(keyName.value.trim())
    keyNote.value = t('ai.keySaved', { var: store.aiKeyVariable })
  } catch (e) {
    keyError.value = e instanceof Error ? e.message : String(e)
  } finally {
    keySaving.value = false
  }
}

const runningLabel = computed(() => {
  const a = ACTIONS.find((x) => x.action === store.aiAction)
  return a ? t('ai.running', { action: t(a.labelKey), model: store.aiModel }) : t('ai.analyzing')
})
</script>

<template>
  <div class="ai-panel">
    <Message v-if="store.aiConfigured === false" severity="warn" class="ai-msg">
      {{ t('ai.keyMissing', { var: store.aiKeyVariable }) }}
    </Message>

    <div class="ai-nl">
      <InputText
        v-model="question"
        :placeholder="t('ai.nlPlaceholder')"
        size="small"
        class="ai-nl-input"
        :disabled="store.aiRunning || store.aiConfigured === false || !store.cube"
        @keydown.enter="actions.generateMdx(question)"
      />
      <Button
        :label="t('ai.generate')"
        icon="pi pi-sparkles"
        size="small"
        :disabled="store.aiRunning || store.aiConfigured === false || !store.cube || !question.trim()"
        @click="actions.generateMdx(question)"
      />
    </div>

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
      <Button
        :label="t('ai.optimizeProfile')"
        icon="pi pi-gauge"
        size="small"
        severity="secondary"
        :disabled="store.aiRunning || store.aiConfigured === false || !store.profile || !store.profileMdx"
        :title="!store.profile || !store.profileMdx ? t('ai.needProfile') : t('ai.optimizeProfile')"
        @click="actions.runAiOptimizeProfile()"
      />
      <span class="ai-spacer" />
      <Button
        icon="pi pi-key"
        size="small"
        severity="secondary"
        text
        :class="{ 'ai-key-on': keyOpen }"
        :title="t('ai.keySettings')"
        :aria-label="t('ai.keySettings')"
        :aria-expanded="keyOpen"
        @click="toggleKey"
      />
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

    <div v-if="keyOpen" class="ai-key">
      <label for="ai-key-variable">{{ t('ai.keyLabel') }}</label>
      <div class="ai-key-row">
        <InputText
          id="ai-key-variable"
          v-model="keyName"
          size="small"
          class="mono ai-key-input"
          spellcheck="false"
          autocomplete="off"
          :placeholder="DEFAULT_KEY_VARIABLE"
          @keydown.enter="saveKey"
        />
        <Button
          :label="t('ai.keySave')"
          size="small"
          :disabled="!keyChanged"
          :loading="keySaving"
          @click="saveKey"
        />
      </div>
      <small v-if="keyError" class="ai-key-error">{{ keyError }}</small>
      <small v-else class="ai-key-help">{{ keyNote || t('ai.keyHelp') }}</small>
    </div>

    <div class="ai-body">
      <div v-if="store.aiRunning" class="ai-center">
        <ProgressSpinner style="width: 40px; height: 40px" />
        <span>{{ runningLabel }}</span>
      </div>
      <Message v-else-if="store.aiError" severity="error" class="ai-msg">{{ store.aiError }}</Message>
      <template v-else-if="store.aiResult">
        <div class="ai-result" v-html="resultHtml" />
        <div class="ai-footer">{{ t('ai.footer', { model: store.aiModel, s: (store.aiDurationMs / 1000).toFixed(1) }) }}</div>
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
.ai-nl {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.5rem 0.5rem 0;
}
.ai-nl-input {
  flex: 1;
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
.ai-key {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  margin: 0 0.5rem 0.4rem;
  padding: 0.5rem 0.6rem;
  border-left: 2px solid var(--p-primary-color);
  background: var(--p-content-hover-background);
  font-size: 0.85rem;
}
.ai-key-row {
  display: flex;
  gap: 0.35rem;
}
.ai-key-input {
  flex: 1;
  min-width: 0;
}
.ai-key-help {
  color: var(--p-text-muted-color);
}
.ai-key-error {
  color: var(--p-red-400);
}
.ai-key-on {
  color: var(--p-primary-color);
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
