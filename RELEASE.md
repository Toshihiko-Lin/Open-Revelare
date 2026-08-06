# 发版清单

发一个版本要动**两条通道**，而且第二条是手工的。只做第一条，仓库里看得见新版本，
但大陆用户——也就是绝大多数用户——不会收到任何通知。

| 通道 | 谁在读 | 更新方式 |
|---|---|---|
| GitHub Releases API | 新版（≥1.0.0）能连上 github.com 的用户 | 打 tag，CI 自动建 draft，**人工点发布** |
| Gitee Releases API | 新版（≥1.0.0）所有用户（race 模式，谁先回谁赢） | **人工**把 GitHub release 搬过去 |
| `version.json` | 旧版 Python 0.8.0 构建 | **人工**推 Gitee `Toshihiko-Lin/revelare-release` |

新版客户端 (≥1.0.0) 两条 Release API 并发查、谁先响应谁赢，见 [`Updater`](src/OpenRevelare.Gui/Services/Updater.cs)。
旧版 (0.8.0) 仍走 `version.json`。

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
- [ ] **点 Publish release。** GitHub 的 `/releases/latest` 不返回 draft 和 prerelease，
      在点下去之前，所有客户端的检测结果都是「已是最新版本」。
- [ ] **手动把这个 release 搬到 Gitee。** 在 `https://gitee.com/Toshihiko-Lin/revelare/releases`
      新建一个标签和 release，标签名与 GitHub 一致（例如 `1.0.0`，不带 v），正文与 GitHub 一致，
      把 GitHub release 的几个包（setup.exe / AppImage / dmg）上传为附件。
      新版客户端 (≥1.0.0) 会同时查询两个 API，谁先响应谁赢——搬到 Gitee 之后大陆用户才能收到通知。

## 四、推 manifest —— 别忘了这步（仅旧版 0.8.0 需要）

旧版 Python 构建 (0.8.0) 仍然轮询 `version.json`，要手工更新。
Gitee 仓库 `Toshihiko-Lin/revelare-release` 根目录的 `version.json`：

```json
{
  "version": "1.0.0",
  "release_date": "2026-08-04",
  "download_url": "https://gitee.com/Toshihiko-Lin/revelare/releases/download/1.0.0/Revelare-1.0.0-setup.exe",
  "changelog": "<b>v1.0.0</b><br><br>…"
}
```

- [ ] `version` 与 csproj、与 tag 一致。**旧版客户端 (0.8.0)** 按它跟自己的版本比大小，
      写小了或者忘了改，等于这次发版对那条通道不存在。新版 (≥1.0.0) 不再读这个文件。
- [ ] `download_url` 指向能直连的镜像（Gitee / 网盘），不要指向 GitHub——
      这条通道存在的理由就是旧版 0.8.0 用户中有人连不上 GitHub。
- [ ] `changelog` 是 **HTML**（`<b>` / `<br>`），不是 Markdown。
      旧版客户端会摊平成纯文本再显示。
- [ ] 目前它只有中文，英文界面下弹出的更新通知正文也是中文。

`https://revelare.netlify.app/version.json` 302 到这个文件，缓存 60 秒，
推完约一分钟后生效。

## 五、验证

- [ ] **旧版 (0.8.0) 通道**：`curl -sL https://revelare.netlify.app/version.json` 读到的是新版本号。
- [ ] **新版 (≥1.0.0) 通道**：
      - GitHub API: `curl -s https://api.github.com/repos/Toshihiko-Lin/Open-Revelare/releases/latest | grep tag_name`
        返回新 tag。
      - Gitee API: `curl -s https://gitee.com/api/v5/repos/Toshihiko-Lin/revelare/releases/latest | grep tag_name`
        返回新 tag。
- [ ] 拿上一个版本的安装包装一台干净机器，等 3 秒看有没有弹「发现新版本」。
      两条通道任何一条通就会弹——所以这一步过了，只说明**至少有一条**通。
