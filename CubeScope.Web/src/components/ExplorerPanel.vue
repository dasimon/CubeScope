<script setup lang="ts">
// Explorateur de métadonnées : mesures (par dossier) + dimensions → hiérarchies → niveaux.
// Double-clic sur un nœud : insère l'UniqueName au curseur de l'éditeur.
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import Tree from 'primevue/tree'
import Select from 'primevue/select'
import Button from 'primevue/button'
import type { TreeNode } from 'primevue/treenode'
import { actions, store } from '../store'

const { t } = useI18n()

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
  // Dossier racine seul → remonter ses mesures directement sous "Mesures"
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
      children: h.levels.map((l) => ({
        key: l.uniqueName,
        label: l.name,
        icon: 'pi pi-minus',
        data: l.uniqueName,
        leaf: true,
      })),
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
    >
      <template #default="{ node }">
        <span
          :title="(node as any).title || (typeof node.data === 'string' ? node.data : '')"
          @dblclick="onNodeDblClick(node)"
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
