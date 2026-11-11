#!/usr/bin/env python3
"""Gate release candidates against the original CoreML model on a macOS builder."""
import argparse
import json
from pathlib import Path
import coremltools as ct
import numpy as np
import onnxruntime as ort

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("coreml", type=Path)
parser.add_argument("onnx", type=Path)
parser.add_argument("report", type=Path)
args = parser.parse_args()
coreml = ct.models.MLModel(str(args.coreml), compute_units=ct.ComputeUnit.CPU_ONLY)
session = ort.InferenceSession(str(args.onnx), providers=["CPUExecutionProvider"])
rng = np.random.default_rng(20261111)
fixtures = {
    "flat": np.full((1, 3, 576, 576), 128 / 255, dtype=np.float32),
    "grain": rng.integers(0, 256, (1, 3, 576, 576), dtype=np.uint8).astype(np.float32) / 255,
}
lines = np.full((1, 3, 576, 576), .6, dtype=np.float32)
lines[:, :, 30:545, 284:287] = .95
lines[:, :, 430:434, 20:550] = .1
fixtures["defect-lines"] = lines
report = []
passed = True
for name, rgb in fixtures.items():
    reference = coreml.predict({"rgb": rgb.astype(np.float16)})["prob"].astype(np.float32)
    actual = session.run(["prob"], {"rgb": rgb})[0]
    error = np.abs(reference - actual)
    # FP32 preserves source weights but differs from CoreML's FP16 intermediates.
    # These are explicit release gates, not a claim of bit identity.
    valid = bool(np.isfinite(actual).all() and error.mean() <= .002 and error.max() <= .05)
    report.append({"fixture": name, "meanError": float(error.mean()), "maxError": float(error.max()), "passed": valid})
    passed &= valid
args.report.write_text(json.dumps({"passed": passed, "fixtures": report,
    "limits": {"meanError": .002, "maxError": .05}}, indent=2) + "\n", encoding="utf8")
print(args.report.read_text())
if not passed: raise SystemExit("CoreML/ONNX parity gate rejected the component")
