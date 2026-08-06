# models/ — 单独授权的第三方资产

⚠ **本目录中的文件不属于 OpenRevelare，不在本项目 GPL-3.0 授权的范围之内。**
它们各自按下述许可分发。仓库其余部分的 GPL-3.0 授权不覆盖、也无权覆盖这些文件。

---

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

OpenRevelare 用它实现「智能白平衡」。本程序免费、不销售、不含内购或订阅，
分发本身不以商业利益为目的。

**你在使用或再分发这个文件时受 CC BY-NC-SA 4.0 约束**，与你对 OpenRevelare 代码
所享有的 GPL-3.0 权利无关。具体说：

- **不得**将它用于商业目的，也不得作为商业产品的一部分再分发 —— 即便你是在
  行使 GPL-3.0 赋予你对 OpenRevelare **代码**的商业再分发权利。要商用请先把
  本目录清空（程序照常构建运行，只有「智能白平衡」一个功能会提示模型未找到）。
- 再分发须保留上述署名，作者亦要求引用其论文。
- 若你修改了权重，衍生物须以相同许可发布。

如果你是本权重的作者且希望我们停止分发，请在
https://github.com/Toshihiko-Lin/Open-Revelare/issues 开一个 issue，会立即照办。
