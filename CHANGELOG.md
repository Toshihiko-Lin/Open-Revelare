# OpenRevelare — 更新日志

## v1.0.0（待发布）

首个开源版本。C# / .NET 8 + Avalonia，Windows / Linux / macOS。

### 成像

- 密度域六步反转：t_base / wb_high / wb_offset / 扫描曝光 / d_max / gamma / 色度补偿
- 窄带光源解耦（Path A）：从 R/G/B 标定帧解算 3×3 矩阵，消掉 LED / 荧光灯箱翻拍的通道串扰
- 自动标定：整卷估片基、齿孔阈值、暗端谷底、d_max、亮部白平衡
- 预反转校正：LCC 平场、镜头畸变、暗角、齿孔遮罩
- Stage 2：曝光 / 色阶 / 对比度 / 高光阴影 / PCHIP 曲线 / 饱和度
- 智能白平衡（需自备 `net_awb.onnx`，见 README）

### 工作流

- 按卷管理 + 图库卷墙，封面为每卷印样
- 自动保存，无「保存工程」动作；`.ncproj` 随源图像放置
- 虚拟副本、整卷 / 勾选帧同步标定与场景
- 冲印店风格整版印样，底部自带卷标识条
- 界面中英双语：默认跟随系统语言，也可在偏好设置里锁定中文 / English，切换即时生效；
  菜单栏 File / Edit / View / Settings / Help 两种语言下都保持英文

### 输入输出

- RAW（LibRaw）、TIFF、JPEG、PNG 输入
- 8/16-bit TIFF 与 JPEG 导出，可嵌 sRGB / Adobe RGB ICC profile

### 平台

- **Windows x64** —— 正式
- **Linux x86_64（Beta）** —— AppImage，需 glibc ≥ 2.35
- **macOS Apple Silicon（Beta，未在真机验证）** —— dmg，ad-hoc 签名，
  首次打开需绕 Gatekeeper（见 README）
