<script setup lang="ts">
// Compare la requête courante entre le catalogue connecté et un autre catalogue du même
// serveur. Répond à « est-ce qu'un chiffre a bougé ? » après un changement de script.
// N'affiche que les ÉCARTS : sur un crossjoin large, deux grilles côte à côte seraient illisibles.
import { ref, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import Button from 'primevue/button'
import Message from 'primevue/message'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import { api, type CatalogComparison } from '../api'
import { store } from '../store'

const visible = defineModel<boolean>('visible', { required: true })

const { t } = useI18n()
const target = ref<string | null>(null)
const busy = ref(false)
const error = ref('')
const result = ref<CatalogComparison | null>(null)

/** Tous les catalogues sauf le courant : se comparer à soi-même n'a pas de sens. */
const targets = computed(() => store.catalogs.filter((c) => c !== store.catalog))

async function run() {
  const mdx = store.selectedMdx.trim() || store.mdx
  if (!target.value || !mdx.trim()) return
  busy.value = true
  error.value = ''
  result.value = null
  try {
    result.value = await api.compare(mdx, target.value)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}

function reset() {
  result.value = null
  error.value = ''
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    modal
    :header="t('compare.title')"
    :style="{ width: '54rem' }"
  >
    <div class="compare-form">
      <span class="compare-left">{{ store.catalog }}</span>
      <span class="compare-vs">↔</span>
      <Select
        v-model="target"
        :options="targets"
        :placeholder="t('compare.pickTarget')"
        size="small"
        class="compare-select"
        @change="reset"
      />
      <Button
        :label="t('compare.run')"
        icon="pi pi-play"
        size="small"
        :disabled="!target"
        :loading="busy"
        @click="run"
      />
    </div>
    <p class="compare-hint">{{ t('compare.hint') }}</p>

    <Message v-if="error" severity="error" class="compare-msg">{{ error }}</Message>

    <template v-if="result">
      <Message :severity="result.match ? 'success' : 'warn'" :closable="false" class="compare-msg">
        <span v-if="result.match">{{ t('compare.identical') }}</span>
        <span v-else>{{ result.summary }}</span>
      </Message>

      <div class="compare-counts">
        {{ t('compare.counts', {
          left: result.leftCatalog, leftCells: result.leftCells, leftMs: result.leftMs,
          right: result.rightCatalog, rightCells: result.rightCells, rightMs: result.rightMs,
        }) }}
      </div>

      <DataTable
        v-if="result.diffs.length"
        :value="result.diffs"
        scrollable
        scroll-height="22rem"
        size="small"
        class="compare-table"
      >
        <Column field="row" :header="t('compare.row')" class="col-num" />
        <Column field="column" :header="t('compare.column')" />
        <Column field="expected" :header="result.leftCatalog" class="col-num" />
        <Column field="actual" :header="result.rightCatalog" class="col-num" />
      </DataTable>

      <p v-if="result.diffCount >= 200" class="compare-capped">{{ t('compare.capped') }}</p>
    </template>
  </Dialog>
</template>

<style scoped>
.compare-form {
  display: flex;
  align-items: center;
  gap: 0.6rem;
}
.compare-left {
  font-weight: 600;
}
.compare-vs {
  color: var(--p-text-muted-color);
}
.compare-select {
  min-width: 14rem;
}
.compare-hint,
.compare-counts,
.compare-capped {
  font-size: 0.8rem;
  color: var(--p-text-muted-color);
  margin: 0.5rem 0 0;
}
.compare-msg {
  margin: 0.75rem 0 0;
}
.compare-table {
  margin-top: 0.5rem;
  font-variant-numeric: tabular-nums;
}
:deep(.col-num) {
  text-align: right;
  white-space: nowrap;
}
</style>
