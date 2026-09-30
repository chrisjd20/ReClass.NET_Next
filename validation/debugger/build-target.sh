#!/bin/bash
set -euo pipefail
fixture_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixture_output=${1:-/out/validation}
fixture_nasm=${2:-/out/nasm/linux/nasm}
mkdir -p "$fixture_output/linux" "$fixture_output/windows"
"$fixture_nasm" -f elf64 "$fixture_root/target.asm" -o "$fixture_output/linux/target.o"
gcc -std=gnu11 -g -O0 -pthread -no-pie "$fixture_root/target.c" "$fixture_output/linux/target.o" -o "$fixture_output/linux/debugger-target"
gcc -std=gnu11 -O2 "$fixture_root/launcher.c" -o "$fixture_output/linux/debugger-launcher"
"$fixture_nasm" -f win64 "$fixture_root/target.asm" -o "$fixture_output/windows/target.o"
x86_64-w64-mingw32-gcc-posix -g -O0 -static "$fixture_root/target.c" "$fixture_output/windows/target.o" -o "$fixture_output/windows/debugger-target.exe"
