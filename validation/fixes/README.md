# ReClass feature-fix regression checks

Synthetic checks for the Part C fixes; no native debugger, target process, NASM or GUI:

- `PatternScanner.FindPattern` finds a match ending at the last byte (previous loop stopped one early) and masked patterns round trip.
- `WatchCondition` signed `/` and `%` (truncating, `MinValue / -1` wraps, division by zero stops the watch) while unsigned stays unsigned.
- `PatchManager.RestoreAllAsync` continues past a conflict, reports it, keeps ownership of the conflicting patch; `ForceRestoreAllAsync` writes originals; `AbandonAllAsync` forgets ownership without writing.
- `DebugWorkspace.TryParseCodeAddress` accepts hex, `module.exe+0xOFFSET` and `module+OFFSET`.
- Linux errno text names the error and adds a `ptrace_scope` hint only for a refused attach.

```sh
docker build --target managed -t reclass-next-managed -f docker/Dockerfile .
docker run --rm --network none --entrypoint /bin/bash reclass-next-managed -c 'set -e
msbuild /src/validation/fixes/FixChecks.csproj /p:Configuration=Release /p:AppOutputPath=/out/app /p:OutputPath=/out/fixes/ /verbosity:minimal
cp /out/app/*.dll /out/fixes/
mono /out/fixes/FixChecks.exe'
```
