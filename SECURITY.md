# 安全策略

## 支持的版本

只有最新发布版会收到修复。请先确认问题在 [latest release](https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest) 上仍然存在。

## 报告漏洞

**请不要开公开 issue。** 用 GitHub 的私密报告通道：
[Security → Report a vulnerability](https://github.com/Toshihiko-Lin/Open-Revelare/security/advisories/new)

请附上：影响的版本与平台、复现步骤、以及触发所需的文件（如果是构造的图像文件，请压成 zip）。

这是一个个人维护的项目，没有 SLA，但我会尽快回复——通常几天之内。

## 这个程序的攻击面

有意保持得很小，知道边界在哪有助于判断什么算漏洞：

- **不联网**，唯一的例外是启动时向 GitHub / Gitee 的 release API 各发一次更新检查（只读，失败静默）。没有账号、没有遥测、不上传任何图像。
- **不改源文件。** 参数写在源文件旁边的 `.ncproj` 里。
- **本地文件解析是主要的攻击面。** RAW 解码走 [LibRaw](https://www.libraw.org/)，TIFF/JPEG 与 ICC、`.cube` LUT 由本仓库代码解析。恶意构造的图像文件导致崩溃、越界读写或任意代码执行——这些都算漏洞，请报告。
- macOS 版为 ad-hoc 签名，未经 Apple 公证；Windows 安装包未做代码签名。这是已知情况（个人项目无开发者证书），不必单独报告。

## 不算漏洞的情况

- 需要攻击者已经能在本机以同一用户身份执行代码的前提
- 更新检查被中间人劫持后诱导用户手动去下载——安装包本身不会被自动下载或执行
