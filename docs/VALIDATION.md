# Validation

The release build performs binary and archive checks automatically. Runtime checks are a separate command; a successful export is not evidence that every operating system has been tested.

## Automated checks

Run `docker compose run --build --rm test` for the existing x64 unit suite, native ABI/loading, process enumeration, module/section listing, reading/writing memory in a controlled child process, managed/native marshaling, project save/load, GUI startup under Xvfb and argument forwarding from directories containing spaces.

Run `bash docker/check-compat.sh` after exporting packages to repeat runtime checks against the same Linux archive in Ubuntu 22.04/24.04, Debian 12/13 and Fedora 43/44. These containers share the build host's Linux kernel. They verify distribution userspace compatibility rather than every kernel, desktop session or permission configuration.

## Results on 2026-09-29

- Windows and Linux x64 release builds completed entirely inside Docker. Archive checksums, contents, architectures, all 17 native exports, glibc requirements and compiler-runtime dependencies passed validation.
- xUnit: **1,226 total; 1,224 passed; 0 failures; 2 existing floating-point tests skipped**. This includes one new regression case preserving legal backslashes in Linux filenames. The original suite exposed a Windows-drive path normalization bug on Linux, which was fixed without excluding tests.
- Native memory checks used a controlled child process. Managed integration verified native marshaling, process/module/section enumeration, memory read/write, project serialization and GUI startup with a project path containing spaces.

| Linux userspace | Native and managed checks | GUI under Xvfb / launcher checks |
| --- | --- | --- |
| Ubuntu 22.04 | Passed | Passed |
| Ubuntu 24.04 | Passed | Passed |
| Debian 12 | Passed | Passed |
| Debian 13 | Passed | Passed |
| Fedora 43 | Passed | Passed |
| Fedora 44 | Passed | Passed |

Mono used its built-in color scheme because GTK was absent in the minimal test containers. This did not prevent GUI startup. Real desktop rendering, Wayland sessions, optional Windows symbol setup, third-party plugins and debugger behavior were not established by these smoke checks.

## Windows 11 x64 manual acceptance

Windows runtime acceptance has not been performed in this Linux workspace.

1. Extract the ZIP to a directory containing spaces on Windows 11 with its installed .NET Framework; start `ReClass.NET.exe` without installing compiler tools or compiler runtime DLLs.
2. Confirm the main window opens without missing native-library or entry-point errors.
3. Start a controlled x64 test process with a known editable value. Confirm process selection, module/section listing, reading and writing that value.
4. Save a project containing a class and nodes, reopen it, and launch the executable with the project path as an argument. Confirm class and node details survive.
5. Exercise x64 disassembly and the existing debugger/breakpoint workflow against the test process. Report failures separately from Linux feature gaps.
6. Check a matching x64 plugin if plugin compatibility is required. PDB support also depends on the upstream Windows symbol/DIA setup and is not established by a cross-build.

## Linux desktop review

Repeat GUI and memory checks on real Ubuntu/Debian/Fedora desktops, including a Wayland session with XWayland. Review fonts, DPI, dialogs and permission errors. Do not interpret the known Linux keyboard, symbol or debugger gaps as newly added support.
