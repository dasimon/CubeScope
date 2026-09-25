// i18n (vue-i18n) — FR by default (the author's daily use), EN for the community.
// Choice persisted in localStorage. The language is also sent on connect (Locale Identifier)
// → the cube's captions (measures, members) come back in that language if it has
// translations. Still in the server OS language (out of our control): SSAS errors and
// perfmon labels. Changing language AFTER connecting does not re-translate the cube (reconnect).
import { createI18n } from 'vue-i18n'
import fr from './locales/fr'
import en from './locales/en'

export type Locale = 'fr' | 'en'
const STORAGE_KEY = 'cubescope.locale'

function initial(): Locale {
  const saved = localStorage.getItem(STORAGE_KEY)
  return saved === 'en' ? 'en' : 'fr'
}

export const i18n = createI18n({
  legacy: false,
  locale: initial(),
  fallbackLocale: 'fr',
  globalInjection: true,
  messages: { fr, en },
})

// <html lang> must match from the start, not only after a language switch.
document.documentElement.lang = i18n.global.locale.value

export function setLocale(l: Locale): void {
  i18n.global.locale.value = l
  localStorage.setItem(STORAGE_KEY, l)
  document.documentElement.lang = l
}

export function currentLocale(): Locale {
  return i18n.global.locale.value as Locale
}

// Translation outside components (store, api) — same catalog.
export const t = i18n.global.t
