"""Independent semantic comparison of native and losslessly compacted binary FBX."""
import math
import struct
import sys
import zlib
from pathlib import Path


def read(path):
    data = Path(path).read_bytes()
    assert data[:18] == b"Kaydara FBX Binary"
    version = struct.unpack_from("<I", data, 23)[0]
    header = "<QQQB" if version >= 7500 else "<IIIB"
    header_size = struct.calcsize(header)

    def node(offset):
        end, count, size, name_length = struct.unpack_from(header, data, offset)
        offset += header_size
        if end == 0:
            return None, offset
        name = data[offset:offset + name_length].decode()
        offset += name_length
        props = []
        for _ in range(count):
            kind = chr(data[offset])
            offset += 1
            if kind in "fdlibc":
                length, encoding, byte_count = struct.unpack_from("<III", data, offset)
                offset += 12
                payload = data[offset:offset + byte_count]
                offset += byte_count
                if encoding:
                    assert encoding == 1
                    payload = zlib.decompress(payload)
                assert len(payload) == length * dict(f=4, d=8, l=8, i=4, b=1, c=1)[kind]
            elif kind in "SR":
                length = struct.unpack_from("<I", data, offset)[0]
                offset += 4
                payload = data[offset:offset + length]
                offset += length
            else:
                length = dict(Y=2, C=1, I=4, F=4, D=8, L=8)[kind]
                payload = data[offset:offset + length]
                offset += length
            props.append((kind, payload))
        children = []
        while offset < end:
            child, offset = node(offset)
            if child is None:
                break
            children.append(child)
        assert offset == end
        return [name, props, children], end

    nodes, offset = [], 27
    while True:
        child, offset = node(offset)
        if child is None:
            break
        nodes.append(child)
    return data[:27], nodes, data[offset:]


def child(node, name):
    return next(n for n in node[2] if n[0] == name)


def integer(prop):
    return struct.unpack("<q" if prop[0] == "L" else "<i", prop[1])[0]


def compare(before_path, after_path):
    before_header, before, before_footer = read(before_path)
    after_header, after, after_footer = read(after_path)
    assert before_header == after_header
    assert before_footer[:16] == after_footer[:16] and before_footer[-140:] == after_footer[-140:]
    old_objects = next(n for n in before if n[0] == "Objects")
    new_objects = next(n for n in after if n[0] == "Objects")
    new_ids = {integer(n[1][0]) for n in new_objects[2]}
    connections = next(n for n in before if n[0] == "Connections")
    referenced = {integer(p) for n in connections[2] for p in n[1] if p[0] == "L"}
    removed = [n for n in old_objects[2] if integer(n[1][0]) not in new_ids]
    for n in removed:
        assert n[0] == "AnimationCurve" and integer(n[1][0]) not in referenced
        assert child(n, "KeyTime")[1][0] == ("l", b"")
        assert child(n, "KeyValueFloat")[1][0] == ("f", b"")
    old_objects[2] = [n for n in old_objects[2] if integer(n[1][0]) in new_ids]
    if removed:
        definitions = next(n for n in before if n[0] == "Definitions")
        definition = next(n for n in definitions[2] if n[0] == "ObjectType" and n[1][0] == ("S", b"AnimationCurve"))
        for node in (child(definitions, "Count"), child(definition, "Count")):
            node[1] = [("I", struct.pack("<i", integer(node[1][0]) - len(removed)))]
    stats = dict(constant_curves=0, removed_keys=0, unused_empty_curves=len(removed), unchanged_nodes=0)

    def equal(a, b, path):
        assert a[0] == b[0] and a[1] == b[1], "Changed properties: " + path
        if a[0] == "AnimationCurve":
            av = next((n for n in a[2] if n[0] == "KeyValueFloat"), None)
            bv = next((n for n in b[2] if n[0] == "KeyValueFloat"), None)
            if av != bv:
                assert av is not None and bv is not None
                values = av[1][0][1]
                count = len(values) // 4
                assert count > 2 and math.isfinite(struct.unpack("<f", values[:4])[0])
                assert values == values[:4] * count and bv[1][0] == ("f", values[:4] * 2)
                times = child(a, "KeyTime")[1][0][1]
                unpacked = struct.unpack("<" + "q" * count, times)
                assert all(x < y for x, y in zip(unpacked, unpacked[1:]))
                assert child(b, "KeyTime")[1][0] == ("l", times[:8] + times[-8:])
                assert child(a, "KeyVer")[1][0] == ("I", struct.pack("<i", 4009))
                assert child(a, "KeyAttrFlags")[1][0] == ("i", struct.pack("<i", 0x2108))
                attributes = child(a, "KeyAttrDataFloat")[1][0][1]
                assert len(attributes) == 16 and struct.unpack("<ff", attributes[:8]) == (0.0, 0.0)
                assert child(a, "KeyAttrRefCount")[1][0] == ("i", struct.pack("<i", count))
                assert child(b, "KeyAttrRefCount")[1][0] == ("i", struct.pack("<i", 2))
                for name in ("KeyValueFloat", "KeyTime", "KeyAttrRefCount"):
                    child(a, name)[1] = child(b, name)[1]
                stats["constant_curves"] += 1
                stats["removed_keys"] += count - 2
        assert len(a[2]) == len(b[2]), "Changed child count: " + path
        for old, new in zip(a[2], b[2]):
            equal(old, new, path + "/" + old[0])
        stats["unchanged_nodes"] += 1

    assert len(before) == len(after)
    for a, b in zip(before, after):
        equal(a, b, a[0])
    print("PASS decoded FBX equality; only mathematically constant redundant keys and unreferenced empty curves removed:", stats)


if __name__ == "__main__":
    compare(sys.argv[1], sys.argv[2])
