<script setup lang="ts">
// Metadata explorer: measures (by folder) + dimensions → hierarchies → levels,
// and under each hierarchy a "Members" folder that goes down to the leaves (SSMS-style).
// Double-click or drag to the editor: inserts the UniqueName.
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Tree from 'primevue/tree'
import Select from 'primevue/select'
import Button from 'primevue/button'
import type { TreeNode } from 'primevue/treenode'
import { api, type MemberNode } from '../api'
import { actions, store } from '../store'

const { t } = useI18n()


// Levels already loaded, by node key. Kept apart from the tree itself because `nodes` is a
// computed: recomputing it (cube change, refresh) would overwrite children stored in the
// nodes, whereas here they survive.
const loaded = ref(new Map<string, { nodes: MemberNode[]; hasMore: boolean }>())
const pending = ref(new Set<string>())
const failed = ref(new Set<string>())

/** Recursively builds the already-loaded subtree under a key. */
function buildMembers(key: string, parentCount: number): TreeNode[] {
  const cran = loaded.value.get(key)
  if (!cran) return []

  const enfants: TreeNode[] = cran.nodes.map((m) => ({
    key: `m:${m.uniqueName}`,
    label: m.caption,
    icon: 'pi pi-circle-fill',
    data: m.uniqueName,
    title: m.uniqueName,
    // childrenCount === 0: true leaf, no arrow. -1: unknown, keep it expandable
    // rather than wrongly declaring it a leaf and making it silent.
    leaf: m.childrenCount === 0,
    loading: pending.value.has(`m:${m.uniqueName}`),
    children: buildMembers(`m:${m.uniqueName}`, m.childrenCount),
  }))

  if (cran.hasMore) {
    // The exact number comes from the parent's cardinality, which the server already gave;
    // so it did not have to count again. Unknown (-1) → say so without a number.
    const reste = parentCount - cran.nodes.length
    enfants.push({
      key: `${key}:more`,
      label: reste > 0 ? t('explorer.more', { n: reste }) : t('explorer.moreUnknown'),
      icon: 'pi pi-ellipsis-h',
      selectable: false,
      leaf: true,
    })
  }
  return enfants
}

async function onNodeExpand(node: TreeNode): Promise<void> {
  const key = node.key as string
  if (!store.cube) return
  if (loaded.value.has(key) || pending.value.has(key)) return
  // Neither the "Members" folder nor a member: measures, dimensions, levels stay static.
  const estHierarchie = key.startsWith('mb:')
  if (!estHierarchie && !key.startsWith('m:')) return

  const parent = key.slice(key.indexOf(':') + 1)
  pending.value = new Set(pending.value).add(key)
  failed.value.delete(key)
  try {
    const cran = await api.children(store.cube, parent, estHierarchie)
    loaded.value = new Map(loaded.value).set(key, cran)
  } catch {
    // An unreadable level (permissions, member gone since the metadata was loaded) must not
    // leave a node spinning forever: mark it, and the user can collapse it.
    failed.value = new Set(failed.value).add(key)
  } finally {
    const p = new Set(pending.value)
    p.delete(key)
    pending.value = p
  }
}

const nodes = computed<TreeNode[]>(() => {
  const meta = store.cubeMeta
  if (!meta) return []

  const measureChildren: TreeNode[] = meta.measureFolders.map((f) => ({
    key: `f:${f.folder}`,
    label: f.folder === '' ? t('explorer.root') : f.folder,
    icon: 'pi pi-folder',
    selectable: false,
    children: f.measures.map((m) => ({
      key: m.uniqueName,
      label: m.name,
      icon: 'pi pi-calculator',
      data: m.uniqueName,
      title: m.description || undefined,
      leaf: true,
    })),
  }))
  // Root folder only → lift its measures directly under "Measures"
  const measuresNode: TreeNode = {
    key: 'measures',
    label: t('explorer.measures', { n: meta.measureFolders.reduce((n, f) => n + f.measures.length, 0) }),
    icon: 'pi pi-calculator',
    selectable: false,
    children:
      measureChildren.length === 1 && meta.measureFolders[0].folder === ''
        ? measureChildren[0].children
        : measureChildren,
  }

  const dimensionNodes: TreeNode[] = meta.dimensions.map((d) => ({
    key: d.uniqueName,
    label: d.name,
    icon: 'pi pi-table',
    data: d.uniqueName,
    children: d.hierarchies.map((h) => ({
      key: h.uniqueName,
      label: h.name,
      icon: 'pi pi-sitemap',
      data: h.uniqueName,
      children: [
        // The "Members" folder is ADDED to the levels, it does not replace them: that is the
        // SSMS layout, and the levels remain useful for inserting into the editor.
        {
          key: `mb:${h.uniqueName}`,
          label: failed.value.has(`mb:${h.uniqueName}`)
            ? t('explorer.membersError')
            : t('explorer.members'),
          icon: 'pi pi-folder',
          selectable: false,
          leaf: false,
          loading: pending.value.has(`mb:${h.uniqueName}`),
          // -1: at the first level there is no parent cardinality to announce.
          children: buildMembers(`mb:${h.uniqueName}`, -1),
        },
        ...h.levels.map((l) => ({
          key: l.uniqueName,
          label: l.name,
          icon: 'pi pi-minus',
          data: l.uniqueName,
          leaf: true,
        })),
      ],
    })),
  }))

  return [
    measuresNode,
    { key: 'dimensions', label: t('explorer.dimensions', { n: dimensionNodes.length }), icon: 'pi pi-database', selectable: false, children: dimensionNodes },
  ]
})

function onNodeDblClick(node: TreeNode) {
  if (typeof node.data === 'string') actions.requestInsert(node.data)
}

/**
 * Dragging carries the UniqueName as plain text: the editor drops it where it is released,
 * whereas double-click inserts at the current cursor. Both gestures remain useful.
 */
function onDragStart(e: DragEvent, node: TreeNode) {
  if (typeof node.data !== 'string' || !e.dataTransfer) return
  e.dataTransfer.setData('text/plain', node.data)
  e.dataTransfer.effectAllowed = 'copy'
}
</script>

<template>
  <div class="explorer">
    <div class="explorer-bar">
      <Select
        v-if="store.cubes.length > 1"
        :model-value="store.cube"
        :options="store.cubes"
        size="small"
        class="explorer-cube"
        @update:model-value="(c: string) => actions.selectCube(c)"
      />
      <span v-else class="explorer-cube-name">{{ store.cube ?? '—' }}</span>
      <Button
        icon="pi pi-refresh"
        text
        size="small"
        :title="t('explorer.refresh')"
        :loading="store.metaLoading"
        @click="store.cube && actions.selectCube(store.cube, true)"
      />
    </div>
    <Tree
      :value="nodes"
      :filter="true"
      filter-mode="lenient"
      :filter-placeholder="t('common.filter')"
      class="explorer-tree"
      :pt="{ nodeLabel: { style: 'user-select: none' } }"
      @node-expand="onNodeExpand"
    >
      <template #default="{ node }">
        <span
          :title="(node as any).title || (typeof node.data === 'string' ? node.data : '')"
          :draggable="typeof node.data === 'string'"
          @dblclick="onNodeDblClick(node)"
          @dragstart="onDragStart($event, node)"
        >
          {{ node.label }}
        </span>
      </template>
    </Tree>
    <div class="explorer-hint">{{ t('explorer.dblclick') }}</div>
  </div>
</template>

<style scoped>
.explorer {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.explorer-bar {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.3rem 0.5rem;
}
.explorer-cube {
  flex: 1;
}
.explorer-cube-name {
  flex: 1;
  font-weight: 600;
  font-size: 0.9rem;
  padding-left: 0.25rem;
}
.explorer-tree {
  flex: 1;
  overflow: auto;
  font-size: 0.88rem;
}
.explorer-hint {
  padding: 0.25rem 0.5rem;
  font-size: 0.75rem;
  color: var(--p-text-muted-color);
  border-top: 1px solid var(--p-surface-700);
}
</style>
