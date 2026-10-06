#!/usr/bin/env python3
"""
mhy1 / mhy0 容器解析参考实现（零第三方依赖）

从 AnimeStudio 源码中提取加密常量表，独立复现 mhy1 的解密与结构解析，
用于验证 docs/mhy1-file-format.md 的推导是否与真实文件一致。

用法:
    python tools/mhy1_parse.py <path-to-blk>            # 解析并打印结构
    python tools/mhy1_parse.py <path-to-blk> -o outdir  # 同时导出内部文件
    python tools/mhy1_parse.py --selftest               # 只跑 AES/LZ4 自检

说明:
    - LZ4 block 解压为纯 Python 实现，无需 lz4 库。
    - Oodle 压缩（ZZZ 2.0+）无法纯 Python 解压，遇到时会跳过解压但仍报告
      解密后的头部结构（足以验证加密部分正确性）。
"""

import argparse
import ctypes
import os
import re
import struct
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CRYPTO_CS = os.path.join(REPO, "AnimeStudio", "Crypto", "CryptoHelper.cs")
MHYFILE_CS = os.path.join(REPO, "AnimeStudio", "MhyFile.cs")


# --------------------------------------------------------------------------
# 常量表提取
# --------------------------------------------------------------------------

def _parse_byte_array(text, name):
    """从 C# 源码中抓取 `name = new byte[...] { ... }` 的字节表。"""
    m = re.search(
        r"\b%s\b\s*=\s*new\s+byte\[[^\]]*\]\s*\{(.*?)\}\s*;" % re.escape(name),
        text, re.S)
    if not m:
        raise KeyError("未找到常量表: %s" % name)
    body = m.group(1)
    out = []
    for tok in body.replace("\n", " ").split(","):
        tok = tok.strip()
        if not tok:
            continue
        out.append(int(tok, 16) if tok.lower().startswith("0x") else int(tok, 10))
    return bytes(out)


def load_constants():
    with open(CRYPTO_CS, "r", encoding="utf-8-sig") as f:
        crypto = f.read()
    with open(MHYFILE_CS, "r", encoding="utf-8-sig") as f:
        mhyfile = f.read()

    c = {
        "SBOX":      _parse_byte_array(crypto, "GISBox"),          # 1024
        "SHIFT_ROW": _parse_byte_array(crypto, "GIMhyShiftRow"),   # 48
        "MHY_KEY":   _parse_byte_array(crypto, "GIMhyKey"),        # 8
        "MHY_MUL":   _parse_byte_array(crypto, "GIMhyMul"),        # 8
        "GF_EXP":    _parse_byte_array(crypto, "GF256Exp"),        # 256
        "GF_LOG":    _parse_byte_array(crypto, "GF256Log"),        # 256
        "RC4_SBOX":  _parse_byte_array(mhyfile, "Key"),            # 256
    }
    expect = {"SBOX": 1024, "SHIFT_ROW": 48, "MHY_KEY": 8, "MHY_MUL": 8,
              "GF_EXP": 256, "GF_LOG": 256, "RC4_SBOX": 256}
    for k, n in expect.items():
        if len(c[k]) != n:
            raise ValueError("常量表 %s 长度异常: %d != %d" % (k, len(c[k]), n))
    return c


# --------------------------------------------------------------------------
# 第一层：白盒 AES-like 变换
# --------------------------------------------------------------------------

def gf256_mul(K, a, b):
    if a == 0 or b == 0:
        return 0
    return K["GF_EXP"][(K["GF_LOG"][a] + K["GF_LOG"][b]) % 0xFF]


def descramble_chunk(K, buf, start, length=16):
    """3 轮白盒变换，就地修改 buf[start:start+length]。"""
    view = bytearray(buf[start:start + length])
    for i in range(3):
        vector = bytearray(length)
        for j in range(length):
            k = K["SHIFT_ROW"][(2 - i) * 0x10 + j]
            idx = j % 8
            v = gf256_mul(K, K["MHY_MUL"][idx], view[k % length])
            vector[j] = K["MHY_KEY"][idx] ^ K["SBOX"][(j % 4) * 0x100 | v]
        view = vector
    buf[start:start + length] = view


# --------------------------------------------------------------------------
# 第二层：AES-128-ECB 单块加密（纯 Python）
# --------------------------------------------------------------------------

_AES_SBOX = None
_AES_RCON = (0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36)


def _build_aes_sbox():
    global _AES_SBOX
    if _AES_SBOX is not None:
        return _AES_SBOX
    p = q = 1
    sbox = [0] * 256
    while True:
        p = p ^ ((p << 1) & 0xFF) ^ (0x1B if p & 0x80 else 0)
        q ^= q << 1
        q ^= q << 2
        q ^= q << 4
        q &= 0xFF
        if q & 0x80:
            q ^= 0x09
        x = q ^ ((q << 1) | (q >> 7)) ^ ((q << 2) | (q >> 6)) \
              ^ ((q << 3) | (q >> 5)) ^ ((q << 4) | (q >> 4))
        sbox[p] = (x ^ 0x63) & 0xFF
        if p == 1:
            break
    sbox[0] = 0x63
    _AES_SBOX = bytes(sbox)
    return _AES_SBOX


def _xtime(a):
    a <<= 1
    return (a ^ 0x1B) & 0xFF if a & 0x100 else a


def _aes_expand_key(key):
    sbox = _build_aes_sbox()
    w = [list(key[i * 4:i * 4 + 4]) for i in range(4)]
    for i in range(4, 44):
        t = list(w[i - 1])
        if i % 4 == 0:
            t = t[1:] + t[:1]
            t = [sbox[b] for b in t]
            t[0] ^= _AES_RCON[i // 4 - 1]
        w.append([w[i - 4][j] ^ t[j] for j in range(4)])
    return [bytes(b for word in w[r * 4:r * 4 + 4] for b in word) for r in range(11)]


def aes128_encrypt_block(key, block):
    """AES-128 单块加密，无填充。"""
    sbox = _build_aes_sbox()
    rk = _aes_expand_key(key)
    s = bytearray(a ^ b for a, b in zip(block, rk[0]))

    for rnd in range(1, 11):
        s = bytearray(sbox[b] for b in s)
        # ShiftRows（列主序：字节 i 的行号为 i%4）
        t = bytearray(16)
        for c in range(4):
            for r in range(4):
                t[r + 4 * c] = s[r + 4 * ((c + r) % 4)]
        s = t
        if rnd != 10:
            for c in range(4):
                col = s[4 * c:4 * c + 4]
                u = col[0] ^ col[1] ^ col[2] ^ col[3]
                a0 = col[0]
                s[4 * c + 0] ^= u ^ _xtime(col[0] ^ col[1])
                s[4 * c + 1] ^= u ^ _xtime(col[1] ^ col[2])
                s[4 * c + 2] ^= u ^ _xtime(col[2] ^ col[3])
                s[4 * c + 3] ^= u ^ _xtime(col[3] ^ a0)
        s = bytearray(a ^ b for a, b in zip(s, rk[rnd]))
    return bytes(s)


# --------------------------------------------------------------------------
# 第三层：RC4 变体
# --------------------------------------------------------------------------

def rc4_variant(K, buf, start, end, key, operation):
    """就地解密 buf[start:end]。key/operation 各 8 字节。"""
    S = bytearray(K["RC4_SBOX"])
    T = bytes(key[i % len(key)] for i in range(256))

    j = 0
    for i in range(256):
        j = (j + S[i] + T[i]) % 256
        S[i], S[j] = S[j], S[i]

    i = j = 0
    for n in range(start, end):
        i = (i + 1) % 256
        j = (j + S[i]) % 256
        S[i], S[j] = S[j], S[i]
        k = S[(S[j] + S[i]) % 256]
        op = operation[i % len(operation)] % 3
        if op == 0:
            buf[n] ^= k
        elif op == 1:
            buf[n] = (buf[n] - k) & 0xFF
        else:
            buf[n] = (buf[n] + k) & 0xFF


# --------------------------------------------------------------------------
# Descramble 调度
# --------------------------------------------------------------------------

def descramble(K, buf, block_size, entry_size):
    """ZZZ (mhynewec) 路径，就地解密。"""
    rounded = (entry_size + 0xF) // 0x10 * 0x10

    descramble_chunk(K, buf, 4, 16)
    sig = bytes(buf[4:12])
    if sig != b"mhynewec":
        raise ValueError("签名校验失败，期望 mhynewec，实际 %r" % sig)
    if block_size <= 35:
        return

    descramble_chunk(K, buf, 20, 16)

    seed = bytes(buf[0:16])
    data = aes128_encrypt_block(seed, bytes(buf[20:36]))
    buf[20:36] = data
    for i in range(4):
        buf[i] ^= data[i]

    rc4_variant(K, buf, 20 + rounded, block_size,
                key=bytes(buf[20:28]), operation=bytes(buf[28:36]))


# --------------------------------------------------------------------------
# 字段编码
# --------------------------------------------------------------------------

def read_mhy_int(b, o):
    return b[o + 2] | (b[o + 4] << 8) | (b[o + 0] << 16) | (b[o + 5] << 24)


def read_mhy_uint(b, o):
    return (b[o + 1] | (b[o + 6] << 8) | (b[o + 3] << 16) | (b[o + 2] << 24)) & 0xFFFFFFFF


def read_mhy_string(b, o):
    field = b[o:o + 0x105]
    z = field.find(b"\0")
    return (field if z < 0 else field[:z]).decode("utf-8", "replace")


# --------------------------------------------------------------------------
# LZ4 block 解压（纯 Python）
# --------------------------------------------------------------------------

def lz4_decompress(src, dst_size):
    dst = bytearray(dst_size)
    s = d = 0
    n = len(src)
    while s < n:
        token = src[s]; s += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[s]; s += 1
                lit += b
                if b != 255:
                    break
        if lit:
            dst[d:d + lit] = src[s:s + lit]
            s += lit; d += lit
        if s >= n:
            break
        offset = src[s] | (src[s + 1] << 8); s += 2
        if offset == 0:
            raise ValueError("LZ4: offset 为 0")
        mlen = (token & 0xF) + 4
        if (token & 0xF) == 15:
            while True:
                b = src[s]; s += 1
                mlen += b
                if b != 255:
                    break
        p = d - offset
        if p < 0:
            raise ValueError("LZ4: 匹配越界")
        for _ in range(mlen):          # 逐字节复制以支持重叠
            dst[d] = dst[p]
            d += 1; p += 1
    if d != dst_size:
        raise ValueError("LZ4: 输出 %d 字节，期望 %d" % (d, dst_size))
    return bytes(dst)


# --------------------------------------------------------------------------
# Oodle 解压（可选，经 ctypes 调用仓库自带的 AnimeStudio.Ooz.dll）
# --------------------------------------------------------------------------

_OOZ = None
_OOZ_TRIED = False


def _load_ooz():
    """定位并加载 AnimeStudio.Ooz.dll，失败返回 None。"""
    global _OOZ, _OOZ_TRIED
    if _OOZ_TRIED:
        return _OOZ
    _OOZ_TRIED = True

    import ctypes
    candidates = [
        os.path.join(REPO, "AnimeStudio.Libraries", "AnimeStudio.Ooz.dll"),
        os.path.join(REPO, "dist", "net10.0-windows", "bin", "AnimeStudio.Ooz.dll"),
        os.path.join(REPO, "dist", "net9.0-windows", "bin", "AnimeStudio.Ooz.dll"),
    ]
    for dll in candidates:
        if not os.path.isfile(dll):
            continue
        try:
            lib = ctypes.CDLL(dll)
            fn = lib.Ooz_Decompress
            fn.restype = ctypes.c_int
            fn.argtypes = [
                ctypes.c_char_p, ctypes.c_int,      # compressed, size
                ctypes.c_char_p, ctypes.c_int,      # decompressed, size
                ctypes.c_int, ctypes.c_int, ctypes.c_int,        # fuzzSafe, checkCRC, verbosity
                ctypes.c_void_p, ctypes.c_int,      # rawBuffer, rawBufferSize
                ctypes.c_void_p, ctypes.c_void_p,   # fpCallback, callbackUserData
                ctypes.c_void_p, ctypes.c_void_p,   # decoderMemory, decoderMemorySize
                ctypes.c_int,                       # threadPhase
            ]
            _OOZ = fn
            return _OOZ
        except Exception:
            continue
    return None


def oodle_decompress(src, dst_size):
    fn = _load_ooz()
    if fn is None:
        raise Mhy1Error("Oodle 压缩，且未找到 AnimeStudio.Ooz.dll（加密层已验证通过）")
    dst = ctypes.create_string_buffer(dst_size)
    n = fn(src, len(src), dst, dst_size, 1, 0, 0, None, 0, None, None, None, None, 3)
    if n != dst_size:
        raise Mhy1Error("Oodle 解压返回 %d，期望 %d" % (n, dst_size))
    return dst.raw[:dst_size]


def decompress(src, dst_size, is_oodle):
    return oodle_decompress(src, dst_size) if is_oodle else lz4_decompress(src, dst_size)


# --------------------------------------------------------------------------
# mhy1 解析
# --------------------------------------------------------------------------

class Mhy1Error(Exception):
    pass


def parse_mhy1(K, data, pos, verbose=True):
    sig = bytes(data[pos:pos + 4])
    if sig not in (b"mhy1", b"mhy0"):
        raise Mhy1Error("位置 0x%X 不是 mhy 文件（%r）" % (pos, sig))

    is_v1 = (sig == b"mhy1")
    # entrySize 两代相同(28/8)，差异在载荷 offset 与加密覆盖长度
    hdr_entry, blk_entry = 28, 8
    hdr_off, blk_off = (48, 28) if is_v1 else (32, 12)
    hdr_bs = 128 if is_v1 else 0x39
    blk_bs_cap = 128 if is_v1 else 0x21

    cbi_size = struct.unpack_from("<I", data, pos + 4)[0]
    bi = bytearray(data[pos + 8: pos + 8 + cbi_size])
    if len(bi) != cbi_size:
        raise Mhy1Error("BlocksInfo 截断")

    descramble(K, bi, min(len(bi), hdr_bs), hdr_entry)

    check = bytes(bi[0:4])
    uncompressed_size = read_mhy_uint(bi, hdr_off)
    compressed = bytes(bi[hdr_off + 7:])
    is_oodle = len(compressed) > 0 and compressed[0] == 0x8C

    if verbose:
        print("  signature          : %s" % sig.decode())
        print("  compressedBIsize   : 0x%X (%d)" % (cbi_size, cbi_size))
        print("  check bytes        : %s" % check.hex().upper())
        print("  uncompressedBIsize : 0x%X (%d)" % (uncompressed_size, uncompressed_size))
        print("  compression        : %s (first byte 0x%02X)"
              % ("Oodle" if is_oodle else "LZ4", compressed[0] if compressed else 0))

    plain = decompress(compressed, uncompressed_size, is_oodle)

    o = 0
    nodes_count = read_mhy_int(plain, o); o += 6
    if not (0 <= nodes_count < 100000):
        raise Mhy1Error("nodesCount 异常: %d" % nodes_count)
    nodes = []
    for _ in range(nodes_count):
        path = read_mhy_string(plain, o); o += 0x105
        serialized = plain[o] != 0;       o += 1
        n_off = read_mhy_int(plain, o);   o += 6
        n_size = read_mhy_uint(plain, o); o += 7
        nodes.append((path, serialized, n_off, n_size))

    blocks_count = read_mhy_int(plain, o); o += 6
    if not (0 <= blocks_count < 100000):
        raise Mhy1Error("blocksCount 异常: %d" % blocks_count)
    blocks = []
    for _ in range(blocks_count):
        c = read_mhy_int(plain, o);  o += 6
        u = read_mhy_uint(plain, o); o += 7
        blocks.append((c, u))

    expected = 6 + 275 * nodes_count + 6 + 13 * blocks_count
    if verbose:
        print("  nodes / blocks     : %d / %d" % (nodes_count, blocks_count))
        print("  BI 明文长度         : %d (公式预测 %d) %s"
              % (len(plain), expected, "✓" if len(plain) == expected else "✗ 不匹配"))

    stream = bytearray()
    p = pos + 8 + cbi_size
    for idx, (c, u) in enumerate(blocks):
        if c < 0x10:
            raise Mhy1Error("Block %d 压缩大小过小: %d" % (idx, c))
        blk = bytearray(data[p:p + c]); p += c
        if len(blk) != c:
            raise Mhy1Error("Block %d 截断" % idx)
        descramble(K, blk, min(c, blk_bs_cap), blk_entry)
        stream += decompress(bytes(blk[blk_off:]), u, is_oodle)

    files = []
    for path, serialized, n_off, n_size in nodes:
        files.append((path, serialized, bytes(stream[n_off:n_off + n_size])))

    total = 8 + cbi_size + sum(c for c, _ in blocks)
    if verbose:
        print("  TotalSize          : 0x%X (%d)" % (total, total))
        for path, serialized, blob in files:
            print("    - %-60s %9d B  %s"
                  % (path[:60], len(blob), "SerializedFile" if serialized else "resource"))

    while p < len(data) and data[p] == 0:
        p += 1
    return files, p


def parse_container(K, path, outdir=None):
    with open(path, "rb") as f:
        data = f.read()
    print("文件: %s (%d 字节)\n" % (path, len(data)))

    pos, idx, ok, fail = 0, 0, 0, 0
    while pos < len(data):
        if bytes(data[pos:pos + 4]) not in (b"mhy1", b"mhy0"):
            print("[!] 偏移 0x%X 处签名不匹配，停止扫描" % pos)
            break
        print("── mhy 子文件 #%d @ 0x%X ──" % (idx, pos))
        try:
            files, nxt = parse_mhy1(K, data, pos)
            ok += 1
            if outdir:
                for name, _, blob in files:
                    dst = os.path.join(outdir, "%04d_%s" % (idx, os.path.basename(name)))
                    os.makedirs(os.path.dirname(dst) or ".", exist_ok=True)
                    with open(dst, "wb") as f:
                        f.write(blob)
        except Mhy1Error as e:
            print("  [跳过] %s" % e)
            fail += 1
            break
        except Exception as e:
            print("  [错误] %s: %s" % (type(e).__name__, e))
            fail += 1
            break
        print()
        if nxt <= pos:
            break
        pos = nxt
        idx += 1

    print("\n汇总: 成功 %d 个子文件，失败 %d 个" % (ok, fail))
    return fail == 0


# --------------------------------------------------------------------------
# 自检
# --------------------------------------------------------------------------

def selftest():
    ok = True

    # FIPS-197 AES-128 测试向量
    key = bytes.fromhex("000102030405060708090a0b0c0d0e0f")
    pt = bytes.fromhex("00112233445566778899aabbccddeeff")
    want = bytes.fromhex("69c4e0d86a7b0430d8cdb78070b4c55a")
    got = aes128_encrypt_block(key, pt)
    print("AES-128 (FIPS-197)  : %s" % ("✓" if got == want else "✗ 得到 " + got.hex()))
    ok &= (got == want)

    # LZ4 block：17 字节纯字面量（token 高 4 位 = 15，续读 2 → 17）
    src = bytes([0xF0, 0x02]) + b"X" * 17
    got = lz4_decompress(src, 17)
    print("LZ4 字面量解压       : %s" % ("✓" if got == b"X" * 17 else "✗"))
    ok &= (got == b"X" * 17)

    # LZ4 匹配复制：4 字面量 "ABCD" + offset=4/len=4 → "ABCDABCD"
    src = bytes([0x40]) + b"ABCD" + bytes([0x04, 0x00])
    got = lz4_decompress(src, 8)
    print("LZ4 匹配复制         : %s" % ("✓" if got == b"ABCDABCD" else "✗ " + repr(got)))
    ok &= (got == b"ABCDABCD")

    # LZ4 重叠复制：offset=1 的自引用游程 → "AAAAA"
    src = bytes([0x10]) + b"A" + bytes([0x01, 0x00])
    got = lz4_decompress(src, 5)
    print("LZ4 重叠复制         : %s" % ("✓" if got == b"AAAAA" else "✗ " + repr(got)))
    ok &= (got == b"AAAAA")

    # 常量表加载
    try:
        K = load_constants()
        print("常量表加载           : ✓ SBox=%d ShiftRow=%d Key=%d Mul=%d RC4=%d"
              % (len(K["SBOX"]), len(K["SHIFT_ROW"]), len(K["MHY_KEY"]),
                 len(K["MHY_MUL"]), len(K["RC4_SBOX"])))
        # 交叉验证：RC4 S 盒应与 Blb3RC4Key 相同
        with open(CRYPTO_CS, "r", encoding="utf-8-sig") as f:
            blb3 = _parse_byte_array(f.read(), "Blb3RC4Key")
        same = (blb3 == K["RC4_SBOX"])
        print("RC4 S盒 == Blb3RC4Key: %s" % ("✓ 逐字节相同（跨格式复用已确认）" if same else "✗ 不同"))
        ok &= same
    except Exception as e:
        print("常量表加载           : ✗ %s" % e)
        ok = False

    print("\n自检%s" % ("通过" if ok else "失败"))
    return ok


def main():
    # Windows 控制台默认 GBK，无法输出 ✓/✗ 等字符
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    ap = argparse.ArgumentParser(description="mhy1/mhy0 容器解析参考实现")
    ap.add_argument("path", nargs="?", help=".blk 文件路径")
    ap.add_argument("-o", "--outdir", help="导出内部文件到该目录")
    ap.add_argument("--selftest", action="store_true", help="只跑自检")
    args = ap.parse_args()

    if args.selftest or not args.path:
        return 0 if selftest() else 1

    K = load_constants()
    if args.outdir:
        os.makedirs(args.outdir, exist_ok=True)
    return 0 if parse_container(K, args.path, args.outdir) else 1


if __name__ == "__main__":
    sys.exit(main())
