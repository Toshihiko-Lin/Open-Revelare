"""
Read a DNG's ColorMatrix2 tag and print the entry to paste into CameraMatrixFallback.

Why this exists: the bundled LibRaw (0.21.x) carries 1181 cameras and its table stops before
several current bodies — the OM System OM-5 among them. For those, LibRaw parses the raw file
fine but returns an identity colour matrix, i.e. "no colorimetry", and the pipeline falls back to
treating camera-native RGB as though it were sRGB. See docs/CALIBRATION.md.

The fix is a hand-maintained fallback table, and the entries have to be MEASURED. This script is
how: convert one frame from the camera with Adobe DNG Converter, run it through here, and paste
what it prints. The number then traces to a file you can re-read, not to anybody's memory.

Usage:
    python3 read_dng_matrix.py /path/to/converted.dng

Pure standard library — DNG is TIFF, and the two tags needed are plain rationals.
"""
import struct
import sys

# DNG/TIFF tags we care about.
TAG_MAKE = 271
TAG_MODEL = 272
TAG_COLOR_MATRIX_1 = 50721
TAG_COLOR_MATRIX_2 = 50722
TAG_CALIBRATION_ILLUMINANT_2 = 50779

TYPE_SIZES = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8}

ILLUMINANTS = {17: "Standard A", 18: "Standard B", 19: "Standard C",
               20: "D55", 21: "D65", 22: "D75", 23: "D50", 24: "ISO studio tungsten"}


def read_ifd(data, offset, endian):
    """Returns {tag: (type, count, value_bytes)} for one IFD, plus the next-IFD offset."""
    (count,) = struct.unpack(endian + "H", data[offset:offset + 2])
    entries = {}
    for i in range(count):
        p = offset + 2 + i * 12
        tag, typ, cnt = struct.unpack(endian + "HHI", data[p:p + 8])
        size = TYPE_SIZES.get(typ, 1) * cnt
        if size <= 4:
            raw = data[p + 8:p + 8 + size]
        else:
            (voff,) = struct.unpack(endian + "I", data[p + 8:p + 12])
            raw = data[voff:voff + size]
        entries[tag] = (typ, cnt, raw)
    (nxt,) = struct.unpack(endian + "I", data[offset + 2 + count * 12:offset + 6 + count * 12])
    return entries, nxt


def srational(raw, endian, count):
    out = []
    for i in range(count):
        num, den = struct.unpack(endian + "ii", raw[i * 8:(i + 1) * 8])
        out.append(num / den if den else 0.0)
    return out


def ascii_val(raw):
    return raw.split(b"\x00")[0].decode("ascii", "replace").strip()


def main(path):
    data = open(path, "rb").read()
    if data[:2] == b"II":
        endian = "<"
    elif data[:2] == b"MM":
        endian = ">"
    else:
        sys.exit("not a TIFF/DNG file")

    (first,) = struct.unpack(endian + "I", data[4:8])
    entries, _ = read_ifd(data, first, endian)

    make = ascii_val(entries[TAG_MAKE][2]) if TAG_MAKE in entries else "?"
    model = ascii_val(entries[TAG_MODEL][2]) if TAG_MODEL in entries else "?"

    tag = TAG_COLOR_MATRIX_2 if TAG_COLOR_MATRIX_2 in entries else TAG_COLOR_MATRIX_1
    if tag not in entries:
        sys.exit("no ColorMatrix tag — is this really a DNG (not a renamed raw)?")

    typ, cnt, raw = entries[tag]
    if cnt != 9:
        sys.exit(f"expected 9 values in the colour matrix, found {cnt}")
    m = srational(raw, endian, 9)

    illum = ""
    if TAG_CALIBRATION_ILLUMINANT_2 in entries:
        (v,) = struct.unpack(endian + "H", entries[TAG_CALIBRATION_ILLUMINANT_2][2][:2])
        illum = ILLUMINANTS.get(v, f"code {v}")

    which = "ColorMatrix2" if tag == TAG_COLOR_MATRIX_2 else "ColorMatrix1"
    basename = path.replace("\\", "/").split("/")[-1]
    print(f"camera      : {make} {model}")
    print(f"tag         : {which}" + (f"  (illuminant {illum})" if illum else ""))
    print(f"direction   : XYZ -> camera native RGB")
    print()
    print("Paste into src/OpenRevelare.Core/CameraMatrixFallback.cs, inside ColorMatrix2:")
    print()
    print(f'            // {make} {model} — {which}'
          + (f", illuminant {illum}" if illum else "")
          + f", read from {basename}")
    print(f'            ["{make} {model}"] = new[,]')
    print("            {")
    for r in range(3):
        row = ", ".join(f"{m[r * 3 + c]:.6f}" for c in range(3))
        print(f"                {{ {row} }},")
    print("            },")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
