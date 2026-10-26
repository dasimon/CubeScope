<script setup lang="ts">
// Member selection for the builder: the hierarchy's member tree, loaded level by level through
// /api/metadata/children (capped, never MDSCHEMA_MEMBERS — see CLAUDE.md), click to check.
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import Tree from 'primevue/tree'
import Button from 'primevue/button'
import Chip from 'primevue/chip'
import type { TreeNode } from 'primevue/treenode'
import { api, type MemberNode } from '../api'
import { store } from '../store'
import type { Picked } from '../builder'

const props = defineProps<{ hierarchy: string | null; title: string; initial: Picked[] }>()
const emit = defineEmits<{ close: []; confirm: [members: Picked[]] }>()
const { t } = useI18n()

const visible = computed(() => props.hierarchy != null)
const levels = ref(new Map<string, { nodes: MemberNode[]; hasMore: boolean }>())
const pending = ref(new Set<string>())
const error = ref('')
// Selection in pick order, with captions (the tree only knows the loaded levels).
const chosen = ref<Picked[]>([])
// A level still loading for the previous hierarchy must not land in this one.
let generation = 0

watch(
  () => props.hierarchy,
  async (h) => {
    generation++
    levels.value = new Map()
    pending.value = new Set()
    error.value = ''
    chosen.value = props.initial.map((m) => ({ ...m }))
    if (h) await loadLevel(`h:${h}`, h, true)
  },
  { immediate: true },
)

async function loadLevel(key: string, parent: string, isHierarchy: boolean) {
  if (!store.cube || levels.value.has(key) || pending.value.has(key)) return
  const mine = generation
  pending.value = new Set(pending.value).add(key)
  try {
    const r = await api.children(store.cube, parent, isHierarchy)
    if (mine === generation) levels.value = new Map(levels.value).set(key, r)
  } catch (e) {
    if (mine === generation) error.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (mine !== generation) return
    const p = new Set(pending.value)
    p.delete(key)
    pending.value = p
  }
}

function build(key: string): TreeNode[] {
  const level = levels.value.get(key)
  if (!level) return []
  const nodes: TreeNode[] = level.nodes.map((m) => ({
    key: m.uniqueName,
    label: m.caption,
    data: m,
    leaf: m.childrenCount === 0,
    loading: pending.value.has(`m:${m.uniqueName}`),
    children: build(`m:${m.uniqueName}`),
  }))
  if (level.hasMore)
    nodes.push({ key: `${key}:more`, label: t('builder.moreMembers'), selectable: false, leaf: true, styleClass: 'more' })
  return nodes
}

const nodes = computed(() => (props.hierarchy ? build(`h:${props.hierarchy}`) : []))

const selectionKeys = computed<Record<string, boolean>>(() =>
  Object.fromEntries(chosen.value.map((m) => [m.uniqueName, true])))

function onSelect(node: TreeNode) {
  const m = node.data as MemberNode | undefined
  if (!m) return
  const i = chosen.value.findIndex((x) => x.uniqueName === m.uniqueName)
  if (i >= 0) chosen.value.splice(i, 1)
  else chosen.value.push({ uniqueName: m.uniqueName, caption: m.caption })
}

function remove(uniqueName: string) {
  chosen.value = chosen.value.filter((m) => m.uniqueName !== uniqueName)
}
</script>

<template>
  <Dialog
    :visible="visible"
    modal
    :header="title"
    :style="{ width: '34rem' }"
    @update:visible="(v: boolean) => !v && emit('close')"
  >
    <p v-if="error" class="picker-error">{{ error }}</p>
    <Tree
      :value="nodes"
      selection-mode="single"
      :selection-keys="selectionKeys"
      :filter="true"
      filter-mode="lenient"
      :filter-placeholder="t('common.filter')"
      class="picker-tree"
      @node-select="onSelect"
      @node-unselect="onSelect"
      @node-expand="(n: TreeNode) => loadLevel(`m:${n.key}`, n.key as string, false)"
    />
    <div class="picker-chosen">
      <span class="picker-count">{{ t('builder.chosenCount', { n: chosen.length }) }}</span>
      <Chip
        v-for="m in chosen"
        :key="m.uniqueName"
        :label="m.caption"
        :title="m.uniqueName"
        removable
        @remove="remove(m.uniqueName)"
      />
    </div>
    <template #footer>
      <Button :label="t('common.cancel')" severity="secondary" text @click="emit('close')" />
      <Button :label="t('builder.apply')" icon="pi pi-check" @click="emit('confirm', chosen)" />
    </template>
  </Dialog>
</template>

<style scoped>
.picker-tree {
  max-height: 22rem;
  overflow: auto;
  font-size: 0.88rem;
}
.picker-chosen {
  display: flex;
  flex-wrap: wrap;
  gap: 0.3rem;
  margin-top: 0.6rem;
  max-height: 8rem;
  overflow: auto;
}
.picker-count {
  width: 100%;
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
}
.picker-error {
  color: var(--p-red-400);
  font-size: 0.85rem;
}
:deep(.more) {
  font-style: italic;
  color: var(--p-text-muted-color);
}
</style>
