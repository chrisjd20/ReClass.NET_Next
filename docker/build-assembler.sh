#!/bin/bash
# Both target assemblers are compiled from the exact same verified release source.
set -euo pipefail
metadata=/src/Dependencies/assembly-dependencies.json
assembler_source_url=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["nasm"]["source_url"])' "$metadata")
assembler_source_sha=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["nasm"]["source_sha256"])' "$metadata")
mkdir -p /out/nasm/linux /out/nasm/windows /tmp/nasm-linux /tmp/nasm-windows
curl --fail --location --retry 3 --proto '=https' --tlsv1.2 "$assembler_source_url" -o /tmp/nasm-source.tar.xz
printf '%s  %s\n' "$assembler_source_sha" /tmp/nasm-source.tar.xz | sha256sum --check -
tar -xJf /tmp/nasm-source.tar.xz --strip-components=1 -C /tmp/nasm-linux
tar -xJf /tmp/nasm-source.tar.xz --strip-components=1 -C /tmp/nasm-windows
cd /tmp/nasm-linux
./configure --without-zlib --disable-debug --disable-gdb CFLAGS=-O2
make --jobs=2 nasm
cp nasm /out/nasm/linux/nasm
cd /tmp/nasm-windows
./configure --build=x86_64-linux-gnu --host=x86_64-w64-mingw32 --without-zlib --disable-debug --disable-gdb CC=x86_64-w64-mingw32-gcc-posix CFLAGS=-O2 LDFLAGS=-static
make --jobs=2 nasm.exe
cp nasm.exe /out/nasm/windows/nasm.exe
python3 - <<'PY'
import hashlib,json,pathlib
metadata=json.loads(pathlib.Path('/src/Dependencies/assembly-dependencies.json').read_text())
nasm=metadata['nasm'].copy()
nasm['builds']={}
for platform, filename in [('linux','nasm'),('windows','nasm.exe')]:
    nasm['builds'][platform]={'sha256':hashlib.sha256((pathlib.Path('/out/nasm') / platform / filename).read_bytes()).hexdigest(),'flags':'--without-zlib --disable-debug --disable-gdb CFLAGS=-O2' + (' --host=x86_64-w64-mingw32 LDFLAGS=-static' if platform=='windows' else '')}
pathlib.Path('/out/nasm/build.json').write_text(json.dumps(nasm,indent=2)+'\n')
PY
