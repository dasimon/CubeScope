<script setup lang="ts">
// MDX editor panel (Monaco). store.mdx is the source of truth;
// mdxRevision signals an external replacement (load from history).
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { monaco } from '../monaco-mdx'
// The named import is enough to load the module, which registers the autocompletion
// and hover providers along the way (side effect).
import { normalizeRef, refAtColumn } from '../mdx-completion'
import { actions, store } from '../store'

const { t } = useI18n()

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
  // Current selection: if non-empty, F5/Ctrl+Enter execute only that (store.run())
  editor.onDidChangeCursorSelection(() => {
    const m = editor!.getModel()
    const sel = editor!.getSelection()
    store.selectedMdx = m && sel && !sel.isEmpty() ? m.getValueInRange(sel) : ''
  })
  // Execution: Ctrl+Enter and F5 (the browser F5 is intercepted at app level)
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => void actions.run())
  editor.addCommand(monaco.KeyCode.F5, () => void actions.run())

  // Go to definition: jumps to the CREATE MEMBER / SET in the MDX Script. This is not a
  // Monaco DefinitionProvider (native F12), because the target is ANOTHER editor, in another
  // dockview panel — a provider must return a position in the current model.
  editor.addAction({
    id: 'mdx.gotoDefinition',
    label: t('editor.gotoDefinition'),
    // NOT F12: in Edge/Chrome, F12 opens the developer tools and cannot be
    // intercepted by page content. Alt+F12 (VS Code's "Peek Definition")
    // and Ctrl+Alt+G do get through — checked in the browser.
    keybindings: [
      monaco.KeyMod.Alt | monaco.KeyCode.F12,
      monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.KeyG,
    ],
    contextMenuGroupId: 'navigation',
    contextMenuOrder: 1,
    run: (ed) => {
      const position = ed.getPosition()
      const model = ed.getModel()
      if (!position || !model) return
      const ref = refAtColumn(model.getLineContent(position.lineNumber), position.column)
      if (!ref) return
      actions.requestDefinition(normalizeRef(ref.text))
    },
  })
})

// External replacement of the MDX (history) without an edit loop
watch(
  () => store.mdxRevision,
  () => {
    if (editor && editor.getValue() !== store.mdx) editor.setValue(store.mdx)
  },
)

// Insert at the cursor (explorer → double-click on a node)
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

/**
 * Drop of a member dragged from the explorer. The text is inserted where the user RELEASES, not at
 * the cursor: that is the whole point of the gesture compared with double-click, which inserts at the cursor.
 *
 * `preventDefault` on dragover is required, otherwise the browser refuses the drop — and
 * Monaco has its own internal drag (moving the selection): only drops carrying text that
 * come from elsewhere are handled.
 */
function onDragOver(e: DragEvent) {
  if (!e.dataTransfer) return
  e.preventDefault()
  e.dataTransfer.dropEffect = 'copy'
}

function onDrop(e: DragEvent) {
  const texte = e.dataTransfer?.getData('text/plain')
  if (!editor || !texte) return
  e.preventDefault()

  const cible = editor.getTargetAtClientPoint(e.clientX, e.clientY)
  const position = cible?.position ?? editor.getPosition()
  if (!position) return

  const range = new monaco.Range(
    position.lineNumber, position.column, position.lineNumber, position.column)
  editor.executeEdits('explorer-drop', [{ range, text: texte, forceMoveMarkers: true }])
  editor.setPosition({ lineNumber: position.lineNumber, column: position.column + texte.length })
  editor.focus()
}

onBeforeUnmount(() => editor?.dispose())
</script>

<template>
  <div ref="host" class="editor-host" @dragover="onDragOver" @drop="onDrop" />
</template>

<style scoped>
.editor-host {
  width: 100%;
  height: 100%;
}
</style>
