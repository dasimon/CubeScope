<script setup lang="ts">
// Harnais de non-régression MDX : enregistrer la requête courante + son résultat comme cas de
// référence (baseline), puis « Tout exécuter » pour relancer et signaler toute valeur changée.
// Dialogue ouvert depuis la barre d'outils (voir App.vue).
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { useToast } from 'primevue/usetoast'
import { api, type RegressionCase, type RegressionRunResult } from '../api'
import { store } from '../store'

const { t } = useI18n()
const toast = useToast()

const visible = ref(false)
const cases = ref<RegressionCase[]>([])
const runResults = ref<RegressionRunResult[]>([])
const running = ref(false)
const newName = ref('')

const passCount = computed(() => runResults.value.filter((r) => r.match).length)

async function loadCases() {
  try {
    cases.value = await api.regressionList()
  } catch {
    cases.value = []
  }
}

function open() {
  visible.value = true
  runResults.value = []
  newName.value = ''
  void loadCases()
}

async function saveCase() {
  const name = newName.value.trim()
  if (!name || !store.result) return
  try {
    await api.regressionSave(name, store.mdx, store.result)
    newName.value = ''
    await loadCases()
    toast.add({ severity: 'success', summary: t('regression.saved'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}

async function remove(id: number) {
  try {
    await api.regressionDelete(id)
    await loadCases()
    runResults.value = runResults.value.filter((r) => r.id !== id)
  } catch (e) {
    toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}

async function runAll() {
  running.value = true
  runResults.value = []
  try {
    runResults.value = await api.regressionRun()
  } catch (e) {
    toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  } finally {
    running.value = false
  }
}

function firstLine(mdx: string) {
  return mdx.split('\n')[0].trim()
}
</script>

<template>
  <Button icon="pi pi-check-square" size="small" severity="secondary" :label="t('regression.title')" @click="open" />

  <Dialog v-model:visible="visible" modal :header="t('regression.title')" :style="{ width: '44rem' }">
    <div class="reg-save">
      <InputText
        v-model="newName"
        :placeholder="t('regression.name')"
        :disabled="!store.result"
        class="reg-name-input"
        @keydown.enter="saveCase"
      />
      <Button
        :label="t('regression.saveCase')"
        icon="pi pi-plus"
        size="small"
        :disabled="!store.result || !newName.trim()"
        @click="saveCase"
      />
    </div>
    <small v-if="!store.result" class="reg-hint">{{ t('regression.needResult') }}</small>

    <div class="reg-cases">
      <div v-if="cases.length === 0" class="reg-empty">{{ t('regression.empty') }}</div>
      <ul v-else class="reg-list">
        <li v-for="c in cases" :key="c.id" class="reg-row">
          <span class="reg-name">{{ c.name }}</span>
          <span class="reg-mdx">{{ firstLine(c.mdx) }}</span>
          <Button icon="pi pi-trash" size="small" text severity="danger" :title="t('regression.delete')" @click="remove(c.id)" />
        </li>
      </ul>
    </div>

    <div class="reg-runbar">
      <Button
        :label="t('regression.runAll')"
        icon="pi pi-play"
        size="small"
        :loading="running"
        :disabled="cases.length === 0 || !store.connected || !store.catalog"
        @click="runAll"
      />
      <span v-if="runResults.length" class="reg-summary">
        {{ t('regression.summary', { pass: passCount, total: runResults.length }) }}
      </span>
    </div>
    <small v-if="!store.connected || !store.catalog" class="reg-hint">{{ t('status.notConnected') }}</small>

    <ul v-if="runResults.length" class="reg-results">
      <li v-for="r in runResults" :key="r.id" :class="['reg-result', r.match ? 'ok' : 'ko']">
        <div class="reg-result-head">
          <i :class="r.match ? 'pi pi-check-circle' : 'pi pi-times-circle'" />
          <span class="reg-name">{{ r.name }}</span>
          <span class="reg-badge">{{ r.match ? t('regression.pass') : t('regression.fail') }}</span>
        </div>
        <div v-if="!r.match" class="reg-detail">
          <span v-if="r.summary" class="reg-summary-text">{{ r.summary }}</span>
          <ul v-if="r.diffs.length" class="reg-diffs">
            <li v-for="(d, i) in r.diffs.slice(0, 8)" :key="i">
              [{{ d.row }}] {{ d.column }} : {{ d.expected }} → {{ d.actual }}
            </li>
          </ul>
        </div>
      </li>
    </ul>

    <template #footer>
      <Button :label="t('common.cancel')" severity="secondary" text @click="visible = false" />
    </template>
  </Dialog>
</template>

<style scoped>
.reg-save {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
.reg-name-input {
  flex: 1;
}
.reg-hint {
  display: block;
  margin-top: 0.35rem;
  color: var(--p-text-muted-color);
}
.reg-cases {
  margin: 0.75rem 0;
}
.reg-empty {
  color: var(--p-text-muted-color);
  font-size: 0.85rem;
  padding: 0.25rem 0;
}
.reg-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 12rem;
  overflow-y: auto;
}
.reg-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.35rem 0.5rem;
  border-radius: 6px;
}
.reg-row:hover {
  background: var(--p-surface-700);
}
.reg-name {
  font-size: 0.9rem;
  font-weight: 600;
  white-space: nowrap;
}
.reg-mdx {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: monospace;
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
}
.reg-runbar {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  border-top: 1px solid var(--p-surface-700);
  padding-top: 0.75rem;
}
.reg-summary {
  font-weight: 600;
}
.reg-results {
  list-style: none;
  margin: 0.75rem 0 0;
  padding: 0;
  max-height: 16rem;
  overflow-y: auto;
}
.reg-result {
  padding: 0.4rem 0.5rem;
  border-radius: 6px;
  margin-bottom: 0.35rem;
}
.reg-result.ok {
  background: color-mix(in srgb, var(--p-primary-color) 12%, transparent);
}
.reg-result.ko {
  background: color-mix(in srgb, #f87171 16%, transparent);
}
.reg-result-head {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
.reg-badge {
  margin-left: auto;
  font-size: 0.75rem;
  font-weight: 700;
}
.reg-detail {
  margin-top: 0.35rem;
  padding-left: 1.5rem;
  font-size: 0.8rem;
}
.reg-summary-text {
  color: var(--p-text-muted-color);
}
.reg-diffs {
  list-style: none;
  margin: 0.25rem 0 0;
  padding: 0;
  font-family: monospace;
}
</style>
