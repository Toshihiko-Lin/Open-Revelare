#!/usr/bin/env python3
"""Adapt the pinned CoreML output reader for Intel Macs without Swift Float16."""
import argparse
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("source", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
source = args.source.read_text(encoding="utf8")
old = """let p = prob.dataPointer.bindMemory(to: Float16.self, capacity: prob.count)
                value = { Float(p[$0]) }"""
new = """let p = prob.dataPointer.bindMemory(to: UInt16.self, capacity: prob.count)
                value = { binary16ToFloat(p[$0]) }"""
if source.count(old) != 1: raise SystemExit("Unexpected pinned CoreML output reader")
helper = """
// IEEE binary16 conversion; Swift Float16 itself is unavailable on Intel macOS.
private func binary16ToFloat(_ bits: UInt16) -> Float {
    let sign = UInt32(bits & 0x8000) << 16
    let exponent = UInt32((bits >> 10) & 31)
    let fraction = UInt32(bits & 1023)
    if exponent == 0 {
        let magnitude = Float(fraction) * 0.000000059604644775390625
        return sign == 0 ? magnitude : -magnitude
    }
    let expanded = exponent == 31 ? UInt32(255) : exponent + 112
    return Float(bitPattern: sign | (expanded << 23) | (fraction << 13))
}
"""
args.output.write_text(source.replace(old, new) + helper, encoding="utf8")
