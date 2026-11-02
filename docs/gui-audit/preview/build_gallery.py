"""Package real Avalonia renders into a portable, offline review page. No image editing."""
from pathlib import Path
import base64
import json

AUDIT = Path(__file__).resolve().parents[1]
SCREENS = [
    ("inspector-calibration", "参数栏 · 底片校准"),
    ("inspector-finishing", "参数栏 · 成片调整"),
    ("library", "图库"), ("import", "导入"), ("export", "导出"),
    ("split", "扫描分割"), ("contact-sheet", "整卷印样"),
    ("preferences", "偏好设置"), ("sync", "同步项"),
    ("roll-info", "卷信息"), ("rename", "重命名"),
    ("dependencies", "工程依赖"), ("save-failure", "保存恢复"),
    ("notice", "信息提示"), ("shortcuts", "快捷键"),
    ("report", "技术报告"), ("docs", "操作文档"),
    ("preferences-en-narrow", "偏好 · 英文窄窗"),
    ("export-en-narrow", "导出 · 英文窄窗"),
]
data = []
for name, label in SCREENS:
    item = {"label": label}
    for theme in ("light", "dark"):
        png = AUDIT / "renders" / "after" / f"{name}-{theme}.png"
        item[theme] = "data:image/png;base64," + base64.b64encode(png.read_bytes()).decode()
    data.append(item)

template = r'''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>OpenRevelare · 界面核查</title>
<link rel="icon" href="data:,"><style>
*{box-sizing:border-box}body{margin:0;font:13px/1.5 "Segoe UI","Microsoft YaHei",sans-serif;background:#e7e7e7;color:#292929}
body.dark{background:#171717;color:#e4e4e4}header{height:56px;display:flex;align-items:center;gap:16px;padding:0 20px;background:#fafafa;border-bottom:1px solid #0001}
.dark header{background:#252525;border-color:#fff1}header strong{font-size:14px;font-weight:600}header div{margin-left:auto;display:flex;gap:8px}
button,select{font:inherit;color:inherit;background:transparent;border:0;border-radius:4px;padding:7px 10px;cursor:pointer}button:hover{background:#80808020}button:focus-visible,select:focus-visible{outline:2px solid #7595ac;outline-offset:2px}
select{background:#80808012;min-width:190px}select option{color:#292929;background:#fafafa}
main{display:grid;grid-template-columns:196px 1fr;height:calc(100vh - 90px)}nav{padding:16px 8px;overflow:auto;background:#f7f7f7;border-right:1px solid #0001}.dark nav{background:#242424;border-color:#fff1}
nav button{display:block;width:100%;text-align:left;margin-bottom:4px;padding:8px 12px}nav button[aria-current=true]{background:#dce4ea;color:#35566d;font-weight:600}.dark nav button[aria-current=true]{background:#35424b;color:#d6e4ed}
section{overflow:auto;display:flex;align-items:flex-start;justify-content:center;padding:24px}img{display:block;max-width:100%;height:auto;box-shadow:0 2px 12px #0001}footer{height:34px;padding:8px 20px;color:#666;font-size:11px}.dark footer{color:#aaa}
@media(max-width:700px){main{grid-template-columns:1fr}nav{display:none}header{padding:0 12px;gap:8px}header strong{display:none}header div{margin-left:0}select{min-width:0;max-width:56vw}section{padding:16px}footer{padding:8px 12px}}
</style></head><body><header><strong>OpenRevelare · 界面核查</strong><div><select id="screen" aria-label="选择界面"></select><button id="previous" aria-label="上一界面">上一页</button><button id="next" aria-label="下一界面">下一页</button><button id="theme" aria-pressed="false">深色</button></div></header>
<main><nav aria-label="所有界面"></nav><section><img id="render" alt=""></section></main><footer>Avalonia 实际控件渲染 · 演示数据 · 不含原生图像呈现与 HDR 验收</footer>
<script>const screens=__DATA__;let index=0,dark=false;const select=document.querySelector('#screen'),nav=document.querySelector('nav'),picture=document.querySelector('#render');
screens.forEach((s,i)=>{const o=document.createElement('option');o.value=i;o.textContent=s.label;select.append(o);const b=document.createElement('button');b.textContent=s.label;b.onclick=()=>{index=i;update()};nav.append(b)});
function update(){select.value=index;document.body.classList.toggle('dark',dark);picture.src=screens[index][dark?'dark':'light'];picture.alt=screens[index].label+' · '+(dark?'深色':'浅色');document.querySelector('#theme').textContent=dark?'浅色':'深色';document.querySelector('#theme').setAttribute('aria-pressed',dark);[...nav.children].forEach((b,i)=>b.setAttribute('aria-current',i===index));document.querySelector('section').scrollTop=0}
select.onchange=()=>{index=+select.value;update()};document.querySelector('#previous').onclick=()=>{index=(index+screens.length-1)%screens.length;update()};document.querySelector('#next').onclick=()=>{index=(index+1)%screens.length;update()};document.querySelector('#theme').onclick=()=>{dark=!dark;update()};update();</script></body></html>'''
(AUDIT / "ui-review.html").write_text(template.replace("__DATA__", json.dumps(data, ensure_ascii=False)), encoding="utf-8")
print(f"Packaged {len(data)} interfaces × 2 themes into {AUDIT / 'ui-review.html'}")
