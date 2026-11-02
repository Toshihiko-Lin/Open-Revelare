# 实际界面渲染与检查

此工具直接加载生产 `App` 的主题、XAML 和代码构造窗口；使用与项目一致的 Avalonia.Headless 11.2.3 + Skia。它不启动用户的桌面会话，也不访问用户图库，配置与数据重定向到独立临时目录。

在仓库根目录执行：

```powershell
dotnet run --project docs/gui-audit/preview/Preview.csproj -- docs/gui-audit/renders/after
python docs/gui-audit/preview/build_gallery.py
```

[打开界面总览](../ui-review.html)，可离线查看 19 个界面／状态的浅深主题。截图渲染不包含系统标题栏。

参数栏沿用生产 MainWindow 内的实际控件树，在独立窗口中渲染。固定标签的选择、内容展示与底部同步按钮仍使用原事件和绑定；它不是重新画出来的表单。六帧身份是演示数据，直方图统计来自仓库示例 JPEG，未解码 RAW。印样和扫描窗口使用同一示例图检查版式，不据此作色彩正确性结论。

页签检查还包含实际方向键输入、选中内容绑定和图表背景与侧栏颜色的一致性。运行中的主程序也已通过 Windows 窗口检查，两个页签均能切换，直方图融入了侧栏底色。波形图使用相同的主题背景绑定；尚未单独完成其原生窗口切换验收。

工具还检查：标签切换后参数内容存在；关闭裁切说明仍保留应用／取消；偏好语言重建保留草稿；取消、Esc、标题栏关闭回滚主题与语言；保存后保留已提交主题／语言。共享目标解析与卷信息字段保留另有 .NET 回归测试。

框架依据：[Avalonia 官方 Headless 文档](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)。此处只新增开发工具依赖，桌面应用的 Avalonia 版本与渲染链路不变。

这不是原生窗口端到端测试，不能覆盖 HWND 叠层、真实键盘／鼠标设备、屏幕阅读器、GPU/HDR 或三平台窗口管理器。界面总览只对实际拍到的状态负责，不代替审计的 T01–T16 完整任务。
