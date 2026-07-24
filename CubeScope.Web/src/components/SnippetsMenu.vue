<script setup lang="ts">
// Bibliothèque de snippets MDX locale (SQLite) : enregistrer la requête courante,
// lister/insérer/supprimer. Popover ouvert depuis la barre d'outils (voir App.vue).
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Popover from 'primevue/popover'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { useToast } from 'primevue/usetoast'
import { api, type Snippet } from '../api'
import { actions, store } from '../store'

const { t } = useI18n()
const toast = useToast()

const popover = ref<InstanceType<typeof Popover>>()
const snippets = ref<Snippet[]>([])

async function loadSnippets() {
  try {
    snippets.value = await api.snippets()
  } catch {
    snippets.value = []
  }
}

function toggle(event: Event) {
  popover.value?.toggle(event)
  void loadSnippets()
}

const showSave = ref(false)
const newName = ref('')

function openSave() {
  newName.value = ''
  showSave.value = true
}

async function confirmSave() {
  const name = newName.value.trim()
  if (!name) return
  try {
    await api.addSnippet(name, store.mdx)
    showSave.value = false
    await loadSnippets()
    toast.add({ severity: 'success', summary: t('snippets.saved'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}

function insert(snippet: Snippet, event: Event) {
  actions.requestInsert(snippet.mdx)
  popover.value?.hide()
  toast.add({ severity: 'success', summary: t('snippets.inserted'), detail: snippet.name, life: 2000 })
  void event
}

async function remove(id: number, event: Event) {
  event.stopPropagation()
  try {
    await api.deleteSnippet(id)
    await loadSnippets()
  } catch (e) {
    toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}
</script>

<template>
  <Button icon="pi pi-bookmark" size="small" severity="secondary" :label="t('snippets.title')" @click="toggle" />

  <Popover ref="popover">
    <div class="snippets-popover">
      <Button
        :label="t('snippets.saveCurrent')"
        icon="pi pi-plus"
        size="small"
        text
        class="snippets-save-btn"
        @click="openSave"
      />
      <div v-if="snippets.length === 0" class="snippets-empty">{{ t('snippets.empty') }}</div>
      <ul v-else class="snippets-list">
        <li v-for="s in snippets" :key="s.id" class="snippets-row" @click="insert(s, $event)">
          <span class="snippets-name">{{ s.name }}</span>
          <Button
            icon="pi pi-trash"
            size="small"
            text
            severity="danger"
            :title="t('snippets.deleteTitle')"
            @click="remove(s.id, $event)"
          />
        </li>
      </ul>
    </div>
  </Popover>

  <Dialog v-model:visible="showSave" modal :header="t('snippets.saveCurrent')" :style="{ width: '24rem' }">
    <label class="snippets-name-label">
      {{ t('snippets.name') }}
      <InputText v-model="newName" autofocus @keydown.enter="confirmSave" />
    </label>
    <template #footer>
      <Button :label="t('common.cancel')" severity="secondary" text @click="showSave = false" />
      <Button :label="t('snippets.save')" icon="pi pi-check" :disabled="!newName.trim()" @click="confirmSave" />
    </template>
  </Dialog>
</template>

<style scoped>
.snippets-popover {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  width: 18rem;
  max-height: 20rem;
}
.snippets-save-btn {
  justify-content: flex-start;
}
.snippets-empty {
  color: var(--p-text-muted-color);
  font-size: 0.85rem;
  padding: 0.25rem 0.5rem;
}
.snippets-list {
  list-style: none;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  max-height: 16rem;
}
.snippets-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.35rem 0.5rem;
  border-radius: 6px;
  cursor: pointer;
}
.snippets-row:hover {
  background: var(--p-surface-700);
}
.snippets-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.9rem;
}
.snippets-name-label {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  font-size: 0.9rem;
}
</style>
