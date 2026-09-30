# Scanner overflow regression check

Issue #264's `long` accumulator still narrowed consolidated sizes to `int` and
read entire regions into a single buffer. Region lengths and address clipping now
stay 64-bit. Fixed-size comparisons use 1 MiB chunks plus value-length overlap,
preserving alignment and assigning each match start to exactly one chunk. The
last complete value in a buffer is included, and refinement uses the bytes actually
read rather than the capacity of a reused buffer.

Regex/custom complex comparers retain contiguous-buffer semantics. They reject
regions above the supported buffer limit before reading, with a message asking
the user to narrow the address range. Arbitrary regex anchors, lookarounds and
unbounded matches cannot safely use a fixed overlap.

Run this one focused batch when changing region/chunk handling. No native build,
running target process, package rebuild or legacy test suite is required:

```sh
docker build --target managed -t reclass-next-scanner-build -f docker/Dockerfile .
docker run --rm --network none --entrypoint /bin/bash reclass-next-scanner-build -c 'set -e
msbuild /src/validation/scanner/ScannerChecks.csproj /p:Configuration=Release /p:AppOutputPath=/out/app /p:OutputPath=/out/scanner/ /verbosity:minimal
cp /out/app/*.dll /out/scanner/
mono /out/scanner/ScannerChecks.exe'
```

Validated on 2026-09-30: managed Release/x64 build and focused batch passed under
Mono 6.12. Checks cover lengths above 2 GiB, adjacent and separated sections, bounded
chunk planning, byte-pattern/string/numeric boundary matches, non-dividing alignment,
the last complete value, refinement, clipping more than 2 GiB from a region start,
and oversized regex rejection. Large-region checks use metadata and small clipped
synthetic memory; they do not scan a live multi-gigabyte process.
