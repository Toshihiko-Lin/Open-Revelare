import { existsSync, mkdirSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
const commit = '802057409787133703c56001080169e347305572'
const path = '.vendor/stageshow'
mkdirSync('.vendor', { recursive: true })
if (!existsSync(path)) execFileSync('git', ['clone', '--filter=blob:none', '--sparse', 'https://github.com/benjitaylor/skills.git', path], { stdio: 'inherit' })
execFileSync('git', ['sparse-checkout', 'set', 'stageshow'], { cwd: path, stdio: 'inherit' })
execFileSync('git', ['checkout', '--detach', commit], { cwd: path, stdio: 'inherit' })
console.log(`StageShow source pinned at ${commit}; upstream license retained in .vendor/stageshow.`)
