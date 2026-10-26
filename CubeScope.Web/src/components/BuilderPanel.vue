<script setup lang="ts">
// Graphical query builder: drop measures, hierarchies, levels and members from the explorer
// into four zones. Each change regenerates the MDX server-side and writes it into the editor
// (undoable with Ctrl+Z there); F5 runs it as usual. One direction only: the builder never
// reads back what is typed in the editor.
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import RadioButton from 'primevue/radiobutton'
import Select from 'primevue/select'
import { useToast } from 'primevue/usetoast'
import { api } from '../api'
import { store } from '../store'
import {
  ITEM_MIME,
  builder,
  clearBuilder,
  dropItem,
  findHierarchy,
  hasQuery,
  hierarchyCaption,
  isEmpty,
  levelCaption,
  toModel,
  type AxisEntry,
  type AxisZone,
  type DragItem,
  type FilterEntry,
  type Picked,
} from '../builder'
import MemberPickerDialog from './MemberPickerDialog.vue'
import PasteListDialog from './PasteListDialog.vue'

const { t } = useI18n()
const toast = useToast()

type Zone = 'measures' | AxisZone | 'filters'
const ZONES: Zone[] = ['measures', 'columns', 'rows', 'filters']

// --- MDX generation (server-side), written into the editor ------------------------------
const error = ref('')
let seq = 0
// Last MDX written by the builder: if the editor holds something else, the user typed or loaded
// it — it is still replaced (the builder writes the editor), but the user is told how to get it back.
let lastGenerated: string | null = null
let timer: ReturnType<typeof setTimeout> | undefined

watch(builder, () => {
  clearTimeout(timer)
  timer = setTimeout(() => void generate(), 250)
}, { deep: true })

async function generate() {
  const mySeq = ++seq
  // Emptying the builder (or a context change) leaves the editor alone, and so do filters alone
  // (a filter waiting for its members would otherwise write `SELECT FROM [cube]`).
  if (!store.cube || !hasQuery()) {
    error.value = ''
    return
  }
  try {
    const { mdx } = await api.builderMdx(toModel(store.cube))
    if (mySeq !== seq) return
    error.value = ''
    if (store.mdx !== mdx) {
      if (store.mdx !== lastGenerated && store.mdx.trim())
        toast.add({ severity: 'info', summary: t('builder.replaced'), life: 5000 })
      store.mdx = mdx
      store.mdxRevision++
    }
    lastGenerated = mdx
  } catch (e) {
    if (mySeq === seq) error.value = e instanceof Error ? e.message : String(e)
  }
}

// --- Drop -------------------------------------------------------------------------------
const over = ref<Zone | null>(null)

function onDragOver(e: DragEvent, zone: Zone) {
  // getData is unreadable during dragover: the type list is.
  if (!e.dataTransfer?.types.includes(ITEM_MIME)) return
  e.preventDefault()
  e.dataTransfer.dropEffect = 'copy'
  over.value = zone
}

function onDrop(e: DragEvent, zone: Zone) {
  over.value = null
  const raw = e.dataTransfer?.getData(ITEM_MIME)
  if (!raw) return
  e.preventDefault()
  let item: DragItem
  try {
    item = JSON.parse(raw) as DragItem
  } catch {
    return
  }
  const incomplete = dropItem(zone, item)
  if (incomplete) openFilterPicker(incomplete)
}

// --- Pasted list of keys (Rows, Filters) -------------------------------------------------
const pasteZone = ref<AxisZone | 'filters' | null>(null)

// --- Member picker (axis entries in "members" mode, filters) ------------------------------
const picker = ref<{ hierarchy: string; initial: Picked[]; apply: (m: Picked[]) => void } | null>(null)

function openFilterPicker(f: FilterEntry) {
  picker.value = { hierarchy: f.hierarchy, initial: f.members, apply: (m) => (f.members = m) }
}

function onPicked(members: Picked[]) {
  picker.value?.apply(members)
  picker.value = null
}

// --- Axis entry options -----------------------------------------------------------------
interface Draft {
  zone: AxisZone
  index: number
  mode: 'level' | 'members'
  level: string | null
  members: Picked[]
  rankMode: 'none' | 'order' | 'top'
  measure: string | null
  topCount: number | null
  descending: boolean
}
const draft = ref<Draft | null>(null)

function editEntry(zone: AxisZone, index: number) {
  const e = builder[zone][index]
  draft.value = {
    zone,
    index,
    mode: e.members.length ? 'members' : 'level',
    level: e.level,
    members: e.members.map((m) => ({ ...m })),
    rankMode: !e.rank ? 'none' : e.rank.topCount != null ? 'top' : 'order',
    measure: e.rank?.measure ?? builder.measures[0]?.uniqueName ?? null,
    topCount: e.rank?.topCount ?? 10,
    descending: e.rank?.descending ?? true,
  }
}

const draftLevels = computed(() => {
  const d = draft.value
  if (!d) return []
  const h = findHierarchy(builder[d.zone][d.index]?.hierarchy ?? '')
  return [...(h?.levels ?? [])].sort((a, b) => a.number - b.number)
})

const allMeasures = computed(() =>
  (store.cubeMeta?.measureFolders ?? []).flatMap((f) => f.measures).map((m) => ({ label: m.name, value: m.uniqueName })))

const draftValid = computed(() => {
  const d = draft.value
  if (!d) return false
  if (d.mode === 'level' ? !d.level : !d.members.length) return false
  if (d.rankMode !== 'none' && !d.measure) return false
  return d.rankMode !== 'top' || (d.topCount ?? 0) > 0
})

function applyDraft() {
  const d = draft.value
  if (!d || !draftValid.value) return
  const e = builder[d.zone][d.index]
  if (!e) {
    draft.value = null
    return
  }
  e.level = d.level
  e.members = d.mode === 'members' ? d.members : []
  e.rank = d.rankMode === 'none' || !d.measure
    ? null
    : { measure: d.measure, topCount: d.rankMode === 'top' ? d.topCount : null, descending: d.descending }
  draft.value = null
}

function pickDraftMembers() {
  const d = draft.value
  if (!d) return
  picker.value = {
    hierarchy: builder[d.zone][d.index].hierarchy,
    initial: d.members,
    apply: (m) => {
      d.members = m
      if (m.length) d.mode = 'members'
    },
  }
}

// PrimeVue InputNumber only updates v-model on blur (see CLAUDE.md): read the typed value.
function onTopInput(e: { value: string | number | undefined | null }) {
  if (draft.value) draft.value.topCount = typeof e.value === 'number' ? e.value : null
}

// --- Display ----------------------------------------------------------------------------
function describe(e: AxisEntry): string {
  const what = e.members.length
    ? t('builder.membersCount', { n: e.members.length })
    : levelCaption(e.level)
  if (!e.rank) return what
  const measure = allMeasures.value.find((m) => m.value === e.rank!.measure)?.label ?? e.rank.measure
  return e.rank.topCount != null
    ? `${what} · ${t('builder.topSummary', { n: e.rank.topCount, measure })}`
    : `${what} · ${t('builder.orderSummary', { measure })}`
}

// Bounded: a pasted list can hold thousands of members.
const TITLE_LINES = 20
function membersTitle(members: Picked[]): string {
  const shown = members.slice(0, TITLE_LINES).map((m) => m.caption)
  if (members.length > TITLE_LINES) shown.push(t('builder.moreInTitle', { n: members.length - TITLE_LINES }))
  return shown.join('\n')
}

function remove(zone: Zone, index: number) {
  if (zone === 'measures') builder.measures.splice(index, 1)
  else if (zone === 'filters') builder.filters.splice(index, 1)
  else builder[zone].splice(index, 1)
}
</script>

<template>
  <div class="builder">
    <div class="builder-bar">
      <span class="builder-cube">{{ store.cube ?? '—' }}</span>
      <Button
        icon="pi pi-trash"
        size="small"
        text
        :title="t('builder.clear')"
        :aria-label="t('builder.clear')"
        :disabled="isEmpty()"
        @click="clearBuilder()"
      />
    </div>
    <Message v-if="error" severity="warn" class="builder-msg" :closable="false">{{ error }}</Message>
    <p v-if="!store.cubeMeta" class="builder-hint">{{ t('builder.noCube') }}</p>

    <div class="builder-zones">
      <section
        v-for="zone in ZONES"
        :key="zone"
        class="zone"
        :class="{ over: over === zone }"
        @dragover="onDragOver($event, zone)"
        @dragleave="over = null"
        @drop="onDrop($event, zone)"
      >
        <header class="zone-head">
          <span class="zone-title">{{ t(`builder.zone.${zone}`) }}</span>
          <Button
            v-if="zone === 'rows' || zone === 'filters'"
            icon="pi pi-clipboard"
            text
            size="small"
            class="zone-paste"
            :title="t('pasteList.button')"
            :aria-label="t('pasteList.button')"
            :disabled="!store.cubeMeta"
            @click="pasteZone = zone"
          />
          <label v-if="zone === 'columns' || zone === 'rows'" class="zone-nonempty">
            <Checkbox
              v-model="builder[zone === 'columns' ? 'nonEmptyColumns' : 'nonEmptyRows']"
              binary
              size="small"
            />
            NON EMPTY
          </label>
        </header>

        <p
          v-if="zone === 'measures' ? !builder.measures.length : !builder[zone].length"
          class="zone-empty"
        >
          {{ t(`builder.drop.${zone}`) }}
        </p>

        <template v-if="zone === 'measures'">
          <div v-for="(m, i) in builder.measures" :key="m.uniqueName" class="entry" :title="m.uniqueName">
            <i class="pi pi-calculator" />
            <span class="entry-label">{{ m.caption }}</span>
            <Button icon="pi pi-times" text size="small" :aria-label="t('builder.remove')" @click="remove(zone, i)" />
          </div>
        </template>

        <template v-else-if="zone === 'filters'">
          <div
            v-for="(f, i) in builder.filters"
            :key="f.hierarchy"
            class="entry"
            :class="{ incomplete: !f.members.length }"
            :title="membersTitle(f.members)"
          >
            <i class="pi pi-filter" />
            <span class="entry-label">
              {{ hierarchyCaption(f.hierarchy) }}
              <small>{{ f.members.length === 1 ? f.members[0].caption : f.members.length
                ? t('builder.membersCount', { n: f.members.length }) : t('builder.chooseMembers') }}</small>
            </span>
            <Button icon="pi pi-list-check" text size="small" :aria-label="t('builder.chooseMembers')"
              :title="t('builder.chooseMembers')" @click="openFilterPicker(f)" />
            <Button icon="pi pi-times" text size="small" :aria-label="t('builder.remove')" @click="remove(zone, i)" />
          </div>
        </template>

        <template v-else>
          <div v-for="(e, i) in builder[zone]" :key="e.hierarchy" class="entry" :title="membersTitle(e.members)">
            <i class="pi pi-sitemap" />
            <span class="entry-label">
              {{ hierarchyCaption(e.hierarchy) }}
              <small>{{ describe(e) }}</small>
            </span>
            <Button icon="pi pi-cog" text size="small" :aria-label="t('builder.options')"
              :title="t('builder.options')" @click="editEntry(zone, i)" />
            <Button icon="pi pi-times" text size="small" :aria-label="t('builder.remove')" @click="remove(zone, i)" />
          </div>
        </template>
      </section>
    </div>

    <Dialog
      :visible="draft != null"
      modal
      :header="draft ? hierarchyCaption(builder[draft.zone][draft.index]?.hierarchy ?? '') : ''"
      :style="{ width: '28rem' }"
      @update:visible="(v: boolean) => !v && (draft = null)"
    >
      <div v-if="draft" class="options">
        <label class="opt-row">
          <RadioButton v-model="draft.mode" value="level" />
          {{ t('builder.wholeLevel') }}
        </label>
        <Select
          v-model="draft.level"
          :options="draftLevels"
          option-label="name"
          option-value="uniqueName"
          size="small"
          class="opt-indent"
          :disabled="draft.mode !== 'level'"
        />
        <label class="opt-row">
          <RadioButton v-model="draft.mode" value="members" />
          {{ t('builder.chosenMembers') }}
        </label>
        <div class="opt-indent opt-members">
          <Button :label="t('builder.chooseMembers')" icon="pi pi-list-check" size="small" outlined
            @click="pickDraftMembers" />
          <span class="opt-count">{{ t('builder.membersCount', { n: draft.members.length }) }}</span>
        </div>

        <span class="opt-title">{{ t('builder.rank') }}</span>
        <div class="opt-row">
          <label class="opt-row"><RadioButton v-model="draft.rankMode" value="none" /> {{ t('builder.rankNone') }}</label>
          <label class="opt-row"><RadioButton v-model="draft.rankMode" value="order" /> {{ t('builder.rankOrder') }}</label>
          <label class="opt-row"><RadioButton v-model="draft.rankMode" value="top" /> {{ t('builder.rankTop') }}</label>
        </div>
        <template v-if="draft.rankMode !== 'none'">
          <Select
            v-model="draft.measure"
            :options="allMeasures"
            option-label="label"
            option-value="value"
            filter
            size="small"
            :placeholder="t('builder.rankMeasure')"
          />
          <div class="opt-row">
            <template v-if="draft.rankMode === 'top'">
              N
              <InputNumber
                v-model="draft.topCount"
                :min="1"
                :use-grouping="false"
                size="small"
                input-class="top-input"
                @input="onTopInput"
              />
            </template>
            <label v-else class="opt-row">
              <Checkbox v-model="draft.descending" binary size="small" />
              {{ t('builder.descending') }}
            </label>
          </div>
        </template>
      </div>
      <template #footer>
        <Button :label="t('common.cancel')" severity="secondary" text @click="draft = null" />
        <Button :label="t('builder.apply')" icon="pi pi-check" :disabled="!draftValid" @click="applyDraft" />
      </template>
    </Dialog>

    <PasteListDialog :zone="pasteZone" @close="pasteZone = null" />

    <MemberPickerDialog
      :hierarchy="picker?.hierarchy ?? null"
      :title="picker ? hierarchyCaption(picker.hierarchy) : ''"
      :initial="picker?.initial ?? []"
      @close="picker = null"
      @confirm="onPicked"
    />
  </div>
</template>

<style scoped>
.builder {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  font-size: 0.88rem;
}
.builder-bar {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.3rem 0.5rem;
}
.builder-cube {
  flex: 1;
  font-weight: 600;
}
.builder-msg {
  margin: 0 0.5rem 0.4rem;
}
.builder-hint,
.zone-empty {
  color: var(--p-text-muted-color);
  font-size: 0.8rem;
  margin: 0.25rem 0.5rem;
}
.builder-zones {
  flex: 1;
  overflow: auto;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0 0.5rem 0.5rem;
}
.zone {
  border: 1px dashed var(--p-surface-600);
  border-radius: 0.4rem;
  padding: 0.3rem;
  min-height: 3.2rem;
}
.zone.over {
  border-color: var(--p-primary-color);
  background: color-mix(in srgb, var(--p-primary-color) 10%, transparent);
}
.zone-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 0.2rem;
}
.zone-title {
  font-weight: 600;
  font-size: 0.8rem;
  text-transform: uppercase;
  color: var(--p-text-muted-color);
}
.zone-paste {
  margin-left: auto;
}
.zone-nonempty {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  font-size: 0.75rem;
  color: var(--p-text-muted-color);
}
.entry {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.1rem 0.2rem;
  border-radius: 0.3rem;
}
.entry:hover {
  background: var(--p-surface-800);
}
.entry.incomplete .entry-label {
  color: var(--p-orange-400);
}
.entry-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.entry-label small {
  margin-left: 0.4rem;
  color: var(--p-text-muted-color);
}
.options {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}
.opt-row {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  flex-wrap: wrap;
}
.opt-indent {
  margin-left: 1.6rem;
}
.opt-members {
  display: flex;
  align-items: center;
  gap: 0.6rem;
}
.opt-count {
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
}
.opt-title {
  font-weight: 600;
  margin-top: 0.4rem;
}
:deep(.top-input) {
  width: 5rem;
}
</style>
