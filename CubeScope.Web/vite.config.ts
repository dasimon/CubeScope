import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: {
    // En dev : CubeScope.Server lancé avec `--port 5199 --no-browser`
    proxy: {
      '/api': 'http://127.0.0.1:5199',
      '/hubs': { target: 'http://127.0.0.1:5199', ws: true },
    },
  },
  build: {
    chunkSizeWarningLimit: 4000, // monaco-editor est volumineux, c'est assumé (outil local)
  },
})
