import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: {
    // In dev: CubeScope.Server started with `--port 5199 --no-browser`
    proxy: {
      '/api': 'http://127.0.0.1:5199',
      '/hubs': { target: 'http://127.0.0.1:5199', ws: true },
    },
  },
  build: {
    chunkSizeWarningLimit: 4000, // monaco-editor is large, and that is accepted (local tool)
  },
})
