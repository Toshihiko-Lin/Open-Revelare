# Rehearsal: OpenRevelare workbench

Gold：StageShow `hero-demo`，commit `802057409787133703c56001080169e347305572`。保留其 Stage、Act、真实测量目标和框选结构；替换布局、词汇、素材、色彩及故事。Gold 原始静态截图存于本地 `captures/gold.png`，与本稿对照。该 gold 的画面焦点是网页批注，本稿是照片判断；不复制其终端卫星窗或卡片内容。

Beats：采样按钮 → 裸片基框选 → 黑端示例读数更新 → 对比 → 停留在正像。once 播放，重播是显式按钮动作；不自动倒退数据。

Captured：1280px 浅深主题，1024 / 740 / 390px 布局；宽屏与窄屏采样 contact sheet、open / sampling / sampled / compare / result states。390 是概念局部特写，不是手机版承诺。最终单文件由本机 Edge headless 打开，阻断所有 HTTP，图像完整且无 pageerror。

操作核查：N、K、?、Esc；数值输入；示例采样响应；整卷同步反馈；曝光预览；导出说明 popover；浅深主题；离线图片。`review.mjs` 全部通过，TypeScript 与 Vite 构建通过。

视觉复核：移除参数套框；统一标签/滑杆/数值对齐；修复 number input 的 content-box 越界；同步整卷固定在底部；采样提示保持挂载并淡出；窄屏去掉不适用的桌面窗框。滚动区明确允许滚动裁切，截图中底部半张缩略图是后续内容的线索。

StageShow film：已运行宽屏、390px 动态 `--gate` 和宽屏 `--states --gate`，**未通过**，不把截图或构建通过等同于 rehearsal passed。残留包括：闭合 details 内原生 input 被识别为 skeleton ladder；贴合分栏、原生表单的 1px 边界与滚动边缘被识别为 near-tangent / tight control / uneven padding；kit 的灰色窗口按钮被判定左右 inset 不等；底片黑边被识别为 light UI 的 dark slab。逐帧核查仍存在采样间隔，故不能保证每个显示帧连续。kit 的 Act 把 crosshair 框选也归入文本 selection，并发出未调用 caret 的 selection drift 提示；这里矩形来自图像区域的实测坐标，不是文本高亮，未伪造 caret 调用来压制提示。

Not checked：独立审稿人、真实触控设备、屏幕阅读器、Avalonia 与原生预览之间的交互、真实 RAW 密度分析、工程持久化与实际导出。这些不属于 React 视觉稿的验证结果。
