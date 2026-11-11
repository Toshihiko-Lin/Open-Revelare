#!/usr/bin/env python3
"""Promote 31x31 pooling accumulation to fp32; keep source weights/network fp16.

CoreML CPU fp16 pooling accumulates 961 samples in half precision. On a flat
0.6 input its mean drifts to about 0.55, which becomes a false high-pass signal.
The ONNX exporter applies the same explicit pool casts for a shared reference.
"""
import argparse
import hashlib
import importlib.util
from pathlib import Path
import shutil
import sys
import types

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("source", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
spec_file = args.source / "Data/com.apple.CoreML/model.mlmodel"
raw = spec_file.read_bytes()
if hashlib.sha256(raw).hexdigest() != "29984a14cd22b9407a3bb04d4647edf0fd9592f9958c1c0a1f5fd528ae72fe58":
    raise SystemExit("Unexpected source FibreNet model")
package = importlib.util.find_spec("coremltools")
shim = types.ModuleType("coremltools")
shim.__path__ = list(package.submodule_search_locations)
sys.modules["coremltools"] = shim
from coremltools.proto import Model_pb2, MIL_pb2

model = Model_pb2.Model()
model.ParseFromString(raw)
block = model.mlProgram.functions["main"].block_specializations["CoreML6"]
types_by_name = {out.name: out.type for op in block.operations for out in op.outputs}
operations = []
def named(op, name):
    value = op.attributes["name"]
    value.type.tensorType.dataType = MIL_pb2.STRING
    value.immediateValue.tensor.strings.values.append(name)
    return op

for name, dtype in (("or_fp32_dtype", "fp32"), ("or_fp16_dtype", "fp16")):
    op = named(MIL_pb2.Operation(type="const"), name)
    out = op.outputs.add(name=name)
    out.type.tensorType.dataType = MIL_pb2.STRING
    op.attributes["val"].type.CopyFrom(out.type)
    op.attributes["val"].immediateValue.tensor.strings.values.append(dtype)
    operations.append(op)

def cast(source, name, shape_type, dtype_name, dtype):
    op = named(MIL_pb2.Operation(type="cast"), name)
    op.inputs["x"].arguments.add(name=source)
    op.inputs["dtype"].arguments.add(name=dtype_name)
    out = op.outputs.add(name=name)
    out.type.CopyFrom(shape_type)
    out.type.tensorType.dataType = dtype
    return op

count = 0
for original in block.operations:
    op = MIL_pb2.Operation()
    op.CopyFrom(original)
    if op.type == "avg_pool":
        name = op.outputs[0].name
        source = op.inputs["x"].arguments[0].name
        operations.append(cast(source, source + "_accum32", types_by_name[source], "or_fp32_dtype", MIL_pb2.FLOAT32))
        op.inputs["x"].arguments[0].name = source + "_accum32"
        op.outputs[0].name = name + "_accum32"
        op.outputs[0].type.tensorType.dataType = MIL_pb2.FLOAT32
        op.attributes["name"].immediateValue.tensor.strings.values[0] = name + "_accum32"
        operations.append(op)
        operations.append(cast(name + "_accum32", name, types_by_name[name], "or_fp16_dtype", MIL_pb2.FLOAT16))
        count += 1
    else:
        operations.append(op)
if count != 2: raise SystemExit("Expected exactly two large-window pooling operations")
del block.operations[:]
block.operations.extend(operations)
shutil.copytree(args.source, args.output)
(args.output / "Data/com.apple.CoreML/model.mlmodel").write_bytes(model.SerializeToString())
print("Prepared CoreML model with fp32 pool accumulation and unchanged fp16 weights")
