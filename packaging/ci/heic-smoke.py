"""Exercise both HEIC routes on every release platform, including CPU-only runners."""
import ctypes as c
import json
from pathlib import Path
import struct
import sys

rid = sys.argv[1]
suffix = ".dll" if rid.startswith("win-") else ".dylib" if rid.startswith("osx-") else ".so"
prefix = "" if rid.startswith("win-") else "lib"
path = Path(__file__).resolve().parents[2] / "native" / rid / (prefix + "OpenRevelare.Heif.Native" + suffix)
library = c.CDLL(str(path))
encode = library.or_heif_encode_exif_accelerated
encode.argtypes = [c.c_void_p, c.c_int, c.c_int, c.c_int, c.c_int,
                   c.c_void_p, c.c_int, c.c_void_p, c.c_int, c.c_void_p, c.c_int,
                   c.c_int, c.POINTER(c.c_int), c.POINTER(c.c_void_p), c.POINTER(c.c_size_t), c.c_void_p, c.c_int]
library.or_heif_free.argtypes = [c.c_void_p]


def properties(data, start=0, end=None):
    end = len(data) if end is None else end
    result = []
    while start < end:
        size, kind = struct.unpack_from(">I4s", data, start)
        assert 8 <= size <= end - start, "Invalid HEIC box size"
        body = start + 8
        if kind == b"meta":
            result += properties(data, body + 4, start + size)
        elif kind in (b"iprp", b"ipco"):
            result += properties(data, body, start + size)
        else:
            result.append((kind, data[body:start + size]))
        start += size
    return result


for width, height in [(33, 17), (512, 512), (1031, 769)]:
    # Odd grid dimensions exercise both right and bottom edge replication.
    pixels = (c.c_uint16 * (width * height * 3))()
    for i in range(len(pixels)):
        pixels[i] = (i * 17 + i // (width * 3)) % 1024
    for hdr in (0, 1):
        for allow_hardware in (0, 1):
            output, size, used, error = c.c_void_p(), c.c_size_t(), c.c_int(), c.create_string_buffer(1024)
            try:
                status = encode(pixels, width, height, 90, hdr, None, 0, None, 0, None, 0,
                                allow_hardware, c.byref(used), c.byref(output), c.byref(size), error, len(error))
                assert status == 0, error.value.decode("utf-8", errors="replace")
                assert output and size.value > 0
                if not allow_hardware:
                    assert used.value == 0, "GPU-off route used hardware"
                data = c.string_at(output, size.value)
                props = properties(data)
                configs = [value for kind, value in props if kind == b"hvcC"]
                assert configs and all((value[1] & 31) == 2 and (value[17] & 7) == 2 for value in configs), "Expected real Main10, 10-bit HEVC"
                assert any((kind == b"ispe" and struct.unpack_from(">II", value, 4) == (width, height))
                           or (kind == b"clap" and struct.unpack_from(">IIII", value) == (width, 1, height, 1))
                           for kind, value in props), "HEIC dimensions changed"
                nclx = [value for kind, value in props if kind == b"colr" and value[:4] == b"nclx"]
                assert nclx and all(struct.unpack_from(">HHH", value, 4) == ((9, 16, 9) if hdr else (2, 2, 1))
                                    and value[10] & 128 for value in nclx), "HEIC color/range metadata changed"
                print(json.dumps(dict(rid=rid, width=width, height=height, hdr=bool(hdr),
                                      hardware_allowed=bool(allow_hardware), hardware_used=bool(used.value),
                                      fallback=error.value.decode("utf-8", errors="replace"))), flush=True)
            finally:
                if output:
                    library.or_heif_free(output)
