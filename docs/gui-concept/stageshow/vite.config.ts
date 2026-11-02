import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
export default defineConfig({
  plugins: [react()], base: './', build: { assetsInlineLimit: 1000000 },
  resolve: { alias: { '@stageshow': fileURLToPath(new URL('./.vendor/stageshow/stageshow/kit/index.ts', import.meta.url)) }, dedupe: ['react', 'react-dom'] },
  server: { fs: { allow: ['../../../', '.'] } }
})
