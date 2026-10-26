// Display helpers shared by the State panel and the status bar.
import { ref } from 'vue'
import { currentLocale, t } from './i18n'
import type { FreshnessStatus, ObjectState } from './api'

/**
 * Current time, ticking every minute — a local clock, never a server call. The status bar
 * measures "last processed N h ago" against it: against the read time, a read done in the
 * morning would still claim "3 h" the next day.
 */
export const nowUtc = ref(new Date().toISOString())
setInterval(() => (nowUtc.value = new Date().toISOString()), 60_000)

/** Elapsed time between two ISO dates, as a short label ("45 min", "31 h", "3 j"). */
export function age(fromUtc: string, toUtc: string): string {
  const ms = Math.max(0, Date.parse(toUtc) - Date.parse(fromUtc))
  const minutes = Math.floor(ms / 60_000)
  if (minutes < 60) return t('processing.unitMinutes', { n: minutes })
  const hours = Math.floor(minutes / 60)
  // Hours up to two days: "31 h" reads better than "1 j" for a nightly job.
  if (hours < 48) return t('processing.unitHours', { n: hours })
  return t('processing.unitDays', { n: Math.floor(hours / 24) })
}

export function dateTime(utc: string | null): string {
  return utc ? new Date(utc).toLocaleString() : '—'
}

export function bytes(n: number): string {
  if (!n) return '—'
  const units = currentLocale() === 'fr' ? ['o', 'Ko', 'Mo', 'Go', 'To'] : ['B', 'KB', 'MB', 'GB', 'TB']
  let v = n
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  const digits = i === 0 || v >= 100 ? 0 : 1
  return `${v.toLocaleString(currentLocale(), { maximumFractionDigits: digits })} ${units[i]}`
}

export function stateLabel(state: ObjectState): string {
  return t(`processing.state${state}`)
}

/** PrimeVue Tag severity for a verdict. */
export function severity(status: FreshnessStatus): 'success' | 'warn' | 'danger' {
  return status === 'Unprocessed' ? 'danger' : status === 'Stale' ? 'warn' : 'success'
}
