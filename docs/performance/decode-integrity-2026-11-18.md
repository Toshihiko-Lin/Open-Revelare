# 解码与数值传递完整性

日期：2026-11-18。接续 [管线核查](../research/capture-pipeline-audit-2026-11-18.md)。用户明确：普通 RAW 导入负责忠实解码和传递，设备验证作为独立用户校准模块。

撤回上一轮尚未提交的 `CaptureQuality`、近黑阈值、有效像素筛选和空间分区报告。普通图像中的浓负片、零值或高亮不能凭通用阈值判作无效采集。原有描述性统计继续保留，不改变像素或片基／端点逻辑。

`audit-input` v2 增加成功文件的审计前后源文件 SHA-256，以及每次实际预览尺寸。文件摘要预热系统缓存，报告明确不宣称冷盘测试；重复像素 hash 检查的是解码可重复性，不是重复拍摄。原片 hash 相同不表示解码无损，须与数值传递及路径测试结合解释。

新增测试对全部 65536 个已解码整数码值验证：归一化后严格单调、0 与 65535 端点保留、回转整数逐值一致、原数组未改写。已有测试覆盖 SIMD／标量转换、代理缩小、全尺寸／共享 RAW、原生解码策略等边界。保证范围从 LibRaw 已解码的 u16 开始，不宣称原生桥的解马赛克、最大值调整、clip 或可选降噪是无损操作。

运行示例（项目根目录）：

```powershell
dotnet run --project src/OpenRevelare.Cli -c Release -- audit-input `
  --input scratch/real-import/optimized/input/PFCS025.raf `
  --output artifacts/decode-integrity-raf-2026-11-18.json `
  --repeat 2 --workers 1 --compare-full
```

该 RAF 元数据为 GFX100S，源尺寸 11648×8735。默认 32 MP 预算将跳过全质量对照；这是已记录的预算限制，不应写成已通过全质量验证。运行报告以 artifacts 中实际生成的文件为准；scratch 与 artifacts 不假定随仓库分发。

本次运行成功：源文件前后 SHA-256 相同，两次预览像素 hash 相同，报告无文件错误，全质量对照按预算跳过。输入审计、全部码值转换、代理缩小、共享 RAW 与原生解码相关测试共 37 项通过；CLI 已通过实际构建与运行。

设备参考采集、dark/open、空间均匀性和硬件适用性检查转入 [设备校准模块方案](../research/device-calibration-module.md)，不依赖标准冲洗。
