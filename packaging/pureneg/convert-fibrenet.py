#!/usr/bin/env python3
"""Convert the pinned FibreNet MIL graph and exact fp16 weights to portable ONNX.

Build-time only: numpy, onnx, coremltools protobuf definitions. No CoreML execution
or Python installation is needed by the end user. Reject different model revisions
and unknown operations rather than silently substituting an approximate network.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys
import types

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

SOURCE_SHA = "29984a14cd22b9407a3bb04d4647edf0fd9592f9958c1c0a1f5fd528ae72fe58"
WEIGHTS_SHA = "130c1b428eaad69e4ea86684148ebda24ca97d5147026ae2a526f95b95f7868a"


def convert(spec_path: Path, weights_path: Path, output: Path):
    spec_bytes, weights = spec_path.read_bytes(), weights_path.read_bytes()
    if hashlib.sha256(spec_bytes).hexdigest() != SOURCE_SHA or hashlib.sha256(weights).hexdigest() != WEIGHTS_SHA:
        raise ValueError("FibreNet input does not match the pinned PURENeg model")
    # Only protobuf definitions are needed. coremltools' top-level package imports
    # optional native macOS prediction libraries that do not exist on Windows/Linux.
    package = importlib.util.find_spec("coremltools")
    shim = types.ModuleType("coremltools")
    shim.__path__ = list(package.submodule_search_locations)
    sys.modules["coremltools"] = shim
    from coremltools.proto import Model_pb2
    spec = Model_pb2.Model()
    spec.ParseFromString(spec_bytes)
    block = spec.mlProgram.functions["main"].block_specializations["CoreML6"]
    constants, nodes, initializers, information = {}, [], {}, []

    def decode(value):
        tensor = value.type.tensorType
        shape = [d.constant.size for d in tensor.dimensions]
        dtype = tensor.DESCRIPTOR.fields_by_name["dataType"].enum_type.values_by_number[tensor.dataType].name
        if value.HasField("blobFileValue"):
            blob = value.blobFileValue
            if blob.fileName != "@model_path/weights/weight.bin" or dtype != "FLOAT16":
                raise ValueError("Unsupported weight storage")
            magic, kind, size, offset = struct.unpack_from("<IIQQ", weights, blob.offset)
            if magic != 0xDEADBEEF or kind != 1 or size != np.prod(shape) * 2:
                raise ValueError("Unexpected CoreML weight header")
            result = np.frombuffer(weights, dtype="<f2", count=int(np.prod(shape)), offset=offset)
        else:
            data = value.immediateValue.tensor
            if dtype == "FLOAT16": result = np.frombuffer(data.bytes.values, dtype="<f2")
            elif dtype == "FLOAT32": result = np.array(data.floats.values, dtype=np.float32)
            elif dtype == "INT32": result = np.array(data.ints.values, dtype=np.int32)
            elif dtype == "BOOL": result = np.array(data.bools.values, dtype=bool)
            elif dtype == "STRING": result = np.array(data.strings.values)
            else: raise ValueError(f"Unsupported constant type: {dtype}")
        return result.reshape(shape)

    def initializer(name, data):
        if name not in initializers:
            initializers[name] = numpy_helper.from_array(np.asarray(data), name)
        return name

    def argument(op, key):
        names = [a.name for a in op.inputs[key].arguments]
        if len(names) != 1: raise ValueError(f"Expected one argument for {key}")
        return names[0]

    def attr(op, key, default=None):
        if key not in op.inputs: return default
        data = constants[argument(op, key)]
        return data.item() if data.ndim == 0 else data.tolist()

    def data(op, key):
        name = argument(op, key)
        if name in constants:
            value = constants[name]
            if value.dtype.kind == "f": value = value.astype(np.float16)
            initializer(name, value)
        return name

    def pads(op):
        if attr(op, "pad_type") not in ("valid", "custom"):
            raise ValueError("Unsupported automatic padding")
        p = attr(op, "pad")
        return p[::2] + p[1::2]

    for op in block.operations:
        name = op.outputs[0].name
        if op.type == "const":
            constants[name] = decode(op.attributes["val"])
            continue
        shape = [d.constant.size for d in op.outputs[0].type.tensorType.dimensions]
        information.append(helper.make_tensor_value_info(name, TensorProto.FLOAT16, shape))
        x = data(op, "x") if "x" in op.inputs else None
        inputs, attributes = [], {}
        if op.type in ("mul", "add", "sub"):
            kind, inputs = {"mul": "Mul", "add": "Add", "sub": "Sub"}[op.type], [x, data(op, "y")]
        elif op.type in ("relu", "sigmoid"):
            kind, inputs = {"relu": "Relu", "sigmoid": "Sigmoid"}[op.type], [x]
        elif op.type == "conv":
            kind, inputs = "Conv", [x, data(op, "weight"), data(op, "bias")]
            attributes = dict(strides=attr(op, "strides"), dilations=attr(op, "dilations"),
                              group=attr(op, "groups"), pads=pads(op))
        elif op.type in ("avg_pool", "max_pool"):
            kind, inputs = ("AveragePool" if op.type == "avg_pool" else "MaxPool"), [x]
            attributes = dict(kernel_shape=attr(op, "kernel_sizes"), strides=attr(op, "strides"),
                              pads=pads(op), ceil_mode=int(attr(op, "ceil_mode")))
            if op.type == "avg_pool": attributes["count_include_pad"] = int(not attr(op, "exclude_padding_from_average"))
            if op.type == "avg_pool":
                # Match prepare-fibrenet.py: prevent half-precision accumulation
                # error across a 31x31 window without changing learned weights.
                nodes.append(helper.make_node("Cast", [x], [name + "_input32"], to=TensorProto.FLOAT))
                nodes.append(helper.make_node(kind, [name + "_input32"], [name + "_accum32"], **attributes))
                nodes.append(helper.make_node("Cast", [name + "_accum32"], [name], to=TensorProto.FLOAT16))
                continue
        elif op.type == "concat":
            if attr(op, "interleave"): raise ValueError("Interleaved concatenation is unsupported")
            kind, inputs = "Concat", [a.name for a in op.inputs["values"].arguments]
            attributes = dict(axis=attr(op, "axis"))
        elif op.type == "pad":
            padding = attr(op, "pad")
            mode = attr(op, "mode")
            if mode != "replicate": raise ValueError("Only replicated input edges are expected")
            kind, inputs = "Pad", [x, initializer(name + "_pads", np.array(padding[::2] + padding[1::2], dtype=np.int64))]
            attributes = dict(mode="edge")
        elif op.type == "slice_by_index":
            begin, end = attr(op, "begin"), attr(op, "end")
            for i, masked in enumerate(attr(op, "end_mask", [False] * len(end))):
                if masked: end[i] = np.iinfo(np.int64).max
            if "stride" in op.inputs or "squeeze_mask" in op.inputs or "begin_mask" in op.inputs:
                raise ValueError("Unexpected slice parameters")
            kind, inputs = "Slice", [x, initializer(name + "_begin", np.array(begin, dtype=np.int64)),
                initializer(name + "_end", np.array(end, dtype=np.int64))]
        elif op.type == "upsample_bilinear":
            kind, inputs = "Resize", [x, "", initializer(name + "_scales", np.array(
                [1, 1, attr(op, "scale_factor_height"), attr(op, "scale_factor_width")], dtype=np.float32))]
            attributes = dict(mode="linear", coordinate_transformation_mode="align_corners" if attr(op, "align_corners") else "half_pixel")
        else:
            raise ValueError(f"Unsupported MIL operation: {op.type}")
        nodes.append(helper.make_node(kind, inputs, [name], name=name, **attributes))
    graph = helper.make_graph(nodes, "PURENeg-FibreNet", [helper.make_tensor_value_info("rgb", TensorProto.FLOAT16, [1, 3, 576, 576])],
        [helper.make_tensor_value_info("prob", TensorProto.FLOAT16, [1, 1, 576, 576])], list(initializers.values()),
        value_info=[v for v in information if v.name != "prob"])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], producer_name="OpenRevelare")
    model.ir_version = 9
    helper.set_model_props(model, {"source.spec.sha256": SOURCE_SHA, "source.weights.sha256": WEIGHTS_SHA,
        "precision": "fp16 network and exact source weights; 31x31 average pool accumulates in fp32 in both CoreML and ONNX"})
    onnx.checker.check_model(model, full_check=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    onnx.save(model, output)
    return {"modelSha256": hashlib.sha256(output.read_bytes()).hexdigest(), "nodes": len(nodes), "weights": len(initializers)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("spec", type=Path)
    parser.add_argument("weights", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(json.dumps(convert(args.spec, args.weights, args.output)))
