# 参与开发

先说最要紧的一条：**这个项目的注释解释「为什么」，不解释「做了什么」。** 代码写了什么，读代码就知道；写注释是为了让下一个人（多半是半年后的你）知道当初为什么这么选、试过什么、哪条路走不通。仓库里现有的注释就是标准，随便打开 `Pipeline.cs` 或 `FrameParams.cs` 看几眼就明白了。

English speakers: contributions are welcome in English. Code comments in this repo are mostly Chinese because that's where its users are, but English comments are fine and won't be rewritten. The rules below apply either way.

---

## 快速上手

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
git clone https://github.com/Toshihiko-Lin/Open-Revelare.git
cd Open-Revelare
dotnet build -c Release
dotnet test  -c Release          # 约 10 秒，提 PR 前必须是绿的
dotnet run --project src/OpenRevelare.Gui -c Release
```

## 仓库结构

| 目录 | 是什么 |
|---|---|
| `src/OpenRevelare.Core` | 全部图像处理。**不依赖任何 UI**，CLI 和 GUI 共用这一份 |
| `src/OpenRevelare.Gui` | Avalonia 界面 |
| `src/OpenRevelare.Cli` | 命令行，也是 Core 的第二个消费者 |
| `src/OpenRevelare.Tests` | xUnit，含金标准回归基线 |
| `docs/calibration/` | 支撑各项物理结论的标定实验脚本 |

**Core 不许 `using Avalonia`。** 这条边界是「同一份计算，GUI 和 CLI 出一样的结果」的唯一保障，也是 Core 能被测试的原因。

## 提 PR 之前

1. **`dotnet build -c Release -warnaserror` 必须零警告。** 这个仓库长期保持 0 警告，CI 也是这么卡的。
2. **`dotnet test -c Release` 必须全绿。**
3. 改了行为就补测试。测试的价值不在覆盖率数字，在于**把一个已经踩过的坑钉死**——所以测试的注释要写清楚「这条在防什么」，仓库里的现有测试都是这么写的。
4. 一个 PR 只做一件事。混着改的 PR 没法单独回滚，而这个项目的很多 bug 是在发版之后才被用户发现的。

## 金标准基线（重要）

`src/OpenRevelare.Tests/Golden/*.txt` 是整条管线的**逐像素**基线，守的是 README 承诺的那句「同一卷底片任何时候、任何机器上处理，结果都一样」。

如果你的改动让它红了，**先停下来回答一个问题：这次改动本就应该改变每一张照片的像素吗？**

- **不应该** —— 那就是引入了回归，去修代码，别动基线。
- **应该**（比如有意修正一个数学错误、调整渲染变换）—— 用下面的方式更新，并在 PR 里说明**为什么该变**、变的方向对不对：

```bash
REVELARE_UPDATE_GOLDEN=1 dotnet test -c Release --filter GoldenFrameTests
```

基线是文本格式（十六进制位模式 + 几行人类可读的通道统计），所以 `git diff` 本身就是一份变更报告：能看出是整体偏移还是某个通道歪了。**review 的时候请真的看那几行统计。**

## 提交信息

用中文，格式是 `类型: 这次改动让用户少踩什么坑`。因果都要在一行里说清：

```
fix: 齿孔阈值改在全分辨率上估，片基透的卷不再漏检
feat: 印样可选版面比例与横竖，差额落在页边距上
```

类型：`feat` / `fix` / `perf` / `ui` / `docs` / `test` / `chore`。破坏性变更用 `feat!:`。

**不要加 `Co-Authored-By` 之类的署名行。**

## 什么改动会被谨慎对待

不是拒绝，是需要更多讨论——这些地方过去都出过事：

- **动 Stage 1 的数学**（`Inversion` / `FilmBase` / `DensityEndpoints`）。这是产品承诺所在，金标准基线首当其冲。
- **给同一个自由度加第二个控件。** 比如再加一组「亮度」「对比度」滑块——`FrameParams` 顶部那段注释详细讲了为什么参数数量必须等于自由度数量，历史上这里有过十几个参数描述同样的六个数，结局是同一个校正被应用两遍。
- **加联网功能。** 目前只有一个更新检查，且失败必须静默。不联网、不要账号是产品定位的一部分。
- **加 GPU 后端。** 之前有过，被删了——`Pipeline.cs` 顶部写了实测结论。要重提请先给出测量数据。

拿不准就先开个 issue 或 Discussion 聊，比写完一大堆代码再被劝退省事。

## 报告问题

见 [issue 模板](https://github.com/Toshihiko-Lin/Open-Revelare/issues/new/choose)。**颜色相关的问题请务必附源文件和 `.ncproj`**——管线是确定性的，有这两样就一定能复现；没有的话谁也只能猜。

## 许可

提交即表示同意你的贡献以 [GPL-3.0-only](LICENSE) 授权。
