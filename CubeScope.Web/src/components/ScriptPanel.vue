<script setup lang="ts">
// Panneau Script : MDX Script du cube (Monaco lecture seule), liste des membres
// calculés / sets / scopes (clic = aller à la définition), arbre de dépendances de
// l'élément sélectionné (double sens), export de la doc Markdown du cube.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Tree from 'primevue/tree'
import type { TreeNode } from 'primevue/treenode'
import { monaco } from '../monaco-mdx'
import { api, type CubeScript, type DependencyGraph, type ScriptCommand } from '../api'
import { store } from '../store'

const { t } = useI18n()

const script = ref<CubeScript | null>(null)
const loading = ref(false)
const error = ref('')
const filter = ref('')
const selected = ref<ScriptCommand | null>(null)
const graph = ref<DependencyGraph | null>(null)
const graphLoading = ref(false)

const host = ref<HTMLElement | null>(null)
let editor: monaco.editor.IStandaloneCodeEditor | null = null

const filteredCommands = computed(() => {
  const list = script.value?.commands ?? []
  const f = filter.value.trim().toLowerCase()
  return f ? list.filter((c) => c.name.toLowerCase().includes(f)) : list
})

async function load(refresh = false) {
  if (!store.cube) return
  loading.value = true
  error.value = ''
  try {
    script.value = await api.script(store.cube, refresh)
    ensureEditor()
    editor?.setValue(script.value.fullText)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
    script.value = null
  } finally {
    loading.value = false
  }
}

function ensureEditor() {
  if (editor || !host.value) return
  editor = monaco.editor.create(host.value, {
    value: '',
    language: 'mdx',
    theme: 'cubescope-dark',
    readOnly: true,
    automaticLayout: true,
    minimap: { enabled: true },
    fontSize: 13,
  })
}

async function select(cmd: ScriptCommand) {
  selected.value = cmd
  editor?.revealLineNearTop(cmd.startLine)
  editor?.setPosition({ lineNumber: cmd.startLine, column: 1 })
  if (cmd.kind === 'CalculatedMember' || cmd.kind === 'NamedSet') {
    graphLoading.value = true
    graph.value = null
    try {
      graph.value = await api.dependencies(store.cube!, cmd.name)
    } catch {
      graph.value = null
    } finally {
      graphLoading.value = false
    }
  } else {
    graph.value = null
  }
}

function toTreeNodes(node: { name: string; kind: string; dependencies: unknown[] }, path = '0'): TreeNode {
  const deps = node.dependencies as { name: string; kind: string; dependencies: unknown[] }[]
  return {
    key: path,
    label: node.name,
    icon:
      node.kind === 'Measure'
        ? 'pi pi-calculator'
        : node.kind === 'Hierarchy'
          ? 'pi pi-sitemap'
          : node.kind === 'NamedSet'
            ? 'pi pi-list'
            : 'pi pi-percentage',
    children: deps.map((d, i) => toTreeNodes(d, `${path}-${i}`)),
  }
}

const depTree = computed<TreeNode[]>(() =>
  graph.value ? toTreeNodes(graph.value.root).children ?? [] : [],
)

// (Re)charger quand le cube change / à la connexion
watch(
  () => store.cube,
  (c) => {
    script.value = null
    selected.value = null
    graph.value = null
    if (c) void load()
  },
  { immediate: true },
)

function exportDoc() {
  if (!store.cube) return
  const a = document.createElement('a')
  a.href = api.docUrl(store.cube)
  a.download = `${store.cube}-doc.md`
  a.click()
}

onBeforeUnmount(() => editor?.dispose())
</script>

<template>
  <div class="script-panel">
    <div class="script-side">
      <div class="script-bar">
        <InputText v-model="filter" :placeholder="t('common.filter')" size="small" class="script-filter" />
        <Button icon="pi pi-refresh" text size="small" :title="t('script.reload')" :loading="loading" @click="load(true)" />
        <Button icon="pi pi-download" text size="small" :title="t('script.exportDoc')" :disabled="!script" @click="exportDoc" />
      </div>
      <Message v-if="error" severity="error" class="script-msg">{{ error }}</Message>
      <div v-else-if="!store.cube" class="script-hint">{{ t('script.needConnect') }}</div>
      <ul v-else class="script-list">
        <li
          v-for="c in filteredCommands"
          :key="c.kind + c.name + c.startLine"
          :class="{ selected: selected === c }"
          :title="t('script.lineTitle', { kind: t('script.kind.' + c.kind), line: c.startLine })"
          @click="select(c)"
        >
          <i
            :class="c.kind === 'CalculatedMember' ? 'pi pi-percentage' : c.kind === 'NamedSet' ? 'pi pi-list' : 'pi pi-code'"
          />
          {{ c.name }}
        </li>
      </ul>
      <div v-if="selected && (graphLoading || graph)" class="script-deps">
        <div class="script-deps-title">{{ t('script.deps', { name: selected.name }) }}</div>
        <div v-if="graphLoading" class="script-hint">{{ t('script.analyzing') }}</div>
        <template v-else-if="graph">
          <Tree :value="depTree" class="script-deps-tree" />
          <div v-if="graph.usedBy.length" class="script-usedby">
            <strong>{{ t('script.usedBy') }}</strong>
            <span v-for="u in graph.usedBy" :key="u" class="script-usedby-item">{{ u }}</span>
          </div>
          <div v-else class="script-usedby script-hint">{{ t('script.usedByNone') }}</div>
        </template>
      </div>
    </div>
    <div ref="host" class="script-editor" />
  </div>
</template>

<style scoped>
.script-panel {
  height: 100%;
  display: flex;
  overflow: hidden;
}
.script-side {
  width: 380px;
  min-width: 260px;
  display: flex;
  flex-direction: column;
  border-right: 1px solid var(--p-surface-700);
  overflow: hidden;
}
.script-bar {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.3rem 0.5rem;
}
.script-filter {
  flex: 1;
}
.script-msg {
  margin: 0.5rem;
}
.script-hint {
  padding: 0.75rem;
  color: var(--p-text-muted-color);
  font-size: 0.85rem;
}
.script-list {
  flex: 1;
  overflow: auto;
  margin: 0;
  padding: 0 0 0.5rem;
  list-style: none;
  font-size: 0.85rem;
}
.script-list li {
  padding: 0.25rem 0.6rem;
  cursor: pointer;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.script-list li:hover {
  background: var(--p-surface-800);
}
.script-list li.selected {
  background: var(--p-highlight-background, var(--p-surface-700));
}
.script-list i {
  margin-right: 0.4rem;
  color: var(--p-primary-color);
}
.script-deps {
  border-top: 1px solid var(--p-surface-700);
  max-height: 45%;
  overflow: auto;
  padding-bottom: 0.5rem;
}
.script-deps-title {
  padding: 0.4rem 0.6rem;
  font-weight: 600;
  font-size: 0.82rem;
}
.script-deps-tree {
  font-size: 0.82rem;
  padding: 0 0.25rem;
}
.script-usedby {
  padding: 0.4rem 0.6rem;
  font-size: 0.8rem;
}
.script-usedby-item {
  display: inline-block;
  background: var(--p-surface-800);
  border: 1px solid var(--p-surface-700);
  border-radius: 4px;
  padding: 0.05rem 0.4rem;
  margin: 0.15rem 0.2rem 0 0;
}
.script-editor {
  flex: 1;
  min-width: 0;
}
</style>
