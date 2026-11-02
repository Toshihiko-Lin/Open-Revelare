# GitHub GUI 设计 skill 筛选

检索与读取日期：2026-11-01。目标是胶片反转的专业桌面工具：整卷一致性、图像判断、数值编辑、批量目标和可恢复交付。以下为适配判断，不是基于安装量或星标的质量排名。

## 1. 主用：Avalonia Design Systems

来源：[wieslawsoltes/development-plugin-for-avalonia](https://github.com/wieslawsoltes/development-plugin-for-avalonia)，[SKILL.md](https://github.com/wieslawsoltes/development-plugin-for-avalonia/blob/main/skills/avalonia-design-systems/SKILL.md)。读取时 main 指向 `95cccf7118f42d9e7fbd479ccedeb3b012f4d421`。

该 skill 涵盖桌面外壳、专业工作流密度、设计 token、信息架构和状态反馈。本轮还读取其[任务导航](https://github.com/wieslawsoltes/development-plugin-for-avalonia/blob/main/references/professional-design/08-information-architecture-navigation-and-progressive-disclosure.md)、[复杂表单与密集操作](https://github.com/wieslawsoltes/development-plugin-for-avalonia/blob/main/references/professional-design/09-forms-decision-heavy-ux-and-data-dense-surfaces.md)、[适应布局与状态](https://github.com/wieslawsoltes/development-plugin-for-avalonia/blob/main/references/professional-design/04-responsive-layout-density-and-stateful-feedback.md)章节。

应用：保留现有导航，按用户当下任务整理局部工具；错误同时给修复动作；统一紧凑密度与可复用控件状态。用户已明确要沿现有框架改善直觉，因此不引入新的顶层流程导航。

适配限制：参考材料以 Avalonia 11.3.12 为基线，项目当前固定 11.2.3。`ContainerQuery` 等示例不能直接复制；本轮仅采用设计方法，实际布局用项目可用控件完成，或另行验证升级。引用的外部示例也不代表项目应该更换技术栈。

## 2. 主用：Interface Design

来源：[Dammyjay93/interface-design](https://github.com/Dammyjay93/interface-design)，[SKILL.md](https://github.com/Dammyjay93/interface-design/blob/main/.claude/skills/interface-design/SKILL.md)。读取时 main 指向 `2f9be3206855bcb2d1d0af262c8bae25cba6658d`。

它面向工具、设置与交互产品，强调先明确用户任务和领域，再决定层级、密度与视觉方向。本轮应用：画面主导、参数与证据相邻、工具状态清晰，以及通过实际操作反馈赋予界面连贯感。

适配限制：包含大量 Web 例子、通用动效与命中面积建议，需要转成桌面输入规则。影像判断区域使用中性围场，不能因为胶片有橙色就给画面四周铺暖色；高频切帧、采样和拖动不加装饰动画。沿用现有 GUI 基本思路，同时重新判断控件分组与细节。概念可逆，因此本轮直接制作供评审，不先要求确认方向。

## 3. 辅用：Avalonia Accessibility and Validation

来源：[SKILL.md](https://github.com/wieslawsoltes/development-plugin-for-avalonia/blob/main/skills/avalonia-accessibility-and-validation/SKILL.md)，同上 Avalonia 仓库版本。另读取[焦点与键盘导航](https://github.com/wieslawsoltes/development-plugin-for-avalonia/blob/main/references/19-focus-and-keyboard-navigation.md)。

应用：表单验证贴近出错字段，工具退出后焦点有归宿，批量目标可通过键盘维护，控件有稳定自动化名称。是否真正可由屏幕阅读器和纯键盘使用，须在 Avalonia 原型中验证。

## 4. 候选保留：UI UX Pro Max

来源：[nextlevelbuilder/ui-ux-pro-max-skill](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill)，实际读取的[SKILL.md](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill/blob/main/.claude/skills/ui-ux-pro-max/SKILL.md)。

当前版本包含可检索设计数据及 Avalonia 等 stack，适合作为细节问题的补充索引。它的内容覆盖广，也混有网站、移动端、营销样式与动画建议。本项目更需要照片编辑时的连贯操作，因此本轮没有把它的样式推荐作为主导，也没有运行其 Python 搜索器或声称得到数据库推荐。

搜索中发现的 fork、汇编转载和通用 frontend-design 条目没有优先于原始来源。未找到并核实专门为“彩色负片反转 GUI”编写的现成 skill；此处领域设计来自项目工作流分析。

## 5. 使用与安装状态

本轮实际阅读并应用适合本项目的相关方法；后文补充了新检索的 Impeccable、UI Skills 与 StageShow 的适配结论。没有全局安装 skill，没有执行远程仓库脚本，没有让 skill 的默认技术或流程覆盖项目与用户要求。

后续若做生产 GUI，可将原始技能包作为工具资源使用。Avalonia skill 的入口依赖仓库 `references`，仅复制一个 SKILL.md 会丢失主要内容；安装时应保留相对目录或采用完整插件。技能来源和引用必须与项目设计决策分开，不能把第三方建议写成已验证产品事实。

## 6. 新增主用：Impeccable，负责视觉与体验打磨

原始来源：[pbakaus/impeccable](https://github.com/pbakaus/impeccable)，已完整读取当前源码入口 [SKILL.src.md](https://github.com/pbakaus/impeccable/blob/main/skill/SKILL.src.md) 及 [Operate](https://github.com/pbakaus/impeccable/blob/main/skill/reference/operate.md)、[Distill](https://github.com/pbakaus/impeccable/blob/main/skill/reference/distill.md)、[Polish](https://github.com/pbakaus/impeccable/blob/main/skill/reference/polish.md)、[Craft floor](https://github.com/pbakaus/impeccable/blob/main/skill/reference/craft-floor.md)。

它区分操作型软件与展示页面；本项目采用操作型界面的克制、稳定和熟悉感。具体使用删繁就简、布局层级、字体数值、控件状态和视觉核查等方法。删去重复副标题、过度编号和说明容器，让对齐、间距和邻近关系承担解释。

没有安装或执行其二进制上下文加载器；按入口提供的不可用回退，直接使用已确认的项目背景、用户明确的架构与风格约束和实际源码。没有创建伪造的 PRODUCT.md / DESIGN.md 或宣称通过其自动检测器。

设计时不照搬“删除侧栏”“一切单列”等通用删繁建议：现有三栏是用户确认保留的专业工具框架。减少的是重复信息和障碍，专业参数仍有入口。

## 7. 补充检查：UI Skills

原始来源：[ibelick/ui-skills](https://github.com/ibelick/ui-skills)。已读取 [baseline-ui](https://github.com/ibelick/ui-skills/blob/main/skills/baseline-ui/SKILL.md) 和 [fixing-accessibility](https://github.com/ibelick/ui-skills/blob/main/skills/fixing-accessibility/SKILL.md)。

适合检查局部的层级、数值排版、图标名称、焦点、表单错误和对话框退出。不把 baseline-ui 的 Tailwind、React 或 motion/react 技术要求套进 Avalonia；实际桌面键盘与自动化语义以 Avalonia 专项指导为准。

## 8. StageShow：适合作为交互演示与呈现补充

原始来源：[benjitaylor/skills · stageshow](https://github.com/benjitaylor/skills/tree/main/stageshow)。已完整读取 [SKILL.md](https://github.com/benjitaylor/skills/blob/main/stageshow/SKILL.md) 与 [craft.md](https://github.com/benjitaylor/skills/blob/main/stageshow/references/craft.md)。

其定位是 React/CSS 的仿真产品静图和短动画：用可信的界面、少量关键文字、明确画面焦点和有因果的运动说明一个交互。它明确区分编排出来的光标动画与实际浏览器操作。

本项目适用的展示片段：拖入底片并出现导入预检；框选片基后相关参数更新；选择几帧并同步；导出失败后只重试失败项。演示可以帮助判断交互是否一眼能懂，同时不增加长说明。

使用边界：展示中裁掉无关区域、以占位线代替次要文字的手法不能照搬到真实软件的功能与无障碍标签。动画好看不证明操作真实可用，也不能替代焦点、原生图像区域、保存和失败恢复验收。

初轮只有适配评估。用户要求实际组合后，已使用固定版本的 React kit 制作工作台和采样动画，运行 film、states 与浏览器核查，详见 [StageShow 源码与记录](stageshow/README.md)。原始 film gate 尚未通过，具体残留与采样限制如实列在 REHEARSAL.md；浏览器操作和布局检查已单独通过。更早的聊天概念仍是 visualize 产物。

推荐组合：Avalonia 专项负责结构与桌面约束；Interface Design + Impeccable 负责布局、交互与视觉；StageShow 负责关键操作的概念演示。按需要使用，避免多套风格规则同时争夺主导。
