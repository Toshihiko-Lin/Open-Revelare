import { chromium } from 'playwright-core'
import { pathToFileURL } from 'node:url'
import { resolve } from 'node:path'
const browser = await chromium.launch({ channel: 'msedge' })
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } })
const errors = []
page.on('pageerror', e => errors.push(e.message))
await page.route('http**', route => route.abort())
await page.goto(pathToFileURL(resolve('../../gui-audit/ui-review.html')).href)
const count = await page.locator('#screen option').count()
if (count !== 19) throw new Error(`Expected 19 screens; got ${count}`)
for (const dark of [false, true]) {
  if (dark) await page.locator('#theme').click()
  for (let i = 0; i < count; i++) {
    await page.locator('#screen').selectOption(String(i))
    await page.waitForFunction(() => { const image = document.querySelector('#render'); return image.complete && image.naturalWidth > 0 })
  }
}
await page.locator('#theme').click()
await page.locator('#screen').selectOption('4')
await page.screenshot({ path: '../../gui-audit/ui-review-overview.png' })
await page.setViewportSize({ width: 390, height: 840 })
await page.locator('#next').click()
await page.locator('#previous').click()
if (await page.locator('#screen').inputValue() !== '4') throw new Error('Previous did not restore the prior screen')
if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('Narrow gallery overflows')
if (errors.length) throw new Error(errors.join('\n'))
console.log('Offline gallery: 19 screens × 2 themes, selection, next/previous controls, 390px layout, no page errors: passed.')
await browser.close()
