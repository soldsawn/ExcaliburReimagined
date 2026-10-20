"""BIOS ROM içinde ACPI tablolarını (DSDT/SSDT) bul: hem ham hem LZMA sıkıştırılmış FFS bölümlerinde.
Kullanım: python -I rom_find_acpi.py <rom> <çıktı_klasörü>
"""
import lzma
import os
import re
import struct
import sys
import uuid




def tables(buf, where, out, seen):
    for m in re.finditer(rb"DSDT|SSDT", buf):
        i = m.start()
        if i + 36 > len(buf):
            continue
        ln = struct.unpack_from("<I", buf, i + 4)[0]
        if not 36 <= ln <= len(buf) - i or sum(buf[i:i + ln]) & 0xFF:
            continue  # boyut ya da checksum tutmuyor
        t = bytes(buf[i:i + ln])
        if t in seen:
            continue
        seen.add(t)
        oem = re.sub(r"[^A-Za-z0-9_]", "", t[16:24].decode("latin1"))
        name = f"{t[:4].decode()}_{oem}_{where}_{i:x}.aml".replace(" ", "")
        open(os.path.join(out, name), "wb").write(t)
        print(name, ln)


LZMA_GUID = uuid.UUID("EE4E5898-3914-4259-9D6E-DC7BD79403CF").bytes_le


def walk(buf, where, out, seen, depth=0):
    tables(buf, where, out, seen)
    # GUID-defined section (tip 0x02): boyut(3) tip(1) GUID(16) DataOffset(2) Attributes(2)
    for m in re.finditer(re.escape(LZMA_GUID), buf):
        s = m.start() - 4
        if s < 0 or buf[s + 3] != 0x02:
            continue
        size = int.from_bytes(buf[s:s + 3], "little")
        off = struct.unpack_from("<H", buf, s + 20)[0]
        try:
            d = lzma.LZMADecompressor(lzma.FORMAT_ALONE).decompress(buf[s + off:s + size])
        except lzma.LZMAError as e:
            print(f"{'  ' * depth}lzma @{where}+{s:x} hata: {e}")
            continue
        print(f"{'  ' * depth}lzma @{where}+{s:x} -> {len(d)}")
        walk(d, f"{where}-{s:x}", out, seen, depth + 1)


def main():
    rom = open(sys.argv[1], "rb").read()
    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    walk(rom, "rom", out, set())


main()
