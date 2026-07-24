<script setup lang="ts">
// Panneau Script : MDX Script du cube (Monaco) — lecture seule en mode cube live,
// lecture/écriture en mode projet SSDT (.cube). Liste des membres calculés / sets /
// scopes groupée par région (#region en mode projet), clic = aller à la définition,
// arbre de dépendances de l'élément sélectionné (double sens), export de la doc
// Markdown du cube (mode cube live uniquement).
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Panel from 'primevue/panel'
import Tree from 'primevue/tree'
import type { TreeNode } from 'primevue/treenode'
import { useToast } from 'primevue/usetoast'
import { monaco } from '../monaco-mdx'
import {
  api,
  type CalculationProp,
  type CubeScript,
  type DependencyGraph,
  type DeployLogEntry,
  type DirectoryListing,
  type ProjectScript,
  type RecentProject,
  type ScriptCommand,
} from '../api'
import type { RenameResult } from '../api'
import { store } from '../store'

const { t } = useI18n()
const toast = useToast()

const script = ref<CubeScript | null>(null)
const loading = ref(false)
const error = ref('')
const filter = ref('')
const selected = ref<ScriptCommand | null>(null)
const graph = ref<DependencyGraph | null>(null)
const graphLoading = ref(false)

// --- Recherche plein texte dans le script (distincte du filtre de la liste des commandes,
// qui ne filtre que par NOM). Balaie tout le texte (expressions de membres, corps SCOPE,
// commentaires) ligne par ligne, insensible à la casse, plafonnée pour rester réactive
// sur un script de plusieurs milliers de lignes. ---
const textSearch = ref('')
const SEARCH_HITS_CAP = 200

const fullScriptText = computed(() => project.value?.fullText ?? script.value?.fullText ?? '')

interface SearchHit {
  line: number
  text: string
}

const searchHits = computed<SearchHit[]>(() => {
  const q = textSearch.value.trim().toLowerCase()
  if (!q) return []
  const hits: SearchHit[] = []
  const lines = fullScriptText.value.split(/\r\n|\r|\n/)
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].toLowerCase().includes(q)) {
      hits.push({ line: i + 1, text: lines[i].trim() })
      if (hits.length >= SEARCH_HITS_CAP) break
    }
  }
  return hits
})

const searchCapped = computed(() => searchHits.value.length >= SEARCH_HITS_CAP)

/** Va à la ligne dans Monaco et sélectionne l'occurrence recherchée sur cette ligne quand
 *  on peut la retrouver (le texte peut avoir bougé depuis le calcul des hits, cas rare). */
function goToLine(line: number) {
  if (!editor) return
  editor.revealLineNearTop(line)
  editor.setPosition({ lineNumber: line, column: 1 })
  const q = textSearch.value.trim()
  const model = editor.getModel()
  if (q && model && line >= 1 && line <= model.getLineCount()) {
    const content = model.getLineContent(line)
    const idx = content.toLowerCase().indexOf(q.toLowerCase())
    if (idx >= 0) {
      const range = new monaco.Range(line, idx + 1, line, idx + 1 + q.length)
      editor.setSelection(range)
      editor.revealRangeNearTop(range)
    }
  }
  editor.focus()
}

/** Références textuelles brutes du membre/set sélectionné (complémentaire du graphe
 *  sémantique usedBy déjà affiché) : réutilise la recherche plein texte ci-dessus. */
function findReferences() {
  if (!selected.value) return
  textSearch.value = selected.value.name
}

const host = ref<HTMLElement | null>(null)
let editor: monaco.editor.IStandaloneCodeEditor | null = null

// --- Mode projet SSDT (édition) vs cube live (lecture seule) ---
const project = ref<ProjectScript | null>(null)
const dirty = ref(false)
const saving = ref(false)
const warnings = ref<string[]>([])
const showOpen = ref(false)
const openPath = ref('')
const openError = ref('')
const recentProjects = ref<RecentProject[]>([])
const browsing = ref(false)
const listing = ref<DirectoryListing | null>(null)
const browseError = ref('')
let suppressDirty = false
let savePromise: Promise<boolean> | null = null

const isProject = computed(() => project.value !== null)

// --- Propriétés de calcul (FormatString/DisplayFolder/Description) du membre sélectionné ---
// Chargées une fois par projet ouvert (pas par sélection) : select() relit ce cache local.
const calcProps = ref<CalculationProp[]>([])
const propFormatString = ref('')
const propDisplayFolder = ref('')
const propDescription = ref('')
const savingProps = ref(false)

const showCalcProps = computed(
  () => isProject.value && !!project.value?.canEdit && selected.value?.kind === 'CalculatedMember',
)

async function loadCalcProps(path: string) {
  try {
    calcProps.value = await api.calcProps(path)
  } catch {
    calcProps.value = []
  }
}

async function saveCalcProps() {
  if (!project.value || !selected.value) return
  savingProps.value = true
  try {
    await api.saveCalcProp(
      project.value.path,
      selected.value.name,
      propFormatString.value.trim() || null,
      propDisplayFolder.value.trim() || null,
      propDescription.value.trim() || null,
    )
    await loadCalcProps(project.value.path)
    toast.add({ severity: 'success', summary: t('calcprops.saved'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  } finally {
    savingProps.value = false
  }
}

// --- Renommage sûr d'un membre calculé / set nommé (définition + toutes les références
// textuelles), via /api/script/rename (MemberRenamer côté serveur). Aperçu = appel serveur
// sans toucher l'éditeur ; Appliquer = même appel (texte courant, potentiellement modifié
// depuis l'aperçu) puis remplace le contenu de l'éditeur — le listener onDidChangeModelContent
// marque alors le projet dirty naturellement (pas de setEditorText, qui force dirty=false). ---
const showRename = ref(false)
const renameNewName = ref('')
const renamePreview = ref<RenameResult | null>(null)
const renameBusy = ref(false)
const renameError = ref('')

const canRename = computed(
  () =>
    isProject.value &&
    !!project.value?.canEdit &&
    (selected.value?.kind === 'CalculatedMember' || selected.value?.kind === 'NamedSet'),
)

const renameApplyDisabled = computed(() => {
  const n = renameNewName.value.trim()
  return !n || n === selected.value?.name
})

function openRename() {
  if (!selected.value) return
  renameNewName.value = selected.value.name
  renamePreview.value = null
  renameError.value = ''
  showRename.value = true
}

async function previewRename() {
  if (!selected.value || renameApplyDisabled.value) return
  renameError.value = ''
  renameBusy.value = true
  try {
    const text = editor?.getValue() ?? project.value?.fullText ?? ''
    renamePreview.value = await api.renameMember(text, selected.value.name, renameNewName.value.trim())
  } catch (e) {
    renameError.value = e instanceof Error ? e.message : String(e)
    renamePreview.value = null
  } finally {
    renameBusy.value = false
  }
}

async function applyRename() {
  if (!selected.value || renameApplyDisabled.value) return
  renameError.value = ''
  renameBusy.value = true
  try {
    const text = editor?.getValue() ?? project.value?.fullText ?? ''
    const r = await api.renameMember(text, selected.value.name, renameNewName.value.trim())
    editor?.setValue(r.newScript) // déclenche onDidChangeModelContent → dirty = true
    showRename.value = false
    toast.add({ severity: 'success', summary: t('rename.done', { n: r.occurrences }), life: 4000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof Error ? e.message : String(e), life: 6000 })
  } finally {
    renameBusy.value = false
  }
}

const commands = computed<ScriptCommand[]>(() =>
  project.value?.commands ?? script.value?.commands ?? [],
)

const filteredCommands = computed(() => {
  const f = filter.value.trim().toLowerCase()
  return f ? commands.value.filter((c) => c.name.toLowerCase().includes(f)) : commands.value
})

/** Liste groupée par section (#region) — les commandes hors région en dernier. */
const groupedCommands = computed(() => {
  const groups = new Map<string, ScriptCommand[]>()
  for (const c of filteredCommands.value) {
    const key = c.section ?? ''
    const list = groups.get(key)
    if (list) list.push(c)
    else groups.set(key, [c])
  }
  return [...groups.entries()]
    .sort(([a], [b]) => (a === '' ? 1 : b === '' ? -1 : 0))
    .map(([section, items]) => ({ section, items }))
})

const hasSections = computed(() => groupedCommands.value.some((g) => g.section !== ''))

async function load(refresh = false) {
  if (!store.cube) return
  loading.value = true
  error.value = ''
  try {
    script.value = await api.script(store.cube, refresh)
    ensureEditor()
    if (!isProject.value) setEditorText(script.value.fullText, true)
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
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => void saveProject())
  editor.onDidChangeModelContent(() => {
    if (!suppressDirty && isProject.value && project.value?.canEdit) dirty.value = true
  })
}

function setEditorText(text: string, readOnly: boolean) {
  suppressDirty = true
  editor?.updateOptions({ readOnly })
  editor?.setValue(text)
  suppressDirty = false
  dirty.value = false
}

async function showOpenDialog() {
  openError.value = ''
  showOpen.value = true
  browsing.value = false
  listing.value = null
  browseError.value = ''
  try {
    recentProjects.value = await api.projectRecent()
  } catch {
    recentProjects.value = []
  }
}

async function browse(path?: string) {
  browseError.value = ''
  try {
    listing.value = await api.fsList(path)
    browsing.value = true
  } catch (e) {
    // Navigation échouée : on garde le dossier courant affiché (l'utilisateur reste
    // où il était et peut réessayer ailleurs), l'erreur s'affiche au-dessus.
    browseError.value = e instanceof Error ? e.message : String(e)
  }
}

async function openProject(path?: string) {
  if (dirty.value && !window.confirm(t('project.discardConfirm'))) return
  const p = (path ?? openPath.value).trim()
  if (!p) return
  openError.value = ''
  try {
    const proj = await api.projectOpen(p)
    project.value = proj
    warnings.value = []
    showOpen.value = false
    openPath.value = p
    ensureEditor()
    setEditorText(proj.fullText, !proj.canEdit)
    calcProps.value = []
    if (proj.canEdit) void loadCalcProps(p)
  } catch (e) {
    openError.value = e instanceof Error ? e.message : String(e)
  }
}

function closeProject() {
  if (dirty.value && !window.confirm(t('project.discardConfirm'))) return
  project.value = null
  dirty.value = false
  warnings.value = []
  calcProps.value = []
  setEditorText(script.value?.fullText ?? '', true)
}

/** Corps réel de la sauvegarde (wrappé par saveProject pour dédupliquer les appels concurrents). */
async function performSaveProject(): Promise<boolean> {
  saving.value = true
  // Identité du projet visé par CETTE sauvegarde — capturée avant tout await. Si l'utilisateur
  // ouvre un autre projet (ou ferme) pendant les awaits ci-dessous, `project.value` aura changé
  // au retour : l'écriture disque pour `savedPath` a quand même eu lieu (c'est ce qui compte),
  // mais il ne faut alors surtout pas réassigner project.value/dirty/warnings ni toaster —
  // ça cibleraient/afficheraient l'état du MAUVAIS projet (celui maintenant ouvert à l'écran).
  const savedPath = project.value!.path
  try {
    const text = editor?.getValue() ?? project.value!.fullText
    const r = await api.projectSave(savedPath, text)
    // Recharge la liste des commandes (sections/lignes à jour) sans toucher à l'éditeur
    const proj = await api.projectOpen(savedPath)
    if (project.value?.path !== savedPath) return true
    warnings.value = r.warnings
    project.value = proj
    // Si l'utilisateur a continué à taper pendant les awaits ci-dessus, `text` n'est plus
    // le contenu courant de l'éditeur : ne pas effacer le flag dirty dans ce cas.
    const stillSame = (editor?.getValue() ?? '') === text
    dirty.value = !stillSame
    toast.add({ severity: 'success', summary: t('project.saved'), life: 3000 })
    return true
  } catch (e) {
    toast.add({
      severity: 'error',
      summary: e instanceof Error ? e.message : String(e),
      life: 6000,
    })
    return false
  } finally {
    saving.value = false
  }
}

/** Sauvegarde dans le .cube ; retourne true si OK (ou rien à sauver). Les appels concurrents
 *  attendent la même sauvegarde en cours plutôt que de renvoyer true immédiatement. */
async function saveProject(): Promise<boolean> {
  if (!project.value || !project.value.canEdit) return true
  if (!dirty.value) return true
  if (savePromise) return savePromise
  savePromise = performSaveProject()
  try {
    return await savePromise
  } finally {
    savePromise = null
  }
}

// --- Déploiement du script seul (idée BIDS Helper) ---
const showDeploy = ref(false)
const deployServer = ref('')
const deployCatalog = ref('')
const deployBusy = ref(false)
const deployDiffers = ref(false)
const serverText = ref('')
const deployError = ref('')
const deployLog = ref<DeployLogEntry[]>([])

async function loadDeployLog() {
  try {
    deployLog.value = await api.deployLog()
  } catch {
    deployLog.value = []
  }
}

const isDevCatalog = computed(() => deployCatalog.value.toLowerCase().includes('dev'))

const diffHost = ref<HTMLElement | null>(null)
let diffEditor: monaco.editor.IStandaloneDiffEditor | null = null

/** Crée le diff editor (serveur → projet) si le conteneur est monté et rien n'existe déjà. */
function mountDiff() {
  if (diffEditor || !diffHost.value) return
  diffEditor = monaco.editor.createDiffEditor(diffHost.value, {
    readOnly: true,
    automaticLayout: true,
    theme: 'cubescope-dark',
    renderSideBySide: true,
    minimap: { enabled: false },
    fontSize: 13,
  })
  diffEditor.setModel({
    original: monaco.editor.createModel(serverText.value, 'mdx'),
    modified: monaco.editor.createModel(project.value?.fullText ?? '', 'mdx'),
  })
}

/** Détruit le diff editor et ses modèles — sinon fuite mémoire à chaque déploiement divergent. */
function disposeDiff() {
  if (!diffEditor) return
  const model = diffEditor.getModel()
  model?.original.dispose()
  model?.modified.dispose()
  diffEditor.dispose()
  diffEditor = null
}

watch(deployDiffers, async (differs) => {
  if (differs) {
    await nextTick()
    mountDiff()
  } else {
    disposeDiff()
  }
})

watch(showDeploy, (visible) => {
  if (!visible) disposeDiff()
})

// Le serveur peut renvoyer un texte différent d'un essai de déploiement à l'autre (le projet a
// été sauvegardé entre-temps) : reconstruire les modèles plutôt que de garder l'ancien diff affiché.
watch(serverText, (text) => {
  if (!deployDiffers.value || !diffEditor) return
  const model = diffEditor.getModel()
  model?.original.dispose()
  model?.modified.dispose()
  diffEditor.setModel({
    original: monaco.editor.createModel(text, 'mdx'),
    modified: monaco.editor.createModel(project.value?.fullText ?? '', 'mdx'),
  })
})

function showDeployDialog() {
  deployServer.value = store.server
  // Catalogue par défaut = premier catalogue « dev » de la connexion courante
  deployCatalog.value = store.catalogs.find((c) => c.toLowerCase().includes('dev')) ?? store.catalog ?? ''
  deployDiffers.value = false
  serverText.value = ''
  deployError.value = ''
  showDeploy.value = true
  void loadDeployLog()
}

async function deploy(force = false) {
  if (!project.value || deployBusy.value) return
  deployBusy.value = true
  deployError.value = ''
  try {
    if (!(await saveProject())) return // l'état DISQUE est déployé : sauvegarde d'abord
    const r = await api.projectDeploy(
      project.value.path, deployServer.value.trim(), deployCatalog.value.trim(), force)
    if (r.differs && !r.deployed) {
      deployDiffers.value = true
      serverText.value = r.serverText ?? ''
      return
    }
    showDeploy.value = false
    toast.add({ severity: 'success', summary: t('project.deployed', { ms: r.durationMs }), life: 4000 })
    void loadDeployLog()
  } catch (e) {
    deployError.value = e instanceof Error ? e.message : String(e)
  } finally {
    deployBusy.value = false
  }
}

async function select(cmd: ScriptCommand) {
  selected.value = cmd
  editor?.revealLineNearTop(cmd.startLine)
  editor?.setPosition({ lineNumber: cmd.startLine, column: 1 })
  if (cmd.kind === 'CalculatedMember') {
    const cp = calcProps.value.find((p) => p.reference === cmd.name)
    propFormatString.value = cp?.formatString ?? ''
    propDisplayFolder.value = cp?.displayFolder ?? ''
    propDescription.value = cp?.description ?? ''
  }
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
    // En mode projet SSDT, l'éditeur affiche déjà le contenu du .cube : pas d'aller-retour
    // réseau vers /api/script.
    if (c && !isProject.value) void load()
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

/** Averti le navigateur (prompt natif) en cas de fermeture/rechargement d'onglet avec des
 *  modifications non enregistrées — le panneau Script est un vrai éditeur d'écriture, pas
 *  seulement une visionneuse. */
function handleBeforeUnload(e: BeforeUnloadEvent) {
  if (dirty.value) {
    e.preventDefault()
    e.returnValue = ''
  }
}

onMounted(() => window.addEventListener('beforeunload', handleBeforeUnload))
onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', handleBeforeUnload)
  editor?.dispose()
  disposeDiff()
})
</script>

<template>
  <div class="script-panel">
    <div class="script-side">
      <div class="script-bar">
        <InputText v-model="filter" :placeholder="t('common.filter')" size="small" class="script-filter" />
        <Button icon="pi pi-folder-open" text size="small" :title="t('project.open')" @click="showOpenDialog" />
        <Button v-if="isProject" icon="pi pi-save" text size="small" :disabled="!dirty || !project?.canEdit"
          :loading="saving" :title="dirty ? t('project.dirty') : t('project.save')" @click="saveProject" />
        <Button v-if="isProject" icon="pi pi-times" text size="small" :title="t('project.close')" @click="closeProject" />
        <Button v-if="isProject" icon="pi pi-cloud-upload" text size="small"
          :disabled="!project?.canEdit" :title="t('project.deploy')" @click="showDeployDialog" />
        <Button v-if="canRename" icon="pi pi-pencil" text size="small" :label="t('rename.button')"
          :title="t('rename.button')" @click="openRename" />
        <Button v-if="!isProject" icon="pi pi-refresh" text size="small" :title="t('script.reload')" :loading="loading" @click="load(true)" />
        <Button v-if="!isProject" icon="pi pi-download" text size="small" :title="t('script.exportDoc')" :disabled="!script" @click="exportDoc" />
      </div>
      <Message v-if="isProject && !project!.canEdit" severity="warn" class="script-msg">
        {{ t('project.readOnly', { reason: project!.readOnlyReason ?? '' }) }}
      </Message>
      <Message v-for="w in warnings" :key="w" severity="warn" class="script-msg">{{ w }}</Message>
      <Message v-if="error && !isProject" severity="error" class="script-msg">{{ error }}</Message>
      <div v-else-if="!store.cube && !isProject" class="script-hint">{{ t('script.needConnect') }}</div>
      <template v-else>
        <div class="script-search-bar">
          <InputText
            v-model="textSearch"
            :placeholder="t('script.searchPlaceholder')"
            size="small"
            class="script-search-input"
          />
          <Button
            v-if="selected"
            icon="pi pi-search-plus"
            text
            size="small"
            :label="t('script.findRefs')"
            :title="t('script.findRefs')"
            @click="findReferences"
          />
        </div>
        <div v-if="textSearch.trim()" class="script-search-results">
          <div class="script-search-summary">
            {{ searchCapped ? t('script.searchCapped', { n: searchHits.length }) : t('script.searchResults', { n: searchHits.length }) }}
          </div>
          <div v-if="!searchHits.length" class="script-hint">{{ t('script.searchNone') }}</div>
          <ul v-else class="script-list script-search-list">
            <li v-for="h in searchHits" :key="h.line" :title="h.text" @click="goToLine(h.line)">
              <span class="search-hit-line">{{ h.line }}</span>
              <span class="search-hit-text">{{ h.text }}</span>
            </li>
          </ul>
        </div>
        <ul v-else class="script-list">
          <template v-for="g in groupedCommands" :key="g.section">
            <li v-if="hasSections" class="script-group">
              {{ g.section === '' ? t('project.noSection') : g.section }}
            </li>
            <li
              v-for="c in g.items"
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
          </template>
        </ul>
      </template>
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
      <div v-if="showCalcProps" class="script-calcprops">
        <div class="script-deps-title">{{ t('calcprops.title') }}</div>
        <div class="calcprops-form">
          <label>{{ t('calcprops.formatString') }}<InputText v-model="propFormatString" size="small" /></label>
          <label>{{ t('calcprops.displayFolder') }}<InputText v-model="propDisplayFolder" size="small" /></label>
          <label>{{ t('calcprops.description') }}<InputText v-model="propDescription" size="small" /></label>
          <Button :label="t('calcprops.save')" size="small" :loading="savingProps" @click="saveCalcProps" />
        </div>
      </div>
    </div>
    <Dialog v-model:visible="showOpen" :header="t('project.open')" modal :style="{ width: '34rem' }">
      <div class="project-open">
        <InputText v-model="openPath" :placeholder="t('project.pathPlaceholder')" class="project-path"
          @keydown.enter="openProject()" />
        <Button :label="t('project.openBtn')" size="small" @click="openProject()" />
        <Button :label="t('project.browse')" size="small" text @click="browse(openPath || undefined)" />
      </div>
      <Message v-if="openError" severity="error">{{ openError }}</Message>
      <Message v-if="browseError" severity="error">{{ browseError }}</Message>
      <div v-if="browsing && listing" class="project-browser">
        <div class="project-browser-path">{{ listing.path }}</div>
        <div v-if="listing.drives.length" class="project-drives">
          <Button v-for="d in listing.drives" :key="d" :label="d" size="small" text
            class="project-drive-btn" @click="browse(d)" />
        </div>
        <ul class="script-list project-browser-list">
          <li v-if="listing.parent" @click="browse(listing.parent!)">
            <i class="pi pi-arrow-up" />{{ t('project.parentDir') }}
          </li>
          <li v-if="listing.directories.length" class="script-group">{{ t('project.folders') }}</li>
          <li v-for="d in listing.directories" :key="d.path" @click="browse(d.path)">
            <i class="pi pi-folder" />{{ d.name }}
          </li>
          <li class="script-group">{{ t('project.cubeFilesLabel') }}</li>
          <li v-for="f in listing.cubeFiles" :key="f.path" @click="openProject(f.path)">
            <i class="pi pi-file" />{{ f.name }}
          </li>
        </ul>
        <div v-if="!listing.cubeFiles.length" class="script-hint">{{ t('project.noCubeHere') }}</div>
      </div>
      <div v-if="recentProjects.length" class="project-recent">
        <div class="script-deps-title">{{ t('project.recent') }}</div>
        <ul class="script-list">
          <li v-for="r in recentProjects" :key="r.path" @click="openProject(r.path)">
            <i class="pi pi-file" />{{ r.path }}
          </li>
        </ul>
      </div>
    </Dialog>
    <Dialog v-model:visible="showDeploy" :header="t('project.deployTitle')" modal :style="{ width: '40rem' }">
      <p class="deploy-hint">{{ t('project.deployHint') }}</p>
      <div class="deploy-form">
        <label>{{ t('project.server') }}<InputText v-model="deployServer" /></label>
        <label>{{ t('project.catalog') }}<InputText v-model="deployCatalog" /></label>
      </div>
      <Message v-if="!isDevCatalog && deployCatalog" severity="warn">
        {{ t('project.devWarning', { catalog: deployCatalog }) }}
      </Message>
      <Message v-if="deployError" severity="error">{{ deployError }}</Message>
      <template v-if="deployDiffers">
        <Message severity="warn">{{ t('project.differs') }}</Message>
        <div class="script-deps-title">{{ t('project.serverScript') }}</div>
        <div class="deploy-diff-hint">{{ t('project.diffHint') }}</div>
        <div ref="diffHost" class="deploy-diff"></div>
        <Button :label="t('project.overwriteBtn')" severity="danger" :loading="deployBusy" @click="deploy(true)" />
      </template>
      <Button v-else :label="t('project.deployBtn')" :loading="deployBusy"
        :disabled="!deployServer.trim() || !deployCatalog.trim()" @click="deploy(false)" />
      <Panel v-if="deployLog.length" :header="t('deploylog.title')" toggleable collapsed class="deploy-log-panel">
        <table class="deploy-log-table">
          <thead>
            <tr>
              <th>{{ t('deploylog.when') }}</th>
              <th>{{ t('project.catalog') }}</th>
              <th></th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="entry in deployLog" :key="entry.id">
              <td>{{ new Date(entry.deployedUtc).toLocaleString() }}</td>
              <td>{{ entry.catalog ?? entry.server }}</td>
              <td>{{ entry.cubeName }} · {{ entry.scriptChars }} {{ t('deploylog.chars') }}</td>
              <td>
                <span v-if="entry.forced" class="deploy-log-forced">{{ t('deploylog.forced') }}</span>
              </td>
            </tr>
          </tbody>
        </table>
      </Panel>
    </Dialog>
    <Dialog v-model:visible="showRename" :header="t('rename.title')" modal :style="{ width: '32rem' }">
      <div class="rename-form">
        <label class="rename-field">
          {{ t('rename.newName') }}
          <InputText v-model="renameNewName" autofocus @keydown.enter="previewRename" />
        </label>
      </div>
      <Message v-if="renameError" severity="error">{{ renameError }}</Message>
      <Message v-if="renamePreview" severity="info">
        {{ t('rename.occurrences', { n: renamePreview.occurrences }) }}
      </Message>
      <template #footer>
        <Button :label="t('common.cancel')" severity="secondary" text @click="showRename = false" />
        <Button :label="t('rename.preview')" severity="secondary" :loading="renameBusy"
          :disabled="renameApplyDisabled" @click="previewRename" />
        <Button :label="t('rename.apply')" icon="pi pi-check" :loading="renameBusy"
          :disabled="renameApplyDisabled" @click="applyRename" />
      </template>
    </Dialog>
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
.script-group {
  font-weight: 600;
  color: var(--p-text-muted-color);
  cursor: default;
  padding-top: 0.5rem;
}
.script-search-bar {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0 0.5rem 0.3rem;
}
.script-search-input {
  flex: 1;
}
.script-search-results {
  flex: 1;
  overflow: auto;
  display: flex;
  flex-direction: column;
}
.script-search-summary {
  padding: 0.2rem 0.6rem 0.4rem;
  font-size: 0.78rem;
  color: var(--p-text-muted-color);
}
.script-search-list {
  flex: 1;
}
.script-search-list li {
  display: flex;
  gap: 0.5rem;
  align-items: baseline;
}
.search-hit-line {
  flex-shrink: 0;
  min-width: 2.2em;
  color: var(--p-text-muted-color);
  font-variant-numeric: tabular-nums;
  font-size: 0.78rem;
}
.search-hit-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono, monospace);
  font-size: 0.8rem;
}
.project-open {
  display: flex;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}
.project-path {
  flex: 1;
}
.project-recent {
  max-height: 14rem;
  overflow: auto;
}
.project-browser {
  margin-bottom: 0.75rem;
  border: 1px solid var(--p-surface-700);
  border-radius: 4px;
}
.project-browser-path {
  padding: 0.4rem 0.6rem;
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--p-text-muted-color);
  border-bottom: 1px solid var(--p-surface-700);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.project-drives {
  display: flex;
  flex-wrap: wrap;
  gap: 0.15rem;
  padding: 0.25rem 0.4rem;
  border-bottom: 1px solid var(--p-surface-700);
}
.project-drive-btn {
  padding: 0.15rem 0.5rem;
}
.project-browser-list {
  max-height: 14rem;
  overflow: auto;
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
.script-calcprops {
  border-top: 1px solid var(--p-surface-700);
  padding-bottom: 0.5rem;
}
.calcprops-form {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0 0.6rem 0.4rem;
}
.calcprops-form label {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  font-size: 0.85rem;
}
.script-editor {
  flex: 1;
  min-width: 0;
}
.deploy-hint {
  font-size: 0.85rem;
  color: var(--p-text-muted-color);
  margin-top: 0;
}
.deploy-form {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
}
.deploy-form label {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  font-size: 0.85rem;
}
.rename-form {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
}
.rename-field {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  font-size: 0.85rem;
}
.deploy-diff-hint {
  font-size: 0.78rem;
  color: var(--p-text-muted-color);
  margin-bottom: 0.3rem;
}
.deploy-diff {
  height: 300px;
  width: 100%;
  border: 1px solid var(--p-surface-700);
  border-radius: 4px;
  margin-bottom: 0.75rem;
}
.deploy-log-panel {
  margin-top: 0.75rem;
  font-size: 0.8rem;
}
.deploy-log-table {
  width: 100%;
  max-height: 220px;
  overflow-y: auto;
  display: block;
  border-collapse: collapse;
}
.deploy-log-table thead,
.deploy-log-table tbody {
  display: block;
}
.deploy-log-table tr {
  display: flex;
  gap: 0.5rem;
}
.deploy-log-table th,
.deploy-log-table td {
  flex: 1;
  min-width: 0;
  padding: 0.2rem 0.3rem;
  text-align: left;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.deploy-log-table th {
  color: var(--p-text-muted-color);
  font-weight: 600;
}
.deploy-log-forced {
  background: var(--p-orange-900, #7c2d12);
  color: var(--p-orange-100, #ffedd5);
  border-radius: 3px;
  padding: 0.05rem 0.35rem;
  font-size: 0.72rem;
}
</style>
