import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: path.resolve(__dirname, '../backend/CopilotMedallion.Api/wwwroot'),
    // IMPORTANT: don't wipe the entire wwwroot — it also holds /workload/ for the Fabric bundle.
    // Vite will still overwrite its own outputs (index.html + assets/).
    emptyOutDir: false,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5174'
    }
  }
})
