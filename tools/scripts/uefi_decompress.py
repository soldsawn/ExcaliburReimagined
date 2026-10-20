"""UEFI (EFI 1.1 / Tiano) Decompress, saf Python. Kanonik Huffman ile; MakeTable yerine bit-bit çözer.
Kullanım: python -I uefi_decompress.py <dosya> <ofset> [out]   (ofset = 8 baytlık başlığın başı: comp_size, orig_size)
"""
import struct
import sys


class Bits:
    def __init__(self, data, pos=0):
        self.d, self.p = data, pos * 8
        self.end = len(data) * 8

    def peek(self, n):
        if n == 0:
            return 0
        byte = self.p >> 3
        chunk = int.from_bytes(self.d[byte:byte + 5].ljust(5, b"\0"), "big")
        return (chunk >> (40 - (self.p & 7) - n)) & ((1 << n) - 1)

    def get(self, n):
        v = self.peek(n)
        self.p += n
        return v


def build(lengths):
    """Kanonik kod: (len, kod) -> sembol. Sıra: uzunluk, sonra sembol indeksi."""
    count = [0] * 17
    for l in lengths:
        count[l] += 1
    count[0] = 0
    first, code = [0] * 18, 0
    for l in range(1, 17):
        code = (code + count[l - 1]) << 1
        first[l] = code
    nxt = first[:]
    table = {}
    for sym, l in enumerate(lengths):
        if l:
            table[(l, nxt[l])] = sym
            nxt[l] += 1
    return table


def sym_read(b, table, single):
    if single is not None:
        return single
    code = 0
    for l in range(1, 17):
        code = (code << 1) | b.get(1)
        s = table.get((l, code))
        if s is not None:
            return s
    raise ValueError("kötü Huffman kodu")


def decompress(src, pos, pbit, np_):
    comp, orig = struct.unpack_from("<II", src, pos)
    b = Bits(src, pos + 8)
    out = bytearray()
    NC, NT, TBIT, CBIT = 510, 19, 5, 9

    def read_pt(nn, nbit, special):
        n = b.get(nbit)
        if n == 0:
            return None, b.get(nbit), [0] * nn
        ln, i = [0] * nn, 0
        while i < n:
            c = b.peek(3)
            if c == 7:
                m = 1 << 12
                v = b.peek(16)
                c = 7
                while v & m:
                    m >>= 1
                    c += 1
                b.p += c - 3
            else:
                b.p += 3
            ln[i] = c
            i += 1
            if i == special:
                for _ in range(b.get(2)):
                    ln[i] = 0
                    i += 1
        return build(ln), None, ln

    block = 0
    while len(out) < orig:
        if block == 0:
            block = b.get(16)
            pt, pt1, _ = read_pt(NT, TBIT, 3)
            n = b.get(CBIT)
            if n == 0:
                ct, ct1 = None, b.get(CBIT)
            else:
                cl, i = [0] * NC, 0
                while i < n:
                    c = sym_read(b, pt, pt1)
                    if c <= 2:
                        c = 1 if c == 0 else (b.get(4) + 3 if c == 1 else b.get(CBIT) + 20)
                        i += c
                    else:
                        cl[i] = c - 2
                        i += 1
                ct, ct1 = build(cl), None
            pp, pp1, _ = read_pt(np_, pbit, -1)
        block -= 1
        c = sym_read(b, ct, ct1)
        if c < 256:
            out.append(c)
        else:
            n = c - 253
            v = sym_read(b, pp, pp1)
            if v > 1:
                v = (1 << (v - 1)) + b.get(v - 1)
            d = v + 1
            for _ in range(n):
                out.append(out[-d])
                if len(out) >= orig:
                    break
    return bytes(out[:orig]), 8 + comp


if __name__ == "__main__":
    data = open(sys.argv[1], "rb").read()
    off = int(sys.argv[2], 0)
    for name, pbit, np_ in (("efi", 4, 14), ("tiano", 5, 20)):
        try:
            res, used = decompress(data, off, pbit, np_)
            print(name, "OK", len(res), res[:4])
            if len(sys.argv) > 3:
                open(sys.argv[3], "wb").write(res)
            break
        except Exception as e:
            print(name, "hata", e)
