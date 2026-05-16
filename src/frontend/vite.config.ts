import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'fs'
import path from 'path'

const backendWwwroot = path.resolve(__dirname, '../backend/CopilotMedallion.Api/wwwroot')

function pruneStaleIndexAssets() {
  return {
    name: 'prune-stale-index-assets',
    closeBundle() {
      const indexPath = path.join(backendWwwroot, 'index.html')
      const assetsDir = path.join(backendWwwroot, 'assets')
      if (!fs.existsSync(indexPath) || !fs.existsSync(assetsDir)) return

      const html = fs.readFileSync(indexPath, 'utf8')
      const referenced = new Set(
        [...html.matchAll(/\/assets\/(index-[^"']+)/g)].map(match => match[1])
      )

      for (const file of fs.readdirSync(assetsDir)) {
        if (file.startsWith('index-') && !referenced.has(file)) {
          fs.rmSync(path.join(assetsDir, file), { force: true })
        }
      }
    },
  }
}

export default defineConfig({
  plugins: [react(), pruneStaleIndexAssets()],
  build: {
    outDir: backendWwwroot,
    // IMPORTANT: don't wipe the entire wwwroot — it also holds /workload/ for the Fabric bundle.
    // Vite still overwrites index.html; the plugin above prunes stale index-* assets only.
    emptyOutDir: false,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5174'
    }
  }
})
