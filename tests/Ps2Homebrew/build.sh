#!/usr/bin/env bash
# GameVault PS2 homebrew: rebuilds every test program's ELF with the system clang and ld.lld,
# then checks every R5900-only and VU instruction with tools/r5900dec.py. GameVault's own code.
# Usage: tests/Ps2Homebrew/build.sh            (writes elf/*.elf)
#        LISTINGS=1 tests/Ps2Homebrew/build.sh (also writes build/*.lst disassembly listings)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CC="${CC:-clang}"
LD="${LD:-ld.lld}"
OBJDUMP="${OBJDUMP:-llvm-objdump}"
PYTHON="${PYTHON:-python3}"
OUT="$HERE/elf"
OBJ="$HERE/build"
PROGRAMS=(gs-basic gs-texture vu1-path1 mmi-fpu-check)

for tool in "$CC" "$LD" "$OBJDUMP" "$PYTHON"; do
    command -v "$tool" >/dev/null 2>&1 || { echo "build.sh: missing tool: $tool" >&2; exit 1; }
done

# R5900: MIPS III-based, so plain MIPS II code (mult/mflo, no MIPS32 mul, no LL/SC needed) runs as is.
CFLAGS=(--target=mipsel-none-elf -mcpu=mips2 -mabi=32 -msoft-float -ffreestanding -nostdlib -fno-pic
        -mno-abicalls -G0 -O2 -fomit-frame-pointer -fno-builtin -fno-common -fno-stack-protector
        -fno-asynchronous-unwind-tables -fno-unwind-tables -Wall -Wextra -Werror -std=c11
        -I"$HERE/common")
ASFLAGS=(--target=mipsel-none-elf -mcpu=mips2 -mabi=32 -msoft-float -fno-pic -mno-abicalls -G0
         -I"$HERE/common")

mkdir -p "$OUT" "$OBJ"

build_one() {
    local name="$1" src objs=() o
    local dir="$HERE/$name"
    [ -d "$dir" ] || { echo "build.sh: no directory $dir" >&2; exit 1; }
    mkdir -p "$OBJ/$name"
    for src in "$HERE/common/crt0.S" "$HERE/common/ee.S" "$HERE/common/common.c" "$dir"/*.c "$dir"/*.S; do
        [ -e "$src" ] || continue
        o="$OBJ/$name/$(basename "$src").o"
        case "$src" in
            *.S) "$CC" "${ASFLAGS[@]}" -I"$dir" -c "$src" -o "$o" ;;
            *.c) "$CC" "${CFLAGS[@]}" -I"$dir" -c "$src" -o "$o" ;;
        esac
        objs+=("$o")
    done
    "$LD" -T "$HERE/common/link.ld" --nmagic --no-undefined -nostdlib -static -o "$OUT/$name.elf" "${objs[@]}"
    if [ "${LISTINGS:-0}" = "1" ]; then
        "$OBJDUMP" -d --mcpu=mips3 "$OUT/$name.elf" > "$OBJ/$name.lst"
    fi
    "$PYTHON" -I "$HERE/tools/r5900dec.py" --check "$OUT/$name.elf" --objdump "$OBJDUMP"
    echo "built elf/$name.elf ($(stat -c %s "$OUT/$name.elf") bytes)"
}

if [ "$#" -gt 0 ]; then PROGRAMS=("$@"); fi
for p in "${PROGRAMS[@]}"; do build_one "$p"; done
