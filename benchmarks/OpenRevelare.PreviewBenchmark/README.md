# 预览与规范场景基准

运行前关闭其他计算负载。同一对照至少运行三个独立进程，保留每次 JSON，不只保留平均数。

## 现有合成 TIFF 工作负载

```powershell
dotnet run --project benchmarks/OpenRevelare.PreviewBenchmark -c Release -- scratch/performance/preview-1.json
```

生成 2400×1600 TIFF，记录解码、1200×800 预览准备、缓存查询、800 长边拖动准备、区域渲染和 TIFF 写出。默认写入临时隔离配置，不打开实际图库或窗口。`pipeline`、`canonical-scene`、`histogram` 包含一次预热；完整准备的 30 次样本不包含预热。P95 使用 nearest-rank。

## 色彩转换串行／并行配对

```powershell
dotnet run --project benchmarks/OpenRevelare.PreviewBenchmark -c Release -- --canonical scratch/performance/canonical-generated-1.json
dotnet run --project benchmarks/OpenRevelare.PreviewBenchmark -c Release -- --canonical scratch/performance/canonical-raw-1.json 'E:/samples/frame.raf'
```

无原片参数时使用固定随机种子的 1200×800 合成 TIFF；有原片参数时通过真实 GUI 预览加载入口读取，固定 LibRaw、FBDD Off、ManagedV2、1600 长边与 GPU Off。未标记 TIFF 明确假设线性；这不是任意 TIFF 的正确输入解释。原片不导出、不修改、不创建工程；不执行完整原片解码／全尺寸导出。

先生成同一份渲染像素，预热两种转换，再交替测量 30 对转换。串行对照使用转发同一个 LittleCmsEngine 的接口包装，走非 bundled-engine 的串行 fallback；请求、ICC、浮点精度、CMM 和矩阵算法一致。每对都检查 RGBA-half 原始字节一致。保留原始耗时和跨全部托管线程的分配字节，避免只测调用线程漏掉工作线程分配。

JSON 保存输入 SHA-256、尺寸、字节数、程序集／CMM 身份；不保存完整输入路径。原片运行前后复核内容哈希，输入有变化则不发布结果。首次哈希会预热文件缓存，因此 `DecodeMs` 不代表冷磁盘。该字段与 `PipelineMs` 各只测一次，不能用于可靠分位数比较。

这些都是组件基准：排除窗口事件、Avalonia bitmap、UI 发布、显示器、合成器和 NegPy。峰值内存包含同进程两条路径、原片读取与正确性比较，不能拆解为某一条路径的内存。内存分配数不包含 native 分配或缓存常驻量。请勿把这些数字作为完整 GUI 响应或软件间速度结论。

2026-11-12 的原始证据和限制见 [M0 首批记录](../../docs/performance/negpy-m0-2026-11-12.md)。

## FilmBase 复用与连续成片编辑

```powershell
dotnet run --project benchmarks/OpenRevelare.PreviewBenchmark -c Release -- --film scratch/performance/film-generated-1.json
dotnet run --project benchmarks/OpenRevelare.PreviewBenchmark -c Release -- --film scratch/performance/film-raw-1.json 'E:/samples/frame.raf'
```

同一输入预热后交替比较完整管线与 GUI `FilmPreviewCache`。连续 30 对曝光变化保留原始 render／prepare 耗时、全部线程托管分配，逐对检查渲染像素、规范场景与渲染身份相同。缓存路径的时间包含参数键、查找和结束阶段的像素复制，不只测跳过的反转函数。

v2 JSON 另有 `Miss` 阶段：再运行 30 对黑端变化，每对前清空缓存，验证全部未命中，记录首次准备与复制的额外成本。清空不计时；此阶段仍已预热解码、CMM 和 JIT，不能称为冷启动。顶层 `Full`／`Cached` 与命中计数仍只描述连续曝光阶段。两个阶段分开报告，不混为整体速度结论。

本负载启用手动暗角、0.7° 拉直、裁切及齿孔填充；不含 AI 除尘，不代表所有照片默认状态。prepare 包含规范场景、直方图和 CPU fallback 编码，不包含 native bitmap 或屏幕。先前 `--canonical` 是另一种负载，不能拼接绝对计时。

桌面 A/B 可在启动前设置 `OPENREVELARE_FILM_CACHE=0`，对照组移除此变量，配合 `OPENREVELARE_RENDER_TRACE=1`。该开关仅用于诊断前台预览；命令行 `--film` 始终显式比较两条路径。日志新增 `preview.film.hit/miss/bypass`。原始证据见 [FilmBase 缓存实测](../../docs/performance/negpy-film-stage-2026-11-12.md)。
