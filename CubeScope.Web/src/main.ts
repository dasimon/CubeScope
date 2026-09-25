import { createApp } from 'vue'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import Aura from '@primeuix/themes/aura'

import 'dockview-vue/dist/styles/dockview.css'
import 'primeicons/primeicons.css'
import './style.css'

import App from './App.vue'
import { i18n } from './i18n'

const app = createApp(App)
app.use(i18n)
app.use(PrimeVue, {
  theme: {
    preset: Aura,
    // Dev tool: permanent dark theme (class set on <html> in index.html)
    options: { darkModeSelector: '.p-dark' },
  },
})
app.use(ToastService)
app.use(ConfirmationService)
app.mount('#app')
