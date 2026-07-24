// i18n (vue-i18n) — FR par défaut (usage quotidien de l'auteur), EN pour la communauté.
// Choix persisté en localStorage. Ce qui vient du serveur SSAS (erreurs, libellés perfmon)
// reste dans la langue de l'OS serveur (français) — hors de notre contrôle.
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

export function setLocale(l: Locale): void {
  i18n.global.locale.value = l
  localStorage.setItem(STORAGE_KEY, l)
  document.documentElement.lang = l
}

export function currentLocale(): Locale {
  return i18n.global.locale.value as Locale
}

// Traduction hors composant (store, api) — même catalogue.
export const t = i18n.global.t
