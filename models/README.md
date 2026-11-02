# models/ — 单独授权的第三方资产

⚠ **本目录中的文件不属于 OpenRevelare，不在本项目 GPL-3.0 授权的范围之内。**
它们各自按下述许可分发。仓库其余部分的 GPL-3.0 授权不覆盖、也无权覆盖这些文件。

---

## 可选安装

默认构建和安装包不包含模型权重，ONNX 推理运行库仍随程序提供。
未安装时「智能色偏修正」按钮置灰，并叠加半透明下载图标及模型大小提示。点击下载图标，确认许可后从独立 GitHub 模型仓库下载约 17.5 MB 的模型，失败时自动尝试 Gitee 国内镜像。
也可选择「同意许可并从国内镜像下载」直接使用 Gitee。安装成功后按钮恢复正常，再次点击即可分析。
下载支持取消，安装前校验文件大小及 SHA-256；失败不会留下半个模型或覆盖已有文件。

也可运行 `OpenRevelare.Cli --install-deep-wb` 主动安装（使用前请阅读下列许可）。
模型保存到用户数据目录的 `OpenRevelare/models/net_awb.onnx`：Windows 为
`%LOCALAPPDATA%/OpenRevelare/models/net_awb.onnx`，Linux/macOS 为
`${XDG_DATA_HOME:-~/.local/share}/OpenRevelare/models/net_awb.onnx`。
设置 `OPENREVELARE_DATA_DIR` 可覆盖 OpenRevelare 数据目录。

离线安装：从 [GitHub 固定版本下载地址](https://github.com/Toshihiko-Lin/openrevelare-models/releases/download/deep-wb-v1/net_awb.onnx)
或从 [Gitee 国内备用地址](https://gitee.com/Toshihiko-Lin/openrevelare-models/releases/download/deep-wb-v1/net_awb.onnx) 下载。
下载后放入上述目录，或可执行文件旁的 `models/net_awb.onnx`。
独立许可声明见 [DeepWB-NOTICE.txt](DeepWB-NOTICE.txt)，国内镜像也提供同名声明附件。
CLI 的 `--print-awb` 也可用 `--awb-model <路径>` 指定模型。
SHA-256：`50503B7F6CFB0663EBA0D0D61CBC68E8E93E56581DB3143B203BAB163F7C9F54`。
卸载可选模型时删除用户数据目录中的该文件即可。
仓库中的权重仅保留供模型来源与开发验证使用，不会复制到默认构建／发布产物。

## net_awb.onnx

| | |
|---|---|
| 来源 | [mahmoudnafifi/Deep_White_Balance](https://github.com/mahmoudnafifi/Deep_White_Balance) |
| 作者 | Mahmoud Afifi, Konstantinos G. Derpanis, Björn Ommer, Michael S. Brown |
| 论文 | *Deep White-Balance Editing*, CVPR 2020 |
| 许可 | **CC BY-NC-SA 4.0**（署名 — 非商业性使用 — 相同方式共享） |
| 全文 | https://creativecommons.org/licenses/by-nc-sa/4.0/legalcode |

原仓库 README 另有一句：
> "This software is provided for research purposes only and CAN NOT be used for
> commercial purposes."

OpenRevelare 用它实现「智能色偏修正」。本程序免费、不销售、不含内购或订阅，
分发本身不以商业利益为目的。

**你在使用或再分发这个文件时受 CC BY-NC-SA 4.0 约束**，与你对 OpenRevelare 代码
所享有的 GPL-3.0 权利无关。具体说：

- **不得**将它用于商业目的，也不得作为商业产品的一部分再分发 —— 即便你是在
  行使 GPL-3.0 赋予你对 OpenRevelare **代码**的商业再分发权利。默认发布包不包含模型；商业分发时请勿另行加入权重。
- 再分发须保留上述署名，作者亦要求引用其论文。
- 若你修改了权重，衍生物须以相同许可发布。

如果你是本权重的作者且希望我们停止分发，请在
https://github.com/Toshihiko-Lin/Open-Revelare/issues 开一个 issue，会立即照办。
