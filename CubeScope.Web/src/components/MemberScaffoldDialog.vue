<script setup lang="ts">
// Squelette de membre calculé : dialogue ouvert depuis la barre d'outils (voir App.vue),
// génère un WITH MEMBER (requête) ou CREATE MEMBER (script) et l'insère au curseur de
// l'éditeur via actions.requestInsert — même mécanisme que l'explorateur et les snippets.
// Ne nécessite aucune connexion (store.cubeMeta reste optionnel, non utilisé pour le MVP).
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import { actions } from '../store'
import { generateMemberScaffold, type MemberScaffoldType } from '../memberScaffold'

const { t } = useI18n()

const visible = ref(false)
const name = ref('')
const type = ref<MemberScaffoldType>('with')
const formatString = ref('')
const displayFolder = ref('')

const typeOptions = computed(() => [
  { label: t('member.with'), value: 'with' as MemberScaffoldType },
  { label: t('member.create'), value: 'create' as MemberScaffoldType },
])

const preview = computed(() =>
  generateMemberScaffold({
    name: name.value,
    type: type.value,
    formatString: formatString.value,
    displayFolder: displayFolder.value,
  }),
)

function open() {
  name.value = ''
  type.value = 'with'
  formatString.value = ''
  displayFolder.value = ''
  visible.value = true
}

function insert() {
  if (!preview.value) return
  actions.requestInsert(preview.value)
  visible.value = false
}
</script>

<template>
  <Button icon="pi pi-plus-circle" size="small" severity="secondary" :label="t('member.title')" @click="open" />

  <Dialog v-model:visible="visible" modal :header="t('member.title')" :style="{ width: '32rem' }">
    <div class="member-form">
      <label class="member-field">
        {{ t('member.name') }}
        <InputText v-model="name" :placeholder="t('member.namePlaceholder')" autofocus @keydown.enter="insert" />
      </label>

      <label class="member-field">
        {{ t('member.type') }}
        <SelectButton v-model="type" :options="typeOptions" option-label="label" option-value="value" />
      </label>

      <label class="member-field">
        {{ t('member.formatString') }}
        <InputText v-model="formatString" placeholder="#,##0.00" />
      </label>

      <label class="member-field">
        {{ t('member.displayFolder') }}
        <InputText v-model="displayFolder" />
      </label>

      <div class="member-field">
        {{ t('member.preview') }}
        <pre class="member-preview">{{ preview }}</pre>
      </div>
    </div>

    <template #footer>
      <Button :label="t('common.cancel')" severity="secondary" text @click="visible = false" />
      <Button :label="t('member.insert')" icon="pi pi-check" :disabled="!preview" @click="insert" />
    </template>
  </Dialog>
</template>

<style scoped>
.member-form {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}
.member-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  font-size: 0.9rem;
}
.member-preview {
  margin: 0;
  padding: 0.6rem;
  background: var(--p-surface-800);
  border: 1px solid var(--p-surface-700);
  border-radius: 6px;
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 0.85rem;
  white-space: pre-wrap;
  max-height: 12rem;
  overflow-y: auto;
}
</style>
