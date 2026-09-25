<script setup lang="ts">
// About: application version (read from the assembly, stamped from the release tag), runtime,
// local data folder and the connected SSAS server — what a bug report needs. No update check:
// it would call GitHub on every launch; the Releases link does the job on demand.
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { useToast } from 'primevue/usetoast'
import { api, type AboutInfo } from '../api'

const REPO = 'https://github.com/dasimon/CubeScope'

const { t } = useI18n()
const toast = useToast()
const visible = ref(false)
const about = ref<AboutInfo | null>(null)
const error = ref('')

// WebView2 identifies itself as Edge ("Edg/<version>"); in the browser fallback this is the browser.
const engine = computed(() => {
  const ua = navigator.userAgent
  return ua.match(/Edg\/[\d.]+/)?.[0] ?? ua.match(/(Chrome|Firefox)\/[\d.]+/)?.[0] ?? ua
})

const versionLine = computed(() =>
  about.value ? about.value.version + (about.value.commit ? ` (${about.value.commit})` : '') : '',
)

async function open() {
  visible.value = true
  error.value = ''
  try {
    about.value = await api.about()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

async function copy() {
  if (!about.value) return
  const a = about.value
  const text = [
    `CubeScope ${versionLine.value}`,
    `${a.runtime} · ${engine.value}`,
    a.ssasServer ? `SSAS ${a.ssasServer} ${a.ssasVersion ?? ''}`.trim() : null,
  ]
    .filter(Boolean)
    .join('\n')
  try {
    await navigator.clipboard.writeText(text)
    toast.add({ severity: 'success', summary: t('about.copied'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: t('results.copyFailed'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
  }
}
</script>

<template>
  <Button icon="pi pi-info-circle" size="small" severity="secondary" :title="t('about.title')" :aria-label="t('about.title')" @click="open" />
  <Dialog v-model:visible="visible" modal :header="t('about.title')" :style="{ width: '32rem' }">
    <p v-if="error" class="about-error">{{ error }}</p>
    <template v-else-if="about">
      <p class="about-name">CubeScope <span class="about-version">{{ versionLine }}</span></p>
      <p class="about-tagline">{{ t('about.tagline') }}</p>
      <dl class="about-grid">
        <dt>{{ t('about.runtime') }}</dt>
        <dd>{{ about.runtime }}</dd>
        <dt>{{ t('about.engine') }}</dt>
        <dd>{{ engine }}</dd>
        <dt>{{ t('about.ssas') }}</dt>
        <dd>{{ about.ssasServer ? `${about.ssasServer} · ${about.ssasVersion ?? '?'}` : t('about.notConnected') }}</dd>
        <dt>{{ t('about.data') }}</dt>
        <dd class="about-path">{{ about.dataFolder }}</dd>
        <dt>{{ t('about.license') }}</dt>
        <dd>MIT</dd>
      </dl>
      <p class="about-links">
        <a :href="REPO" target="_blank" rel="noopener noreferrer">GitHub</a>
        ·
        <a :href="`${REPO}/releases`" target="_blank" rel="noopener noreferrer">{{ t('about.releases') }}</a>
        ·
        <a :href="`${REPO}/issues`" target="_blank" rel="noopener noreferrer">{{ t('about.issues') }}</a>
      </p>
    </template>
    <template #footer>
      <Button :label="t('about.copy')" icon="pi pi-copy" severity="secondary" size="small" :disabled="!about" @click="copy" />
      <Button :label="t('common.close')" size="small" @click="visible = false" />
    </template>
  </Dialog>
</template>

<style scoped>
.about-name {
  margin: 0;
  font-size: 1.15rem;
  font-weight: 600;
}
.about-version {
  font-weight: 400;
  opacity: 0.8;
}
.about-tagline {
  margin: 0.25rem 0 1rem;
  opacity: 0.75;
}
.about-grid {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0.35rem 1rem;
  margin: 0;
}
.about-grid dt {
  opacity: 0.7;
}
.about-grid dd {
  margin: 0;
  overflow-wrap: anywhere;
}
.about-path {
  font-family: var(--p-font-family-mono, monospace);
  font-size: 0.85em;
}
.about-links {
  margin: 1rem 0 0;
}
.about-links a {
  color: var(--p-primary-color);
}
.about-error {
  color: var(--p-red-400);
}
</style>
