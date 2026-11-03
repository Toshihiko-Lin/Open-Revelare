# PR #7 评审 — 三个阻塞性缺陷

## 摘要
PR 的功能设计正确，但代码有三个 P1/P2 级bug，导致Sony ARW固件参数和DNG Opcode几乎无法被读取。建议在合并前修复。

## 缺陷详表

### [P1] Sony MakerNote 目录类型错误 
**位置：** `LensMetadata.cs` 第 59 行  
**问题：**
```csharp
var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
// ... 后续只在 subIfds 中查找 0x7037 / 0x7032
```

MetadataExtractor 会把 Sony MakerNote 数据放入 `SonyType1MakernoteDirectory`（或其他 MakerNote 目录），而不是 `ExifSubIfdDirectory`。当前代码永远不会命中 Sony 固件参数，99% 回退到硬编码预设。

**实际影响：**
- 对 Sony A7R V + FE 70-200mm F4 的真实 ARW 文件，应得 K1≈0.0136（固件参数），却得 K1=0.0089（硬编码预设）
- 对镜头来说误差 >34%，扫胶工作流感知明显

**修复：** 添加对 `SonyType1MakernoteDirectory` 的查询
```csharp
var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
var sonyMakerNote = directories.OfType<SonyType1MakernoteDirectory>().FirstOrDefault();

short[]? sonyDist = null, sonyVign = null;
// 先查 Sony MakerNote
if (sonyMakerNote != null)
{
    if (sonyDist is null && sonyMakerNote.GetObject(0x7037) is short[] sDist)
        sonyDist = sDist;
    if (sonyVign is null && sonyMakerNote.GetObject(0x7032) is short[] sVign)
        sonyVign = sVign;
}
// 后备查 SubIFD（某些转换后的 DNG）
foreach (var d in subIfds)
{
    if (sonyDist is null && d.GetObject(0x7037) is short[] sDist)
        sonyDist = sDist;
    ...
}
```

---

### [P1] DNG OpcodeList3 Opcode ID 错误

**位置：** `LensMetadata.cs` 第 320 行  
**问题：**
```csharp
if (opcodeId == 4 && paramBytes >= 32) // WarpRectilinear
```

按 Adobe DNG Opcode Registry：
- **Opcode 1** = `WarpRectilinear` 
- **Opcode 3** = `FixVignetteRadial`
- **Opcode 4** = `WarpBayer`（四色滤阵扭曲，不是径向矫正）

当前代码把 Opcode 4（WarpBayer）当作 WarpRectilinear，导致**任何符合规范的 DNG 都无法解析**。单测通过是因为测试数据按错误协议构造。

**修复：**
```csharp
if (opcodeId == 1 && paramBytes >= 32) // WarpRectilinear
{
    // ... 保持参数解析不变
}
else if (opcodeId == 3 && paramBytes >= 40) // FixVignetteRadial
{
    // ... 保持参数解析不变
}
```

---

### [P2] EXIF 标签误读 

**位置：** `LensMetadata.cs` 第 61–62 行  
**问题：**
```csharp
string lensModel = subIfds.Select(d => d.GetString(0xa434))...
    ?? subIfds.Select(d => d.GetString(0xa431))...  // ← 这是 BodySerialNumber，不是 LensModel
```

标签定义（MetadataExtractor.Formats.Exif.ExifTags）：
- **0xA434** = `LensModel`  
- **0xA431** = `BodySerialNumber`

用机身序列号作为 fallback 可能导致：
- 用 "2024000123" 这样的序号去 lookup 镜头预设，全部不匹配
- 返回 K1=0 的"未内置校正曲线"估计，**覆盖用户已有的手动校正**

**修复：** 删除第二行 fallback，或用正确标签
```csharp
string lensModel = subIfds.Select(d => d.GetString(0xa434))
    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    ?? "";
```

---

## 测试建议

1. **用真实 ARW 文件验证** Sony MakerNote 修复
   - 已有单测 `EstimateCorrections_FromActualArwIfPresent_MatchesGroundTruth` 可参考

2. **构造符合规范的 DNG 测试数据** 
   - 按 Adobe DNG Opcode Registry 生成 Opcode 1/3 的有效字节序列
   - 不能用当前单测中的 Opcode 4 测试数据

3. **补中文翻译**
   - `zh-CN.json` 缺少 UI 按钮和状态文本的中文

---

## 建议合并步骤

1. 修复上述三处代码
2. 验证 Sony ARW 和符合规范的 DNG
3. 补中文翻译
4. 考虑后续用 Lensfun 替换硬编码镜头库（可作为 v1.x 后续工作）

---

## 其他观察

- 代码结构清晰，ViewModel 异步处理正确，undo/redo 支持完善
- 文档详尽（`lens-correction-metadata.md`）
- 单测覆盖三层逻辑（但当前测试协议有误，修复后可信度↑）

欢迎讨论或提问。
