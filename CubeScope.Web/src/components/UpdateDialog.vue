<script setup lang="ts">
// Built-in update dialog: what is installed, what is available, the download progress, and how
// the last install ended. Opened from the status bar pill and from About.
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import ProgressBar from 'primevue/progressbar'
import { update, updateActions } from '../update'

const { t, locale } = useI18n()

const status = computed(() => update.view?.status ?? null)
const outcome = computed(() => update.view?.lastOutcome ?? null)
const idle = computed(() => status.value?.phase === 'Idle')
const restarting = computed(() => outcome.value?.kind === 'restarting')
const canInstall = computed(() => !!status.value?.available && idle.value && !restarting.value)
const sizeMb = computed(() =>
  status.value?.available ? (status.value.available.size / 1024 / 1024).toFixed(0) : '',
)
const lastCheck = computed(() =>
  status.value?.lastCheckUtc ? new Date(status.value.lastCheckUtc).toLocaleString(locale.value) : '',
)
const percent = computed(() => Math.round((status.value?.progress ?? 0) * 100))

function onAuto(value: boolean) {
  void updateActions.setAuto(value)
}
</script>

<template>
  <Dialog v-model:visible="update.showDialog" modal :header="t('update.title')" :style="{ width: '32rem' }">
    <div v-if="!status" class="upd">{{ t('update.reading') }}</div>

    <div v-else-if="!status.canUpdate" class="upd">
      <p class="upd-notice">{{ t('update.devBuild') }}</p>
    </div>

    <div v-else class="upd">
      <p class="upd-line">
        {{ t('update.installed') }} <span class="mono">{{ status.currentVersion }}</span>
      </p>

      <template v-if="status.available">
        <p class="upd-available">
          {{ t('update.available', { version: status.available.version }) }}
          <span class="upd-meta">{{ t('update.size', { mb: sizeMb }) }}</span>
        </p>
        <p>
          <a :href="status.available.pageUrl" target="_blank" rel="noopener noreferrer">{{ t('update.notes') }}</a>
        </p>
      </template>
      <p v-else-if="status.lastCheckUtc && !status.lastError" class="upd-line">{{ t('update.upToDate') }}</p>

      <p v-if="status.lastCheckUtc" class="upd-meta">{{ t('update.lastCheck', { when: lastCheck }) }}</p>

      <div v-if="status.phase === 'Downloading'" class="upd-progress">
        <ProgressBar :value="percent" :show-value="false" style="height: 6px" />
        <span class="upd-meta mono">{{ t('update.downloading', { percent }) }}</span>
      </div>
      <p v-else-if="status.phase === 'Installing'" class="upd-meta">{{ t('update.installing') }}</p>
      <p v-else-if="status.phase === 'Checking'" class="upd-meta">{{ t('update.checking') }}</p>

      <p v-if="restarting" class="upd-notice upd-ok">{{ t('update.restarting') }}</p>
      <p v-else-if="outcome?.kind === 'savedToDownloads'" class="upd-notice upd-warn">
        {{ t('update.savedToDownloads') }}
        <span class="mono upd-path">{{ outcome.detail }}</span>
      </p>
      <p v-else-if="outcome?.kind === 'failed'" class="upd-notice upd-danger">
        {{ t('update.failed', { reason: outcome.detail }) }}
      </p>
      <p v-else-if="status.lastError" class="upd-notice upd-warn">
        {{ t('update.checkFailed', { reason: status.lastError }) }}
      </p>
      <p v-if="update.error" class="upd-notice upd-danger">{{ update.error }}</p>

      <p v-if="canInstall" class="upd-meta">{{ t('update.restartHint') }}</p>

      <label class="upd-auto">
        <Checkbox
          :model-value="update.view?.autoCheck ?? true"
          binary
          input-id="updAuto"
          @update:model-value="onAuto"
        />
        <span>{{ t('update.auto') }}</span>
      </label>
    </div>

    <template #footer>
      <Button
        v-if="status?.canUpdate"
        :label="t('update.checkNow')"
        icon="pi pi-refresh"
        severity="secondary"
        size="small"
        :disabled="!idle || restarting"
        :loading="status?.phase === 'Checking'"
        @click="updateActions.check()"
      />
      <Button
        v-if="status?.available"
        :label="t('update.install')"
        icon="pi pi-download"
        size="small"
        :disabled="!canInstall"
        :loading="status?.phase === 'Downloading' || status?.phase === 'Installing'"
        @click="updateActions.install()"
      />
      <Button :label="t('common.close')" severity="secondary" text size="small" @click="update.showDialog = false" />
    </template>
  </Dialog>
</template>

<style scoped>
.upd {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}
.upd p {
  margin: 0;
}
.upd-available {
  font-weight: 560;
}
.upd-meta {
  color: var(--scope-text-2);
  font-size: 0.85rem;
  font-weight: 400;
  margin-left: 0.4rem;
}
p.upd-meta {
  margin-left: 0;
}
.upd a {
  color: var(--scope-face-top);
}
.upd-progress {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}
.upd-notice {
  padding: 0.5rem 0.7rem;
  border-radius: 4px;
  border-left: 3px solid var(--scope-line-2);
  background: var(--scope-bg-1);
  color: var(--scope-text-2);
}
.upd-ok {
  border-left-color: var(--scope-face-left);
  color: var(--scope-text);
}
.upd-warn {
  border-left-color: var(--scope-prod);
  background: var(--scope-prod-dim);
  color: var(--scope-text);
}
.upd-danger {
  border-left-color: var(--scope-danger);
  background: var(--scope-danger-dim);
  color: var(--scope-text);
}
.upd-path {
  display: block;
  margin-top: 0.3rem;
  overflow-wrap: anywhere;
  font-size: 0.85rem;
}
.upd-auto {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 0.4rem;
  cursor: pointer;
}
</style>
