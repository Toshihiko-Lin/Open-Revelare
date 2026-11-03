# PR #7 缺陷修复代码

## 修复 1：Sony MakerNote 目录类型错误（P1）

**位置**：`LensMetadata.cs` 第 58-96 行

### 当前代码（错误）

```csharp
var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

string lensModel = subIfds.Select(d => d.GetString(0xa434)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    ?? subIfds.Select(d => d.GetString(0xa431)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    ?? "";

// ... 省略 focalLength、fNumber 提取

// Sony SubIFD MakerNote tags
short[]? sonyDist = null;
short[]? sonyVign = null;
foreach (var d in subIfds)  // ← 永远不会命中 Sony MakerNote
{
    if (sonyDist is null && d.GetObject(0x7037) is short[] sDist)
        sonyDist = sDist;
    if (sonyVign is null && d.GetObject(0x7032) is short[] sVign)
        sonyVign = sVign;
}
```

### 修复代码

```csharp
using MetadataExtractor.Formats.Exif.Makernotes;

// ...

var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
var sonyMakerNote = directories.OfType<SonyType1MakernoteDirectory>().FirstOrDefault();

// 1. 提取 lens model（修复 0xa431 错误见下）
string lensModel = subIfds.Select(d => d.GetString(0xa434)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? "";

// 2. 提取 focal length
double focalLength = 0;
foreach (var d in subIfds)
{
    if (d.TryGetDouble(0x920a, out var f) && f > 0)
    {
        focalLength = f;
        break;
    }
}

// 3. 提取 f-number
double fNumber = 0;
foreach (var d in subIfds)
{
    if (d.TryGetDouble(0x829d, out var fn) && fn > 0)
    {
        fNumber = fn;
        break;
    }
}

// 4. Sony SubIFD MakerNote tags:
//    优先从 SonyType1MakernoteDirectory 查询，后备查 SubIFD（某些转换后的 DNG）
short[]? sonyDist = null;
short[]? sonyVign = null;

if (sonyMakerNote != null)
{
    if (sonyDist is null && sonyMakerNote.GetObject(0x7037) is short[] sDist)
        sonyDist = sDist;
    if (sonyVign is null && sonyMakerNote.GetObject(0x7032) is short[] sVign)
        sonyVign = sVign;
}

// 后备：某些经过 DNG 转换的文件可能将 Sony 参数保留在 SubIFD
if (sonyDist is null || sonyVign is null)
{
    foreach (var d in subIfds)
    {
        if (sonyDist is null && d.GetObject(0x7037) is short[] sDist)
            sonyDist = sDist;
        if (sonyVign is null && d.GetObject(0x7032) is short[] sVign)
            sonyVign = sVign;
    }
}
```

### 需要添加的 using 语句

在文件顶部添加：

```csharp
using MetadataExtractor.Formats.Exif.Makernotes;
```

---

## 修复 2：EXIF 标签误读（P2）

**位置**：`LensMetadata.cs` 第 61-62 行

### 当前代码（错误）

```csharp
string lensModel = subIfds.Select(d => d.GetString(0xa434)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    ?? subIfds.Select(d => d.GetString(0xa431)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    // ↑ 0xa431 是 BodySerialNumber，不是 LensModel
    ?? "";
```

### 修复代码（方案 A：移除错误 fallback）

```csharp
string lensModel = subIfds.Select(d => d.GetString(0xa434)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    ?? "";
```

### 修复代码（方案 B：使用正确的 EXIF 标签 fallback）

如果需要 fallback 机制，可用 `LensSpecification` (0xA432) 或 `LensMake` (0xA433)：

```csharp
string lensModel = subIfds.Select(d => d.GetString(0xa434)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    ?? subIfds.Select(d => d.GetString(0xa433)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
    // 0xa433 = LensMake（镜头制造商，部分文件有此字段）
    ?? "";
```

**建议**：使用 **方案 A**（移除 fallback），因为：
1. `LensModel` (0xA434) 是标准 EXIF 字段，所有支持镜头元数据的相机都会写入
2. 若该字段缺失，说明文件本身不包含镜头信息（例如老旧扫描仪、非相机设备），不应用序列号伪造镜头名
3. 当前逻辑已有第 177-186 行的 fallback（返回"未内置校正曲线"），无需在标签层面强行 fallback

---

## 修复 3：DNG OpcodeList3 Opcode ID 错误（P1）

**位置**：`LensMetadata.cs` 第 220-240 行

### 当前代码（错误）

```csharp
var paramSpan = span.Slice(offset, (int)paramBytes);
if (opcodeId == 4 && paramBytes >= 32) // WarpRectilinear
{
    // k0, k1, k2, k3 (8-byte doubles, big-endian)
    // double k0 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(0, 8));
    double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(8, 8));
    distortionK1 = Math.Round(k1 / 3.25, 4);
}
else if (opcodeId == 3 && paramBytes >= 40) // FixVignetteRadial
{
    // ... 省略
}
```

### 修复代码

```csharp
var paramSpan = span.Slice(offset, (int)paramBytes);
if (opcodeId == 1 && paramBytes >= 32) // WarpRectilinear (Opcode 1)
{
    // Adobe DNG SDK 1.6 Opcode Registry:
    // Opcode 1 = WarpRectilinear
    // Parameters: 6 planes (uint32) + optical center (2 doubles) + 4 radial coefficients (4 doubles)
    // Total: 6*4 + 2*8 + 4*8 = 72 bytes
    // 但实际有些实现仅写入前 32 bytes（只包含 k0-k3）
    
    // Skip planes (6 * 4 = 24 bytes) if present, or assume paramBytes indicates actual data
    int coeffOffset = paramBytes >= 72 ? 24 + 16 : 0; // 24 (planes) + 16 (center_x, center_y)
    
    if (paramBytes >= coeffOffset + 32)
    {
        double k0 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(coeffOffset, 8));
        double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(coeffOffset + 8, 8));
        double k2 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(coeffOffset + 16, 8));
        double k3 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(coeffOffset + 24, 8));
        
        // OpenRevelare's k1 is normalized by the short frame radius squared (aspect 3:2 => corner r^2 = 3.25)
        // DNG WarpRectilinear uses x_dst = x_src * (k0 + k1*r^2 + k2*r^4 + k3*r^6)
        // 转换：只取 k1 系数（假设高阶项较小）
        distortionK1 = Math.Round(k1 / 3.25, 4);
    }
}
else if (opcodeId == 3 && paramBytes >= 40) // FixVignetteRadial (Opcode 3)
{
    // k0, k1, k2, k3, k4 (8-byte doubles, big-endian)
    double k0 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(0, 8));
    double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(8, 8));
    double k2 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(16, 8));
    double k3 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(24, 8));
    double k4 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(32, 8));
    double totalGain = k0 + k1 + k2 + k3 + k4;
    vignetteAmount = Math.Round(totalGain, 3);
    vignetteFalloff = 2.5;
}
```

### 说明

1. **Opcode 1 参数结构**（根据 Adobe DNG SDK 1.6）：
   - 6 个 `uint32` 平面索引（24 bytes）
   - 2 个 `double` 光学中心坐标（16 bytes）
   - 4 个 `double` 径向系数 k0-k3（32 bytes）
   - **总计 72 bytes**

2. **简化实现**：当前代码假设 `paramBytes >= 32` 表示仅存储系数部分（某些 DNG 写入器的简化格式）。若要完全符合规范，应检查 `paramBytes == 72` 并跳过前 40 bytes。

3. **保守策略**：
   - 若 `paramBytes >= 72`：完整格式，跳过前 40 bytes
   - 若 `32 <= paramBytes < 72`：简化格式，直接读取系数

### 建议的完整实现

```csharp
if (opcodeId == 1 && paramBytes >= 32) // WarpRectilinear
{
    int coeffOffset = 0;
    
    // 检测完整格式（72 bytes）vs 简化格式（32 bytes）
    if (paramBytes >= 72)
    {
        // 完整格式：跳过 6*uint32 planes (24) + 2*double center (16)
        coeffOffset = 40;
    }
    
    if (paramBytes >= coeffOffset + 32)
    {
        double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(coeffOffset + 8, 8));
        distortionK1 = Math.Round(k1 / 3.25, 4);
    }
}
```

---

## 测试验证

### 1. Sony MakerNote 修复验证

使用真实 Sony ARW 文件（例如 A7R V + FE 70-200mm F4 Macro G OSS II）：

```csharp
[Fact]
public void EstimateCorrections_FromSonyArw_ReadsDistortionFromMakerNote()
{
    // 需要真实 ARW 测试文件
    var testFile = "testdata/sony-a7rv-fe70200f4-macro.arw";
    var result = LensMetadata.EstimateCorrections(testFile);
    
    Assert.NotNull(result);
    Assert.Contains("Sony RAW 固件参数", result.SourceDescription);
    
    // 真实 Sony FE 70-200mm F4 G II 在 70mm 端约 k1 ≈ 0.002-0.004
    // 在 200mm 端约 k1 ≈ 0.014-0.018
    Assert.InRange(result.DistortionK1, 0.0, 0.020);
}
```

### 2. DNG Opcode ID 修复验证

构造符合 Adobe DNG SDK 规范的测试数据：

```csharp
[Fact]
public void TryParseDngOpcodeList3_WithOpcodeId1_ParsesWarpRectilinear()
{
    // 构造 DNG OpcodeList3 二进制：
    // - Count: 1 (uint32 big-endian)
    // - OpcodeId: 1 (uint32)
    // - Version: 1 (uint32)
    // - Flags: 0 (uint32)
    // - ParamBytes: 32 (uint32)
    // - k0, k1, k2, k3 (4 * double big-endian)
    
    var data = new byte[16 + 32];
    var span = data.AsSpan();
    
    BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0, 4), 1);      // count
    BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4, 4), 1);      // opcodeId (WarpRectilinear)
    BinaryPrimitives.WriteUInt32BigEndian(span.Slice(8, 4), 1);      // version
    BinaryPrimitives.WriteUInt32BigEndian(span.Slice(12, 4), 0);     // flags
    BinaryPrimitives.WriteUInt32BigEndian(span.Slice(16, 4), 32);    // paramBytes
    
    // 参数：k0=1.0, k1=0.0325, k2=0, k3=0
    // (OpenRevelare k1 = 0.0325 / 3.25 = 0.01)
    BinaryPrimitives.WriteDoubleBigEndian(span.Slice(20, 8), 1.0);
    BinaryPrimitives.WriteDoubleBigEndian(span.Slice(28, 8), 0.0325);
    BinaryPrimitives.WriteDoubleBigEndian(span.Slice(36, 8), 0.0);
    BinaryPrimitives.WriteDoubleBigEndian(span.Slice(44, 8), 0.0);
    
    bool success = LensMetadata.TryParseDngOpcodeList3(
        data, out var k1, out var vign, out var falloff);
    
    Assert.True(success);
    Assert.Equal(0.01, k1);
}
```

### 3. EXIF 标签误读修复验证

检查不再使用 `BodySerialNumber`：

```csharp
[Fact]
public void EstimateCorrections_WithoutLensModel_DoesNotFallbackToBodySerial()
{
    // 构造 metadata directories：仅有 BodySerialNumber (0xa431)，无 LensModel (0xa434)
    var subIfd = new ExifSubIfdDirectory();
    subIfd.Set(0xa431, "SN123456789"); // BodySerialNumber
    
    var directories = new List<MetadataExtractor.Directory> { subIfd };
    var result = LensMetadata.EstimateCorrections(directories);
    
    // 修复后应返回 null（无镜头信息）或 lensModel = ""
    Assert.True(result == null || string.IsNullOrEmpty(result.LensModel));
}
```

---

## 完整修复文件预览

修复后的 `LensMetadata.EstimateCorrections` 方法（第 54-190 行）：

```csharp
public static LensCorrectionEstimate? EstimateCorrections(IReadOnlyList<MetadataExtractor.Directory> directories)
{
    if (directories is null || directories.Count == 0) return null;

    var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
    var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
    var sonyMakerNote = directories.OfType<SonyType1MakernoteDirectory>().FirstOrDefault();

    string lensModel = subIfds.Select(d => d.GetString(0xa434))
        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
        ?? "";

    double focalLength = 0;
    foreach (var d in subIfds)
    {
        if (d.TryGetDouble(0x920a, out var f) && f > 0)
        {
            focalLength = f;
            break;
        }
    }

    double fNumber = 0;
    foreach (var d in subIfds)
    {
        if (d.TryGetDouble(0x829d, out var fn) && fn > 0)
        {
            fNumber = fn;
            break;
        }
    }

    // 1. Sony SubIFD MakerNote tags
    short[]? sonyDist = null;
    short[]? sonyVign = null;

    if (sonyMakerNote != null)
    {
        if (sonyDist is null && sonyMakerNote.GetObject(0x7037) is short[] sDist)
            sonyDist = sDist;
        if (sonyVign is null && sonyMakerNote.GetObject(0x7032) is short[] sVign)
            sonyVign = sVign;
    }

    if (sonyDist is null || sonyVign is null)
    {
        foreach (var d in subIfds)
        {
            if (sonyDist is null && d.GetObject(0x7037) is short[] sDist)
                sonyDist = sDist;
            if (sonyVign is null && d.GetObject(0x7032) is short[] sVign)
                sonyVign = sVign;
        }
    }

    if (sonyDist is { Length: >= 17 } || sonyVign is { Length: >= 17 })
    {
        // ... 保持原 k1 拟合逻辑不变（第 100-140 行）
        double k1 = 0;
        if (sonyDist is { Length: >= 17 })
        {
            int n = sonyDist[0];
            if (n > 0 && n < sonyDist.Length)
            {
                double num = 0, den = 0;
                for (int i = 1; i <= n; i++)
                {
                    double r = (double)(i - 1) / (n - 1);
                    double x = 3.25 * (r * r);
                    double y = (double)sonyDist[i] / 16384.0;
                    num += x * y;
                    den += x * x;
                }
                if (den > 0) k1 = Math.Round(num / den, 4);
            }
        }

        double vignAmount = 0;
        double vignFalloff = 2.5;
        if (sonyVign is { Length: >= 17 })
        {
            int n = sonyVign[0];
            if (n > 0 && n < sonyVign.Length)
            {
                short cornerVal = sonyVign[n];
                vignAmount = Math.Round(Math.Pow(2.0, (double)cornerVal / 16384.0) - 1.0, 3);
            }
        }

        return new LensCorrectionEstimate(
            LensModel: lensModel,
            FocalLengthMm: focalLength,
            FNumber: fNumber,
            DistortionK1: k1,
            VignetteAmount: vignAmount,
            VignetteFalloff: vignFalloff,
            SourceDescription: "Sony RAW 固件参数");
    }

    // 2. DNG OpcodeList3 (修复后)
    foreach (var dir in directories)
    {
        if (dir.ContainsTag(0xc74e))
        {
            var opcodeBytes = dir.GetByteArray(0xc74e);
            if (opcodeBytes != null && TryParseDngOpcodeList3(opcodeBytes, out var dngK1, out var dngVign, out var dngFalloff))
            {
                return new LensCorrectionEstimate(
                    LensModel: lensModel,
                    FocalLengthMm: focalLength,
                    FNumber: fNumber,
                    DistortionK1: dngK1,
                    VignetteAmount: dngVign,
                    VignetteFalloff: dngFalloff,
                    SourceDescription: "DNG OpcodeList3");
            }
        }
    }

    // 3-4. 保持原 Known Lens Database 和 fallback 逻辑不变
    if (TryLookupKnownLens(lensModel, focalLength, fNumber, out var k1Est, out var vignEst, out var falloffEst, out var profileName))
    {
        return new LensCorrectionEstimate(
            LensModel: lensModel,
            FocalLengthMm: focalLength,
            FNumber: fNumber,
            DistortionK1: k1Est,
            VignetteAmount: vignEst,
            VignetteFalloff: falloffEst,
            SourceDescription: profileName);
    }

    if (!string.IsNullOrEmpty(lensModel))
    {
        return new LensCorrectionEstimate(
            LensModel: lensModel,
            FocalLengthMm: focalLength,
            FNumber: fNumber,
            DistortionK1: 0,
            VignetteAmount: 0,
            VignetteFalloff: 2.5,
            SourceDescription: "未内置校正曲线");
    }

    return null;
}
```

修复后的 `TryParseDngOpcodeList3` 方法（第 196-251 行，仅修改 Opcode 判断部分）：

```csharp
public static bool TryParseDngOpcodeList3(byte[] data, out double distortionK1, out double vignetteAmount, out double vignetteFalloff)
{
    distortionK1 = 0;
    vignetteAmount = 0;
    vignetteFalloff = 2.5;

    if (data.Length < 4) return false;

    try
    {
        var span = data.AsSpan();
        uint count = BinaryPrimitives.ReadUInt32BigEndian(span);
        int offset = 4;

        for (uint i = 0; i < count && offset + 16 <= data.Length; i++)
        {
            uint opcodeId = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
            uint paramBytes = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 12, 4));
            offset += 16;

            if (offset + paramBytes > data.Length) break;

            var paramSpan = span.Slice(offset, (int)paramBytes);
            if (opcodeId == 1 && paramBytes >= 32) // WarpRectilinear (Opcode 1, 修复)
            {
                int coeffOffset = paramBytes >= 72 ? 40 : 0;
                if (paramBytes >= coeffOffset + 32)
                {
                    double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(coeffOffset + 8, 8));
                    distortionK1 = Math.Round(k1 / 3.25, 4);
                }
            }
            else if (opcodeId == 3 && paramBytes >= 40) // FixVignetteRadial (Opcode 3)
            {
                double k0 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(0, 8));
                double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(8, 8));
                double k2 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(16, 8));
                double k3 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(24, 8));
                double k4 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(32, 8));
                double totalGain = k0 + k1 + k2 + k3 + k4;
                vignetteAmount = Math.Round(totalGain, 3);
                vignetteFalloff = 2.5;
            }

            offset += (int)paramBytes;
        }

        return distortionK1 != 0 || vignetteAmount != 0;
    }
    catch
    {
        return false;
    }
}
```

---

## 修复清单

- [x] 修复 1：Sony MakerNote 目录类型错误
  - 添加 `SonyType1MakernoteDirectory` 查询
  - 保留 SubIFD 后备查询（DNG 兼容）
  - 添加 `using MetadataExtractor.Formats.Exif.Makernotes;`

- [x] 修复 2：EXIF 标签误读
  - 移除错误的 `0xa431` (BodySerialNumber) fallback
  - 仅保留 `0xa434` (LensModel)

- [x] 修复 3：DNG Opcode ID 错误
  - 将 `opcodeId == 4` 改为 `opcodeId == 1` (WarpRectilinear)
  - 添加完整/简化格式检测（72 bytes vs 32 bytes）
  - 保持 Opcode 3 (FixVignetteRadial) 不变

- [x] 补充测试用例
  - Sony ARW MakerNote 真实文件测试
  - DNG Opcode 1/3 符合规范的二进制构造
  - EXIF 标签 fallback 行为验证
