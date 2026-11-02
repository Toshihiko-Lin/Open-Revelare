import React, { useState, type CSSProperties } from 'react'
import { createRoot } from 'react-dom/client'
import { Stage, Window, States, between, type Act, type Rect } from '@stageshow'
import { ArrowDownToLine, ArrowLeftRight, ChevronDown, ChevronLeft, ChevronRight, Crop, Expand, Film, FolderOpen, Grid2X2, Maximize, Moon, MousePointer2, Plus, RotateCcw, RotateCw, Scan, SlidersHorizontal, Sun, Undo2, Redo2 } from 'lucide-react'
import photo from './frame.jpg'
import './workbench.css'

// Adapted from StageShow hero-demo's Stage/Act measured marquee, not a recording
// of Avalonia. Numeric values and the histogram are illustrative UI data.
type S = { sampling: boolean; dragging: boolean; box: Rect | null; done: boolean; compare: boolean }
const initial: S = { sampling: false, dragging: false, box: null, done: false, compare: false }
const poster: S = { ...initial, done: true }
async function script(a: Act<S>) {
  await a.wait(800)
  await a.move({ at: 'sample', x: .14, y: .5 })
  await a.wait(250)
  await a.click()
  await a.set({ sampling: true })
  a.mark('sampling')
  await a.wait(400)
  const patch = a.rect('base-patch')
  const start = { x: patch.x, y: patch.y }
  await a.move(start)
  await a.drag({ x: patch.x + patch.width, y: patch.y + patch.height }, at => a.put({ dragging: true, box: between(start, at) }), { duration: 1000 })
  await a.set({ dragging: false, done: true, sampling: false })
  a.mark('sampled')
  await a.wait(1600)
  await a.move({ at: 'compare', x: .14, y: .5 })
  await a.click()
  await a.set({ compare: true })
  a.mark('compare')
  await a.wait(1100)
  await a.click()
  await a.set({ compare: false })
  await a.move('rest')
  a.mark('result')
  await a.wait(1800)
}

function Instrument({ label, value, min = -1, max = 3, onChange }: { label: string; value: number; min?: number; max?: number; onChange?: (value: number) => void }) {
  const [local, setLocal] = useState(value)
  const shown = onChange ? value : local
  const change = onChange ?? setLocal
  const pct = (shown - min) / (max - min) * 100
  return <div className="instrument"><label>{label}</label><input aria-label={`${label} 滑杆`} type="range" min={min} max={max} step="0.001" value={shown} onChange={e => change(+e.target.value)} style={{ '--fill': `${pct}%` } as CSSProperties}/><input aria-label={`${label} 数值`} type="number" min={min} max={max} step="0.001" value={shown.toFixed(3)} onChange={e => { if (e.target.value) change(Math.max(min, Math.min(max, +e.target.value))) }} /></div>
}

function Histogram() {
  // A silhouette for layout evaluation, not computed from the example JPEG.
  return <svg className="histogram" viewBox="0 0 272 68" aria-label="直方图，示意数据" role="img">
    <path className="hist-grid" d="M0 66H272 M68 6V66 M136 6V66 M204 6V66"/>
    <path fill="#7088a0" opacity=".6" d="M0 66L2 21L4 58L15 62L28 60L41 61L54 57L68 59L82 54L96 55L110 48L124 51L138 39L150 44L161 35L174 40L185 31L198 41L209 43L221 53L235 55L248 61L272 64V66Z"/>
    <path fill="#88a292" opacity=".65" d="M0 66L2 31L5 64L25 63L45 62L65 60L85 58L105 51L121 46L135 37L147 41L159 31L170 38L180 35L194 48L210 50L229 61L272 65V66Z"/>
    <path fill="#bc8c84" opacity=".6" d="M0 66L2 39L4 64L32 65L52 63L72 61L92 58L112 55L132 50L150 44L163 39L175 32L182 35L190 24L199 33L209 49L222 54L241 63L272 65V66Z"/>
  </svg>
}

function Scene({ s, compact, playing }: { s: S; compact: boolean; playing: boolean }) {
  const [tab, setTab] = useState(0)
  const [negative, setNegative] = useState(false)
  const [compare, setCompare] = useState(false)
  const [hints, setHints] = useState(false)
  const [manualSample, setManualSample] = useState(false)
  const [black, setBlack] = useState<number | null>(null)
  const [white, setWhite] = useState(2.142)
  const [exposure, setExposure] = useState(0)
  const [selected, setSelected] = useState(2)
  const [sync, setSync] = useState(false)
  const [rotation, setRotation] = useState(0)
  const [zoom, setZoom] = useState(false)
  const sampling = manualSample || s.sampling
  const showCompare = compare || s.compare
  const blackValue = black ?? (s.done ? .426 : .382)
  const reset = () => { setBlack(null); setWhite(2.142); setExposure(0); setRotation(0) }
  const finishSample = () => { if (manualSample) { setBlack(.426); setManualSample(false) } }
  return <div className="workbench" tabIndex={0} data-compact={compact} onKeyDown={e => {
    if ((e.target as HTMLElement).matches('input,select,textarea')) return
    if (e.key.toLowerCase() === 'n') setNegative(v => !v)
    if (e.key.toLowerCase() === 'k') setCompare(v => !v)
    if (e.key.toLowerCase() === 'f') setZoom(false)
    if (e.key === '?') setHints(v => !v)
    if (e.key === 'Escape') { setHints(false); setManualSample(false) }
  }}>
    <div className="appbar">
      <div className="roll-title"><Film/><span>未命名卷</span><span className="subtle">6 张</span></div>
      <nav aria-label="工作区"><button disabled title="此稿聚焦修片工作区"><Grid2X2/>图库</button><button className="selected" title="修片（D）"><SlidersHorizontal/>修片</button></nav>
      <div className="header-actions"><button title="重置演示调整" onClick={reset}><RotateCcw/></button><span className="rule"/><button title="导入演示说明" onClick={() => document.getElementById('import-info')?.showPopover()}><Plus/>导入</button></div>
    </div>
    <div className="desk">
      {!compact && <aside className="filmrail" aria-label="卷内底片">
        <div className="rail-top"><Film/><span>全部底片</span></div>
        <div className="frames" data-crop>{Array.from({ length: 6 }, (_, i) => <button key={i} className={`frame ${selected === i ? 'current' : ''}`} onClick={() => { setSelected(i); setBlack(null) }} aria-label={`选择示例帧 ${i + 1}`} aria-pressed={selected === i}>
          <div className="thumb"><img src={photo} alt="底片示例" style={{ objectPosition: `${35 + i * 6}% 50%`, filter: `brightness(${.85 + i * .045})` }}/></div><span className="frame-meta"><span>{String(i + 1).padStart(2, '0')}</span>{selected === i && <span className="current-mark">当前</span>}</span>
        </button>)}</div>
        <button className="rail-add" onClick={() => document.getElementById('import-info')?.showPopover()} title="导入底片"><Plus/></button>
      </aside>}
      <main className="viewer">
        <div className="viewerbar"><div className="view-switch"><button aria-pressed={negative} title="查看负片（N）" onClick={() => setNegative(v => !v)}><Film/>负片<kbd>N</kbd></button><button data-target="compare" aria-pressed={showCompare} title="前后对比（K）" onClick={() => setCompare(v => !v)}><ArrowLeftRight/>对比<kbd>K</kbd></button></div>
          <div className="zoom-tools"><span className="subtle">{zoom ? '100%' : '适合'}</span><button title="适合窗口（F）" onClick={() => setZoom(false)}><Expand/></button><button title="实际像素（Ctrl+1）" onClick={() => setZoom(v => !v)}><Maximize/></button><button className="hint-trigger" aria-pressed={hints} title="快捷键（?）" onClick={() => setHints(v => !v)}>?</button></div>
        </div>
        {hints && <div className="shortcut-shelf"><span><kbd>N</kbd>负片</span><span><kbd>K</kbd>对比</span><span><kbd>F</kbd>适合</span><span><kbd>Esc</kbd>退出工具</span></div>}
        <div className="image-surround" data-sampling={sampling} data-target="rest">
          <div className={`image-sheet ${zoom ? 'zoomed' : ''}`} onClick={finishSample} data-cursor={sampling ? 'crosshair' : 'pointer'}>
            <img className="master-photo" src={photo} alt="树与落日的底片正像，项目现有素材" style={{ filter: negative ? 'invert(1)' : `brightness(${2 ** exposure})`, transform: `rotate(${rotation}deg)` }}/>
            <div className="base-patch" data-target="base-patch" />
            <div className="comparison" data-visible={showCompare}><img src={photo} alt="调整前示意"/><span>调整前</span></div>
          </div>
          <div className="sampling-prompt" data-visible={sampling} aria-hidden={!sampling}><Scan/>框选未曝光的片基<span><kbd>Esc</kbd>退出</span></div>
        </div>
        <div className="image-footer"><span className="frame-counter">{String(selected + 1).padStart(2, '0')}<span> / 06</span></span><div className="orientation"><button title="向左旋转" onClick={() => setRotation(v => v - 90)}><RotateCcw/></button><button title="向右旋转" onClick={() => setRotation(v => v + 90)}><RotateCw/></button><button title="裁切为画幅示意" onClick={() => setZoom(v => !v)}><Crop/></button></div><span className="subtle filename">示例底片.tif</span></div>
      </main>
      <aside className="inspector">
        <div className="meter"><Histogram/><div className="meter-scale"><span>0</span><span>0.5</span><span>1</span></div></div>
        <div className="panel-tabs" role="tablist" aria-label="调整面板"><button role="tab" aria-selected={tab === 0} onClick={() => setTab(0)}>底片校准</button><button role="tab" aria-selected={tab === 1} onClick={() => setTab(1)}>成片调整</button></div>
        <div className="parameter-scroll" role="tabpanel" data-crop>
          {tab === 0 ? <>
            <div className="auto-actions"><button className="primary" onClick={() => { setBlack(.426); setWhite(2.142); setSync(true) }}>自动整卷</button><button onClick={() => { setBlack(.426); setWhite(2.142) }}>自动本帧</button></div>
            <section className="parameter-group"><div className="group-title"><span>黑端</span></div>
              <Instrument label="D_min" min={-.5} max={2} value={blackValue} onChange={setBlack}/>
              <div className="paired-actions"><button data-target="sample" aria-pressed={sampling} onClick={() => setManualSample(v => !v)}><Scan/>片基采样</button><button onClick={() => setBlack(.426)}>自动黑点</button></div>
              <details><summary>逐通道<ChevronDown/></summary><Instrument label="R" value={.198}/><Instrument label="G" value={.429}/><Instrument label="B" value={.651}/></details>
            </section>
            <section className="parameter-group"><div className="group-title"><span>白端</span></div><Instrument label="D_max" min={0} max={4} value={white} onChange={setWhite}/><div className="paired-actions"><button onClick={() => { setManualSample(true) }}><Scan/>高光采样</button><button onClick={() => setWhite(2.142)}>自动白点</button></div><details><summary>逐通道<ChevronDown/></summary><Instrument label="R" value={2.1}/><Instrument label="G" value={2.15}/><Instrument label="B" value={2.18}/></details></section>
            <section className="parameter-group"><div className="group-title"><span>色偏修正</span></div><div className="paired-actions"><button onClick={() => setBlack(.426)}><Scan/>灰卡采样</button><button onClick={() => setBlack(.426)}>智能修正</button></div></section>
            <details className="minor-group"><summary>镜头校正<ChevronRight/></summary><Instrument label="畸变" value={0}/><Instrument label="暗角" value={0}/></details>
          </> : <>
            <section className="parameter-group"><div className="group-title">明暗</div><Instrument label="曝光" value={exposure} min={-2} max={2} onChange={setExposure}/><Instrument label="对比" value={1} min={0} max={2}/><Instrument label="高光" value={0} min={-1} max={1}/><Instrument label="阴影" value={0} min={-1} max={1}/></section>
            <section className="parameter-group"><div className="group-title">色彩</div><Instrument label="色温" value={0}/><Instrument label="色调" value={0}/><Instrument label="饱和度" value={1}/></section>
          </>}
          <div className="common-tools"><details className="minor-group"><summary>齿孔遮罩<ChevronRight/></summary><label className="check-row"><input type="checkbox"/>显示遮罩</label></details><details className="minor-group"><summary>几何与裁切<ChevronRight/></summary><button onClick={() => setZoom(v => !v)}><Crop/>裁切预览</button></details></div>
        </div>
        {tab === 0 && <div className="sync-footer"><button className="sync-action" onClick={() => setSync(true)}><States value={sync ? 'done' : 'idle'} states={{ idle: <>同步标定到整卷</>, done: <>已同步到整卷</> }}/><span>6 张</span></button></div>}
      </aside>
    </div>
    <footer className="outputbar"><span className="output-label">输出</span><select aria-label="输出色彩空间"><option>Display P3</option><option>sRGB</option></select><span className="output-divider"/><select aria-label="胶片风格"><option>标准显示渲染</option><option>无风格</option></select><label className="hdr-option"><input type="checkbox"/>HDR</label><button className="export primary" onClick={() => document.getElementById('export-info')?.showPopover()}><ArrowDownToLine/>导出<ChevronDown/></button></footer>
    <div className="statusbar"><span>{sampling ? '片基采样' : '调整已保存在演示中'}</span><span>交互概念 · 示意数据{playing ? ' · 演示播放中' : ''}</span></div>
    <div id="import-info" popover="auto" className="demo-popover"><div>拖入底片，加入这一卷</div><p>生产版导入支持 RAW / TIFF 与文件夹。此处是视觉概念，不读取本地底片。</p><button onClick={() => document.getElementById('import-info')?.hidePopover()}>返回工作台</button></div>
    <div id="export-info" popover="auto" className="demo-popover"><div>导出当前卷</div><p>此处仅演示输出入口；图像处理与实际导出仍由桌面软件完成。</p><button onClick={() => document.getElementById('export-info')?.hidePopover()}>返回工作台</button></div>
  </div>
}

function Workbench({ dark, playing }: { dark: boolean; playing: boolean }) {
  return <Stage<S> width={1280} height={816} compact={{ below: 740, width: 390, height: 816 }} initial={initial} poster={poster} script={playing ? script : undefined} play="once" fidelity="realistic" theme={dark ? 'dark' : 'light'} className="revelare-study" accent={dark ? '#a3b9c8' : '#4c6678'} cursor={playing ? { at: 'rest', idle: 1000 } : undefined} tokens={{ '--ss-font': '"Segoe UI", "Microsoft YaHei", sans-serif', '--ss-canvas': dark ? '#222222' : '#f3f3f3', '--ss-surface': dark ? '#282828' : '#fafafa', '--ss-radius-window': '0px', '--ss-shadow-window': 'none' }}>
    {(s, _rect, layout) => <>{layout === 'compact' ? <Scene s={playing ? s : poster} compact playing={playing}/> : <Window title="OpenRevelare" lights="grey" className="studio-window"><Scene s={playing ? s : poster} compact={false} playing={playing}/></Window>}<div className="sample-marquee" data-visible={s.dragging} style={{ left: s.box?.x ?? 0, top: s.box?.y ?? 0, width: s.box?.width ?? 0, height: s.box?.height ?? 0 }}/></>}
  </Stage>
}

function App() {
  const [dark, setDark] = useState(new URLSearchParams(location.search).get('theme') === 'dark')
  const [run, setRun] = useState(0)
  const [playing, setPlaying] = useState(new URLSearchParams(location.search).has('play'))
  return <div className="review-page" data-dark={dark}><header className="review-toolbar"><span>OpenRevelare <span> / 工作台设计</span></span><div><button onClick={() => setDark(v => !v)} aria-label={dark ? '切换浅色' : '切换深色'}>{dark ? <Sun/> : <Moon/>}</button><button onClick={() => { setPlaying(true); setRun(v => v + 1) }}><MousePointer2/>{playing ? '重播采样' : '演示采样'}</button></div></header><Workbench key={run} dark={dark} playing={playing}/></div>
}
createRoot(document.getElementById('root')!).render(<App/> )
