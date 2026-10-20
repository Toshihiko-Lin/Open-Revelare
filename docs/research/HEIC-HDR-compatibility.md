# HDR 照片跨设备兼容性与商业方案调研

调研批次：2026-10-20 / 21。范围包括手机、电脑、电视和相机。依据为官方文档及本对话用户报告，未进行跨品牌真机验收。“未确认”不等于“不支持”。

## 面向未知设备的交付选择

若只能选一种分享文件，建议 **sRGB SDR JPEG 主图＋HDR 增益图，同时写入 Adobe/Ultra HDR 与 ISO 21496-1 元数据**。这是对现有商业实现的综合判断，不是所有平台均保留 HDR 的保证。旧查看器仍可显示正常 SDR，支持增益图的查看器恢复 HDR。[Lightroom 官方](https://helpx.adobe.com/my_en/lightroom/desktop/edit-photos/hdr-output.html)推荐 JPEG 用于分享；[Android 规范](https://developer.android.com/media/platform/hdr-image-format)建议为跨平台兼容同时支持两套元数据。

Apple 生态另提供 Adaptive HDR HEIC；直接 HLG/PQ HEIC 用于已验证的目标设备和紧凑单层交付。没有证据证明 HLG 普遍比 PQ 更兼容手机相册。项目现已恢复 PQ 为直接 HDR HEIC 默认；PQ 或 HLG 均不能宣称经过跨设备验证的“最大兼容”。

商业产品确实考虑兼容，但服务的目标不同：相机可以依赖厂商软件或 HDMI，手机可以依赖系统相册，而未知用户间的分享更依赖 SDR 回退。某种格式作为商业产品推出，不等于任意厂商的任意相册都支持它。

## 兼容性需要拆开的四层

1. 能打开：容器、HEVC profile、位深、采样方式、网格与尺寸。
2. 画面正常：ICC/NCLX、曲线、色域和 SDR 回退。
3. HDR 正确显示：格式识别、应用 HDR 渲染、屏幕余量及显示设置。
4. 发布仍保留 HDR：传输、裁切、滤镜、重编码和服务端处理。

OLED、10-bit、HEIF、HDR 视频播放和相机 HDR 合成模式，均不能单独证明外部 HDR HEIC 的显示兼容。

## 手机、电脑与电视

- **Apple**：官方提供 PQ/HLG ISO HDR 与 Adaptive HDR 路径；Photos、Preview、Quick Look 的支持随系统版本不同。系统 API 能力不能外推为每个应用或本项目输出都已兼容。[WWDC24](https://developer.apple.com/videos/play/wwdc2024/10177/)
- **Android / Pixel**：Android 16 新增带增益图的 HEIC_ULTRAHDR；Google Photos 支持 Ultra HDR。具体厂商相册对 PQ、HLG、标准增益图 HEIC 仍需逐项验证。[Android 16](https://developer.android.com/about/versions/16/features)、[Google Photos](https://support.google.com/photos/answer/14159275?hl=en-en)
- **小米**：用户报告 17 Pro Max／澎湃 OS 4 的系统相册未呈现项目 PQ 文件的 HDR，QQ 可呈现，增益图 JPEG 正常；未核实 QQ 是否转换原文件。新 HLG/Adaptive 输出待测。小米对 14 Ultra 的 Pro HDR 说明不能外推全系列。[小米支持](https://www.mi.com/global/support/faq/details/KA-171463/)
- **三星**：有 Super HDR，One UI 7 开关位于系统高级功能；这不是外部 HEIC 各种编码的完整兼容清单。[Samsung 支持](https://www.samsung.com/ae/support/mobile-devices/where-can-you-find-the-super-hdr-option-in-the-one-ui-7-gallery/)
- **OPPO／一加**：ProXDR、HEIF、UHDR 功能有机型、相册或硬件条件，不能仅凭商标确认 ISO gain-map HEIC 支持。[OPPO](https://support.oppo.com/eg-en/answer/?aid=2170948)、[OnePlus 公告](https://community.oneplus.com/thread/1616294695330119680)
- **vivo、荣耀、华为**：有官方 XDR/HDR 大图显示说明，但未找到覆盖外部 PQ/HLG/标准增益图 HEIC 的完整输入矩阵；查看与编辑状态也可能不同。[vivo](https://www.vivo.com/ae/en/products/v27)、[荣耀](https://www.honor.com/cn/support/content/zh-cn15854482/)、[华为](https://consumer.huawei.com/cn/support/content/zh-cn16027928/)
- **Windows**：HEIF 解码与 HDR 显示是两层。2026-10-15 新版 Photos 公告提及 HDR 查看改进和 HDR/HEIC 编辑，但不构成三种 HDR HEIC 的逐项保证。应绑定应用版本验收。[Photos 公告](https://blogs.windows.com/windowsexperience/2026/10/15/meet-the-new-photos-app-on-windows-11/)、[WIC HEIF codec](https://learn.microsoft.com/en-us/windows/win32/wic/heif-codec)
- **电视**：Sony 部分 BRAVIA 手册列有 HEIF；Panasonic TH-50/43MX900 手册列 JPEG 和 HLG Photo .HSP。HLG 视频/HDMI 支持不能推导 USB 相册支持 HLG HEIC。普通电脑显示器接收解码后的信号，文件兼容主要由主机应用决定。[Sony 手册](https://helpguide.sony.net/tv/iusltn1/v1/en-003/print.html)、[Panasonic 手册](https://av.jpn.support.panasonic.com/support/tv/em/th_50_43mx900_jp_em_s_files/part172.htm)

## 相机厂商

- **佳能 EOS R5 示例**：HDR PQ 产生 HEIF/RAW，属于直接 PQ；相机 HDMI HDR 输出可提供受控观看路径，不保证手机相册兼容。[官方手册](https://cam.start.canon/en/C003/manual/html/UG-03_Shooting-1_0280.html)
- **索尼 ZV-E10 II 示例**：HEIF 支持 4:2:0/4:2:2，HLG Still Image 开启时为 BT.2100，关闭时可为普通 sRGB。采样方式影响解码要求，不能只比较 HDR 曲线。[官方手册](https://helpguide.sony.net/ilc/2430/v1/en/print.html)
- **尼康 Z6III／Z8 示例**：HLG HEIF .HIF 是直接 HLG HDR；可结合 NX Studio 和兼容显示链路使用。SnapBridge 能下载，不代表每个系统与应用版本都能正常显示。[Z6III 手册](https://onlinemanual.nikonimglib.com/z6III/en/psm_tone_mode_130.html)、[HLG 系统配置](https://onlinemanual.nikonimglib.com/notice/hlg_setting_guide/en/)
- **富士 X-T5 示例**：有 10-bit 4:2:2 HEIF，但不能据此宣称 HDR 显示编码。HDR 合成拍摄改存 JPEG，是不同概念。[格式参数](https://app.fujifilm-dsc.com/en-int/manual/x-t5/technical_notes/spec/index.html)、[拍摄设置](https://fujifilm-dsc.com/en/manual/x-t5/menu_shooting/image_quality_setting/)
- **松下**：早期部分 LUMIX S 系列有 HLG Photo .HSP；新 S1IIE 示例已有 HEVC 10-bit 4:2:0 HEIF，并可选 HDR(HLG) 或 SDR，不能概括成只支持 HSP。[早期 HLG Photo](https://help.na.panasonic.com/answers/how-to-take-pictures-using-hlg-photo-lumix-s-series/)、[S1IIE 手册](https://eww.pavc.panasonic.co.jp/dscoi/DC-S1M2ES/html/DC-S1M2ES_DVQP3355_eng/0080.html)

厂商 App 也可能改变格式。LUMIX Lab 官方说明 HEIF 编辑会从 10-bit 转为 8-bit；能导入不意味着导出保持原始 HDR 表现。[官方说明](https://av.jpn.support.panasonic.com/support/global/cs/soft/lumix_lab/en/cts/a1.html)

## 产品建议与最小实测集

建议按用途提供“跨平台分享：增益图 JPEG”“Apple 相册：Adaptive HDR HEIC”“目标 HDR 查看器：HLG/PQ HEIC”。不把某个曲线名称当作兼容性等级。PQ 仍有正式厂商支持，不应认定为错误或过时格式。本轮调研没有自动改变当前导出选项。

以 Lightroom 和 Apple 系统原生导出为参照组，同一母版准备 SDR HEIC、PQ 对照、HLG HEIC、Adaptive HDR HEIC、双标准增益图 JPEG。覆盖小图/原尺寸、灰阶、细小高光、饱和色、颗粒、黑场和横竖方向；另验证 standalone/grid、4:2:0/4:2:2 的差异。

先以文件方式传输并核对 SHA-256。记录机型、系统/相册版本、HDR 开关、亮度和省电状态；分别记录缩略图、大图、编辑页、发布后结果。验收解码、SDR 回退、HDR 触发、颜色亮度、几何与上传保留情况。SDR 截图和 HDR 标识不能单独证明实际 HDR 峰值与色彩正确。项目软件测试通过，不能代替多设备真机验收。
