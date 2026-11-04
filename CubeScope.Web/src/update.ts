// Built-in update, SPA side. The server does all the network work (GitHub is never reached from
// the page: CSP + LocalRequestGuard); this module only reads its state and relays the user's
// choices. Polled, not pushed: a new release is a once-a-day event, the download is the only
// moment that needs a fast refresh.
import { reactive } from 'vue'
import { api, type UpdateView } from './api'

export const update = reactive({
  view: null as UpdateView | null,
  /** The dialog is opened from the status bar pill and from About. */
  showDialog: false,
  error: '' as string,
})

const IDLE_POLL_MS = 10 * 60_000
const BUSY_POLL_MS = 500
// The server's first automatic check runs 10 s after start: read its result a little later.
const FIRST_READ_MS = 15_000

let timer: number | undefined

function schedule(ms: number) {
  window.clearTimeout(timer)
  timer = window.setTimeout(() => void refresh(), ms)
}

function busy(view: UpdateView | null) {
  return !!view && view.status.phase !== 'Idle'
}

export async function refresh(): Promise<void> {
  try {
    update.view = await api.update()
  } catch {
    // Server restarting or gone: keep the last state, try again later.
  }
  schedule(busy(update.view) ? BUSY_POLL_MS : IDLE_POLL_MS)
}

export function startUpdatePolling(): void {
  schedule(FIRST_READ_MS)
}

async function run(action: () => Promise<UpdateView>) {
  update.error = ''
  try {
    update.view = await action()
  } catch (e) {
    update.error = e instanceof Error ? e.message : String(e)
  }
  schedule(busy(update.view) ? BUSY_POLL_MS : IDLE_POLL_MS)
}

export const updateActions = {
  check: () => run(api.updateCheck),
  install: () => run(api.updateInstall),
  setAuto: (enabled: boolean) => run(() => api.updateAuto(enabled)),
  open: () => {
    update.showDialog = true
    void refresh()
  },
}
