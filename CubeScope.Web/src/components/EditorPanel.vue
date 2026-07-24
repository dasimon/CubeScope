<script setup lang="ts">
// Panneau éditeur MDX (Monaco). store.mdx est la source de vérité ;
// mdxRevision signale un remplacement externe (chargement depuis l'historique).
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { monaco } from '../monaco-mdx'
import '../mdx-completion' // enregistre le provider d'autocomplétion (effet de bord)
import { actions, store } from '../store'

const host = ref<HTMLElement | null>(null)
let editor: monaco.editor.IStandaloneCodeEditor | null = null

onMounted(() => {
  editor = monaco.editor.create(host.value!, {
    value: store.mdx,
    language: 'mdx',
    theme: 'cubescope-dark',
    automaticLayout: true,
    minimap: { enabled: false },
    fontSize: 14,
    scrollBeyondLastLine: false,
    fixedOverflowWidgets: true,
  })
  editor.onDidChangeModelContent(() => {
    store.mdx = editor!.getValue()
  })
  // Sélection courante : si non vide, F5/Ctrl+Entrée n'exécutent qu'elle (store.run())
  editor.onDidChangeCursorSelection(() => {
    const m = editor!.getModel()
    const sel = editor!.getSelection()
    store.selectedMdx = m && sel && !sel.isEmpty() ? m.getValueInRange(sel) : ''
  })
  // Exécution : Ctrl+Entrée et F5 (le F5 navigateur est intercepté au niveau app)
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => void actions.run())
  editor.addCommand(monaco.KeyCode.F5, () => void actions.run())
})

// Remplacement externe du MDX (historique) sans boucle de réédition
watch(
  () => store.mdxRevision,
  () => {
    if (editor && editor.getValue() !== store.mdx) editor.setValue(store.mdx)
  },
)

// Insertion au curseur (explorateur → double-clic sur un nœud)
watch(
  () => store.insertRevision,
  () => {
    if (!editor || !store.insertText) return
    const sel = editor.getSelection()
    if (sel) {
      editor.executeEdits('explorer', [{ range: sel, text: store.insertText, forceMoveMarkers: true }])
      editor.focus()
    }
  },
)

onBeforeUnmount(() => editor?.dispose())
</script>

<template>
  <div ref="host" class="editor-host" />
</template>

<style scoped>
.editor-host {
  width: 100%;
  height: 100%;
}
</style>
