#!/bin/sh
# Kullanım: scan_banks.sh <etiket> <lo> <hi>   (xdata aralığına MOV DPTR yükleyen fonksiyonları tüm bank'larda dök)
# Çıktı: analysis/cq171/ghidra/<etiket>_bank<n>.c
# GHIDRA_HOME: Ghidra klasörü (içinde support/analyzeHeadless.bat olan).
: "${GHIDRA_HOME:?GHIDRA_HOME is not set}"
R="$(cd "$(dirname "$0")/../../.." && pwd -W 2>/dev/null || pwd)"
G="$R\analysis\cq171"
for n in ${BANKS:-0 1 2 3 4 5 6 7 8 9}; do
  "$GHIDRA_HOME/support/analyzeHeadless.bat" "$G\ghidra" bank$n \
    -import "$G\unpacked\bank$n.bin" -overwrite -processor "8051:BE:16:default" \
    -loader BinaryLoader -loader-baseAddr 0x0 -scriptPath "$R\tools\scripts\ghidra" \
    -postScript FindPwmWriters.java "$G\ghidra\$1_bank$n.c" "$2" "$3" seed 2>&1 | grep 'sayısı' | sed "s/^/bank$n /"
done
