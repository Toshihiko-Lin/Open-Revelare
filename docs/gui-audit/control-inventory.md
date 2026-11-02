# GUI 控件逐项索引

由 `build_inventory.py` 生成。语义解读见同目录审计文档。

基线：`0e638380d6a8d953ef8b970c6bce903a2410a19e`；核对日期：2026-11-01。

扫描 10 个 XAML 文件，记录 392 个声明；另列 106 条 C# 动态构造／接线候选。

计数是源代码声明数，不是功能数或屏幕上同时可见的按钮数。子菜单、选项、折叠组和复合控件内部声明分别计数；模板在运行时可重复出现。

标签优先采用无障碍名称、Label、Header、Content、Watermark，其次子节点文字、tooltip 或 Name。祖先条件存于 JSON，不能把控件自己的 IsEnabled 当作最终可用状态。输入事件表面也纳入索引，例如可点击卷卡和预览 Canvas。

## ContactSheetDialog（25）

来源：`src/OpenRevelare.Gui/Views/ContactSheetDialog.axaml`

### L19 · Button · 关闭

- Content：`{i18n:T '关闭'}`
- IsCancel：`True`
- Click → `OnCloseClick`

### L20 · Button · 导出印样…

- Content：`{i18n:T '导出印样…'}`
- Click → `OnExportClick`

### L34 · RadioButton · 浅色

- Name：`StyleLight`
- GroupName：`SheetStyle`
- Content：`{i18n:T '浅色'}`
- IsCheckedChanged → `OnStyleChanged`

### L36 · RadioButton · 深色

- Name：`StyleDark`
- GroupName：`SheetStyle`
- Content：`{i18n:T '深色'}`
- IsCheckedChanged → `OnStyleChanged`

### L44 · ComboBox · AspectBox

- Name：`AspectBox`
- SelectionChanged → `OnAspectChanged`

### L46 · ComboBoxItem · 自动（1:1 ~ 4:3）

- 所在分组：AspectBox
- Content：`{i18n:T '自动（1:1 ~ 4:3）'}`

### L47 · ComboBoxItem · 1:1

- 所在分组：AspectBox
- Content：`1:1`

### L48 · ComboBoxItem · 4:3

- 所在分组：AspectBox
- Content：`4:3`

### L49 · ComboBoxItem · 3:2

- 所在分组：AspectBox
- Content：`3:2`

### L53 · RadioButton · 横

- 所在分组：OrientPanel
- Name：`OrientWide`
- GroupName：`SheetOrient`
- Content：`{i18n:T '横'}`
- IsCheckedChanged → `OnOrientChanged`

### L55 · RadioButton · 竖

- 所在分组：OrientPanel
- Name：`OrientTall`
- GroupName：`SheetOrient`
- Content：`{i18n:T '竖'}`
- IsCheckedChanged → `OnOrientChanged`

### L60 · ComboBox · SizeBox

- Name：`SizeBox`
- SelectionChanged → `OnSizeChanged`

### L62 · ComboBoxItem · 小 · 约 6 MP

- 所在分组：SizeBox
- Content：`{i18n:T '小 · 约 6 MP'}`

### L63 · ComboBoxItem · 中 · 约 12 MP

- 所在分组：SizeBox
- Content：`{i18n:T '中 · 约 12 MP'}`

### L64 · ComboBoxItem · 大 · 约 24 MP

- 所在分组：SizeBox
- Content：`{i18n:T '大 · 约 24 MP'}`

### L73 · CheckBox · HDR 印样

- 所在分组：HdrPanel
- Name：`HdrBox`
- Content：`{i18n:T 'HDR 印样'}`
- IsCheckedChanged → `OnHdrChanged`

### L82 · TextBox · TextBox

- Text：`{Binding CameraBody}`

### L84 · TextBox · TextBox

- Text：`{Binding FilmStock}`

### L86 · TextBox · TextBox

- Text：`{Binding FilmIso}`

### L88 · TextBox · TextBox

- Text：`{Binding RollNumber}`

### L90 · TextBox · TextBox

- Text：`{Binding DevLab}`

### L92 · TextBox · TextBox

- Text：`{Binding DevProcess}`

### L94 · TextBox · TextBox

- Text：`{Binding DevDate}`

### L96 · TextBox · TextBox

- Text：`{Binding Location}`

### L99 · TextBox · TextBox

- Text：`{Binding RollNote}`

## ExportDialog（54）

来源：`src/OpenRevelare.Gui/Views/ExportDialog.axaml`

### L78 · Button · 关闭

- 所在分组：FilmInfoDrawer
- Content：`{i18n:T '关闭'}`
- Click → `OnCloseFilmInfoClick`

### L86 · Button · ←

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup / FilmFrameNavigator
- Name：`PreviousFilmFrameBtn`
- Content：`←`
- ToolTip.Tip：`{i18n:T '上一张'}`
- Click → `OnPreviousFilmFrame`
- 祖先 L83 StackPanel FilmFrameNavigator：`IsVisible=False`

### L88 · Button · →

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup / FilmFrameNavigator
- Name：`NextFilmFrameBtn`
- Content：`→`
- ToolTip.Tip：`{i18n:T '下一张'}`
- Click → `OnNextFilmFrame`
- 祖先 L83 StackPanel FilmFrameNavigator：`IsVisible=False`

### L91 · CheckBox · 机身同步整卷

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup / FilmFrameNavigator / FilmDeviceSyncRow
- Name：`CameraSyncChk`
- Content：`{i18n:T '机身同步整卷'}`
- IsChecked：`True`
- IsCheckedChanged → `OnDeviceSyncChanged`
- 祖先 L83 StackPanel FilmFrameNavigator：`IsVisible=False`

### L92 · CheckBox · 镜头同步整卷

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup / FilmFrameNavigator / FilmDeviceSyncRow
- Name：`LensSyncChk`
- Content：`{i18n:T '镜头同步整卷'}`
- IsChecked：`True`
- IsCheckedChanged → `OnDeviceSyncChanged`
- 祖先 L83 StackPanel FilmFrameNavigator：`IsVisible=False`

### L99 · ComboBox · CameraLibraryBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`CameraLibraryBox`
- SelectionChanged → `OnCameraSelected`

### L103 · TextBox · CameraMakeBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`CameraMakeBox`
- TextChanged → `OnCameraEdited`

### L107 · TextBox · CameraModelBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`CameraModelBox`
- TextChanged → `OnCameraEdited`

### L111 · TextBox · CameraSerialBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`CameraSerialBox`
- TextChanged → `OnCameraEdited`

### L118 · ComboBox · LensLibraryBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`LensLibraryBox`
- SelectionChanged → `OnLensSelected`

### L122 · TextBox · LensMakeBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`LensMakeBox`
- TextChanged → `OnLensEdited`

### L126 · TextBox · LensModelBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`LensModelBox`
- TextChanged → `OnLensEdited`

### L130 · TextBox · LensSerialBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`LensSerialBox`
- TextChanged → `OnLensEdited`

### L140 · TextBox · 400

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`FilmIsoBox`
- Watermark：`400`
- TextChanged → `OnFilmFieldChanged`

### L146 · TextBox · 2.8

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`FilmApertureBox`
- Watermark：`2.8`
- TextChanged → `OnFilmFieldChanged`

### L152 · TextBox · 1/125

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`FilmShutterBox`
- Watermark：`1/125`
- TextChanged → `OnFilmFieldChanged`

### L158 · TextBox · 50

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`FilmFocalBox`
- Watermark：`50`
- TextChanged → `OnFilmFieldChanged`

### L164 · TextBox · YYYY-MM-DD HH:mm:ss

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`FilmDateBox`
- Watermark：`YYYY-MM-DD HH:mm:ss`
- TextChanged → `OnFilmFieldChanged`

### L170 · TextBox · FilmArtistBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`FilmArtistBox`
- TextChanged → `OnFilmFieldChanged`

### L176 · TextBox · FilmCopyrightBox

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent / FilmEditorGroup
- Name：`FilmCopyrightBox`
- TextChanged → `OnFilmFieldChanged`

### L181 · CheckBox · 导出时写入胶片拍摄信息

- 所在分组：FilmInfoDrawer / FilmInfoPaneContent
- Name：`FilmInfoChk`
- Content：`{i18n:T '导出时写入胶片拍摄信息'}`
- IsCheckedChanged → `OnAnyChanged`

### L207 · Button · 取消

- 所在分组：FilmInfoDrawer
- Content：`{i18n:T '取消'}`
- IsCancel：`True`
- Click → `OnCancelClick`

### L208 · Button · 选择位置 →

- 所在分组：FilmInfoDrawer
- Name：`OkBtn`
- IsDefault：`True`
- Content：`{i18n:T '选择位置 →'}`
- Click → `OnAcceptClick`

### L222 · RadioButton · JPEG

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`FmtJpeg`
- AutomationProperties.Name：`JPEG`
- GroupName：`fmt`
- IsCheckedChanged → `OnFormatChanged`

### L230 · RadioButton · HEIC

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`FmtHeic`
- AutomationProperties.Name：`HEIC`
- GroupName：`fmt`
- IsCheckedChanged → `OnFormatChanged`

### L238 · RadioButton · TIFF

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`FmtTiff`
- AutomationProperties.Name：`TIFF`
- GroupName：`fmt`
- IsCheckedChanged → `OnFormatChanged`

### L246 · RadioButton · 线性 DNG

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`FmtDng`
- AutomationProperties.Name：`{i18n:T '线性 DNG'}`
- GroupName：`fmt`
- IsCheckedChanged → `OnFormatChanged`

### L260 · ComboBox · CompressBox

- 所在分组：FilmInfoDrawer / OptionsScroll / TiffGroup
- Name：`CompressBox`
- SelectionChanged → `OnAnyChanged`

### L262 · ComboBoxItem · LZW（无损，推荐）

- 所在分组：FilmInfoDrawer / OptionsScroll / TiffGroup / CompressBox
- Content：`{i18n:T 'LZW（无损，推荐）'}`

### L263 · ComboBoxItem · Deflate（无损，文件更小、更慢）

- 所在分组：FilmInfoDrawer / OptionsScroll / TiffGroup / CompressBox
- Content：`{i18n:T 'Deflate（无损，文件更小、更慢）'}`

### L264 · ComboBoxItem · 不压缩（最快、最大）

- 所在分组：FilmInfoDrawer / OptionsScroll / TiffGroup / CompressBox
- Content：`{i18n:T '不压缩（最快、最大）'}`

### L271 · CheckBox · 场景线性 ACEScg（32-bit float，不含帧编辑）

- 所在分组：FilmInfoDrawer / OptionsScroll / TiffGroup
- Name：`LinearChk`
- Content：`{i18n:T '场景线性 ACEScg（32-bit float，不含帧编辑）'}`
- IsCheckedChanged → `OnAnyChanged`

### L284 · Slider · QualitySlider

- 所在分组：FilmInfoDrawer / OptionsScroll / JpegGroup / JpegQualityRow
- Name：`QualitySlider`
- Minimum：`40`
- Maximum：`100`
- ValueChanged → `OnQualityChanged`

### L295 · ComboBox · HeicHdrBox

- 所在分组：FilmInfoDrawer / OptionsScroll / JpegGroup / HeicHdrRow
- Name：`HeicHdrBox`
- SelectionChanged → `OnAnyChanged`

### L297 · ComboBoxItem · 单层 HDR（PQ）

- 所在分组：FilmInfoDrawer / OptionsScroll / JpegGroup / HeicHdrRow / HeicHdrBox
- Content：`{i18n:T '单层 HDR（PQ）'}`

### L298 · ComboBoxItem · Adaptive HDR（标准增益图）

- 所在分组：FilmInfoDrawer / OptionsScroll / JpegGroup / HeicHdrRow / HeicHdrBox
- Content：`{i18n:T 'Adaptive HDR（标准增益图）'}`

### L306 · Slider · HeicQualitySlider

- 所在分组：FilmInfoDrawer / OptionsScroll / JpegGroup / HeicQualityRow
- Name：`HeicQualitySlider`
- Minimum：`40`
- Maximum：`100`
- ValueChanged → `OnQualityChanged`

### L315 · CheckBox · 限制文件大小

- 所在分组：FilmInfoDrawer / OptionsScroll / JpegGroup
- Name：`SizeLimitChk`
- Content：`{i18n:T '限制文件大小'}`
- IsCheckedChanged → `OnAnyChanged`

### L323 · NumericUpDown · SizeLimitBox

- 所在分组：FilmInfoDrawer / OptionsScroll / JpegGroup / SizeLimitRow
- Name：`SizeLimitBox`
- Minimum：`0.1`
- Maximum：`1000`
- Increment：`0.5`
- ValueChanged → `OnAnyChanged`

### L344 · ComboBox · HdrBaseBox

- 所在分组：FilmInfoDrawer / OptionsScroll / HdrBaseGroup
- Name：`HdrBaseBox`
- SelectionChanged → `OnAnyChanged`

### L346 · ComboBoxItem · sRGB（通用）

- 所在分组：FilmInfoDrawer / OptionsScroll / HdrBaseGroup / HdrBaseBox
- Content：`{i18n:T 'sRGB（通用）'}`

### L347 · ComboBoxItem · Display P3（宽色域）

- 所在分组：FilmInfoDrawer / OptionsScroll / HdrBaseGroup / HdrBaseBox
- Content：`{i18n:T 'Display P3（宽色域）'}`

### L356 · CheckBox · 嵌入 ICC 配置文件

- 所在分组：FilmInfoDrawer / OptionsScroll / IccRow
- Name：`IccChk`
- Content：`{i18n:T '嵌入 ICC 配置文件'}`
- IsCheckedChanged → `OnIccChanged`

### L371 · CheckBox · 缩放到指定长边

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`DownsampleChk`
- Content：`{i18n:T '缩放到指定长边'}`
- IsCheckedChanged → `OnDownsampleToggled`

### L377 · NumericUpDown · LongEdgeBox

- 所在分组：FilmInfoDrawer / OptionsScroll / LongEdgeRow
- Name：`LongEdgeBox`
- Minimum：`256`
- Maximum：`20000`
- Increment：`256`
- ValueChanged → `OnAnyChanged`

### L382 · CheckBox · 长边不足时也放大

- 所在分组：FilmInfoDrawer / OptionsScroll / LongEdgeRow
- Name：`UpscaleChk`
- Content：`{i18n:T '长边不足时也放大'}`
- IsCheckedChanged → `OnAnyChanged`

### L391 · ComboBox · SharpenBox

- 所在分组：FilmInfoDrawer / OptionsScroll / SharpenRow
- Name：`SharpenBox`
- SelectionChanged → `OnAnyChanged`

### L404 · CheckBox · 保留源文件 EXIF／数字化来源

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`KeepExifChk`
- Content：`{i18n:T '保留源文件 EXIF／数字化来源'}`
- ToolTip.Tip：`{i18n:T '未填写胶片信息时沿用源 EXIF；填写后，源翻拍／扫描信息单独保存在 XMP 中。'}`
- IsCheckedChanged → `OnAnyChanged`

### L405 · Button · 编辑胶片拍摄信息 →

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`FilmInfoEntry`
- Content：`{i18n:T '编辑胶片拍摄信息 →'}`
- Click → `OnOpenFilmInfoClick`

### L419 · ComboBox · NamePresetBox

- 所在分组：FilmInfoDrawer / OptionsScroll
- Name：`NamePresetBox`
- SelectionChanged → `OnNamePresetChanged`

### L426 · TextBox · NameTemplateBox

- 所在分组：FilmInfoDrawer / OptionsScroll / CustomNameRow
- Name：`NameTemplateBox`
- TextChanged → `OnTemplateChanged`

### L449 · RadioButton · 另存为新名

- 所在分组：FilmInfoDrawer / OptionsScroll / ConflictGroup
- Name：`ConflictUnique`
- GroupName：`conflict`
- Content：`{i18n:T '另存为新名'}`
- IsCheckedChanged → `OnAnyChanged`

### L451 · RadioButton · 覆盖

- 所在分组：FilmInfoDrawer / OptionsScroll / ConflictGroup
- Name：`ConflictOverwrite`
- GroupName：`conflict`
- Content：`{i18n:T '覆盖'}`
- IsCheckedChanged → `OnAnyChanged`

### L453 · RadioButton · 跳过该帧

- 所在分组：FilmInfoDrawer / OptionsScroll / ConflictGroup
- Name：`ConflictSkip`
- GroupName：`conflict`
- Content：`{i18n:T '跳过该帧'}`
- IsCheckedChanged → `OnAnyChanged`

## ImportDialog（19）

来源：`src/OpenRevelare.Gui/Views/ImportDialog.axaml`

### L12 · RadioButton · 新建卷

- 所在分组：DestinationRow
- Name：`NewRollChoice`
- GroupName：`destination`
- Content：`{i18n:T '新建卷'}`
- IsChecked：`True`
- 祖先 L11 StackPanel DestinationRow：`IsVisible=False`

### L13 · RadioButton · 添加到当前卷

- 所在分组：DestinationRow
- Name：`CurrentRollChoice`
- GroupName：`destination`
- Content：`{i18n:T '添加到当前卷'}`
- IsCheckedChanged → `OnDestinationChanged`
- 祖先 L11 StackPanel DestinationRow：`IsVisible=False`

### L17 · Button · 取消

- Content：`{i18n:T '取消'}`
- IsCancel：`True`
- Click → `OnCancelClick`

### L18 · Button · 导入

- Name：`OkBtn`
- Content：`{i18n:T '导入'}`
- IsDefault：`True`
- IsEnabled：`False`
- Click → `OnAcceptClick`

### L28 · ListBox · FileList

- Name：`FileList`
- SelectionMode：`Multiple`

### L31 · Button · 添加文件…

- Content：`{i18n:T '添加文件…'}`
- Click → `OnAddFiles`

### L32 · Button · 添加文件夹…

- Content：`{i18n:T '添加文件夹…'}`
- Click → `OnAddFolder`

### L33 · Button · 移除选中

- Content：`{i18n:T '移除选中'}`
- Click → `OnRemoveSelected`

### L34 · Button · 清空

- Content：`{i18n:T '清空'}`
- Click → `OnClear`

### L40 · Expander · 高级导入设置（分割 / 光源 / 平场）

- Name：`AdvancedImportSettings`
- Header：`{i18n:T '高级导入设置（分割 / 光源 / 平场）'}`
- IsExpanded：`False`

### L45 · CheckBox · 导入时识别一张里的多格底片并分割

- 所在分组：高级导入设置（分割 / 光源 / 平场）
- Name：`SplitChk`
- Content：`{i18n:T '导入时识别一张里的多格底片并分割'}`
- ToolTip.Tip：`{i18n:T '扫描仪常把一整条底片扫成一张图。勾选后导入时先识别每条有几格，并弹窗让你确认分隔线。'}`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`

### L54 · RadioButton · 宽谱光源（白光）— Path B

- 所在分组：高级导入设置（分割 / 光源 / 平场）
- Name：`SrcB`
- GroupName：`src`
- Content：`{i18n:T '宽谱光源（白光）— Path B'}`
- IsChecked：`True`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`

### L55 · RadioButton · 窄谱光源（RGB 合成）— Path A，需校正图

- 所在分组：高级导入设置（分割 / 光源 / 平场）
- Name：`SrcA`
- GroupName：`src`
- Content：`{i18n:T '窄谱光源（RGB 合成）— Path A，需校正图'}`
- ToolTip.Tip：`{i18n:T 'RGB LED 灯板翻拍：分别拍红 / 绿 / 蓝三张，加一张空白灯板 RAW，放在同一文件夹（自动识别）。据此解出通道串扰，还原更准确的原色。'}`
- Checked → `OnSourceChanged`
- Unchecked → `OnSourceChanged`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`

### L61 · TextBox · 选择含 R/G/B 校正图的文件夹

- 所在分组：高级导入设置（分割 / 光源 / 平场） / CalRow
- Name：`CalEdit`
- IsReadOnly：`True`
- Watermark：`{i18n:T '选择含 R/G/B 校正图的文件夹'}`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`
- 祖先 L58 StackPanel CalRow：`IsVisible=False`

### L63 · Button · 选择…

- 所在分组：高级导入设置（分割 / 光源 / 平场） / CalRow
- Content：`{i18n:T '选择…'}`
- Click → `OnPickCal`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`
- 祖先 L58 StackPanel CalRow：`IsVisible=False`

### L72 · CheckBox · 启用 LCC 平场校正

- 所在分组：高级导入设置（分割 / 光源 / 平场）
- Name：`LccChk`
- Content：`{i18n:T '启用 LCC 平场校正'}`
- ToolTip.Tip：`{i18n:T '拍一张均匀光的平场参考图，逐像素/逐通道修正本翻拍镜头的暗角与角落偏色。'}`
- IsCheckedChanged → `OnLccToggled`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`

### L76 · TextBox · 均匀光的平场参考图（RAW 或 TIFF）

- 所在分组：高级导入设置（分割 / 光源 / 平场） / LccRow
- Name：`LccEdit`
- IsReadOnly：`True`
- Watermark：`{i18n:T '均匀光的平场参考图（RAW 或 TIFF）'}`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`
- 祖先 L75 Grid LccRow：`IsVisible=False`

### L78 · Button · 选择…

- 所在分组：高级导入设置（分割 / 光源 / 平场） / LccRow
- Content：`{i18n:T '选择…'}`
- Click → `OnPickLcc`
- 祖先 L40 Expander AdvancedImportSettings：`IsExpanded=False`
- 祖先 L75 Grid LccRow：`IsVisible=False`

### L87 · CheckBox · 导入后自动分析整卷（推荐）

- 所在分组：AutoImportOptions
- Name：`AutoInvertChk`
- Content：`{i18n:T '导入后自动分析整卷（推荐）'}`
- IsChecked：`True`
- ToolTip.Tip：`{i18n:T '分析整卷的片基与高光代理，统一标定端点；完成后刷新预览。'}`

## InfoDialog（3）

来源：`src/OpenRevelare.Gui/Views/InfoDialog.axaml`

### L33 · Button · 关闭

- Name：`CloseButton`
- Content：`{i18n:T '关闭'}`
- IsCancel：`True`
- Click → `OnCloseClick`

### L35 · Button · SecondaryActionButton

- Name：`SecondaryActionButton`
- IsVisible：`False`
- Click → `OnSecondaryActionClick`

### L36 · Button · ActionButton

- Name：`ActionButton`
- IsVisible：`False`
- Click → `OnActionClick`

## LibraryView（16）

来源：`src/OpenRevelare.Gui/Views/LibraryView.axaml`

### L67 · TextBox · 搜索卷名 / 卷号 / 胶卷 / 相机

- Text：`{Binding SearchText}`
- Watermark：`{i18n:T '搜索卷名 / 卷号 / 胶卷 / 相机'}`
- 祖先 L52 Border ：`IsVisible={Binding !IsEmpty}`

### L72 · ComboBox · {Binding Name}

- ItemsSource：`{Binding SortOptions}`
- SelectedItem：`{Binding SelectedSort}`
- ToolTip.Tip：`{i18n:T '卷墙的排列顺序。该项为空的卷排在最后'}`
- 祖先 L52 Border ：`IsVisible={Binding !IsEmpty}`

### L82 · Button · 排序方向

- AutomationProperties.Name：`{i18n:T '排序方向'}`
- ToolTip.Tip：`{Binding SortDirectionTip}`
- Click → `OnSortDirectionClick`
- 祖先 L52 Border ：`IsVisible={Binding !IsEmpty}`

### L99 · Button · 清除

- Content：`{i18n:T '清除'}`
- IsVisible：`{Binding HasActiveFilter}`
- Click → `OnClearFiltersClick`
- 祖先 L52 Border ：`IsVisible={Binding !IsEmpty}`

### L114 · CheckBox · {Binding Label}

- IsChecked：`{Binding IsChecked}`
- Content：`{Binding Label}`
- 祖先 L52 Border ：`IsVisible={Binding !IsEmpty}`

### L143 · Border · 新建卷… / 选择一张或多张负片（多选 = 整卷） /

- 所在分组：RollWallScroll
- IsVisible：`{Binding IsNewTile}`
- Tapped → `OnNewTileTapped`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`

### L168 · Border · （暂无印样） / {Binding Title} / {Binding Subtitle} / {Binding Detail}

- 所在分组：RollWallScroll
- Name：`Card`
- IsVisible：`{Binding !IsNewTile}`
- DoubleTapped → `OnCardDoubleTapped`
- PointerPressed → `OnCardPressed`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`

### L176 · MenuItem · 打开这一卷

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '打开这一卷'}`
- Click → `OnOpenClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L177 · MenuItem · 重命名…

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '重命名…'}`
- Click → `OnRenameClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L178 · MenuItem · 卷信息…

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '卷信息…'}`
- ToolTip.Tip：`{i18n:T '相机 / 胶卷 / 卷号 / 冲洗 …，会烧在印样标识条上'}`
- Click → `OnInfoClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L180 · MenuItem · 保存封面印样副本…

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '保存封面印样副本…'}`
- ToolTip.Tip：`{i18n:T '另存这一卷的印样封面（不需要重新解码整卷）'}`
- Click → `OnExportSheetClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L182 · MenuItem · 重新定位工程…

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '重新定位工程…'}`
- ToolTip.Tip：`{i18n:T '工程文件被移动过时，指向它的新位置'}`
- Click → `OnRelocateClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L185 · MenuItem · 在资源管理器中显示

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '在资源管理器中显示'}`
- Click → `OnRevealClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L187 · MenuItem · 从目录移除（保留工程文件）

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '从目录移除（保留工程文件）'}`
- Click → `OnForgetClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L188 · MenuItem · 删除工程文件…

- 所在分组：RollWallScroll / Card
- Header：`{i18n:T '删除工程文件…'}`
- Click → `OnDeleteClick`
- 祖先 L129 ScrollViewer RollWallScroll：`IsVisible={Binding !IsEmpty}`
- 祖先 L168 Border Card：`IsVisible={Binding !IsNewTile}`

### L239 · Button · 导入第一卷

- Click → `OnNewRollClick`
- 祖先 L228 Border ：`IsVisible={Binding IsEmpty}`

## MainWindow（243）

来源：`src/OpenRevelare.Gui/Views/MainWindow.axaml`

### L36 · MenuItem · _File

- 所在分组：NavigationBar / WindowMenu
- Header：`_File`
- SubmenuOpened → `OnFileMenuOpened`

### L37 · MenuItem · 新建卷…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '新建卷…'}`
- InputGesture：`{i18n:Accel 'N'}`
- Click → `OnOpenClick`

### L38 · MenuItem · 添加图像到当前卷…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '添加图像到当前卷…'}`
- InputGesture：`{i18n:Accel 'O'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnAddImagesClick`

### L43 · MenuItem · 最近的卷

- 所在分组：NavigationBar / WindowMenu / _File
- Name：`RecentRollsMenu`
- Header：`{i18n:T '最近的卷'}`

### L44 · MenuItem · 打开工程…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '打开工程…'}`
- Click → `OnOpenProjectClick`

### L45 · MenuItem · 扫描文件夹加入目录…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '扫描文件夹加入目录…'}`
- ToolTip.Tip：`{i18n:T '找出该文件夹（含子文件夹）里所有 .ncproj 并登记到图库；目录丢了也能这样重建'}`
- Click → `OnScanFolderClick`

### L47 · MenuItem · 另存工程副本…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '另存工程副本…'}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '当前卷会自动保存；这里是导出一份副本'}`
- Click → `OnSaveProjectClick`

### L49 · MenuItem · 查看工程依赖…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '查看工程依赖…'}`
- IsEnabled：`{Binding CanInspectProjectDependencies}`
- Click → `OnProjectDependenciesClick`

### L51 · MenuItem · 归档工程与原片…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '归档工程与原片…'}`
- IsEnabled：`{Binding CanInspectProjectDependencies}`
- Click → `OnArchiveProjectClick`

### L54 · MenuItem · 导出当前帧…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '导出当前帧…'}`
- InputGesture：`{i18n:Accel 'E'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnExportClick`

### L60 · MenuItem · 导出整卷…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '导出整卷…'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnExportRollClick`

### L61 · MenuItem · 查看上次批量导出结果…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '查看上次批量导出结果…'}`
- IsEnabled：`{Binding CanShowBatchExportReport}`
- Click → `OnBatchExportReportClick`

### L63 · MenuItem · 导出印样…

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '导出印样…'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnContactSheetClick`

### L69 · MenuItem · 退出

- 所在分组：NavigationBar / WindowMenu / _File
- Header：`{i18n:T '退出'}`
- InputGesture：`Alt+F4`
- Click → `OnQuitClick`

### L72 · MenuItem · _Edit

- 所在分组：NavigationBar / WindowMenu
- Header：`_Edit`

### L73 · MenuItem · 撤销

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '撤销'}`
- InputGesture：`{i18n:Accel 'Z'}`
- IsEnabled：`{Binding CanUndo}`
- Click → `OnUndoClick`

### L78 · MenuItem · 重做

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '重做'}`
- InputGesture：`{i18n:Accel 'Y'}`
- IsEnabled：`{Binding CanRedo}`
- Click → `OnRedoClick`

### L84 · MenuItem · 重置本帧调整

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '重置本帧调整'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnResetClick`

### L86 · MenuItem · 复制（按当前面板）

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '复制（按当前面板）'}`
- InputGesture：`{i18n:Accel 'C'}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '整卷校准面板 → 复制底片校准；帧编辑面板 → 复制成片调整'}`
- Click → `OnCopyActiveClick`

### L93 · MenuItem · 粘贴到本帧（按当前面板）

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '粘贴到本帧（按当前面板）'}`
- InputGesture：`{i18n:Accel 'V'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnPasteActiveClick`

### L100 · MenuItem · 底片校准

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '底片校准'}`
- IsEnabled：`{Binding HasImage}`

### L101 · MenuItem · 复制底片校准

- 所在分组：NavigationBar / WindowMenu / _Edit / 底片校准
- Header：`{i18n:T '复制底片校准'}`
- Click → `OnCopyCalClick`
- 祖先 L100 MenuItem ：`IsEnabled={Binding HasImage}`

### L106 · MenuItem · 粘贴底片校准到本帧

- 所在分组：NavigationBar / WindowMenu / _Edit / 底片校准
- Header：`{i18n:T '粘贴底片校准到本帧'}`
- IsEnabled：`{Binding HasCalClipboard}`
- Click → `OnPasteCalToCurrentClick`
- 祖先 L100 MenuItem ：`IsEnabled={Binding HasImage}`

### L112 · MenuItem · 应用底片校准到勾选帧

- 所在分组：NavigationBar / WindowMenu / _Edit / 底片校准
- Header：`{i18n:T '应用底片校准到勾选帧'}`
- IsEnabled：`{Binding HasCalClipboard}`
- Click → `OnPasteCalClick`
- 祖先 L100 MenuItem ：`IsEnabled={Binding HasImage}`

### L114 · MenuItem · 应用底片校准到整卷

- 所在分组：NavigationBar / WindowMenu / _Edit / 底片校准
- Header：`{i18n:T '应用底片校准到整卷'}`
- Click → `OnApplyCalToRollClick`
- 祖先 L100 MenuItem ：`IsEnabled={Binding HasImage}`

### L116 · MenuItem · 成片调整

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '成片调整'}`
- IsEnabled：`{Binding HasImage}`

### L117 · MenuItem · 复制成片调整

- 所在分组：NavigationBar / WindowMenu / _Edit / 成片调整
- Header：`{i18n:T '复制成片调整'}`
- Click → `OnCopySceneClick`
- 祖先 L116 MenuItem ：`IsEnabled={Binding HasImage}`

### L122 · MenuItem · 粘贴成片调整到本帧

- 所在分组：NavigationBar / WindowMenu / _Edit / 成片调整
- Header：`{i18n:T '粘贴成片调整到本帧'}`
- IsEnabled：`{Binding HasSceneClipboard}`
- Click → `OnPasteSceneToCurrentClick`
- 祖先 L116 MenuItem ：`IsEnabled={Binding HasImage}`

### L128 · MenuItem · 应用成片调整到勾选帧

- 所在分组：NavigationBar / WindowMenu / _Edit / 成片调整
- Header：`{i18n:T '应用成片调整到勾选帧'}`
- IsEnabled：`{Binding HasSceneClipboard}`
- Click → `OnPasteSceneClick`
- 祖先 L116 MenuItem ：`IsEnabled={Binding HasImage}`

### L130 · MenuItem · 应用成片调整到整卷

- 所在分组：NavigationBar / WindowMenu / _Edit / 成片调整
- Header：`{i18n:T '应用成片调整到整卷'}`
- Click → `OnApplySceneToRollClick`
- 祖先 L116 MenuItem ：`IsEnabled={Binding HasImage}`

### L133 · MenuItem · 选择色彩同步项…

- 所在分组：NavigationBar / WindowMenu / _Edit
- Header：`{i18n:T '选择色彩同步项…'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnSyncOptionsClick`

### L136 · MenuItem · _View

- 所在分组：NavigationBar / WindowMenu
- Header：`_View`

### L137 · MenuItem · 图库（卷墙）

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '图库（卷墙）'}`
- InputGesture：`G`
- Click → `OnLibraryModeClick`

### L142 · MenuItem · 修片

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '修片'}`
- InputGesture：`D`
- IsEnabled：`{Binding HasImage}`
- Click → `OnDevelopModeClick`

### L148 · MenuItem · 查看负片

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '查看负片'}`
- InputGesture：`N`
- IsEnabled：`{Binding HasImage}`
- Click → `OnMenuViewNegClick`

### L153 · MenuItem · 前后对比

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '前后对比'}`
- InputGesture：`K`
- IsEnabled：`{Binding HasImage}`
- Click → `OnMenuCompareClick`

### L159 · MenuItem · 适合窗口

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '适合窗口'}`
- InputGesture：`F`
- IsEnabled：`{Binding HasImage}`
- Click → `OnFitClick`

### L164 · MenuItem · 实际像素 100%（Ctrl+1）

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '实际像素 100%（Ctrl+1）'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnActualSizeClick`

### L171 · MenuItem · 显示齿孔遮罩

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '显示齿孔遮罩'}`
- IsChecked：`{Binding ShowSprocketMask, Mode=TwoWay}`
- IsEnabled：`{Binding HasImage}`

### L175 · MenuItem · 预览背景色

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '预览背景色'}`
- Name：`BgMenu`

### L176 · MenuItem · 白

- 所在分组：NavigationBar / WindowMenu / _View / 预览背景色
- Header：`{i18n:T '白'}`
- Tag：`#FFFFFF`
- GroupName：`viewerbg`
- Click → `OnViewerBgClick`

### L177 · MenuItem · 浅灰

- 所在分组：NavigationBar / WindowMenu / _View / 预览背景色
- Header：`{i18n:T '浅灰'}`
- Tag：`#B4B4B4`
- GroupName：`viewerbg`
- Click → `OnViewerBgClick`

### L178 · MenuItem · 中灰（默认）

- 所在分组：NavigationBar / WindowMenu / _View / 预览背景色
- Header：`{i18n:T '中灰（默认）'}`
- Tag：`#5E5E5E`
- GroupName：`viewerbg`
- Click → `OnViewerBgClick`

### L179 · MenuItem · 18% 中性灰

- 所在分组：NavigationBar / WindowMenu / _View / 预览背景色
- Header：`{i18n:T '18% 中性灰'}`
- Tag：`#777777`
- GroupName：`viewerbg`
- Click → `OnViewerBgClick`

### L180 · MenuItem · 深灰

- 所在分组：NavigationBar / WindowMenu / _View / 预览背景色
- Header：`{i18n:T '深灰'}`
- Tag：`#323232`
- GroupName：`viewerbg`
- Click → `OnViewerBgClick`

### L181 · MenuItem · 黑

- 所在分组：NavigationBar / WindowMenu / _View / 预览背景色
- Header：`{i18n:T '黑'}`
- Tag：`#0E0F11`
- GroupName：`viewerbg`
- Click → `OnViewerBgClick`

### L183 · MenuItem · 切换浅色/深色主题

- 所在分组：NavigationBar / WindowMenu / _View
- Header：`{i18n:T '切换浅色/深色主题'}`
- InputGesture：`{i18n:Accel 'Shift+T'}`
- Click → `OnToggleThemeClick`

### L186 · MenuItem · _Settings

- 所在分组：NavigationBar / WindowMenu
- Header：`_Settings`

### L187 · MenuItem · 偏好设置…（Ctrl+,）

- 所在分组：NavigationBar / WindowMenu / _Settings
- Header：`{i18n:T '偏好设置…（Ctrl+,）'}`
- Click → `OnPreferencesClick`

### L190 · MenuItem · _Help

- 所在分组：NavigationBar / WindowMenu
- Header：`_Help`

### L191 · MenuItem · 操作指引 / 技术原理…

- 所在分组：NavigationBar / WindowMenu / _Help
- Header：`{i18n:T '操作指引 / 技术原理…'}`
- Click → `OnDocsClick`

### L192 · MenuItem · 快捷键…

- 所在分组：NavigationBar / WindowMenu / _Help
- Header：`{i18n:T '快捷键…'}`
- Click → `OnHelpClick`

### L196 · MenuItem · 本帧技术报告…

- 所在分组：NavigationBar / WindowMenu / _Help
- Header：`{i18n:T '本帧技术报告…'}`
- Click → `OnFrameReportClick`

### L199 · MenuItem · 打开 LUT 文件夹

- 所在分组：NavigationBar / WindowMenu / _Help
- Header：`{i18n:T '打开 LUT 文件夹'}`
- Click → `OnOpenLutFolderClick`

### L201 · MenuItem · 检查更新…

- 所在分组：NavigationBar / WindowMenu / _Help
- Header：`{i18n:T '检查更新…'}`
- Click → `OnCheckUpdateClick`

### L205 · MenuItem · 复制色彩诊断

- 所在分组：NavigationBar / WindowMenu / _Help
- Name：`CopyColorDiagnosticsMenuItem`
- Header：`{i18n:T '复制色彩诊断'}`
- Click → `OnCopyColorDiagnosticsClick`

### L208 · MenuItem · 关于 OpenRevelare

- 所在分组：NavigationBar / WindowMenu / _Help
- Header：`{i18n:T '关于 OpenRevelare'}`
- Click → `OnAboutClick`

### L215 · Button · 图库

- 所在分组：NavigationBar / WorkspaceSwitcher
- ToolTip.Tip：`{i18n:T '工作区：图库 / 修片（G / D）'}`
- Click → `OnLibraryModeClick`

### L222 · Button · 修片

- 所在分组：NavigationBar / WorkspaceSwitcher
- IsEnabled：`{Binding HasImage}`
- Click → `OnDevelopModeClick`

### L235 · Button · 撤销

- 所在分组：NavigationBar
- Name：`UndoButton`
- IsEnabled：`{Binding CanUndo}`
- AutomationProperties.Name：`{i18n:T '撤销'}`
- ToolTip.Tip：`{i18n:T '编辑：撤销（Ctrl+Z）'}`
- Click → `OnUndoClick`

### L240 · Button · 重做

- 所在分组：NavigationBar
- Name：`RedoButton`
- IsEnabled：`{Binding CanRedo}`
- AutomationProperties.Name：`{i18n:T '重做'}`
- ToolTip.Tip：`{i18n:T '编辑：重做（Ctrl+Y）'}`
- Click → `OnRedoClick`

### L245 · Button · 重置本帧调整

- 所在分组：NavigationBar
- Name：`ResetButton`
- IsEnabled：`{Binding HasImage}`
- AutomationProperties.Name：`{i18n:T '重置本帧调整'}`
- ToolTip.Tip：`{i18n:T '编辑：重置本帧调整'}`
- Click → `OnResetClick`

### L252 · Button · 打开工程…

- 所在分组：NavigationBar
- ToolTip.Tip：`{i18n:T '打开工程…'}`
- AutomationProperties.Name：`{i18n:T '打开工程…'}`
- Click → `OnOpenProjectClick`

### L256 · Button · 新建卷…

- 所在分组：NavigationBar
- Click → `OnOpenClick`

### L280 · Button · 取消

- Content：`{i18n:T '取消'}`
- IsVisible：`{Binding HasLongTask}`
- IsEnabled：`{Binding CanCancelLongTask}`
- Click → `OnCancelLongTaskClick`

### L283 · Button · {Binding SaveStatusText}

- Content：`{Binding SaveStatusText}`
- IsVisible：`{Binding HasSaveStatus}`
- ToolTip.Tip：`{Binding SaveStatusDetail}`
- Click → `OnRetrySaveClick`

### L323 · Button · 添加图像

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail
- Click → `OnAddImagesClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L332 · ListBox · 解码中… / {Binding Label}

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail
- Name：`FilmStrip`
- ItemsSource：`{Binding Frames}`
- SelectedItem：`{Binding CurrentFrame}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L337 · MenuItem · 添加图像到当前卷…

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '添加图像到当前卷…'}`
- Click → `OnAddImagesClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L343 · MenuItem · 全选帧

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '全选帧'}`
- Click → `OnSelectAllFramesClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L344 · MenuItem · 清除选择

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '清除选择'}`
- Click → `OnClearFrameSelectionClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L347 · MenuItem · 创建虚拟副本

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '创建虚拟副本'}`
- ToolTip.Tip：`{i18n:T '复制全部参数（含裁切）；勾选多帧时各建一个'}`
- Click → `OnVirtualCopyClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L353 · MenuItem · 从卷中移除

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '从卷中移除'}`
- ToolTip.Tip：`{i18n:T '移除勾选的全部帧；真实帧连同其副本'}`
- Click → `OnRemoveFrameClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L360 · MenuItem · 同步底片 · Cineon

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '同步底片 · Cineon'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L361 · MenuItem · 复制底片校准

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步底片 · Cineon
- Header：`{i18n:T '复制底片校准'}`
- Click → `OnCopyCalClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L364 · MenuItem · 粘贴底片校准到本帧

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步底片 · Cineon
- Header：`{i18n:T '粘贴底片校准到本帧'}`
- IsEnabled：`{Binding HasCalClipboard}`
- Click → `OnPasteCalToCurrentClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L368 · MenuItem · 应用底片校准到勾选帧

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步底片 · Cineon
- Header：`{i18n:T '应用底片校准到勾选帧'}`
- IsEnabled：`{Binding HasCalClipboard}`
- Click → `OnPasteCalClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L370 · MenuItem · 应用底片校准到整卷

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步底片 · Cineon
- Header：`{i18n:T '应用底片校准到整卷'}`
- Click → `OnApplyCalToRollClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L372 · MenuItem · 同步观感 · Display

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '同步观感 · Display'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L373 · MenuItem · 复制成片调整

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步观感 · Display
- Header：`{i18n:T '复制成片调整'}`
- Click → `OnCopySceneClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L374 · MenuItem · 粘贴成片调整到本帧

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步观感 · Display
- Header：`{i18n:T '粘贴成片调整到本帧'}`
- IsEnabled：`{Binding HasSceneClipboard}`
- Click → `OnPasteSceneToCurrentClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L376 · MenuItem · 应用成片调整到勾选帧

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步观感 · Display
- Header：`{i18n:T '应用成片调整到勾选帧'}`
- IsEnabled：`{Binding HasSceneClipboard}`
- Click → `OnPasteSceneClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L378 · MenuItem · 应用成片调整到整卷

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步观感 · Display
- Header：`{i18n:T '应用成片调整到整卷'}`
- Click → `OnApplySceneToRollClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L380 · MenuItem · 同步构图

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '同步构图'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L381 · MenuItem · 同步旋转 / 翻转 / 拉直

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步构图
- Header：`{i18n:T '同步旋转 / 翻转 / 拉直'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnApplyGeometryClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L383 · MenuItem · 同步裁切

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip / 同步构图
- Header：`{i18n:T '同步裁切'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnApplyCropClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L387 · MenuItem · 按文件名排序

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '按文件名排序'}`
- ToolTip.Tip：`{i18n:T '把整卷恢复成文件名顺序（撤销手动排序）'}`
- Click → `OnSortFramesClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L394 · MenuItem · 选择色彩同步项…

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- Header：`{i18n:T '选择色彩同步项…'}`
- Click → `OnSyncOptionsClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L417 · CheckBox · 批量操作目标；Ctrl 点选，Shift 连选

- 所在分组：WorkspaceScroll / WorkspaceGrid / FilmRail / FilmStrip
- IsChecked：`{Binding IsSelected}`
- ToolTip.Tip：`{i18n:T '批量操作目标；Ctrl 点选，Shift 连选'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L318 Border FilmRail：`IsVisible={Binding HasImage}`

### L441 · GridSplitter · FilmRailSplitter

- 所在分组：WorkspaceScroll / WorkspaceGrid
- Name：`FilmRailSplitter`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L452 · Button · 恢复工程参考…

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- Content：`{i18n:T '恢复工程参考…'}`
- IsEnabled：`{Binding CanRestoreProjectReferences}`
- Click → `OnRestoreProjectReferencesClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L450 StackPanel ：`IsVisible={Binding HasProjectDependencyWarning}`

### L456 · ToggleButton · 负片

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- Name：`ViewNegBtn`
- AutomationProperties.Name：`{i18n:T '负片'}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '切到负片视图，用于对准片基与齿孔（N）'}`
- Click → `OnViewNegToggle`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L464 · ToggleButton · 对比

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- Name：`CompareBtn`
- AutomationProperties.Name：`{i18n:T '对比'}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '显示未经帧编辑的画面，用于前后对比（K）'}`
- Click → `OnCompareToggle`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L474 · Button · 逆时针 90°

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- IsEnabled：`{Binding HasImage}`
- AutomationProperties.Name：`{i18n:T '逆时针 90°'}`
- ToolTip.Tip：`{i18n:T '逆时针 90°'}`
- Click → `OnRotateCcwClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L479 · Button · 顺时针 90°

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- IsEnabled：`{Binding HasImage}`
- AutomationProperties.Name：`{i18n:T '顺时针 90°'}`
- ToolTip.Tip：`{i18n:T '顺时针 90°'}`
- Click → `OnRotateCwClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L484 · Button · 水平翻转

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- IsEnabled：`{Binding HasImage}`
- AutomationProperties.Name：`{i18n:T '水平翻转'}`
- ToolTip.Tip：`{i18n:T '水平翻转'}`
- Click → `OnFlipHClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L489 · Button · 竖直翻转

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- IsEnabled：`{Binding HasImage}`
- AutomationProperties.Name：`{i18n:T '竖直翻转'}`
- ToolTip.Tip：`{i18n:T '竖直翻转'}`
- Click → `OnFlipVClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L494 · Button · {Binding GeometrySyncLabel}

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- IsEnabled：`{Binding HasImage}`
- AutomationProperties.Name：`{Binding GeometrySyncLabel}`
- ToolTip.Tip：`{Binding GeometrySyncLabel}`
- Click → `OnApplyGeometryClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L519 · Button · 迁移到色彩管理版…

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- Content：`{i18n:T '迁移到色彩管理版…'}`
- Click → `OnMigrateColorPipelineClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L510 Border ：`IsVisible={Binding ShowLegacyColorPipelineNotice}`

### L522 · Button · 关闭提示

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- ToolTip.Tip：`{i18n:T '关闭提示'}`
- Click → `OnDismissLegacyColorPipelineNoticeClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L510 Border ：`IsVisible={Binding ShowLegacyColorPipelineNotice}`

### L539 · Button · 适合窗口（F）

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '适合窗口（F）'}`
- Click → `OnFitClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L543 · Button · 实际像素 100%（Ctrl+1）

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / PreviewToolbar
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '实际像素 100%（Ctrl+1）'}`
- Click → `OnActualSizeClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L571 · Button · 应用

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / NoticeStrip / Banner
- Name：`CropApplyBtn`
- Content：`{i18n:T '应用'}`
- IsVisible：`False`
- Click → `OnCropApplyClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L562 Border Banner：`IsVisible=False`

### L575 · Button · 取消

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / NoticeStrip / Banner
- Name：`CropCancelBtn`
- Content：`{i18n:T '取消'}`
- IsVisible：`False`
- Click → `OnCropCancelClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L562 Border Banner：`IsVisible=False`

### L580 · Button · 关闭提示

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / NoticeStrip
- Name：`BannerCloseBtn`
- IsVisible：`False`
- ToolTip.Tip：`{i18n:T '关闭提示'}`
- Click → `OnBannerCloseClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L602 · Button · 改为线性

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / NoticeStrip
- Content：`{i18n:T '改为线性'}`
- Click → `OnTiffInputLinearClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L593 Border ：`IsVisible={Binding ShowTiffInputNotice}`

### L604 · Button · 确认 sRGB

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / NoticeStrip
- Content：`{i18n:T '确认 sRGB'}`
- Click → `OnTiffInputSrgbClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L593 Border ：`IsVisible={Binding ShowTiffInputNotice}`

### L606 · Button · 关闭提示

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / NoticeStrip
- ToolTip.Tip：`{i18n:T '关闭提示'}`
- Click → `OnDismissTiffInputNoticeClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L593 Border ：`IsVisible={Binding ShowTiffInputNotice}`

### L628 · Button · 关闭提示

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / NoticeStrip
- ToolTip.Tip：`{i18n:T '关闭提示'}`
- Click → `OnDismissRollAnalysisNoticeClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L617 Border ：`IsVisible={Binding ShowRollAnalysisNotice}`

### L651 · MenuItem · 复制 / 粘贴参数

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '复制 / 粘贴参数'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L652 · MenuItem · 复制底片校准

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 复制 / 粘贴参数
- Header：`{i18n:T '复制底片校准'}`
- Click → `OnCopyCalClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L655 · MenuItem · 粘贴底片校准到本帧

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 复制 / 粘贴参数
- Header：`{i18n:T '粘贴底片校准到本帧'}`
- IsEnabled：`{Binding HasCalClipboard}`
- Click → `OnPasteCalToCurrentClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L659 · MenuItem · 复制成片调整

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 复制 / 粘贴参数
- Header：`{i18n:T '复制成片调整'}`
- Click → `OnCopySceneClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L660 · MenuItem · 粘贴成片调整到本帧

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 复制 / 粘贴参数
- Header：`{i18n:T '粘贴成片调整到本帧'}`
- IsEnabled：`{Binding HasSceneClipboard}`
- Click → `OnPasteSceneToCurrentClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L663 · MenuItem · Ctrl+C / Ctrl+V 复制 / 粘贴当前打开标签的那一组

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T 'Ctrl+C / Ctrl+V 复制 / 粘贴当前打开标签的那一组'}`
- IsEnabled：`False`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L666 · MenuItem · 适合窗口

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '适合窗口'}`
- InputGesture：`F`
- Click → `OnFitClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L671 · MenuItem · 实际像素 100%

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '实际像素 100%'}`
- InputGesture：`Ctrl+1`
- Click → `OnActualSizeClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L677 · MenuItem · 查看负片

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '查看负片'}`
- InputGesture：`N`
- Click → `OnMenuViewNegClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L682 · MenuItem · 前后对比

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '前后对比'}`
- InputGesture：`K`
- Click → `OnMenuCompareClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L687 · MenuItem · 过曝 / 欠曝指示

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '过曝 / 欠曝指示'}`
- InputGesture：`J`
- IsChecked：`{Binding ShowClipping, Mode=TwoWay}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L690 · MenuItem · 构图

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '构图'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L691 · MenuItem · 裁切（拖框）

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图
- Header：`{i18n:T '裁切（拖框）'}`
- Click → `OnCropClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L694 · MenuItem · 同步裁切

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图
- Header：`{i18n:T '同步裁切'}`
- Click → `OnApplyCropClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L695 · MenuItem · 旋转 / 翻转

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图
- Header：`{i18n:T '旋转 / 翻转'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L696 · MenuItem · 逆时针 90°

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图 / 旋转 / 翻转
- Header：`{i18n:T '逆时针 90°'}`
- Click → `OnRotateCcwClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L701 · MenuItem · 顺时针 90°

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图 / 旋转 / 翻转
- Header：`{i18n:T '顺时针 90°'}`
- Click → `OnRotateCwClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L706 · MenuItem · 水平翻转

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图 / 旋转 / 翻转
- Header：`{i18n:T '水平翻转'}`
- Click → `OnFlipHClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L711 · MenuItem · 竖直翻转

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图 / 旋转 / 翻转
- Header：`{i18n:T '竖直翻转'}`
- Click → `OnFlipVClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L717 · MenuItem · 同步旋转 / 翻转 / 拉直

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / 构图
- Header：`{i18n:T '同步旋转 / 翻转 / 拉直'}`
- Click → `OnApplyGeometryClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L720 · MenuItem · 撤销

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '撤销'}`
- InputGesture：`Ctrl+Z`
- Click → `OnUndoClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L725 · MenuItem · 重做

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '重做'}`
- InputGesture：`Ctrl+Y`
- Click → `OnRedoClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L730 · MenuItem · 全部快捷键…

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Header：`{i18n:T '全部快捷键…'}`
- Click → `OnHelpClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L733 · Grid · ViewPort

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace
- Name：`ViewPort`
- PointerWheelChanged → `OnViewportWheel`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L778 · Canvas · Overlay

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / ViewPort / ZoomGrid
- Name：`Overlay`
- PointerPressed → `OnOverlayPressed`
- PointerMoved → `OnOverlayMoved`
- PointerReleased → `OnOverlayReleased`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L853 · ComboBox · 整卷渲染空间

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar
- Name：`OutputSpacePicker`
- AutomationProperties.Name：`{i18n:T '整卷渲染空间'}`
- SelectedIndex：`{Binding OutputSpaceIndex}`
- IsVisible：`{Binding CanChooseOutputSpace}`
- ToolTip.Tip：`{i18n:T 'Cineon 第 4 步的目标空间：反相结果转换到它，帧编辑在它内进行，导出也写成它。工作空间为 ACEScg，每个选项都是真实的色域变换。切换空间保留滑块数值，画面随之改变。属于胶卷参数，随工程保存。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L856 · ComboBoxItem · sRGB

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / OutputSpacePicker
- Content：`sRGB`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L853 ComboBox OutputSpacePicker：`IsVisible={Binding CanChooseOutputSpace}`

### L857 · ComboBoxItem · Display P3

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / OutputSpacePicker
- Content：`Display P3`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L853 ComboBox OutputSpacePicker：`IsVisible={Binding CanChooseOutputSpace}`

### L858 · ComboBoxItem · Adobe RGB

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / OutputSpacePicker
- Content：`Adobe RGB`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L853 ComboBox OutputSpacePicker：`IsVisible={Binding CanChooseOutputSpace}`

### L863 · ComboBox · 外观 LUT

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar
- Name：`LookLutPicker`
- AutomationProperties.Name：`{i18n:T '外观 LUT'}`
- ItemsSource：`{Binding PrintLutNames}`
- SelectedIndex：`{Binding PrintLutIndex}`
- ToolTip.Tip：`{i18n:T '在数字中间片反相之后应用外观 LUT，不改变输入设备标定、片基或密度端点。输入固定为 Cineon Film Log，输出须与制作 LUT 时的编码一致。

可使用随程序提供的 Film Looks 或自行制作的 .cube；「标准」表示不加外观 LUT。通过帮助菜单打开 LUT 文件夹可批量加入。随工程保存。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L868 · MenuItem · {Binding PrintLutContractText}

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / LookLutPicker
- Header：`{Binding PrintLutContractText}`
- IsVisible：`{Binding CanEditPrintLutContract}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L869 · MenuItem · Rec.709 Gamma 2.4

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / LookLutPicker / {Binding PrintLutContractText}
- Header：`Rec.709 Gamma 2.4`
- Tag：`0`
- Click → `OnPrintLutOutputClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L868 MenuItem ：`IsVisible={Binding CanEditPrintLutContract}`

### L870 · MenuItem · DCI-P3 Gamma 2.6

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / LookLutPicker / {Binding PrintLutContractText}
- Header：`DCI-P3 Gamma 2.6`
- Tag：`1`
- Click → `OnPrintLutOutputClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L868 MenuItem ：`IsVisible={Binding CanEditPrintLutContract}`

### L871 · MenuItem · sRGB

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / LookLutPicker / {Binding PrintLutContractText}
- Header：`sRGB`
- Tag：`2`
- Click → `OnPrintLutOutputClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L868 MenuItem ：`IsVisible={Binding CanEditPrintLutContract}`

### L872 · MenuItem · Rec.2100 ST2084（HDR · PQ）

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / LookLutPicker / {Binding PrintLutContractText}
- Header：`{i18n:T 'Rec.2100 ST2084（HDR · PQ）'}`
- Tag：`3`
- IsVisible：`{Binding HdrEnabled}`
- Click → `OnPrintLutOutputClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L868 MenuItem ：`IsVisible={Binding CanEditPrintLutContract}`

### L877 · ToggleSwitch · {Binding HdrToggleTooltip}

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar
- IsChecked：`{Binding HdrEnabled}`
- IsEnabled：`{Binding CanChooseHdrPeak}`
- ToolTip.Tip：`{Binding HdrToggleTooltip}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L881 · Button · 导出…

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar
- Name：`ExportButton`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '导出当前帧（Ctrl+E）'}`
- Click → `OnExportClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L889 · Button · 整卷导出 / 印样

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar
- Name：`ExportMoreButton`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '整卷导出 / 印样'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L896 · MenuItem · 导出整卷…

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / ExportMoreButton
- Name：`ExportRollButton`
- Header：`{i18n:T '导出整卷…'}`
- Click → `OnExportRollClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L889 Button ExportMoreButton：`IsEnabled={Binding HasImage}`

### L897 · MenuItem · 查看上次批量导出结果…

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / ExportMoreButton
- Header：`{i18n:T '查看上次批量导出结果…'}`
- IsEnabled：`{Binding CanShowBatchExportReport}`
- Click → `OnBatchExportReportClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L889 Button ExportMoreButton：`IsEnabled={Binding HasImage}`

### L899 · MenuItem · 导出印样…

- 所在分组：WorkspaceScroll / WorkspaceGrid / PreviewWorkspace / OutputToolbar / ExportMoreButton
- Name：`ContactSheetButton`
- Header：`{i18n:T '导出印样…'}`
- ToolTip.Tip：`{i18n:T '把整卷所有帧拼成一张网格印样图导出'}`
- Click → `OnContactSheetClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L889 Button ExportMoreButton：`IsEnabled={Binding HasImage}`

### L911 · GridSplitter · InspectorSplitter

- 所在分组：WorkspaceScroll / WorkspaceGrid
- Name：`InspectorSplitter`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L939 · HistogramView · {Binding HistogramTooltip}

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- IsVisible：`{Binding ShowHistogram}`
- ToolTip.Tip：`{Binding HistogramTooltip}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L942 · WaveformView · 波形：横轴是画面的横向位置，纵轴是显示亮度，三通道叠加。中性是灰的，偏色会看到通道上下分开；整幅渐变（灯板不匀、翻拍架偏斜）在直方图上看不出来，在这里一眼可见。

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- IsVisible：`{Binding ShowWaveform}`
- ToolTip.Tip：`{i18n:T '波形：横轴是画面的横向位置，纵轴是显示亮度，三通道叠加。中性是灰的，偏色会看到通道上下分开；整幅渐变（灯板不匀、翻拍架偏斜）在直方图上看不出来，在这里一眼可见。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L946 · ToggleButton · 过曝 / 欠曝指示

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Name：`ClippingBtn`
- IsChecked：`{Binding ShowClipping, Mode=TwoWay}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '在预览上叠加红色（过曝）和蓝色（欠曝）指示（J）。两端的判据在下面调，按显示亮度计。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L953 · ToggleButton · 直方图

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Name：`HistogramBtn`
- IsChecked：`{Binding ShowHistogram, Mode=TwoWay}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '直方图：各亮度上有多少像素。判断有没有溢出。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L959 · ToggleButton · 波形

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Name：`WaveformBtn`
- IsChecked：`{Binding ShowWaveform, Mode=TwoWay}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '波形：同样的亮度分布，但保留横向位置。判断偏色是整幅均匀还是渐变。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L973 · SliderRow · HDR 上限

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Label：`{i18n:T 'HDR 上限'}`
- Minimum：`0.5`
- Maximum：`4`
- Decimals：`1`
- Increment：`0.1`
- DefaultValue：`2.3`
- Value：`{Binding HdrLimitStops}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L972 StackPanel ：`IsVisible={Binding HdrEnabled}`

### L983 · SliderRow · 欠曝 ≤

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Label：`{i18n:T '欠曝 ≤'}`
- Minimum：`0`
- Maximum：`20`
- Decimals：`0`
- Increment：`1`
- DefaultValue：`2`
- Value：`{Binding ClipShadowPercent}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L981 StackPanel ：`IsVisible={Binding ShowClipping}`

### L985 · SliderRow · 过曝 ≥

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Label：`{i18n:T '过曝 ≥'}`
- Minimum：`80`
- Maximum：`100`
- Decimals：`0`
- Increment：`1`
- DefaultValue：`98`
- Value：`{Binding ClipHighlightPercent}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L981 StackPanel ：`IsVisible={Binding ShowClipping}`

### L993 · TabStrip · InspectorTabs

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Name：`InspectorTabs`
- SelectedIndex：`{Binding #PanelTabs.SelectedIndex, Mode=TwoWay}`
- KeyDown → `OnInspectorTabKeyDown`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L996 · TabStripItem · 底片校准

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / InspectorTabs
- Content：`{i18n:T '底片校准'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L997 · TabStripItem · 成片调整

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / InspectorTabs
- Content：`{i18n:T '成片调整'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1002 · Button · {Binding CalibrationApplyLabel}

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Name：`CalibrationSyncFooter`
- Content：`{Binding CalibrationApplyLabel}`
- IsEnabled：`{Binding HasOtherFrames}`
- ToolTip.Tip：`{Binding CalibrationBroadcastHint}`
- Click → `OnApplyCalToRollClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1004 · Button · {Binding SceneApplyLabel}

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace
- Name：`SceneSyncFooter`
- Content：`{Binding SceneApplyLabel}`
- IsVisible：`False`
- IsEnabled：`{Binding CanApplySceneToRoll}`
- ToolTip.Tip：`{Binding SceneBroadcastHint}`
- Click → `OnApplySceneToRollClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1035 · TabItem · 底片校准 / {Binding RecalibrationNoticeText} / 黑白负片（整卷） / 自动（整卷） / 自动（单张） / 片基采样 / 自动黑点 / {Binding FilmBaseText} / 高光采样 / 自动白点 / {Binding HighlightConfidenceText} / 灰卡采样 / 智能色偏修正（Beta） / {Binding SmartCastFeedbackText} / {Binding GreyCardText} / {Binding InputCharacterizationText} / {Binding DensityCorrectionText} / {Binding CaptureCorrectionText} / {Binding CaptureCorrectionSourceText} / {Binding DensityCorrectionSourceText} / {Binding CaptureCorrectionScopeText} / 镜头与平场校正 / 翻拍镜头的桶形/枕形畸变与四角压暗，反相后会让边框弯曲、四角泛白，此处补偿（不依赖镜头库）。 / LCC 平场校正：用平场参考图逐像素修正翻拍镜头的暗角/角落偏色（比公式暗角更准）。 / {Binding LccStatus}

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1060 · CheckBox · 黑白负片（整卷）

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- IsChecked：`{Binding Monochrome, Mode=TwoWay}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{Binding MonochromeHint}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1072 · Button · 自动（整卷）

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '遍历整卷解出片基、白平衡、D-max 与黑白场，一套参数应用到所有帧。覆盖已有的白平衡与色阶，不改动裁切。'}`
- Click → `OnAutoInvertRollClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1082 · Button · 自动（单张）

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '只对当前帧做同样的自动流程，不动其他帧、也不改动裁切——用于整卷参数对这一张不合适时。'}`
- Click → `OnAutoInvertFrameClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1102 · Expander · 黑端

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{i18n:T '黑端'}`
- IsExpanded：`True`
- Tag：`1`
- ToolTip.Tip：`{i18n:T '每个通道读作黑的密度（相对完全透光 T=1）。C-41 橙色片基必然 R<G<B，典型 0.09 / 0.29 / 0.54。展开三个分量可调暗部色偏。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1105 · SliderRow · D_min

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 黑端
- Label：`D_min`
- Minimum：`-0.5`
- Maximum：`2`
- Decimals：`3`
- Increment：`0.005`
- DefaultValue：`0`
- Value：`{Binding DMinLevel}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1102 Expander ：`IsExpanded=True`

### L1108 · ToggleButton · 片基采样

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 黑端
- Name：`FilmBaseBtn`
- IsEnabled：`{Binding HasImage}`
- Click → `OnSampleFilmBaseClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1102 Expander ：`IsExpanded=True`

### L1115 · Button · 自动黑点

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 黑端
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '自动找出画面中裸露的片基并写入黑端。依次尝试：灯板下的片基峰、边缘裸片基窄带、亮端分位。'}`
- Click → `OnAutoFilmBaseClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1102 Expander ：`IsExpanded=True`

### L1133 · Expander · 逐通道

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 黑端
- Header：`{i18n:T '逐通道'}`
- IsVisible：`{Binding IsColourRoll}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1102 Expander ：`IsExpanded=True`

### L1136 · SliderRow · R

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 黑端 / 逐通道
- Label：`R`
- Minimum：`-0.5`
- Maximum：`2`
- Decimals：`3`
- Increment：`0.001`
- DefaultValue：`0`
- Value：`{Binding DMinR}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1102 Expander ：`IsExpanded=True`
- 祖先 L1133 Expander ：`IsVisible={Binding IsColourRoll}`

### L1137 · SliderRow · G

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 黑端 / 逐通道
- Label：`G`
- Minimum：`-0.5`
- Maximum：`2`
- Decimals：`3`
- Increment：`0.001`
- DefaultValue：`0`
- Value：`{Binding DMinG}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1102 Expander ：`IsExpanded=True`
- 祖先 L1133 Expander ：`IsVisible={Binding IsColourRoll}`

### L1138 · SliderRow · B

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 黑端 / 逐通道
- Label：`B`
- Minimum：`-0.5`
- Maximum：`2`
- Decimals：`3`
- Increment：`0.001`
- DefaultValue：`0`
- Value：`{Binding DMinB}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1102 Expander ：`IsExpanded=True`
- 祖先 L1133 Expander ：`IsVisible={Binding IsColourRoll}`

### L1145 · Expander · 白端

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{i18n:T '白端'}`
- IsExpanded：`True`
- Tag：`2`
- ToolTip.Tip：`{i18n:T '每个通道读作白的密度，典型 1.8–2.4。与 D_min 的距离即反差，三通道之差即高光色偏。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1148 · SliderRow · D_max

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白端
- Label：`D_max`
- Minimum：`0.3`
- Maximum：`4`
- Decimals：`3`
- Increment：`0.005`
- DefaultValue：`2`
- Value：`{Binding DMaxLevel}`
- ToolTip.Tip：`{i18n:T 'Cineon 亮端映射：改变高光反差和亮端位置，保持三通道色彩比例。需要整体提亮请使用 Display 曝光。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1145 Expander ：`IsExpanded=True`

### L1152 · ToggleButton · 高光采样

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白端
- Name：`DMaxBtn`
- IsEnabled：`{Binding HasImage}`
- Click → `OnSampleDMaxClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1145 Expander ：`IsExpanded=True`

### L1159 · Button · 自动白点

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白端
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '自动找到画面最亮点，把它当作纯白来解出亮端三个密度'}`
- Click → `OnAutoWbHighClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1145 Expander ：`IsExpanded=True`

### L1171 · Expander · 逐通道

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白端
- Header：`{i18n:T '逐通道'}`
- IsVisible：`{Binding IsColourRoll}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1145 Expander ：`IsExpanded=True`

### L1174 · SliderRow · R

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白端 / 逐通道
- Label：`R`
- Minimum：`0.3`
- Maximum：`4`
- Decimals：`3`
- Increment：`0.001`
- DefaultValue：`2`
- Value：`{Binding DMaxR}`
- ToolTip.Tip：`{i18n:T '高级：单独改变红色亮端，会改变高光色偏。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1145 Expander ：`IsExpanded=True`
- 祖先 L1171 Expander ：`IsVisible={Binding IsColourRoll}`

### L1175 · SliderRow · G

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白端 / 逐通道
- Label：`G`
- Minimum：`0.3`
- Maximum：`4`
- Decimals：`3`
- Increment：`0.001`
- DefaultValue：`2`
- Value：`{Binding DMaxG}`
- ToolTip.Tip：`{i18n:T '高级：单独改变绿色亮端，会改变高光色偏。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1145 Expander ：`IsExpanded=True`
- 祖先 L1171 Expander ：`IsVisible={Binding IsColourRoll}`

### L1176 · SliderRow · B

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白端 / 逐通道
- Label：`B`
- Minimum：`0.3`
- Maximum：`4`
- Decimals：`3`
- Increment：`0.001`
- DefaultValue：`2`
- Value：`{Binding DMaxB}`
- ToolTip.Tip：`{i18n:T '高级：单独改变蓝色亮端，会改变高光色偏。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1145 Expander ：`IsExpanded=True`
- 祖先 L1171 Expander ：`IsVisible={Binding IsColourRoll}`

### L1188 · Expander · {Binding CastCardHeader}

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{Binding CastCardHeader}`
- IsExpanded：`True`
- Tag：`3`
- ToolTip.Tip：`{Binding CastCardHint}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1192 · ToggleButton · 灰卡采样

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / {Binding CastCardHeader}
- Name：`GreyBtn`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '框选画面中拍到的灰卡，将其定为 Cineon 标准灰：三通道落在码值 470，色偏与曝光位置一同确定。与高光采样互为替代，后执行者生效。'}`
- Click → `OnSampleNeutralGreyClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1188 Expander ：`IsExpanded=True`

### L1201 · Button · 智能色偏修正（Beta）

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / {Binding CastCardHeader}
- IsVisible：`{Binding IsColourRoll}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '不需要灰卡。建议先裁掉齿孔和片边；结果可以撤销。'}`
- Click → `OnAutoWbAiClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1188 Expander ：`IsExpanded=True`

### L1222 · Expander · 输入与校正详情

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{i18n:T '输入与校正详情'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1230 · Button · 查看本帧校准依据…

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 输入与校正详情
- Content：`{i18n:T '查看本帧校准依据…'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnFrameReportClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1236 · SliderRow · 畸变

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 输入与校正详情
- Label：`{i18n:T '畸变'}`
- Minimum：`-0.5`
- Maximum：`0.5`
- Decimals：`3`
- Increment：`0.005`
- DefaultValue：`0`
- Value：`{Binding DistortionK1}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1237 · SliderRow · 暗角

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 输入与校正详情
- Label：`{i18n:T '暗角'}`
- Minimum：`-1`
- Maximum：`2`
- Decimals：`2`
- Increment：`0.01`
- DefaultValue：`0`
- Value：`{Binding VignetteAmount}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1238 · SliderRow · 范围

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 输入与校正详情
- Label：`{i18n:T '范围'}`
- Minimum：`1`
- Maximum：`6`
- Decimals：`2`
- Increment：`0.05`
- DefaultValue：`2.5`
- Value：`{Binding VignetteFalloff}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1242 · Button · 载入 LCC 平场…

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 输入与校正详情
- Content：`{i18n:T '载入 LCC 平场…'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnLoadLccClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1244 · CheckBox · 启用

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 输入与校正详情
- Content：`{i18n:T '启用'}`
- IsChecked：`{Binding LccEnabled}`
- IsEnabled：`{Binding LccAvailable}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1255 · TabItem · 成片调整 / 自动色阶（0.1% / 99.9%）

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1268 · Expander · 白平衡

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{i18n:T '白平衡'}`
- IsExpanded：`True`
- IsVisible：`{Binding IsColourRoll}`
- ToolTip.Tip：`{i18n:T '场景白平衡，正片域的线性增益，只管光源的冷暖。片子本身的色偏在 Cineon 标签中标定。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1273 · SliderRow · 色温

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白平衡
- Label：`{i18n:T '色温'}`
- Minimum：`-250`
- Maximum：`250`
- Decimals：`0`
- Increment：`1`
- DefaultValue：`0`
- Value：`{Binding Temp}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1268 Expander ：`IsExpanded=True; IsVisible={Binding IsColourRoll}`

### L1278 · ToggleButton · 框选一块本应中性的地方（灰墙、白纸、路面），解出色温 / 色调。只动这两条滑条，不碰 Cineon 两端。

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白平衡
- Name：`NeutralBtn`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '框选一块本应中性的地方（灰墙、白纸、路面），解出色温 / 色调。只动这两条滑条，不碰 Cineon 两端。'}`
- Click → `OnSampleDisplayNeutralClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1268 Expander ：`IsExpanded=True; IsVisible={Binding IsColourRoll}`

### L1284 · SliderRow · 色调

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 白平衡
- Label：`{i18n:T '色调'}`
- Minimum：`-250`
- Maximum：`250`
- Decimals：`0`
- Increment：`1`
- DefaultValue：`0`
- Value：`{Binding Tint}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1268 Expander ：`IsExpanded=True; IsVisible={Binding IsColourRoll}`

### L1290 · Expander · 基本

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{i18n:T '基本'}`
- IsExpanded：`True`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1292 · SliderRow · 曝光

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 基本
- Label：`{i18n:T '曝光'}`
- Minimum：`-3`
- Maximum：`3`
- Decimals：`2`
- Increment：`0.1`
- DefaultValue：`0`
- Value：`{Binding ExposureEv}`
- ToolTip.Tip：`{i18n:T '成片曝光：改变最终照片亮度，不重新解释底片曝光位置。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1290 Expander ：`IsExpanded=True`

### L1294 · SliderRow · 反差

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 基本
- Label：`{i18n:T '反差'}`
- Minimum：`-1`
- Maximum：`1`
- Decimals：`2`
- Increment：`0.01`
- DefaultValue：`0`
- Value：`{Binding Contrast}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1290 Expander ：`IsExpanded=True`

### L1295 · SliderRow · 饱和度

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 基本
- Label：`{i18n:T '饱和度'}`
- Minimum：`-1`
- Maximum：`1`
- Decimals：`2`
- Increment：`0.01`
- DefaultValue：`0`
- Value：`{Binding Saturation}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1290 Expander ：`IsExpanded=True`

### L1300 · Expander · 影调

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{i18n:T '影调'}`
- IsExpanded：`True`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1303 · SliderRow · 黑场

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 影调
- Label：`{i18n:T '黑场'}`
- Minimum：`-1`
- Maximum：`1`
- Decimals：`2`
- Increment：`0.01`
- DefaultValue：`0`
- Value：`{Binding Black}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1300 Expander ：`IsExpanded=True`

### L1304 · ToggleButton · 框选最暗有效区设为黑场端点

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 影调
- Name：`BlackBtn`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '框选最暗有效区设为黑场端点'}`
- Click → `OnSampleBlackClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1300 Expander ：`IsExpanded=True`

### L1310 · SliderRow · 阴影

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 影调
- Label：`{i18n:T '阴影'}`
- Minimum：`-1`
- Maximum：`1`
- Decimals：`2`
- Increment：`0.01`
- DefaultValue：`0`
- Value：`{Binding Shadows}`
- ToolTip.Tip：`{i18n:T '调整照片阴影层次；新版会保留通道余量，避免单通道先溢出。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1300 Expander ：`IsExpanded=True`

### L1312 · SliderRow · 高光

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 影调
- Label：`{i18n:T '高光'}`
- Minimum：`-1`
- Maximum：`1`
- Decimals：`2`
- Increment：`0.01`
- DefaultValue：`0`
- Value：`{Binding Highlights}`
- ToolTip.Tip：`{i18n:T '调整照片高光层次；不会改变 Cineon 的亮端标定。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1300 Expander ：`IsExpanded=True`

### L1315 · SliderRow · 白场

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 影调
- Label：`{i18n:T '白场'}`
- Minimum：`-1`
- Maximum：`1`
- Decimals：`2`
- Increment：`0.01`
- DefaultValue：`0`
- Value：`{Binding White}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1300 Expander ：`IsExpanded=True`

### L1316 · ToggleButton · 框选最亮有效区设为白场端点

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 影调
- Name：`WhiteBtn`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '框选最亮有效区设为白场端点'}`
- Click → `OnSampleWhiteClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1300 Expander ：`IsExpanded=True`

### L1322 · Button · 自动色阶（0.1% / 99.9%）

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 影调
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '按全图分位自动设定黑场/白场端点'}`
- Click → `OnAutoLevelsClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`
- 祖先 L1300 Expander ：`IsExpanded=True`

### L1333 · Expander · 色调曲线

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs
- Header：`{i18n:T '色调曲线'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1334 · CurveEditor · Curves

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / PanelTabs / 色调曲线
- Name：`Curves`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1375 · CheckBox · 启用齿孔遮罩

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll
- Content：`{i18n:T '启用齿孔遮罩'}`
- IsChecked：`{Binding SprocketEnabled}`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '默认关闭；包含齿孔或灯板时启用，展开「齿孔设置」预览和调整遮罩。'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1378 · Button · {Binding SprocketSyncLabel}

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll
- IsEnabled：`{Binding HasImage}`
- AutomationProperties.Name：`{Binding SprocketSyncLabel}`
- ToolTip.Tip：`{Binding SprocketSyncLabel}`
- Click → `OnApplySprocketClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1385 · Expander · 齿孔设置

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll
- Header：`{i18n:T '齿孔设置'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1389 · CheckBox · 显示遮罩

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 齿孔设置
- Content：`{i18n:T '显示遮罩'}`
- IsChecked：`{Binding ShowSprocketMask}`
- IsEnabled：`{Binding HasImage}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1390 · SliderRow · 阈值

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 齿孔设置
- Label：`{i18n:T '阈值'}`
- Minimum：`0.05`
- Maximum：`1.0`
- Decimals：`3`
- Increment：`0.005`
- DefaultValue：`0.9`
- Value：`{Binding SprocketThreshold}`
- IsEnabled：`{Binding HasImage}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1396 · Expander · 除尘 / 修复

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll
- Header：`{i18n:T '除尘 / 修复'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1400 · CheckBox · 启用除尘

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 除尘 / 修复
- Content：`{i18n:T '启用除尘'}`
- IsChecked：`{Binding DustEnabled}`
- IsEnabled：`{Binding HasImage}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1401 · CheckBox · 显示修复范围

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 除尘 / 修复
- Content：`{i18n:T '显示修复范围'}`
- IsChecked：`{Binding ShowDustMask}`
- IsEnabled：`{Binding HasImage}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1403 · SliderRow · 笔刷半径

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 除尘 / 修复
- Label：`{i18n:T '笔刷半径'}`
- Minimum：`0.001`
- Maximum：`0.03`
- Decimals：`4`
- Increment：`0.001`
- DefaultValue：`0.006`
- Value：`{Binding DustBrushSize}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1405 · ToggleButton · 修复笔

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 除尘 / 修复
- Name：`DustAddBtn`
- IsEnabled：`{Binding HasImage}`
- Content：`{i18n:T '修复笔'}`
- Click → `OnDustAddClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1408 · ToggleButton · 擦除笔

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 除尘 / 修复
- Name：`DustEraseBtn`
- IsEnabled：`{Binding HasImage}`
- Content：`{i18n:T '擦除笔'}`
- Click → `OnDustEraseClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1415 · Button · 清除全部

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 除尘 / 修复
- Content：`{i18n:T '清除全部'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnClearAllDustClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1420 · Expander · 裁切

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll
- Header：`{i18n:T '裁切'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1422 · Button · {Binding CropSyncLabel}

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切
- IsEnabled：`{Binding HasImage}`
- Content：`{Binding CropSyncLabel}`
- ToolTip.Tip：`{i18n:T '把本帧的裁切应用到勾选帧，未勾选则整卷；会按各帧底片重定位'}`
- Click → `OnApplyCropClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1427 · ComboBox · 选择后裁切框以此比例居中显示，拖框时保持该比例

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切
- Name：`CropPresetCombo`
- SelectedIndex：`0`
- ToolTip.Tip：`{i18n:T '选择后裁切框以此比例居中显示，拖框时保持该比例'}`
- SelectionChanged → `OnCropPresetChanged`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1430 · ComboBoxItem · 自由

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '自由'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1431 · ComboBoxItem · 135 全幅 — 内容 (3:2)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '135 全幅 — 内容 (3:2)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1432 · ComboBoxItem · 135 全幅 — 含边框 (37:35)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '135 全幅 — 含边框 (37:35)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1433 · ComboBoxItem · 半格 — 内容 (3:4)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '半格 — 内容 (3:4)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1434 · ComboBoxItem · XPan 全幅 (65:24)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T 'XPan 全幅 (65:24)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1435 · ComboBoxItem · 645 — 内容 (4:3)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '645 — 内容 (4:3)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1436 · ComboBoxItem · 6×6 — 内容 (1:1)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '6×6 — 内容 (1:1)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1437 · ComboBoxItem · 6×7 — 内容 (7:6)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '6×7 — 内容 (7:6)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1438 · ComboBoxItem · 6×9 — 内容 (3:2)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '6×9 — 内容 (3:2)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1439 · ComboBoxItem · 6×12 全景 (2:1)

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切 / CropPresetCombo
- Content：`{i18n:T '6×12 全景 (2:1)'}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1443 · ToggleButton · 裁切（拖框）

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切
- Name：`CropBtn`
- IsEnabled：`{Binding HasImage}`
- Click → `OnCropClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1450 · Button · 清除

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切
- Content：`{i18n:T '清除'}`
- IsEnabled：`{Binding HasImage}`
- Click → `OnClearCropClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1453 · SliderRow · 拉直

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切
- Label：`{i18n:T '拉直'}`
- Minimum：`-15`
- Maximum：`15`
- Decimals：`1`
- Increment：`0.5`
- DefaultValue：`0`
- Value：`{Binding Rotation}`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1455 · ToggleButton · 取水平

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切
- Name：`StraightenHBtn`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '在预览上拖一条应当水平的参考线，松开即转正'}`
- Click → `OnStraightenHClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

### L1463 · ToggleButton · 取垂直

- 所在分组：WorkspaceScroll / WorkspaceGrid / InspectorWorkspace / PanelScroll / 裁切
- Name：`StraightenVBtn`
- IsEnabled：`{Binding HasImage}`
- ToolTip.Tip：`{i18n:T '在预览上拖一条应当垂直的参考线，松开即转正'}`
- Click → `OnStraightenVClick`
- 祖先 L311 ScrollViewer WorkspaceScroll：`IsVisible={Binding !IsLibraryMode}`

## SplitDialog（14）

来源：`src/OpenRevelare.Gui/Views/SplitDialog.axaml`

### L16 · Button · 全部不分割

- Content：`{i18n:T '全部不分割'}`
- Click → `OnSkipAllClick`

### L17 · Button · 取消导入

- Content：`{i18n:T '取消导入'}`
- IsCancel：`True`
- Click → `OnCancelClick`

### L18 · Button · 按此导入

- Content：`{i18n:T '按此导入'}`
- Click → `OnAcceptClick`

### L23 · ListBox · {Binding FileName} / {Binding Summary}

- Name：`FileList`
- SelectionChanged → `OnFileSelected`

### L40 · Button · −

- Content：`−`
- Click → `OnFewerClick`

### L44 · Button · +

- Content：`+`
- Click → `OnMoreClick`

### L45 · CheckBox · 这条不分割

- Name：`SkipChk`
- Content：`{i18n:T '这条不分割'}`
- Click → `OnSkipToggled`

### L52 · Slider · MarginSlider

- Name：`MarginSlider`
- Minimum：`0`
- Maximum：`0.5`
- Value：`0.15`
- PropertyChanged → `OnMarginChanged`

### L79 · Button · ↺

- Content：`↺`
- ToolTip.Tip：`{i18n:T '逆时针旋转 90°（仅影响预览）'}`
- Click → `OnRotateLeftClick`

### L81 · Button · ↻

- Content：`↻`
- ToolTip.Tip：`{i18n:T '顺时针旋转 90°（仅影响预览）'}`
- Click → `OnRotateRightClick`

### L84 · Button · −

- Content：`−`
- ToolTip.Tip：`{i18n:T '缩小'}`
- Click → `OnZoomOutClick`

### L89 · Button · +

- Content：`+`
- ToolTip.Tip：`{i18n:T '放大'}`
- Click → `OnZoomInClick`

### L91 · Button · 复位

- Content：`{i18n:T '复位'}`
- ToolTip.Tip：`{i18n:T '回到适应窗口、未旋转'}`
- Click → `OnZoomResetClick`

### L108 · Canvas · Stage

- Name：`Stage`
- PointerPressed → `OnStagePressed`
- PointerMoved → `OnStageMoved`
- PointerReleased → `OnStageReleased`
- PointerWheelChanged → `OnStageWheel`

## SyncDialog（8）

来源：`src/OpenRevelare.Gui/Views/SyncDialog.axaml`

### L16 · Button · 关闭

- Content：`{i18n:T '关闭'}`
- IsCancel：`True`
- Click → `OnCloseClick`

### L21 · CheckBox · 片基与两端点（含色偏）

- Content：`{i18n:T '片基与两端点（含色偏）'}`
- IsChecked：`{Binding CalFilmBase}`

### L22 · CheckBox · 色度通道缩放

- Content：`{i18n:T '色度通道缩放'}`
- IsChecked：`{Binding CalChroma}`

### L23 · CheckBox · 镜头畸变 / 暗角 / LCC

- Content：`{i18n:T '镜头畸变 / 暗角 / LCC'}`
- IsChecked：`{Binding CalLens}`

### L25 · CheckBox · 白平衡（色温 / 色调）

- Content：`{i18n:T '白平衡（色温 / 色调）'}`
- IsChecked：`{Binding SceneWb}`

### L26 · CheckBox · 曝光

- Content：`{i18n:T '曝光'}`
- IsChecked：`{Binding SceneExposure}`

### L27 · CheckBox · 影调（黑白场 / 反差 / 高光阴影 / 饱和度）

- Content：`{i18n:T '影调（黑白场 / 反差 / 高光阴影 / 饱和度）'}`
- IsChecked：`{Binding SceneTone}`

### L28 · CheckBox · 色调曲线

- Content：`{i18n:T '色调曲线'}`
- IsChecked：`{Binding SceneCurves}`

## CurveEditor（7）

来源：`src/OpenRevelare.Gui/Controls/CurveEditor.axaml`

### L10 · ToggleButton · W

- Name：`BtnW`
- Content：`W`

### L12 · ToggleButton · R

- Name：`BtnR`
- Content：`R`

### L14 · ToggleButton · G

- Name：`BtnG`
- Content：`G`

### L16 · ToggleButton · B

- Name：`BtnB`
- Content：`B`

### L18 · Button · 重置

- Name：`BtnReset`
- Content：`{i18n:T '重置'}`

### L20 · CurveCanvas · Canvas

- Name：`Canvas`

### L21 · CheckBox · 白光曲线保留色相

- Name：`PreserveChk`
- Content：`{i18n:T '白光曲线保留色相'}`
- IsChecked：`True`
- ToolTip.Tip：`{i18n:T '勾选（W 键）只改亮度，按比例保持颜色；取消则为传统 RGB 曲线，会改色相与饱和度。'}`

## SliderRow（3）

来源：`src/OpenRevelare.Gui/Controls/SliderRow.axaml`

### L27 · TextBlock · 双击重置

- 所在分组：Root
- Name：`LabelText`
- Text：`{Binding Label, ElementName=Root}`
- ToolTip.Tip：`{i18n:T '双击重置'}`
- DoubleTapped → `OnLabelDoubleTapped`

### L48 · Slider · Sld

- 所在分组：Root
- Name：`Sld`
- Minimum：`{Binding Minimum, ElementName=Root}`
- Maximum：`{Binding Maximum, ElementName=Root}`
- Value：`{Binding Value, ElementName=Root, Mode=TwoWay}`

### L54 · NumericUpDown · Spin

- 所在分组：Root
- Name：`Spin`
- Minimum：`{Binding Minimum, ElementName=Root}`
- Maximum：`{Binding Maximum, ElementName=Root}`
- Value：`{Binding SpinValue, ElementName=Root, Mode=TwoWay}`
- Increment：`{Binding Increment, ElementName=Root}`

## C# 动态界面与事件接线候选

以下是定位索引，可能包含重复接线、辅助方法和非控件语句，不纳入 XAML 控件总数。运行时生成的偏好设置、卷信息、报告、保存失败、工程依赖、原生菜单等需要阅读对应方法上下文。

- `src/OpenRevelare.Gui/Views/DocDialog.axaml.cs:27` — `tabs.Items.Add(new TabItem { Header = Loc.T("第一卷"), Content = DocPane("FIRST-ROLL.md") });`
- `src/OpenRevelare.Gui/Views/DocDialog.axaml.cs:28` — `tabs.Items.Add(new TabItem { Header = Loc.T("操作指南"), Content = DocPane("GUIDE.md") });`
- `src/OpenRevelare.Gui/Views/DocDialog.axaml.cs:29` — `tabs.Items.Add(new TabItem { Header = Loc.T("技术原理"), Content = DocPane("THEORY.md") });`
- `src/OpenRevelare.Gui/Views/DocDialog.axaml.cs:31` — `var close = new Button { Content = Loc.T("关闭"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };`
- `src/OpenRevelare.Gui/Views/DocDialog.axaml.cs:32` — `close.Click += (_, _) => Close();`
- `src/OpenRevelare.Gui/Views/FrameReportDialog.axaml.cs:25` — `var text = new TextBox`
- `src/OpenRevelare.Gui/Views/FrameReportDialog.axaml.cs:38` — `var copy = new Button { Content = Loc.T("复制全部") };`
- `src/OpenRevelare.Gui/Views/FrameReportDialog.axaml.cs:39` — `copy.Click += async (_, _) =>`
- `src/OpenRevelare.Gui/Views/FrameReportDialog.axaml.cs:44` — `var close = new Button { Content = Loc.T("关闭"), IsCancel = true };`
- `src/OpenRevelare.Gui/Views/FrameReportDialog.axaml.cs:45` — `close.Click += (_, _) => Close();`
- `src/OpenRevelare.Gui/Views/MainWindow.axaml.cs:2525` — `var item = new MenuItem`
- `src/OpenRevelare.Gui/Views/MainWindow.axaml.cs:2531` — `item.Click += async (_, _) =>`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:88` — `Item(Loc.T("关于 OpenRevelare"), OnAboutClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:90` — `Item(Loc.T("偏好设置…"), OnPreferencesClick, gesture: "OemComma"),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:105` — `Item(Loc.T("新建卷…"), OnOpenClick, gesture: "N"),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:106` — `Bind(Item(Loc.T("添加图像到当前卷…"), OnAddImagesClick, gesture: "O"), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:109` — `Item(Loc.T("打开工程…"), OnOpenProjectClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:110` — `Item(Loc.T("扫描文件夹加入目录…"), OnScanFolderClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:111` — `Bind(Item(Loc.T("另存工程副本…"), OnSaveProjectClick), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:112` — `Bind(Item(Loc.T("查看工程依赖…"), OnProjectDependenciesClick), nameof(Vm.CanInspectProjectDependencies)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:113` — `Bind(Item(Loc.T("归档工程与原片…"), OnArchiveProjectClick), nameof(Vm.CanInspectProjectDependencies)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:115` — `Bind(Item(Loc.T("导出当前帧…"), OnExportClick, gesture: "E"), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:116` — `Bind(Item(Loc.T("导出整卷…"), OnExportRollClick), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:117` — `Bind(Item(Loc.T("查看上次批量导出结果…"), OnBatchExportReportClick), nameof(Vm.CanShowBatchExportReport)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:118` — `Bind(Item(Loc.T("导出印样…"), OnContactSheetClick), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:126` — `Item(Loc.T("复制底片校准"), OnCopyCalClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:127` — `Bind(Item(Loc.T("粘贴底片校准到本帧"), OnPasteCalToCurrentClick), nameof(Vm.HasCalClipboard)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:128` — `Bind(Item(Loc.T("应用底片校准到勾选帧"), OnPasteCalClick), nameof(Vm.HasCalClipboard)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:129` — `Item(Loc.T("应用底片校准到整卷"), OnApplyCalToRollClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:137` — `Item(Loc.T("复制成片调整"), OnCopySceneClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:138` — `Bind(Item(Loc.T("粘贴成片调整到本帧"), OnPasteSceneToCurrentClick), nameof(Vm.HasSceneClipboard)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:139` — `Bind(Item(Loc.T("应用成片调整到勾选帧"), OnPasteSceneClick), nameof(Vm.HasSceneClipboard)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:140` — `Item(Loc.T("应用成片调整到整卷"), OnApplySceneToRollClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:148` — `Bind(Item(Loc.T("同步旋转 / 翻转 / 拉直"), OnApplyGeometryClick), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:149` — `Bind(Item(Loc.T("同步裁切"), OnApplyCropClick), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:157` — `Bind(Item(Loc.T("撤销"), OnUndoClick, gesture: "Z"), nameof(Vm.CanUndo)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:158` — `Bind(Item(Loc.T("重做"), OnRedoClick, gesture: "Y"), nameof(Vm.CanRedo)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:160` — `Bind(Item(Loc.T("重置本帧调整"), OnResetClick), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:162` — `Bind(Item(Loc.T("复制（按当前面板）"), OnCopyActiveClick, gesture: "C"), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:163` — `Bind(Item(Loc.T("粘贴到本帧（按当前面板）"), OnPasteActiveClick, gesture: "V"), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:169` — `Bind(Item(Loc.T("选择色彩同步项…"), OnSyncOptionsClick), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:177` — `Item(Loc.T("图库（卷墙）"), OnLibraryModeClick, gesture: "G", bare: true),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:178` — `Bind(Item(Loc.T("修片"), OnDevelopModeClick, gesture: "D", bare: true), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:180` — `Bind(Item(Loc.T("查看负片"), OnMenuViewNegClick, gesture: "N", bare: true), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:181` — `Bind(Item(Loc.T("前后对比"), OnMenuCompareClick, gesture: "K", bare: true), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:183` — `Bind(Item(Loc.T("适合窗口"), OnFitClick, gesture: "F", bare: true), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:184` — `Bind(Item(Loc.T("实际像素 100%"), OnActualSizeClick, gesture: "D1"), nameof(Vm.HasImage)),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:189` — `Item(Loc.T("切换浅色/深色主题"), OnToggleThemeClick, gesture: "Shift+T"),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:197` — `Item(Loc.T("操作指引 / 技术原理…"), OnDocsClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:198` — `Item(Loc.T("快捷键…"), OnHelpClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:199` — `Item(Loc.T("打开 LUT 文件夹"), OnOpenLutFolderClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:201` — `Item(Loc.T("检查更新…"), OnCheckUpdateClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:205` — `Item(Loc.T("本帧技术报告…"), OnFrameReportClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:206` — `Item(Loc.T("复制色彩诊断"), OnCopyColorDiagnosticsClick),`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:244` — `var item = new NativeMenuItem`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:250` — `item.Click += async (_, _) =>`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:269` — `var item = new NativeMenuItem`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:290` — `var item = new NativeMenuItem`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:295` — `item.Click += (_, _) =>`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:334` — `var item = new NativeMenuItem`
- `src/OpenRevelare.Gui/Views/MainWindow.NativeMenu.cs:340` — `item.Click += click;`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:120` — `_autoInvert = new CheckBox { Content = Loc.T("导入后自动整卷分析去色罩"), IsChecked = v.AutoInvert };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:121` — `_cacheOn = new CheckBox { Content = Loc.T("启用 DNG 转换磁盘缓存"), IsChecked = v.CacheOn };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:122` — `_cachePersist = new CheckBox { Content = Loc.T("跨会话保留（退出不删除，下次启动直接命中）"), IsChecked = v.CachePersist };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:123` — `_cacheDir = new TextBox { Watermark = Loc.T("留空 = 和源文件同目录"), Text = v.CacheDir };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:124` — `_sheetDir = new TextBox { Watermark = Loc.T(@"留空 = %LOCALAPPDATA%\OpenRevelare\sheets"), Text = v.SheetDir };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:131` — `_cacheOn.IsCheckedChanged += (_, _) => UpdateCacheNote();`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:132` — `_cachePersist.IsCheckedChanged += (_, _) => UpdateCacheNote();`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:143` — `panel.Children.Add(Row(Loc.T("RAW 解码后端"), _backend));`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:145` — `panel.Children.Add(Row(Loc.T("FBDD 色度降噪（去马赛克前）"), _fbdd));`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:146` — `panel.Children.Add(Row(Loc.T("RAW 并发解码数"), _concurrency));`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:148` — `panel.Children.Add(Row(Loc.T("GPU 加速"), _gpu));`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:153` — `var browse = new Button { Content = Loc.T("浏览…"), Margin = new Thickness(6, 0, 0, 0) };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:154` — `browse.Click += async (_, _) =>`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:160` — `var clear = new Button { Content = Loc.T("恢复默认"), Margin = new Thickness(6, 0, 0, 0) };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:161` — `clear.Click += (_, _) => { _cacheDir.Text = ""; UpdateCacheNote(); };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:174` — `panel.Children.Add(Row(Loc.T("缓存上限"), _cacheBudget));`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:177` — `var sheetBrowse = new Button { Content = Loc.T("浏览…"), Margin = new Thickness(6, 0, 0, 0) };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:178` — `sheetBrowse.Click += async (_, _) =>`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:184` — `var sheetClear = new Button { Content = Loc.T("恢复默认"), Margin = new Thickness(6, 0, 0, 0) };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:185` — `sheetClear.Click += (_, _) => { _sheetDir.Text = ""; UpdateSheetNote(); };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:191` — `panel.Children.Add(Row(Loc.T("存放上限"), _sheetBudget));`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:194` — `panel.Children.Add(Row(Loc.T("界面主题"), _theme));`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:199` — `var save = new Button { Content = Loc.T("保存"), IsDefault = true, Classes = { "primary" } };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:200` — `var cancel = new Button { Content = Loc.T("取消"), IsCancel = true };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:201` — `save.Click += (_, _) => { Persist(); _accepted = true; Close(); };`
- `src/OpenRevelare.Gui/Views/PreferencesDialog.axaml.cs:204` — `cancel.Click += (_, _) => Close();`
- `src/OpenRevelare.Gui/Views/ProjectDependenciesDialog.cs:19` — `var close = new Button { Content = Loc.T("关闭"), IsCancel = true };`
- `src/OpenRevelare.Gui/Views/ProjectDependenciesDialog.cs:20` — `close.Click += (_, _) => Close(false);`
- `src/OpenRevelare.Gui/Views/ProjectDependenciesDialog.cs:21` — `var save = new Button { Content = Loc.T("导出依赖清单…"), Margin = new Thickness(8, 0, 0, 0), Classes = { "primary" } };`
- `src/OpenRevelare.Gui/Views/ProjectDependenciesDialog.cs:22` — `save.Click += (_, _) => Close(true);`
- `src/OpenRevelare.Gui/Views/ProjectDependenciesDialog.cs:23` — `var copy = new Button { Content = Loc.T("复制全部"), Margin = new Thickness(8, 0, 0, 0) };`
- `src/OpenRevelare.Gui/Views/ProjectDependenciesDialog.cs:51` — `copy.Click += async (_, _) => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(body); };`
- `src/OpenRevelare.Gui/Views/PromptDialog.axaml.cs:29` — `var ok = new Button { Content = Loc.T("确定"), IsDefault = true, Classes = { "primary" } };`
- `src/OpenRevelare.Gui/Views/PromptDialog.axaml.cs:30` — `var cancel = new Button { Content = Loc.T("取消"), IsCancel = true };`
- `src/OpenRevelare.Gui/Views/PromptDialog.axaml.cs:31` — `ok.Click += (_, _) => { _result = _input.Text; Close(_result); };`
- `src/OpenRevelare.Gui/Views/PromptDialog.axaml.cs:32` — `cancel.Click += (_, _) => Close(null);`
- `src/OpenRevelare.Gui/Views/RollInfoDialog.axaml.cs:67` — `var ok = new Button { Content = Loc.T("保存"), IsDefault = true, Classes = { "primary" } };`
- `src/OpenRevelare.Gui/Views/RollInfoDialog.axaml.cs:68` — `var cancel = new Button { Content = Loc.T("取消"), IsCancel = true };`
- `src/OpenRevelare.Gui/Views/RollInfoDialog.axaml.cs:69` — `ok.Click += (_, _) => Close(Collect());`
- `src/OpenRevelare.Gui/Views/RollInfoDialog.axaml.cs:70` — `cancel.Click += (_, _) => Close(null);`
- `src/OpenRevelare.Gui/Views/SaveFailureDialog.cs:27` — `var button = new Button { Content = label, Margin = new Thickness(0, 8, 8, 0) };`
- `src/OpenRevelare.Gui/Views/SaveFailureDialog.cs:32` — `button.Click += (_, _) => Close(choice);`
- `src/OpenRevelare.Gui/Controls/CurveEditor.axaml.cs:45` — `_buttons[i].IsCheckedChanged += (_, _) => { if (_buttons[idx].IsChecked == true) SwitchChannel(idx); };`
- `src/OpenRevelare.Gui/Controls/CurveEditor.axaml.cs:47` — `BtnReset.Click += (_, _) => Canvas.Reset();`
- `src/OpenRevelare.Gui/Controls/CurveEditor.axaml.cs:51` — `PreserveChk.IsCheckedChanged += (_, _) => { if (!_suppressEmit) PreserveHueChanged?.Invoke(this, EventArgs.Empty); };`
