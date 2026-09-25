<script setup lang="ts">
// Connection dialog: server (Integrated Security only — settled decision),
// then catalog selection. Pre-filled from recent connections.
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import Select from 'primevue/select'
import InputText from 'primevue/inputtext'
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import { useToast } from 'primevue/usetoast'
import { actions, store } from '../store'
import { setLocale, type Locale } from '../i18n'

const { t, locale } = useI18n()
const toast = useToast()

function errorToast(e: unknown) {
  toast.add({ severity: 'error', summary: t('toast.error'), detail: e instanceof Error ? e.message : String(e), life: 6000 })
}

const LANGS: { label: string; value: Locale }[] = [
  { label: 'FR', value: 'fr' },
  { label: 'EN', value: 'en' },
]

const server = ref('')

/**
 * Declaring a development server is done HERE, not at deploy time: if the checkbox
 * lived in the deploy dialog, the safeguard would be disarmed with one click under the
 * pressure of the action in progress. The list is persisted on the server side (SQLite).
 */
const isDevServer = computed({
  get: () =>
    store.devServers.some((s) => s.trim().toLowerCase() === server.value.trim().toLowerCase()),
  set: (v: boolean) => {
    if (server.value.trim()) actions.setDevServer(server.value.trim(), v).catch(errorToast)
  },
})
const catalog = ref<string | null>(null)

onMounted(async () => {
  // The dev list does not depend on a connection: load it now so the checkbox is right
  // before connecting, not only after.
  void actions.loadDevServers()
  await actions.loadRecent()
  if (store.recent.length > 0) {
    server.value = store.recent[0].server
    catalog.value = store.recent[0].catalog
  }
})

async function connect() {
  if (!server.value.trim()) return
  const wanted = catalog.value
  if (await actions.connect(server.value.trim())) {
    // Re-selects the last used catalog if it still exists
    try {
      if (wanted && store.catalogs.includes(wanted)) {
        await actions.setCatalog(wanted)
        store.showConnect = false
      } else if (store.catalogs.length === 1) {
        await actions.setCatalog(store.catalogs[0])
        store.showConnect = false
      }
    } catch (e) {
      errorToast(e) // stay in the dialog to choose another catalog
    }
    // Otherwise: stay in the dialog to choose the catalog
    catalog.value = store.catalog
  }
}

async function chooseCatalog() {
  if (!catalog.value) return
  try {
    await actions.setCatalog(catalog.value)
    store.showConnect = false
  } catch (e) {
    errorToast(e)
  }
}

function pickRecent(r: { server: string; catalog: string | null }) {
  server.value = r.server
  catalog.value = r.catalog
}
</script>

<template>
  <Dialog
    v-model:visible="store.showConnect"
    modal
    :header="t('connect.title')"
    :closable="store.connected"
    :style="{ width: '32rem' }"
  >
    <div class="connect-form">
      <div class="connect-lang">
        <Button
          v-for="l in LANGS"
          :key="l.value"
          :label="l.label"
          :severity="locale === l.value ? 'primary' : 'secondary'"
          size="small"
          text
          @click="setLocale(l.value)"
        />
      </div>
      <label>
        {{ t('connect.server') }}
        <InputText
          v-model="server"
          :placeholder="t('connect.serverPlaceholder')"
          :disabled="store.connecting"
          autofocus
          @keydown.enter="connect"
        />
      </label>

      <div class="connect-dev">
        <Checkbox v-model="isDevServer" input-id="devServer" binary :disabled="!server.trim()" />
        <label for="devServer">{{ t('connect.devServer') }}</label>
      </div>

      <div v-if="store.recent.length" class="connect-recent">
        <span class="connect-recent-label">{{ t('connect.recent') }}</span>
        <Button
          v-for="r in store.recent.slice(0, 5)"
          :key="r.server + '|' + (r.catalog ?? '')"
          :label="r.catalog ? `${r.server} · ${r.catalog}` : r.server"
          link
          size="small"
          @click="pickRecent(r)"
        />
      </div>

      <Message v-if="store.connectError" severity="error">{{ store.connectError }}</Message>

      <Button
        :label="t('connect.connect')"
        icon="pi pi-link"
        :loading="store.connecting"
        :disabled="!server.trim()"
        @click="connect"
      />

      <template v-if="store.connected && store.catalogs.length">
        <label>
          {{ t('connect.catalog') }}
          <Select
            v-model="catalog"
            :options="store.catalogs"
            :placeholder="t('connect.catalogPlaceholder')"
            @keydown.enter="chooseCatalog"
          />
        </label>
        <Button :label="t('connect.open')" icon="pi pi-database" :disabled="!catalog" @click="chooseCatalog" />
      </template>
    </div>
  </Dialog>
</template>

<style scoped>
.connect-form {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
}
.connect-form label {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  font-size: 0.9rem;
}
.connect-dev {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: -0.35rem;
}
/* `.connect-form label` lays out ALL the form's labels as a column (field above
   its caption). A checkbox needs the opposite: the text goes beside it, not
   below — without this reset to a row, the checkbox and its label stack up. */
.connect-dev label {
  flex-direction: row;
  font-size: 0.9rem;
  cursor: pointer;
}
.connect-recent {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.1rem;
  font-size: 0.85rem;
}
.connect-recent-label {
  color: var(--p-text-muted-color);
  margin-right: 0.3rem;
}
.connect-lang {
  display: flex;
  justify-content: flex-end;
  gap: 0.25rem;
  margin-bottom: -0.4rem;
}
</style>
