; OpenRevelare (C# / .NET 8) Windows 安装包
;
; AppId 是全新的 GUID —— OpenRevelare 与此前的闭源版本是两个独立产品，不做覆盖升级。
; 装在自己的目录、控制面板里是自己的一条记录；旧版若还在，由用户自行卸载。
; 从本版起，后续 OpenRevelare 之间仍是正常的覆盖升级（AppId 不再变）。
;
; 偏好设置与卷目录在 %APPDATA%\OpenRevelare 与 %LOCALAPPDATA%\OpenRevelare，
; 安装与卸载都不碰，升级后原样保留。
;
; 编译：ISCC.exe open-revelare.iss（先跑 README「从源码构建」里的 dotnet publish）

#define MyAppName "OpenRevelare"
#define MyAppVersion "1.5.3"
#define MyAppPublisher "Toshihiko-Lin"
#define MyAppURL "https://github.com/Toshihiko-Lin/Open-Revelare"
#define MyAppExeName "OpenRevelare.exe"
#define MyAppSourceDir "publish\win-x64"

; 版本号有两处（csproj 的 <Version> 和上面这行），编译期核对，防止只改了一处。
; 报错说明忘了 dotnet publish 或忘了改 MyAppVersion。
#define ExeVersion GetVersionNumbersString(MyAppSourceDir + "\" + MyAppExeName)
#if ExeVersion != MyAppVersion + ".0"
  ; ISPP 的 #error 不做宏展开，所以这里只能写死一句话，不带具体版本号。
  #error 版本号不一致：本文件的 MyAppVersion 与 publish\win-x64\OpenRevelare.exe 的文件版本对不上。先跑 dotnet publish，再核对 csproj 的 <Version> 和上面的 MyAppVersion。
#endif

[Setup]
; ⚠ 不要改这个 GUID —— 它是 OpenRevelare 各版本之间「覆盖升级」的全部依据。
AppId={{D6F99E4A-7603-4982-AEA8-11133A9F94AE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=installer
OutputBaseFilename=OpenRevelare-{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
ShowLanguageDialog=no
LanguageDetectionMethod=locale
; .NET 8 的下限（Windows 10 1607）。产物是 self-contained，用户无需装 .NET 运行时。
MinVersion=10.0.14393
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 升级时若程序正开着，用重启管理器提示关闭，而不是留下一堆改不动的文件。
CloseApplications=yes
RestartApplications=no
SetupIconFile=src\OpenRevelare.Gui\Assets\icons\app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[InstallDelete]
; 上一次安装留下的程序集/原生库：Inno 不会自动清理旧文件，而依赖升级会改动
; 文件名（如 onnxruntime 换版本），残留的旧 DLL 会被新版本按名加载到。下面几行删完
; 立刻由 [Files] 全量重装，所以是安全的。
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: filesandordirs; Name: "{app}\models"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
; publish 目录整搬。排除调试符号与 onnxruntime 的导入库（链接期产物，运行时用不到）。
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; \
    Excludes: "*.pdb,*.lib,createdump.exe"; \
    Flags: ignoreversion recursesubdirs createallsubdirs
; 许可文本。GPL-3.0 要求随二进制给出许可，LibRaw 的 LGPL-2.1 要求给出第三方声明
; —— 两者都不是可选项。
Source: "LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清程序目录。%APPDATA%\OpenRevelare（settings.json / catalog.json）和
; %LOCALAPPDATA%\OpenRevelare（印样封面缓存 / onboarded 标记）**故意保留**：
; 卸载重装后卷目录和偏好设置原样都在。
Type: filesandordirs; Name: "{app}\models"
