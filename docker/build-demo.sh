#!/bin/bash
# Compile both targets without running the acceptance batch or a window.
set -euo pipefail
demo_source=/src/demo/ReClassBreakout
demo_nasm=/out/nasm/linux/nasm
cmake -S "$demo_source" -B /out/demo/linux -DCMAKE_BUILD_TYPE=Release -DCMAKE_ASM_NASM_COMPILER="$demo_nasm"
cmake --build /out/demo/linux --parallel 2
cmake -S "$demo_source" -B /out/demo/windows -DCMAKE_BUILD_TYPE=Release -DCMAKE_ASM_NASM_COMPILER="$demo_nasm" \
  -DCMAKE_TOOLCHAIN_FILE=/src/docker/mingw-x64.cmake \
  -DFETCHCONTENT_SOURCE_DIR_RAYLIB=/out/demo/linux/_deps/raylib-src
cmake --build /out/demo/windows --parallel 2
