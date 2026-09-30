# Debugger and assembly editing validation

This record covers the two consolidated checkpoints in [the roadmap](../planning/debugger-assembly-patching-roadmap.md). Checks use the small [controlled fixture and harnesses](../validation/debugger/README.md); no game process, historical suite, distribution matrix, exhaustive instruction tests, or benchmark runs are involved. Builds and harness compilation run in Docker. Windows runtime checks execute on real Windows through WSL interop.

## Checkpoint 1: Windows feature milestone

Environment: Windows 11 x64, actual OS build `10.0.26200.0`, .NET Framework CLR `4.0.30319.42000`. Unmanifested console programs report compatibility version `6.2.9200.0`; that is not the actual host build. Managed compilation uses Mono 6.12.0.182/MSBuild; native compilation uses the pinned Ubuntu 22.04 Linux/MinGW stages. Dependencies are Iced 1.21.0 and offline NASM 3.02, with exact download hashes and bundled licenses recorded in `Dependencies/assembly-dependencies.json`.

| Group | Observed result |
| --- | --- |
| Conversion/boundaries | Passed: known bytes, origin-sensitive branch, incomplete instructions, shorter NOP padding and oversized in-place rejection. |
| Transactions/recovery | Passed: exact restoration, stale originals, ownership conflicts, partial-write recovery and failure retaining a stopped target. |
| Hook logic | Passed: near/far layouts, semantic modes, relocation, and published/retired allocation retention. |
| Operand/condition logic | Passed: shared operand addresses and bounded restricted conditions. |
| Persistence/resolution logic | Passed: inactive definitions, unique matching and resolution failure handling. |
| Real native writer/edit workflow | Passed: write watch, increment application, observed values, NOP, restoration and conflict rejection. |
| Real native hooks | Passed: replacement plus extra displaced instructions, RIP-relative reference, direct conditional branch, observed target behavior and retirement. |
| Real native instruction watch/trace/thread lifecycle | Passed: both object addresses, completed Before snapshots, a condition selecting one object, an eight-instruction CSV trace, and a watch on a newly created thread. |
| Real native archive/restart/exit | Passed: archive reload stays inactive, module offset resolves after restart, ambiguity and changed image identity are rejected, target exit invalidates active state. |

Failures are retained in ignored `dist/` logs. The first assembler failure was fixed by using primitive `[BITS 64]`/`[ORG ...]` headers with preprocessing disabled. A subsequent hook-logic failure came from the fake allocator reusing an already retained mapping; its ledger now honors the actual allocation size and never overlaps live mappings. Conversion and transactions were not repeated after that harness-only correction: `FocusedChecks hooks conditions persistence` resumed the remaining groups. The first native walkthrough passed groups 1–3; subsequent runtime retries used `--from=4` and did not repeat those passed scenarios. Native diagnostics exposed `DR6=0`, `DR7=0` on the selected thread after the fixture instruction executed. Windows now recognizes that completion only for an outstanding step on the selected thread, a first-chance single-step event and matching exception/RIP address. Enabled unrelated hardware causes remain unhandled, and stepping ICEBP is rejected. The final affected-group retry passed groups 4–5 with an eight-entry trace and actual new-thread watch hit.

## Checkpoint 2: Linux parity and final packages

Environment: Ubuntu 22.04 container, Mono 6.8.0.105, WSL Linux kernel 6.6.87.2, x64. The acceptance container alone has `SYS_PTRACE` and an unrestricted seccomp profile; host ptrace policy is unchanged. The walkthrough loads the application/core/assembler from the extracted release archive.

Package verification passed inside the final Docker build: archive checksums, contents, x64 executable formats, legacy and optional advanced exports, runtime imports, glibc baseline, NASM version, dependency hashes and licenses. It was not repeated separately.

Linux native groups 1–5 passed: conversion/boundaries, discovery and increment/NOP/restore/conflicts, both relocation/hook behavior cases, both object addresses, conditions, an eight-entry trace, newly created/exited worker handling, inactive archive reload, restart resolution, ambiguity/image-identity rejection, and process-exit invalidation.

The new-thread audit corrected rebuilding owned inherited debug-register slots when Linux copied enabled control bits but cleared the corresponding breakpoint addresses. A separate harness race came from managed child-process reaping: [Mono's process implementation](https://raw.githubusercontent.com/mono/mono/main/mono/metadata/w32process-unix.c) calls `waitpid` for registered children from its finalizer. The Linux fixture now runs behind a tiny fork/exec launcher, keeping the traced PID outside that registry; redirected-output EOF replaces managed exit polling. No debugger exceptions are ignored to work around the harness. Once group 4 passed, group 5 exposed an exit during the all-stop barrier; the native wait path now rechecks the terminal state and delivers its queued exit event. Only group 5 was repeated for that correction, and it passed.

The final Windows archive passed `RuntimeWalkthrough --smoke` with the harness copied alongside its actual application, core and NASM: origin-aware conversion, an increment affecting both objects, exact restoration, restored decrement behavior and detach. The packaged GUI launched, opened the controlled fixture and visibly displayed the Debugger menu; its own-window capture is retained in ignored `dist/final-acceptance/windows-main.png`. Windows UI Automation exposed only unnamed panes, so automated inspector/button interaction is **not** claimed. The real-provider walkthrough establishes shared service/native behavior; source integration plus GUI startup/visual inspection establish the current UI evidence. This was a brief package confirmation, not a second full Windows walkthrough.

### Final package identities

Built at `2026-09-30T01:46:17.680164+00:00` (2026-09-29 local time). `BUILD.json` labels the repository revision `working-tree`; the exact build-input tree SHA-256 is `59beef5c3ab31f5e4a2ef70a89d176de22da3c5e69f250e75dcefa8c61d7793e`. This result record and roadmap completion status were finalized afterward without changing executable code or rerunning validation.

| Artifact | SHA-256 |
| --- | --- |
| `ReClass.NET_Next-windows-x64.zip` | `1b9be5ba9e78141ef325faf1867ef32c8cb78717a7da3a6f626b6af0768cba47` |
| `ReClass.NET_Next-linux-x64.tar.gz` | `c4c51368ddcf15021c69a33901d5d6a7e80961d1a7129ffbdc03d6bc655690a8` |

Local evidence: `dist/debugger-final-build.log`, `dist/windows-checkpoint.log`, `dist/windows-focused-remaining.log`, `dist/windows-walkthrough.log`, `dist/windows-walkthrough-remaining.log`, `dist/linux-checkpoint.log`, `dist/linux-checkpoint-remaining.log`, `dist/linux-checkpoint-exit.log`, `dist/windows-final-package.log` and `dist/windows-final-launch.log`. Earlier failures are kept rather than presented as successes. Rebuilds and affected-only retries were tied to the concrete failures described above; no extra broad validation checkpoint was added.

## Scope of evidence

Source inspection and cross-compilation do not establish runtime acceptance. The focused batch simulates transaction failures; the native walkthrough uses the actual provider and actual controlled processes. Supported workflows still have the instruction-layout, platform, permission, scheduling and allocation-lifetime limits described in [the usage guide](DEBUGGER.md). No general Cheat Engine compatibility, arbitrary hook correctness, x86/ARM/macOS support, or complete unrelated Windows integration parity is claimed.

## Git hygiene

Build/export directories, native/CMake intermediates, caches, local environment files and OS metadata are ignored. `git ls-files -ci --exclude-standard` returned no tracked ignored files; no generated outputs needed removal from the index or history. Checked-in dependency DLLs and their licenses are required source/build inputs. Validation logs, traces, fixtures' compiled binaries and release archives remain under ignored `dist/`.
