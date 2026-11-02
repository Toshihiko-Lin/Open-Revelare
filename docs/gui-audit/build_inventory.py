"""Rebuild the source-level GUI inventory with Python's standard library.

Run from any directory: python docs/gui-audit/build_inventory.py
This is a static inventory, not a runtime accessibility-tree capture.
"""
from __future__ import annotations

import hashlib
import json
import re
import subprocess
from collections import Counter
from pathlib import Path
from xml.parsers import expat

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent
GUI = ROOT / "src/OpenRevelare.Gui"
CONTROLS = {
    "MenuItem", "Button", "ToggleButton", "ToggleSwitch", "CheckBox",
    "RadioButton", "ComboBox", "ComboBoxItem", "TextBox", "NumericUpDown",
    "Slider", "SliderRow", "TabItem", "TabStrip", "TabStripItem", "Expander", "ListBox", "CurveEditor",
    "CurveCanvas", "HistogramView", "WaveformView", "GridSplitter", "AutoCompleteBox",
}
PROPS = {
    "Name", "Header", "Content", "Label", "Text", "Watermark", "ToolTip.Tip",
    "AutomationProperties.Name", "InputGesture", "IsEnabled", "IsVisible",
    "IsChecked", "IsExpanded", "SelectedIndex", "SelectedItem", "ItemsSource",
    "Value", "Minimum", "Maximum", "Increment", "DefaultValue", "Decimals",
    "IsDefault", "IsCancel", "SelectionMode", "IsReadOnly", "GroupName", "Tag",
}
EVENTS = {
    "Click", "Checked", "Unchecked", "IsCheckedChanged", "SelectionChanged",
    "TextChanged", "ValueChanged", "PointerPressed", "PointerReleased",
    "PointerMoved", "PointerWheelChanged", "Tapped", "DoubleTapped", "SubmenuOpened",
    "PropertyChanged", "KeyDown",
}


def short(name: str) -> str:
    return name.rsplit("|", 1)[-1]


def clean(value: str) -> str:
    return re.sub(r"\{i18n:T '([^']*)'\}", r"\1", value)


def extract(path: Path) -> list[dict]:
    parser = expat.ParserCreate(namespace_separator="|")
    stack: list[dict] = []
    records = []

    def start(name, attrs):
        attrs = {short(k): v for k, v in attrs.items()}
        tag = short(name)
        node = {"type": tag, "line": parser.CurrentLineNumber, "attrs": attrs}
        stack.append(node)
        if tag == "TextBlock" and attrs.get("Text"):
            for ancestor in stack[:-1]:
                ancestor.setdefault("texts", []).append(clean(attrs["Text"]))
        if tag not in CONTROLS and not any(k in attrs for k in EVENTS):
            return
        labels = []
        for p in stack[:-1]:
            a = p["attrs"]
            label = a.get("Header") or a.get("Name")
            if label:
                labels.append(clean(label))
        ancestors = [
            {"line": n["line"], "type": n["type"],
             **{k: v for k, v in n["attrs"].items()
                if k in {"Name", "IsEnabled", "IsVisible", "IsExpanded", "DataContext"}}}
            for n in stack[:-1]
            if any(k in n["attrs"] for k in
                   {"IsEnabled", "IsVisible", "IsExpanded", "DataContext"})
        ]
        record = {
            "id": f"{path.relative_to(ROOT).as_posix()}:{node['line']}",
            "file": path.relative_to(ROOT).as_posix(), "line": node["line"],
            "type": tag, "path": " / ".join(labels),
            "label": clean(next((attrs[k] for k in
                ("AutomationProperties.Name", "Label", "Header", "Content", "Watermark", "Name")
                if attrs.get(k)), tag)),
            "properties": {k: v for k, v in attrs.items() if k in PROPS},
            "events": {k: v for k, v in attrs.items() if k in EVENTS},
            "ancestor_conditions": ancestors,
            "verification": "static-xaml",
        }
        node["record"] = record
        records.append(record)

    def end(_):
        node = stack.pop()
        record = node.get("record")
        attrs = node["attrs"]
        if record is not None and not any(attrs.get(k) for k in
                ("AutomationProperties.Name", "Label", "Header", "Content", "Watermark")):
            record["label"] = " / ".join(node.get("texts", [])) or clean(
                attrs.get("ToolTip.Tip") or attrs.get("Name") or node["type"])

    parser.StartElementHandler = start
    parser.EndElementHandler = end
    parser.Parse(path.read_bytes(), True)
    return records


def main():
    xaml_files = sorted((GUI / "Views").glob("*.axaml")) + sorted((GUI / "Controls").glob("*.axaml"))
    controls = [r for p in xaml_files for r in extract(p)]
    dynamic = []
    methods = []
    for p in sorted((GUI / "Views").glob("*.cs")) + sorted((GUI / "Controls").glob("*.cs")):
        for i, line in enumerate(p.read_text(encoding="utf-8-sig").splitlines(), 1):
            if re.search(r"\bnew\s+(?:NativeMenuItem|Button|CheckBox|RadioButton|ComboBox|TextBox|NumericUpDown|TabItem|MenuItem)\b|\bItem\(Loc\.|\.Click\s*\+=|\.IsCheckedChanged\s*\+=|\bRow\(Loc\.", line):
                dynamic.append({"file": p.relative_to(ROOT).as_posix(), "line": i,
                                "source": line.strip(), "verification": "candidate-needs-context"})
            if re.search(r"\b(?:void|Task(?:<[^>]+>)?)\s+On\w+\s*\(", line):
                methods.append({"file": p.relative_to(ROOT).as_posix(), "line": i, "source": line.strip()})
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    sources = sorted(set(xaml_files + list((GUI / "Views").glob("*.cs"))
                         + list((GUI / "Controls").glob("*.cs"))))
    data = {"schema": 1, "audit_date": "2026-11-01", "head": revision,
            "scope": "Views/Controls XAML declarations + C# dynamic-control candidates; excludes runtime templates, styles, OS dialogs and repeated frame instances",
            "counts": {"xaml_files": len(xaml_files), "xaml_declarations": len(controls),
                       "dynamic_candidates": len(dynamic), "by_type": dict(Counter(r["type"] for r in controls))},
            "sources": [{"file": p.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in sources],
            "controls": controls, "dynamic_candidates": dynamic, "event_methods": methods}
    (OUT / "inventory.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    lines = ["# GUI 控件逐项索引", "", "由 `build_inventory.py` 生成。语义解读见同目录审计文档。", "",
             f"基线：`{revision}`；核对日期：2026-11-01。", "",
             f"扫描 {len(xaml_files)} 个 XAML 文件，记录 {len(controls)} 个声明；另列 {len(dynamic)} 条 C# 动态构造／接线候选。", "",
             "计数是源代码声明数，不是功能数或屏幕上同时可见的按钮数。子菜单、选项、折叠组和复合控件内部声明分别计数；模板在运行时可重复出现。", "",
             "标签优先采用无障碍名称、Label、Header、Content、Watermark，其次子节点文字、tooltip 或 Name。祖先条件存于 JSON，不能把控件自己的 IsEnabled 当作最终可用状态。输入事件表面也纳入索引，例如可点击卷卡和预览 Canvas。", ""]
    for p in xaml_files:
        rel = p.relative_to(ROOT).as_posix()
        rows = [r for r in controls if r["file"] == rel]
        lines += [f"## {p.stem}（{len(rows)}）", "", f"来源：`{rel}`", ""]
        for r in rows:
            lines += [f"### L{r['line']} · {r['type']} · {r['label']}", ""]
            if r["path"]:
                lines += [f"- 所在分组：{r['path']}"]
            for k, v in r["properties"].items():
                lines += [f"- {k}：`{v}`"]
            for k, v in r["events"].items():
                lines += [f"- {k} → `{v}`"]
            for a in r["ancestor_conditions"]:
                conditions = "; ".join(f"{k}={v}" for k, v in a.items() if k not in {"line", "type", "Name"})
                if conditions:
                    lines += [f"- 祖先 L{a['line']} {a['type']} {a.get('Name', '')}：`{conditions}`"]
            lines += [""]
    lines += ["## C# 动态界面与事件接线候选", "", "以下是定位索引，可能包含重复接线、辅助方法和非控件语句，不纳入 XAML 控件总数。运行时生成的偏好设置、卷信息、报告、保存失败、工程依赖、原生菜单等需要阅读对应方法上下文。", ""]
    for r in dynamic:
        lines += [f"- `{r['file']}:{r['line']}` — `{r['source']}`"]
    (OUT / "control-inventory.md").write_text("\n".join(line.rstrip() for line in lines) + "\n", encoding="utf-8")
    print(json.dumps(data["counts"], ensure_ascii=False))


if __name__ == "__main__":
    main()
