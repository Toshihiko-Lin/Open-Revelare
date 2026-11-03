# Lensfun 集成设计 — 镜头库后备方案

## 动机

当前 PR #7 使用硬编码镜头配置文件（`LensMetadata.TryLookupKnownLens`），覆盖约 10 支常见扫胶微距镜头。这种方案有明显局限：

1. **覆盖率低**：仅覆盖 Sony、Sigma、Canon、Nikon 的部分微距镜头
2. **维护负担**：每支新镜头需手动测量、校准、提交代码
3. **参数粗糙**：单点或线性插值，无法应对变焦镜头的非线性畸变
4. **用户困境**：非内置镜头用户必须手动校正或放弃畸变/暗角修正

[Lensfun](https://lensfun.github.io/) 是成熟的开源镜头校正库（LGPL-3.0），已被 darktable、RawTherapee、digiKam 等专业 RAW 工作流采用：

- **15,000+ 镜头配置文件**（机身 + 镜头 + 焦距 + 光圈组合）
- **三类校正参数**：几何畸变（Distortion）、横向色差（TCA）、暗角（Vignetting）
- **社区维护**：用户可通过 Lensfun Calibration Tool 提交新配置
- **跨平台 C API**：可通过 P/Invoke 或 C++/CLI 包装层调用

## 设计目标

1. **优先级分层**：嵌入元数据（Sony/DNG）> Lensfun 查询 > 硬编码预设 > 用户自定义
2. **零破坏**：不移除现有硬编码预设（作为 Lensfun 未覆盖时的后备）
3. **可选依赖**：Lensfun 不可用时（例如未安装库），优雅降级到当前逻辑
4. **性能优先**：Lensfun 查询应在后台线程执行，避免阻塞 UI

## 架构设计

### 1. 抽象层：`ILensCorrectionProvider`

```csharp
namespace OpenRevelare.Core;

public interface ILensCorrectionProvider
{
    /// <summary>
    /// 尝试为指定镜头查找校正参数。
    /// </summary>
    /// <param name="cameraModel">机身型号（如 "ILCE-7RM5"）</param>
    /// <param name="lensModel">镜头型号（如 "FE 70-200mm F4 Macro G OSS II"）</param>
    /// <param name="focalLengthMm">焦距（mm）</param>
    /// <param name="fNumber">光圈值</param>
    /// <returns>校正参数，或 null（未找到）</returns>
    LensCorrectionEstimate? GetCorrection(
        string cameraModel,
        string lensModel,
        double focalLengthMm,
        double fNumber);
    
    /// <summary>
    /// Provider 优先级（数值越小优先级越高）
    /// </summary>
    int Priority { get; }
    
    /// <summary>
    /// Provider 可用性（例如 Lensfun 库是否已加载）
    /// </summary>
    bool IsAvailable { get; }
}
```

### 2. Provider 实现

#### (A) `EmbeddedMetadataProvider`（优先级 10）

从 Sony MakerNote / DNG OpcodeList3 提取固件内置参数（当前 `LensMetadata.EstimateCorrections` 的前两节逻辑）。

```csharp
public sealed class EmbeddedMetadataProvider : ILensCorrectionProvider
{
    public int Priority => 10;
    public bool IsAvailable => true;
    
    public LensCorrectionEstimate? GetCorrection(
        string cameraModel, string lensModel, double focalLengthMm, double fNumber)
    {
        // 从已读取的 metadata directories 中提取
        // Sony 0x7037/0x7032 或 DNG 0xC74E
        // 具体实现即当前 LensMetadata.cs 第 85-161 行逻辑
    }
}
```

#### (B) `LensfunProvider`（优先级 20）

查询 Lensfun 数据库（本地 XML + 内置数据）。

```csharp
public sealed class LensfunProvider : ILensCorrectionProvider
{
    private readonly LensfunDatabase? _db;
    
    public int Priority => 20;
    public bool IsAvailable => _db != null;
    
    public LensfunProvider()
    {
        try
        {
            _db = LensfunDatabase.Load(); // P/Invoke wrapper
        }
        catch
        {
            _db = null; // Lensfun 库未安装或版本不兼容
        }
    }
    
    public LensCorrectionEstimate? GetCorrection(
        string cameraModel, string lensModel, double focalLengthMm, double fNumber)
    {
        if (_db == null) return null;
        
        // 1. 根据 cameraModel 查找 Camera（crop factor、传感器尺寸）
        var camera = _db.FindCamera(cameraModel);
        if (camera == null) return null;
        
        // 2. 根据 lensModel 查找 Lens
        var lens = _db.FindLens(lensModel);
        if (lens == null) return null;
        
        // 3. 查询 CalibrationData（焦距、光圈插值）
        var calib = lens.GetCalibration(focalLengthMm, fNumber);
        if (calib == null) return null;
        
        // 4. 转换 Lensfun 模型 → OpenRevelare 模型
        // Lensfun 支持多种畸变模型（PTLens, poly3, poly5, rectilinear）
        // OpenRevelare 当前使用 simple radial: scale(r) = 1 + k1*(r/norm)²
        // 需要将 Lensfun 参数转换或近似到我们的单参数模型
        
        double k1 = ConvertLensfunDistortionToK1(calib.Distortion, camera.CropFactor);
        double vignAmount = calib.Vignetting?.Amount ?? 0;
        double vignFalloff = calib.Vignetting?.Falloff ?? 2.5;
        
        return new LensCorrectionEstimate(
            LensModel: lensModel,
            FocalLengthMm: focalLengthMm,
            FNumber: fNumber,
            DistortionK1: k1,
            VignetteAmount: vignAmount,
            VignetteFalloff: vignFalloff,
            SourceDescription: "Lensfun 数据库");
    }
    
    private static double ConvertLensfunDistortionToK1(
        LensfunDistortion distortion, double cropFactor)
    {
        // Lensfun 畸变模型转换逻辑（需根据 Lensfun 实际 API 实现）
        // 可能需要多项式拟合或在特定半径处采样
        // 示例（简化）：
        // if (distortion.Model == LensfunDistortionModel.Poly3)
        //     return distortion.K1 / (cropFactor * cropFactor);
        // ...
        return 0;
    }
}
```

#### (C) `HardcodedProfileProvider`（优先级 30）

当前 `LensMetadata.TryLookupKnownLens` 的硬编码配置文件（10 支微距镜头）。

```csharp
public sealed class HardcodedProfileProvider : ILensCorrectionProvider
{
    public int Priority => 30;
    public bool IsAvailable => true;
    
    public LensCorrectionEstimate? GetCorrection(
        string cameraModel, string lensModel, double focalLengthMm, double fNumber)
    {
        // 当前 LensMetadata.TryLookupKnownLens 逻辑（第 256-357 行）
        if (TryLookupKnownLens(lensModel, focalLengthMm, fNumber, 
            out var k1, out var vign, out var falloff, out var profile))
        {
            return new LensCorrectionEstimate(
                lensModel, focalLengthMm, fNumber, k1, vign, falloff,
                profile);
        }
        return null;
    }
}
```

#### (D) `UserProfileProvider`（优先级 5，未来扩展）

用户自定义校正参数（存储在 `~/.openrevelare/lens-profiles.json`）。

### 3. 统一查询逻辑：`LensCorrectionResolver`

```csharp
public sealed class LensCorrectionResolver
{
    private readonly List<ILensCorrectionProvider> _providers;
    
    public LensCorrectionResolver()
    {
        _providers = new List<ILensCorrectionProvider>
        {
            new EmbeddedMetadataProvider(),
            new LensfunProvider(),
            new HardcodedProfileProvider(),
        }
        .Where(p => p.IsAvailable)
        .OrderBy(p => p.Priority)
        .ToList();
    }
    
    public LensCorrectionEstimate? Resolve(
        IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        // 1. 提取基础 EXIF 信息
        var (cameraModel, lensModel, focalLength, fNumber) = 
            ExtractExifInfo(directories);
        
        // 2. 按优先级顺序查询各 Provider
        foreach (var provider in _providers)
        {
            var result = provider.GetCorrection(
                cameraModel, lensModel, focalLength, fNumber);
            if (result != null)
                return result;
        }
        
        // 3. 如果镜头已识别但无配置文件，返回零校正
        if (!string.IsNullOrEmpty(lensModel))
        {
            return new LensCorrectionEstimate(
                lensModel, focalLength, fNumber, 0, 0, 2.5,
                "未内置校正曲线");
        }
        
        return null;
    }
}
```

### 4. 修改 `LensMetadata.EstimateCorrections`

```csharp
public static LensCorrectionEstimate? EstimateCorrections(
    IReadOnlyList<MetadataExtractor.Directory> directories)
{
    if (directories is null || directories.Count == 0) return null;
    
    var resolver = new LensCorrectionResolver();
    return resolver.Resolve(directories);
}
```

## Lensfun C API 集成方案

Lensfun 提供 C API（`lensfun.h`），可通过以下方式集成：

### 方案 A：P/Invoke（推荐）

在 `OpenRevelare.Core` 中创建 `Interop/Lensfun.cs`：

```csharp
using System.Runtime.InteropServices;

namespace OpenRevelare.Core.Interop;

internal static partial class Lensfun
{
    private const string LibName = "lensfun";
    
    [LibraryImport(LibName)]
    public static partial IntPtr lf_db_new();
    
    [LibraryImport(LibName)]
    public static partial void lf_db_destroy(IntPtr db);
    
    [LibraryImport(LibName)]
    public static partial long lf_db_load(IntPtr db);
    
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr lf_db_find_cameras_ext(
        IntPtr db, string maker, string model, int sflags);
    
    // ... 其他 API 声明
}
```

**优点**：
- 轻量，无需额外依赖
- 跨平台（Windows/Linux/macOS）
- 运行时动态加载（库缺失时优雅降级）

**缺点**：
- 需要手动管理内存（`Marshal.PtrToStructure`）
- 字符串编码处理（UTF-8 ↔ UTF-16）

### 方案 B：C++/CLI 包装层

创建独立项目 `OpenRevelare.LensfunBridge`（C++/CLI）：

```cpp
// LensfunBridge.h
#pragma once
#include <lensfun.h>

namespace OpenRevelare::LensfunBridge
{
    public ref class LensfunDatabase
    {
    private:
        lfDatabase* _db;
    
    public:
        LensfunDatabase();
        ~LensfunDatabase();
        !LensfunDatabase();
        
        LensCorrectionEstimate^ GetCorrection(
            System::String^ camera,
            System::String^ lens,
            double focalLength,
            double aperture);
    };
}
```

**优点**：
- 更好的类型安全
- 自动内存管理（Dispose pattern）
- 更接近 C++ 原生性能

**缺点**：
- 需要编译 C++/CLI 程序集（Windows only）
- 增加构建复杂度
- Linux/macOS 需要 Mono 或额外 P/Invoke 路径

**建议**：优先使用 **方案 A（P/Invoke）**，因为 OpenRevelare 已经是跨平台项目（Avalonia + .NET），保持纯托管代码有利于部署。

## 部署与分发

### 1. Lensfun 库依赖

**Windows**：
- 通过 vcpkg 或 MSYS2 安装 `lensfun.dll`
- 或捆绑预编译二进制（GPL-3.0 兼容，需声明）

**Linux**：
- 系统包管理器（`apt install liblensfun1`、`dnf install lensfun`）
- AppImage/Flatpak 可捆绑库

**macOS**：
- Homebrew（`brew install lensfun`）
- 或捆绑到 `.app` bundle

### 2. 数据库更新机制

Lensfun 数据库定期更新（新镜头配置文件）：
- 用户可通过 `lensfun-update-data` 工具更新
- 或应用内提供"更新镜头数据库"功能（调用 `lf_db_load` + 指定路径）

### 3. 可选依赖声明

在安装文档中说明：
- Lensfun 为**可选增强功能**
- 未安装时仍可使用嵌入元数据 + 硬编码配置文件
- 推荐高级用户安装以获得最大镜头覆盖

## 许可证兼容性

- **OpenRevelare**：GPL-3.0
- **Lensfun**：LGPL-3.0

LGPL-3.0 允许动态链接到 GPL-3.0 项目，无需修改 OpenRevelare 许可证。需在文档中声明 Lensfun 依赖及其许可证。

## 实施计划

### Phase 1：修复 PR #7 阻塞缺陷（P0）
1. 修复 Sony MakerNote 目录类型错误
2. 修复 DNG Opcode ID 错误
3. 修复 EXIF 标签误读
4. 补充真实 ARW/DNG 测试用例

### Phase 2：抽象层重构（P1）
1. 定义 `ILensCorrectionProvider` 接口
2. 拆分 `EmbeddedMetadataProvider`、`HardcodedProfileProvider`
3. 实现 `LensCorrectionResolver`
4. 确保功能无回归

### Phase 3：Lensfun 集成（P2）
1. 实现 `Lensfun.cs` P/Invoke 包装
2. 实现 `LensfunProvider`
3. 添加 Lensfun 可用性检测（库加载失败时优雅降级）
4. 补充集成测试（真实镜头查询 vs 预期参数）

### Phase 4：文档与部署（P2）
1. 更新 `docs/lens-correction-metadata.md`
2. 添加 Lensfun 安装指南
3. 在 Release Notes 中说明新功能

## 参考资料

- [Lensfun 官方文档](https://lensfun.github.io/)
- [Lensfun C API 参考](https://lensfun.github.io/manual/latest/annotated.html)
- [darktable Lensfun 集成实现](https://github.com/darktable-org/darktable/blob/master/src/external/lensfun.c)
- [RawTherapee Lensfun 集成实现](https://github.com/Beep6581/RawTherapee/blob/dev/rtengine/lensgeom.cc)

## 问题讨论

1. **畸变模型转换**：Lensfun 支持多种模型（PTLens poly3、poly5），OpenRevelare 当前仅支持单参数 k1。是否需要扩展到多项式？
   
2. **横向色差（TCA）**：Lensfun 提供 TCA 参数，OpenRevelare 当前未实现。是否纳入 v2.0 计划？

3. **性能**：Lensfun 查询是否需要缓存（避免重复查询同一镜头）？

4. **UI 反馈**：是否在"自动镜头校正"按钮附近显示数据来源（"来自 Lensfun：FE 70-200mm F4 G II"）？
