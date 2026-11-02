# OpenRevelare 工作台设计稿

直接打开 [可操作预览](../workbench-preview.html)，无需安装依赖，也不连接网络。顶部可切换浅深主题、播放或重播采样。预览保留三栏与两个调整页；数值、切帧、负片/对比、快捷键提示和示例采样可操作。图库导航未在此稿展开。

这是 React 视觉与交互概念，不是 Avalonia 或负片处理引擎。直方图、读数、比较结果均为示意；六张缩略图复用仓库既有示例图，目的是检视节奏和选择状态。导入/导出入口明确说明边界，不读取用户文件。

## 实际使用的技能

- [StageShow](https://github.com/benjitaylor/skills/blob/802057409787133703c56001080169e347305572/stageshow/SKILL.md)：真实 `Stage`、`Window`、`States`、`Act` 与 film 工具。改编 `hero-demo` 的测量目标、光标、框选结构，换成摄影工作台；没有使用生图替代 UI。
- [Interface Design](https://github.com/Dammyjay93/interface-design/blob/main/.claude/skills/interface-design/SKILL.md)：领域探索、主次、比例、连续参数栏和组件约束，见 [DESIGN-NOTES.md](DESIGN-NOTES.md)。
- [Impeccable](https://github.com/pbakaus/impeccable/blob/main/skill/SKILL.src.md)：Operate、Quieter 与 Craft floor。尝试运行官方 Windows context launcher，未正常返回上下文；随后采用入口规定的直接读取项目回退。未声称自动检测通过。

## 源码运行

需要 Node 20.19+ 和 Git。在此目录执行 `npm ci`、`npm run setup`，然后 `npm run dev`。setup 将上游固定版本放进忽略的 `.vendor`，保留上游许可，不做全局安装。

`npm run build` 做 TypeScript 检查并构建，`pack.mjs` 生成上一级单文件预览。`node review.mjs` 使用本机 Edge 核查离线文件、主题、操作及布局并保存截图。StageShow 原始 kit 与核查工具未被修改。

检视记录与限制见 [REHEARSAL.md](REHEARSAL.md)。此稿独立保留为概念探索。后续已开始修改生产样式与窗口，最新状态以 [实际 Avalonia 界面总览](../../gui-audit/ui-review.html) 和 [审计实施进展](../../gui-audit/05-polish-progress.md) 为准。
