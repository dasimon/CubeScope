<script setup lang="ts">
// Dialogue de connexion : serveur (Integrated Security uniquement — décision actée),
// puis choix du catalogue. Pré-rempli par les connexions récentes.
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import Select from 'primevue/select'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { actions, store } from '../store'
import { setLocale, type Locale } from '../i18n'

const { t, locale } = useI18n()

const LANGS: { label: string; value: Locale }[] = [
  { label: 'FR', value: 'fr' },
  { label: 'EN', value: 'en' },
]

const server = ref('')
const catalog = ref<string | null>(null)

onMounted(async () => {
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
    // Re-sélectionne le dernier catalogue utilisé s'il existe encore
    if (wanted && store.catalogs.includes(wanted)) {
      await actions.setCatalog(wanted)
      store.showConnect = false
    } else if (store.catalogs.length === 1) {
      await actions.setCatalog(store.catalogs[0])
      store.showConnect = false
    }
    // Sinon : on reste dans le dialogue pour choisir le catalogue
    catalog.value = store.catalog
  }
}

async function chooseCatalog() {
  if (!catalog.value) return
  await actions.setCatalog(catalog.value)
  store.showConnect = false
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
    :style="{ width: '28rem' }"
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
