<script setup lang="ts">
// Explorateur de métadonnées : mesures (par dossier) + dimensions → hiérarchies → niveaux,
// et sous chaque hiérarchie un dossier « Membres » qui descend jusqu'aux feuilles (façon SSMS).
// Double-clic ou glisser vers l'éditeur : insère l'UniqueName.
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Tree from 'primevue/tree'
import Select from 'primevue/select'
import Button from 'primevue/button'
import type { TreeNode } from 'primevue/treenode'
import { api, type MemberNode } from '../api'
import { actions, store } from '../store'

const { t } = useI18n()


// Crans déjà chargés, par clé de nœud. Séparé de l'arbre lui-même parce que `nodes` est un
// computed : le recalculer (changement de cube, rafraîchissement) écraserait des enfants
// stockés dans les nœuds, alors qu'ici ils survivent.
const loaded = ref(new Map<string, { nodes: MemberNode[]; hasMore: boolean }>())
const pending = ref(new Set<string>())
const failed = ref(new Set<string>())

/** Construit récursivement le sous-arbre déjà chargé sous une clé. */
function buildMembers(key: string, parentCount: number): TreeNode[] {
  const cran = loaded.value.get(key)
  if (!cran) return []

  const enfants: TreeNode[] = cran.nodes.map((m) => ({
    key: `m:${m.uniqueName}`,
    label: m.caption,
    icon: 'pi pi-circle-fill',
    data: m.uniqueName,
    title: m.uniqueName,
    // childrenCount === 0 : vraie feuille, pas de flèche. -1 : inconnu, on laisse dépliable
    // plutôt que de la déclarer feuille à tort et de la rendre muette.
    leaf: m.childrenCount === 0,
    loading: pending.value.has(`m:${m.uniqueName}`),
    children: buildMembers(`m:${m.uniqueName}`, m.childrenCount),
  }))

  if (cran.hasMore) {
    // Le nombre exact vient de la cardinalité du parent, que le serveur a déjà donnée ;
    // il n'a donc pas eu à recompter. Inconnue (-1) → on le dit sans chiffre.
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
  // Ni le dossier « Membres » ni un membre : mesures, dimensions, niveaux restent statiques.
  const estHierarchie = key.startsWith('mb:')
  if (!estHierarchie && !key.startsWith('m:')) return

  const parent = key.slice(key.indexOf(':') + 1)
  pending.value = new Set(pending.value).add(key)
  failed.value.delete(key)
  try {
    const cran = await api.children(store.cube, parent, estHierarchie)
    loaded.value = new Map(loaded.value).set(key, cran)
  } catch {
    // Un cran illisible (droits, membre disparu depuis le chargement des métadonnées) ne doit
    // pas laisser un nœud à tourner indéfiniment : on le marque et l'utilisateur peut replier.
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
      children: [
        // Le dossier « Membres » s'AJOUTE aux niveaux, il ne les remplace pas : c'est la
        // disposition de SSMS, et les niveaux restent utiles à l'insertion dans l'éditeur.
        {
          key: `mb:${h.uniqueName}`,
          label: failed.value.has(`mb:${h.uniqueName}`)
            ? t('explorer.membersError')
            : t('explorer.members'),
          icon: 'pi pi-folder',
          selectable: false,
          leaf: false,
          loading: pending.value.has(`mb:${h.uniqueName}`),
          // -1 : au premier cran on n'a pas de cardinalité de parent à annoncer.
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
 * Le glisser transporte l'UniqueName en texte brut : l'éditeur le dépose à l'endroit lâché,
 * là où le double-clic insère au curseur courant. Les deux gestes restent utiles.
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
