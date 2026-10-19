"""Repair AnimeStudio FBXs exported with Unity meters mislabeled as centimeters.

Write a new directory tree, retaining textures and changing only the two global
unit doubles. Meshes, skeletons, bind poses, morphs and animation keys stay intact.
"""
import argparse
import math
import shutil
import struct
from pathlib import Path


def repair(data):
    if data[:23] != b"Kaydara FBX Binary  \x00\x1a\x00":
        raise ValueError("Expected a binary FBX exported by AnimeStudio")
    version = struct.unpack_from("<I", data, 23)[0]
    header = "<QQQB" if version >= 7500 else "<IIIB"
    header_size = struct.calcsize(header)
    units = {}

    def node(offset, path=()):
        end, count, size, name_length = struct.unpack_from(header, data, offset)
        offset += header_size
        if end == 0:
            return None
        if end > len(data) or end <= offset:
            raise ValueError("Invalid FBX node range")
        name = data[offset:offset + name_length].decode("utf-8")
        offset += name_length
        path = (*path, name)
        property_end = offset + size
        properties = []
        for _ in range(count):
            kind = chr(data[offset])
            offset += 1
            payload = offset
            value = None
            if kind in "SR":
                length = struct.unpack_from("<I", data, offset)[0]
                offset += 4
                if kind == "S":
                    value = data[offset:offset + length].decode("utf-8")
                offset += length
            elif kind in "fdlibc":
                _, _, length = struct.unpack_from("<III", data, offset)
                offset += 12 + length
            elif kind in "YCIFDL":
                fmt = dict(Y="h", C="?", I="i", F="f", D="d", L="q")[kind]
                value = struct.unpack_from("<" + fmt, data, offset)[0]
                offset += struct.calcsize(fmt)
            else:
                raise ValueError("Unknown FBX property type: " + kind)
            properties.append((kind, value, payload))
        if offset != property_end:
            raise ValueError("Invalid FBX property length")
        if path == ("GlobalSettings", "Properties70", "P") and properties:
            key = properties[0][1]
            if key in ("UnitScaleFactor", "OriginalUnitScaleFactor"):
                if key in units or len(properties) != 5 or properties[-1][0] != "D":
                    raise ValueError("Invalid FBX unit property")
                units[key] = properties[-1][1:]
        while offset < end - header_size:
            next_offset = node(offset, path)
            if next_offset is None:
                break
            offset = next_offset
        return end

    offset = 27
    while True:
        next_offset = node(offset)
        if next_offset is None:
            break
        offset = next_offset
    if set(units) != {"UnitScaleFactor", "OriginalUnitScaleFactor"}:
        raise ValueError("Missing FBX units")
    if all(math.isclose(value, 100, rel_tol=0, abs_tol=1e-9) for value, _ in units.values()):
        return data, False
    if any(value != 1 for value, _ in units.values()):
        raise ValueError("Expected old AnimeStudio unit=1; refusing to alter custom units")
    result = bytearray(data)
    for _, position in units.values():
        struct.pack_into("<d", result, position, 100)
    # Prove every byte outside these global settings is retained.
    restored = bytearray(result)
    for _, position in units.values():
        struct.pack_into("<d", restored, position, 1)
    assert restored == data
    return bytes(result), True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path, help="New output directory; source stays intact")
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    output = args.output.resolve()
    if not source.is_dir() or output.exists() or output.is_relative_to(source):
        parser.error("Source must be a directory and output must be new and outside source")
    files = sorted(source.rglob("*"))
    replacements = {}
    repaired = 0
    # Validate all FBXs before publishing anything.
    for file in files:
        if file.is_file() and file.suffix.lower() == ".fbx":
            replacements[file], changed = repair(file.read_bytes())
            repaired += changed
    if not replacements:
        parser.error("No FBX files found")
    output.mkdir(parents=True)
    for file in files:
        destination = output / file.relative_to(source)
        if file.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
        elif file in replacements:
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(replacements[file])
        else:
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(file, destination)
    print(f"Repaired {repaired}/{len(replacements)} FBXs (units only); copied accompanying files to {output}")


if __name__ == "__main__":
    main()
