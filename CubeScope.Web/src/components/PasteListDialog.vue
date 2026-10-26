<script setup lang="ts">
// Pasted list of keys (ISIN, fund codes…) → members of a chosen level, added to Rows or Filters.
// Splitting and resolution happen server-side; missing keys are shown apart, never dropped
// silently, and only found ones enter the query. Lists can be saved by name (SQLite) and
// reloaded — a reloaded list is resolved again, the cube may have changed.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { useToast } from 'primevue/usetoast'
import { api, type KeyResolution, type NamedList } from '../api'
import { store } from '../store'
import { addMembersTo, type AxisZone } from '../builder'

const props = defineProps<{ zone: AxisZone | 'filters' | null }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const toast = useToast()

const LEVEL_KEY = 'cubescope.pasteLevel'

// Every level that holds members (not (All)), labelled "Dimension › Hierarchy › Level".
const levels = computed(() =>
  (store.cubeMeta?.dimensions ?? []).flatMap((d) =>
    d.hierarchies.flatMap((h) =>
      h.levels
        .filter((l) => !l.isAll)
        .map((l) => ({
          label: [d.name, h.name !== d.name ? h.name : null, l.name !== h.name ? l.name : null]
            .filter(Boolean)
            .join(' › '),
          value: l.uniqueName,
          hierarchy: h.uniqueName,
        })),
    ),
  ),
)

const level = ref<string | null>(null)
const text = ref('')
// The level travels with its result: "Add" must use the level that produced it, not the one
// selected since.
const result = ref<(KeyResolution & { level: string }) | null>(null)
let seq = 0
let abort: AbortController | null = null
const resolving = ref(false)
const error = ref('')
const lists = ref<NamedList[]>([])
const listName = ref('')

/** Drops the check in flight (closing the dialog, a new check): its answer is ignored and the
 *  server stops resolving — 5,000 keys are 100 queries on the shared SSAS session. */
function cancel() {
  seq++
  abort?.abort()
  abort = null
  resolving.value = false
}
onBeforeUnmount(cancel)

// Catalog or cube change while open: a level of the old cube means nothing here.
watch(levels, (ls) => {
  if (level.value && !ls.some((l) => l.value === level.value)) level.value = null
})

watch(
  () => props.zone,
  async (z) => {
    if (!z) {
      cancel()
      return
    }
    result.value = null
    error.value = ''
    let saved: string | null = null
    try {
      saved = localStorage.getItem(LEVEL_KEY)
    } catch {
      /* storage unavailable */
    }
    level.value = levels.value.some((l) => l.value === saved) ? saved : null
    try {
      lists.value = await api.namedLists()
    } catch {
      lists.value = []
    }
  },
)

// A new text or level invalidates the previous verification.
watch([text, level], () => (result.value = null))

async function verify(input: { text: string } | { keys: string[] } = { text: text.value }) {
  if (!store.cube || !level.value) return
  cancel()
  const mine = ++seq
  const forLevel = level.value
  abort = new AbortController()
  resolving.value = true
  error.value = ''
  try {
    const r = await api.resolveKeys(store.cube, forLevel, input, abort.signal)
    if (mine !== seq) return
    result.value = { ...r, level: forLevel }
    try {
      localStorage.setItem(LEVEL_KEY, forLevel)
    } catch {
      /* storage unavailable */
    }
  } catch (e) {
    if (mine === seq && !(e instanceof DOMException && e.name === 'AbortError'))
      error.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (mine === seq) resolving.value = false
  }
}

function add() {
  const r = result.value
  const target = levels.value.find((l) => l.value === r?.level)
  if (!r || !props.zone || !target || !r.found.length) return
  addMembersTo(
    props.zone,
    target.hierarchy,
    r.found.map((f) => ({ uniqueName: f.uniqueName, caption: f.caption || f.key })),
  )
  emit('close')
}

async function save() {
  const r = result.value
  if (!r || !listName.value.trim() || !r.keys.length) return
  try {
    // Every parsed key in pasted order, found or not: a saved list is resolved again on reload.
    await api.saveNamedList(listName.value.trim(), r.level, r.keys)
    toast.add({ severity: 'success', summary: t('pasteList.saved', { name: listName.value.trim() }), life: 3000 })
    listName.value = ''
    lists.value = await api.namedLists()
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}

async function load(list: NamedList) {
  if (!levels.value.some((l) => l.value === list.level)) {
    toast.add({ severity: 'warn', summary: t('pasteList.levelGone', { level: list.level }), life: 6000 })
    return
  }
  level.value = list.level
  text.value = list.keys.join('\n')
  await verify({ keys: list.keys })
}

async function remove(list: NamedList) {
  try {
    await api.deleteNamedList(list.id)
    lists.value = lists.value.filter((l) => l.id !== list.id)
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}

async function copyMissing() {
  try {
    await navigator.clipboard.writeText(result.value?.missing.join('\n') ?? '')
    toast.add({ severity: 'success', summary: t('pasteList.missingCopied'), life: 3000 })
  } catch {
    /* clipboard unavailable */
  }
}
</script>

<template>
  <Dialog
    :visible="zone != null"
    modal
    :header="t('pasteList.title', { zone: zone ? t(`builder.zone.${zone}`) : '' })"
    :style="{ width: '40rem' }"
    @update:visible="(v: boolean) => !v && emit('close')"
  >
    <div class="paste">
      <div v-if="lists.length" class="paste-lists">
        <span class="paste-label">{{ t('pasteList.savedLists') }}</span>
        <div v-for="l in lists" :key="l.id" class="paste-list">
          <Button
            :label="`${l.name} (${l.keys.length})`"
            :title="l.level"
            size="small"
            text
            :disabled="resolving"
            @click="load(l)"
          />
          <Button
            icon="pi pi-trash"
            size="small"
            text
            severity="secondary"
            :aria-label="t('pasteList.delete')"
            :title="t('pasteList.delete')"
            @click="remove(l)"
          />
        </div>
      </div>

      <label class="paste-label" for="paste-level">{{ t('pasteList.level') }}</label>
      <Select
        v-model="level"
        input-id="paste-level"
        :options="levels"
        option-label="label"
        option-value="value"
        filter
        size="small"
        :placeholder="t('pasteList.levelPlaceholder')"
      />
      <label class="paste-label" for="paste-text">{{ t('pasteList.keys') }}</label>
      <span class="paste-hint">{{ t('pasteList.dateHint') }}</span>
      <Textarea id="paste-text" v-model="text" rows="8" class="paste-text" :placeholder="t('pasteList.keysPlaceholder')" />

      <p v-if="error" class="paste-error">{{ error }}</p>
      <div v-if="result" class="paste-result">
        <span class="paste-found">{{ t('pasteList.found', { n: result.found.length }) }}</span>
        <template v-if="result.missing.length">
          <span class="paste-missing">
            {{ t('pasteList.missing', { n: result.missing.length }) }}
            <Button icon="pi pi-copy" size="small" text :aria-label="t('pasteList.copyMissing')"
              :title="t('pasteList.copyMissing')" @click="copyMissing" />
          </span>
          <pre class="paste-missing-list">{{ result.missing.join('\n') }}</pre>
        </template>
        <template v-if="result.failed.length">
          <span class="paste-failed">{{ t('pasteList.failed', { n: result.failed.length }) }}</span>
          <pre class="paste-missing-list">{{ result.failed.join('\n') }}</pre>
        </template>
        <div class="paste-save">
          <InputText v-model="listName" size="small" :placeholder="t('pasteList.namePlaceholder')" />
          <Button :label="t('pasteList.save')" icon="pi pi-save" size="small" outlined
            :disabled="!listName.trim()" @click="save" />
        </div>
      </div>
    </div>
    <template #footer>
      <Button :label="t('common.cancel')" severity="secondary" text @click="emit('close')" />
      <Button
        :label="t('pasteList.verify')"
        icon="pi pi-search"
        outlined
        :loading="resolving"
        :disabled="!level || !text.trim()"
        @click="verify()"
      />
      <Button
        :label="t('pasteList.add', { n: result?.found.length ?? 0 })"
        icon="pi pi-check"
        :disabled="!result?.found.length"
        @click="add"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.paste {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}
.paste-label {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--p-text-muted-color);
}
.paste-lists {
  display: flex;
  flex-direction: column;
  max-height: 8rem;
  overflow: auto;
  margin-bottom: 0.4rem;
}
.paste-list {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.paste-text {
  font-family: var(--font-mono, monospace);
  font-size: 0.8rem;
}
.paste-error {
  color: var(--p-red-400);
  font-size: 0.85rem;
}
.paste-result {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}
.paste-found {
  color: var(--p-green-400);
  font-weight: 600;
}
.paste-missing {
  color: var(--p-orange-400);
  font-weight: 600;
  display: flex;
  align-items: center;
}
.paste-failed {
  color: var(--p-red-400);
  font-weight: 600;
}
.paste-hint {
  font-size: 0.75rem;
  color: var(--p-text-muted-color);
}
.paste-missing-list {
  margin: 0;
  max-height: 7rem;
  overflow: auto;
  font-size: 0.78rem;
  background: var(--p-surface-900);
  padding: 0.3rem 0.5rem;
  border-radius: 0.3rem;
}
.paste-save {
  display: flex;
  gap: 0.4rem;
  margin-top: 0.3rem;
}
</style>
