# 发版清单

发一个版本要动**三条通道**，而且只有第一条是自动的。只做第一条，仓库里看得见新版本，
但大陆用户——也就是绝大多数用户——不会收到任何通知，官网也还在推上一版的包。

| 通道 | 谁在读 | 更新方式 |
|---|---|---|
| GitHub Releases API | 新版（≥1.0.0）能连上 github.com 的用户 | 打 tag，CI 自动建 draft，**人工点发布** |
| Gitee Releases API | 新版（≥1.0.0）所有用户（race 模式，谁先回谁赢） | **人工**把 GitHub release 搬到 `Toshihiko-Lin/revelare-release` |
| `version.json` | **官网**（版号 + 6 个下载按钮）<br>+ 旧版 Python 0.8.0 构建 | **人工**推 Gitee `Toshihiko-Lin/revelare-release` |

新版客户端 (≥1.0.0) 两条 Release API 并发查、谁先响应谁赢，见 [`Updater`](src/OpenRevelare.Gui/Services/Updater.cs)。
旧版 (0.8.0) 仍走 `version.json`。

> **`version.json` 不是只给 0.8.0 的。** `revelare.netlify.app` 的首页和 `/download` 页
> 都在运行时 `fetch('/version.json')`，页面上的版号和全部 6 个下载按钮
> （Gitee ×3 + GitHub ×3）都由它驱动。不推它，官网就一直挂着上一版的号和上一版的包链接。
> 取不到时页面回落到 HTML 里写死的兜底值——也是上一版。所以这一步**每次发版都要做**，
> 与还管不管 0.8.0 无关。

> **Gitee 侧只认 `Toshihiko-Lin/revelare-release` 这一个仓库。** 客户端查的是
> [`GiteeRepo`](src/OpenRevelare.Gui/Services/Updater.cs)，Release 和 `version.json` 都在它里面。
> 另有一个旧的 `Toshihiko-Lin/revelare` 仓库，**没有任何客户端读它**——发到那儿等于没发，
> 而且不会报错：检测静默返回 null，用户看到的是「已是最新版本」。

---

## 一、发版前

- [ ] `src/OpenRevelare.Gui/OpenRevelare.Gui.csproj` 的 `<Version>` 是要发的号。
      这是**唯一**的版本来源：关于框、更新检测、`open-revelare.iss` 都读它，
      release.yml 的 guard job 会拿它和 tag 比，对不上直接失败。
- [ ] `CHANGELOG.md` 顶部那节改成正式版本号（别留「待发布」）。
- [ ] **仓库已经是 public。** 私有仓库对匿名客户端来说不存在：
      `api.github.com/repos/…/releases/latest` 返回 404，更新检测拿不到东西，
      README 的三个下载徽章全是死链。

## 二、打 tag，等 CI

```bash
git tag v1.0.0 && git push origin v1.0.0        # tag 必须是 v + csproj 的 <Version>
```

`release.yml` 会并行出三个平台的包（macOS 约 9 分钟，编 LibRaw 占大头），
然后建一个 **draft** release 把产物收进去。

- [ ] 四个 job 全绿。Linux / macOS 要跑 `packaging/**.sh`，
      这些脚本的可执行位记录在 git index 里（`100755`）——在 Windows 上新增脚本时
      记得 `git update-index --chmod=+x`，否则 runner 上是 Permission denied。

## 三、发布 draft + 搬 Gitee

- [ ] 补网盘 / Gitee 镜像链接，写正文。
- [ ] **正文就是更新弹窗的内容。** release 的 `body` 会被客户端原样取走，摊平成纯文本填进
      「发现新版本」弹窗的「更新说明：」一段（见 `Updater.MarkdownToText`）。
      所以别只写「见 CHANGELOG.md」——那样用户在弹窗里读到的就是这七个字。
      把这一版的要点直接写进正文。两个镜像的正文都要写，各自的弹窗读各自的。
      正文是**原样透传、不做翻译**的，英文界面的用户看到的也是你写的中文；
      在意的话就在正文里中英并排写。
- [ ] **点 Publish release。** GitHub 的 `/releases/latest` 不返回 draft 和 prerelease，
      在点下去之前，所有客户端的检测结果都是「已是最新版本」。
- [ ] **手动把这个 release 搬到 Gitee。** 在 `https://gitee.com/Toshihiko-Lin/revelare-release/releases`
      新建一个标签和 release，标签名与 GitHub 一致（例如 `v1.1.0`，带 v，与仓库现有的保持一致；
      客户端两种都认，`tag_name` 读进来会 `TrimStart('v')`），正文与 GitHub 一致，
      把 GitHub release 的几个包（setup.exe / AppImage / dmg）上传为附件。
      新版客户端 (≥1.0.0) 会同时查询两个 API，谁先响应谁赢——搬到 Gitee 之后大陆用户才能收到通知。
- [ ] 附件名保持 CI 产出的原名。客户端按**扩展名 + 架构**挑包
      （`.exe` / `.appimage` / `.dmg` 且匹配 `arm64`|`x86_64`，见 `Updater.PlatformAssetUrl`），
      不匹配产品名——Gitee 为每个 tag 自动生成的 `v1.1.0.zip` / `.tar.gz` 源码包因此会被跳过。
      一个平台都匹配不上时，「前往下载」会退回到 release 页面，不会是死按钮。

## 四、推 manifest —— 官网靠它，**别忘了这步**

这一步不影响 ≥1.0.0 的更新弹窗（那个走 Release API，见第三步），但**官网整个跟着它走**：
首页和 `/download` 页运行时 `fetch('/version.json')`，版号和 6 个下载按钮都由它填。
漏了这步，客户端能弹「发现新版本」，用户点到官网却下到上一版的包。
旧版 Python 构建 (0.8.0) 也还在轮询同一个文件。

Gitee 仓库 `Toshihiko-Lin/revelare-release` 的 `version.json`（`main` 分支根目录）：

```json
{
  "version": "1.1.0",
  "release_date": "2026-08-06",
  "download_url": "https://revelare.netlify.app/download",
  "changelog": "<b>v1.1.0</b><br><br>…",
  "downloads": {
    "windows": "https://gitee.com/Toshihiko-Lin/revelare-release/releases/download/v1.1.0/OpenRevelare-1.1.0-setup.exe",
    "macos":   "https://gitee.com/Toshihiko-Lin/revelare-release/releases/download/v1.1.0/OpenRevelare-1.1.0-arm64.dmg",
    "linux":   "https://gitee.com/Toshihiko-Lin/revelare-release/releases/download/v1.1.0/OpenRevelare-1.1.0-x86_64.AppImage"
  },
  "downloads_github": {
    "windows": "https://github.com/Toshihiko-Lin/Open-Revelare/releases/download/v1.1.0/OpenRevelare-1.1.0-setup.exe",
    "macos":   "https://github.com/Toshihiko-Lin/Open-Revelare/releases/download/v1.1.0/OpenRevelare-1.1.0-arm64.dmg",
    "linux":   "https://github.com/Toshihiko-Lin/Open-Revelare/releases/download/v1.1.0/OpenRevelare-1.1.0-x86_64.AppImage"
  }
}
```

- [ ] **`downloads` / `downloads_github` 两组六个链接全部更新**，别只改 `version`。
      官网按 `downloads.*`（Gitee，主按钮）和 `downloads_github.*`（次按钮）分别填；
      **某个平台的键缺了或为空，那个按钮就变成灰的「即将发布」**，不是回落到旧链接。
      三个平台的文件名见第三步。
- [ ] `download_url` 是**给旧版 0.8.0 和首页用的单一入口**，指向 `https://revelare.netlify.app/download`
      这个落地页而不是某个 exe——首页的「下载」按钮和 0.8.0 的更新弹窗都用它，
      落地页再按平台分流。分平台的精确链接在上面两组里。
      不管填落地页还是具体包，都得是国内能直连的（Netlify / Gitee / 网盘），别指向 GitHub——
      这条通道存在的理由就是有人连不上 GitHub。
- [ ] `version` 与 csproj、与 tag 一致。**官网的版号直接显示它**，
      旧版客户端 (0.8.0) 也按它跟自己的版本比大小。写小了或者忘了改，
      官网就还是上一版的号，0.8.0 那条通道也等于这次没发。
- [ ] `changelog` 是 **HTML**（`<b>` / `<br>`），不是 Markdown——
      注意与第三步的 release 正文相反，那边是 Markdown。旧版客户端会摊平成纯文本再显示。
      官网不读它，≥1.0.0 也不读，所以这个字段现在只影响 0.8.0 用户看到的弹窗。
- [ ] 目前它只有中文，英文界面下弹出的更新通知正文也是中文。

`https://revelare.netlify.app/version.json` 302 到这个文件，缓存 60 秒，
推完约一分钟后生效。

## 五、验证

- [ ] **新版 (≥1.0.0) 通道**——这两条都要过，缺一条就有一整片用户收不到通知：

```bash
# GitHub：返回新 tag（若仍是旧 tag，多半是 draft 没点 Publish）
curl -s https://api.github.com/repos/Toshihiko-Lin/Open-Revelare/releases/latest | grep '"tag_name"'

# Gitee：注意仓库是 revelare-release。顺便核对附件齐不齐——
# 三个平台的包都在，国内用户的「前往下载」才不会退回 release 页面
curl -s https://gitee.com/api/v5/repos/Toshihiko-Lin/revelare-release/releases/latest \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['tag_name']); [print(' -',a['name']) for a in d['assets']]"
```

- [ ] **官网 + 旧版 (0.8.0) 通道**——同一个 manifest，一起验：

```bash
# 版号对不对，6 个下载链接在不在、指没指向新 tag
curl -sL https://revelare.netlify.app/version.json | python3 -c "
import json,sys; d=json.load(sys.stdin)
print('version:', d['version'], '| download_url:', d['download_url'])
for k in ('downloads','downloads_github'):
    for p,u in (d.get(k) or {}).items(): print(f'  {k}.{p}:', u)
    if not d.get(k): print(f'  !! {k} 整组缺失，官网那三个按钮会变「即将发布」')
"
```

- [ ] 开 `https://revelare.netlify.app/download` 看一眼：标题版号是新的，
      6 个按钮都可点、没有灰的「即将发布」。页面有 HTML 兜底值，
      **manifest 没推成功时页面不会报错，只会安静地显示上一版**——所以必须眼看。
- [ ] 拿上一个版本的安装包装一台干净机器，等 3 秒看有没有弹「发现新版本」。
      **两条 Release API 任何一条通就会弹**——在能连 GitHub 的机器上这一步必过，
      它证明不了 Gitee 那条通了。国内通道只能靠上面的 curl 或断网/墙内环境验。
- [ ] 弹窗里「更新说明：」显示的是这一版的要点，不是「见 CHANGELOG.md」。
