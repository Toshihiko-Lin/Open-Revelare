# 相机操作引导与标准扫描校准产物接入

日期：2026-11-18。本轮替代“所有扫描仪用户先编辑自定义密度 JSON”的默认入口。物理测量、输入色度表征和负片反转继续区分，不把行业文件中的 XYZ／Lab 改名成密度。

## 相机：按操作顺序采集

应用内“素材采集引导…”已改为七步：架好相机并移除胶片 → M 档、固定 ISO／光圈、手动对焦 → 以无片图确定不过曝的快门并锁定 → 盖镜头盖、保持相同曝光拍暗场 → 取下盖放回胶片拍内容 → 从卡中直接拷贝 RAW 并登记 → 需要时独立重复采集验证。

引导明确不能在暗场照片里改短快门，不能把片基当无片场，不能仅凭相机 JPEG 预览直方图证明 RAW 没有饱和。换曝光／光源／几何后使用新的参考会话。既有 CFA 试算条件、格式限制和不改胶卷的边界保持不变。

## 扫描仪：标准入口与高级测量分开

配置新增流程来源：SilverFast IT8、VueScan IT8、ArgyllCMS、厂家维护、自定义实测密度。该值记录用户的流程，不依据品牌自动补齐验证能力。

普通入口为“导入标准扫描校准文件…”，支持内容识别：

- **输入 ICC／ICM**：必须为 `scnr` 输入设备类、三通道 RGB；通过现有 LittleCMS 验证载荷。显示／输出类 ICC 不当作扫描设备配置。
- **IT8／Q60／CIE 的 CGATS 文本**：识别 IT8.* 或 CGATS.* 标识、表头、样本字段、测量行和元数据，保留参考值及原始含义。
- **ArgyllCMS CTI3／.ti3**：支持单表 INPUT／EMISINPUT 测量数据，识别设备 RGB 与 XYZ、Lab、SPEC_ 光谱字段；拒绝打印机／显示测量表，未支持的多表格式明确报错。

导入时核验结构、声明行列数、测量字段有限性和摘要一致性，将 ICC 登记为输入来源、CGATS／CTI3 登记为目标数据。显示格式、字段、行数和可用的供应商／批次信息，用户保存后可随配置重开。不会修改原文件，也不自动执行外部工具。

标准文件导入本身负责识别、解析、验证与配置登记。现已另在新建卷入口提供显式的工作流选择：仅记录上游流程，或将输入 ICC 转为线性 ACEScg 工作副本，详见 [工作流接入](capture-calibration-workflow.md)。未内置 IT8 布局识别和 ICC 生成。没有“只要导入就完成物理校准”的状态。

## 用户可以沿用的真实流程

### SilverFast

使用与透射／反射模式匹配的实体 IT8 色靶及批次参考，按安装版本的 IT8 Calibration 对齐色块，生成并导出输入 ICC。导入 ICC 与参考数据，并保留软件、扫描模式和设置。官方说明参见 [SilverFast 参考数据](https://www.silverfast.com/it8calibration/en.html)和 [色彩管理手册](https://www.silverfast.com/PDF/english/SF-Color-Management.pdf)。手册界面版本较旧，不把其菜单位置当作所有现代版本固定界面。

### VueScan

按 [当前官方 IT8 流程](https://www.hamrick.com/vuescan/html/vuesc17.htm)选择 Profile scanner，指定 Scanner IT8 data 和 Scanner ICC profile，预览对齐并执行 Profile scanner，取得 `scanner.icc` 或用户指定名称的 ICC。当前官方说明该功能需要 Professional Edition；界面与功能以用户安装版本为准。RAW 输出与已颜色处理 TIFF 分开记录，不重复应用已用过的 ICC。

### ArgyllCMS

使用实体靶对应的布局 `.cht` 与厂商参考数据，按 [官方示例](https://www.argyllcms.com/doc/Scenarios.html)通过 scanin 提取测量、colprof 生成输入 ICC。本模块接收其输入设备单表 `.ti3` 和 ICC，不自动安装／调用工具；`.cht` 不是测量值文件，不能当密度或 ICC 导入。

[CTI3 官方定义](https://www.argyllcms.com/doc/ti3_format.html)说明它存储设备值与 CIE／光谱值对，不能因为文件名包含 calibration 就按透射密度处理。具体厂商参考文件仍需与目标匹配，不同设备和材料的外推能力需要另外验证。

### 厂家维护

硬件白校准、对焦等按对应机型和厂家软件执行，记录流程和软件版本。厂商专有二进制维护文件不在通用解析器支持范围内；本软件不替代扫描仪驱动内部维护功能。

## 高级实测密度入口

此前的扫描参考 JSON 与逐通道密度拟合保留，但已收进“高级：自定义实测密度响应”。只有具备密度定义、实测值、独立扫描与误差预算的用户需要使用。普通 IT8 的 XYZ／Lab 不会自动喂给该拟合；详见 [高级测量说明](scanner-response-and-collection-guide.md)。

## 实际验证

本机从 SilverFast 官方参考下载入口读取 `E150127`，解析得到 IT8/CGATS、7 个字段、288 个样本，包含 XYZ 与 Lab；供应商为 LaserSoft Imaging，材料为 Fuji Provia，批次来源在元数据中保留。该参考只在 scratch 中用于格式检查，不随仓库重新分发；这不验证任意用户的实体靶或扫描仪。

新增单元测试覆盖引号与注释、样本 ID／PCS 保留、CTI3 输入类与错误设备类、计数／非有限值／多表拒绝、ICC 原生验证及显示类拒绝。并验证流程来源随配置保存和非法枚举拒绝。真实品牌 ICC、硬件校准精度和图形界面操作仍需独立素材与实机验证。
