import { readFileSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
let html = readFileSync('dist/index.html', 'utf8')
html = html.replace(/<script[^>]*src="([^"]+)"[^>]*><\/script>/g, (_, path) => `<script type="module">${readFileSync(resolve('dist', path), 'utf8').replace(/<\/script/gi, '<\\/script')}</script>`)
html = html.replace(/<link[^>]*rel="stylesheet"[^>]*href="([^"]+)"[^>]*>/g, (_, path) => `<style>${readFileSync(resolve('dist', path), 'utf8')}</style>`)
writeFileSync('../workbench-preview.html', html)
console.log('Standalone review: ../workbench-preview.html')
