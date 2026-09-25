// Markdown → HTML for AI answers. The text comes from an LLM (which may echo cube metadata or
// user input): it is sanitized before any v-html, and every link opens outside the application.
import DOMPurify from 'dompurify'
import { marked } from 'marked'

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A' && node.hasAttribute('href')) {
    node.setAttribute('target', '_blank')
    node.setAttribute('rel', 'noopener noreferrer')
  }
})

export function renderMarkdown(text: string): string {
  return DOMPurify.sanitize(marked.parse(text, { async: false }))
}
