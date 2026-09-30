# Instruction editing and debugging

These workflows target Windows x64 and native Linux x64 processes. The new debugger uses an optional versioned provider API. Existing process plugins remain loadable; a plugin without advanced capabilities can still use its original process operations, while the editor reports which new operations it cannot provide. The application always uses the selected provider.

## Find a writer and edit it

1. Open a process and use the existing scanner or structure view to locate a value.
2. Choose **Find what writes to this address** or **Find what accesses this address**. Change the value in the target to capture an event.
3. The finder shows separate instruction and data addresses, module offsets, bytes, NASM text, counts, threads, and snapshot phase. Hardware data watches capture **After** context. Several preceding instruction candidates may be plausible; these remain uncertain.
4. Select a candidate and choose **Confirm next execution**. Confirmation records a **Before** context and checks that the completed instruction accesses the watched range. Alternatively, explicitly choose a decode origin in the editor. An uncertain backward guess alone cannot authorize a patch.
5. Choose **Inspect / edit**. Edit NASM assembly or hexadecimal bytes, then choose **Preview**. The last side you edit supplies the replacement. The unedited original selection retains its exact bytes.
6. Choose **Apply**, then **Restore original** when finished. A shorter replacement is padded with NOPs; an oversized replacement requires an explicit larger whole-instruction selection or a hook.

For example, `dec dword [rax]` encodes as `FF 08`; `inc dword [rax]` encodes as `FF 00`. The pointer is 64 bits and the data operand is 32 bits. A shared instruction may affect several objects, so changing it can affect more than the originally watched value.

The **Debugger** menu also opens the inspector directly at a hexadecimal code address and the saved/active patch list. Reading and conversion do not attach a debugger automatically. Applying or preparing a patch obtains a debugger session and a coordinated stop.

## Assembly and hexadecimal input

The editable dialect is **NASM Intel, BITS 64**. Use `inc dword [rax]`, numeric destinations, and local labels. The service supplies the origin and bitness. Includes, preprocessor commands, custom sections, external symbols, and build directives are unavailable. Hex input accepts separated bytes or contiguous pairs; patterns and incomplete bytes are errors.

Iced 1.21.0 decodes instructions, provides operand/register/flag metadata, and relocates displaced code. Bundled NASM 3.02 assembles text offline. Both packages include the executable, managed library, dependency hashes, and licenses. No host assembler or runtime download is required. Conversion is bounded to 64 KiB source/output and five seconds.

Explanations use local templates for common operations. Unsupported explanations and unavailable operands are labeled explicitly. A formatted instruction can assemble to an equivalent encoding with different bytes after an intentional assembly edit; text round trips do not promise byte identity.

## Longer replacements

Choose **Hook** and a semantic mode:

- **Replace selection:** replacement body, additional instructions displaced for the entry jump, then continuation.
- **Insert before:** body, selected originals and extra displaced instructions, then continuation.
- **Insert after:** selected originals, body, extra displaced instructions, then continuation.

**Prepare hook** reserves memory without redirecting the original entry. It assembles at the actual allocated origin, relocates supported originals, verifies the code, and makes the allocation readable/executable. Review the selected and extra instructions, relocated mappings, body, entry jump, and return address before applying.

Near entries use a five-byte relative jump. A distant allocation needs a fourteen-byte indirect absolute jump. Overwrites always end on complete instruction boundaries. Known incoming entries into the interior, unsupported relocation, displaced returns/indirect transfers, and unsupported body control flow are rejected. Static inspection cannot prove that every computed branch is absent.

Generated jumps preserve registers and flags. Your body can intentionally change them. Extra instrumentation that calls functions must observe Windows x64 shadow space/alignment or System V AMD64 alignment/red-zone rules; the editor does not automatically preserve arbitrary state or make exception unwinding transparent.

Each prepared hook reserves 128 KiB. Only one unpublished preparation is retained per session. Cancelling an unpublished preparation frees it. Once published, its memory is never freed or reused while the process is alive, including after Restore or detach. Restored hooks are **Retired** so an invocation already inside one can finish. Published/retired code is capped at 16 MiB per attached session; restarting the target reclaims it. Repeated detach/reattach cannot reclaim allocations left by earlier sessions.

## Access discovery, conditions, and tracing

**Find accessed addresses** watches an instruction before execution, resolves supported scalar/stack operands, steps it, and distinguishes completed accesses from attempts interrupted by faults. It groups both object addresses by width and access kind. FS/GS addressing requires captured segment bases. Repeated strings, vector gather/scatter, and incomplete multi-address semantics are marked unavailable.

Follow a captured register or operand into a ReClass structure. Following reads current memory, which may have changed since capture. Zero, unreadable, or stale-session pointers produce a status message.

Conditions use a restricted interpreter with registers/subregisters, flag names, `threadId`, `hitCount`, arithmetic/bitwise/comparison/boolean operators, and `mem8/16/32/64(address)`. Hex requires `0x`; other literals are decimal. Comparisons are unsigned unless `signed32(value)` or `signed64(value)` is used. Boolean operations short-circuit. Read errors, unavailable registers, and division by zero stop the watch with a diagnostic. Conditions are limited to 2 KiB, 256 nodes, depth 32, and sixteen reads.

Choose filtering or **Pause on match** explicitly. Execution watches evaluate Before context; data watches evaluate After context. Pause, Resume, and Step into operate through the same session.

A trace holds other target threads and records the selected thread's instructions and Before/After registers. This changes scheduling; a traced call waiting on another held thread can time out. Defaults are 1,000 instructions/five seconds, with maximums of 100,000 instructions/thirty seconds. Set a stop condition, cancel, or export the bounded result to CSV. A trace is not a complete memory snapshot or call stack.

Windows stepping rejects ICEBP/INT1 rather than claiming its target-owned debug exception. Target-owned trap flags and unrelated exceptions interrupt the requested step instead of being treated as ordinary completion.

## Saved definitions and restoration

Definitions are XML in the project under `ReClassNET.Next.Patches`, schema version 1. **Save definition**, then save the project file. Loading keeps them inactive and never attaches, allocates, or writes by itself. Unknown future schemas remain intact and read-only.

Persisted resolution uses an explicit module offset or a unique module-scoped byte pattern, together with the exact image SHA-256 and selected original bytes. ASLR changes the base, not the saved offset. No match, ambiguity, incomplete reads, changed images/mappings, and stale originals are separate failures. Session-only drafts require a new explicit address after restart. Definitions for Windows do not automatically target similarly named Linux binaries.

Applying and restoring recheck process creation/session identity, provider, module instance, original/installed bytes, overlapping ownership, and stopped instruction pointers. They coordinate executable writes, preserve protection, synchronize instruction execution, and verify readback. A thread inside the overwrite span requires resuming and retrying.

Ordinary close/detach restores owned patches and removes watches. A conflict retains originals and refuses to overwrite external changes. Unverified recovery keeps the target stopped and cancels ordinary close. **Recover owned changes** retries retained native bytes/context/protection first, then restores owned patches; Resume remains explicit after recovery. Unexpected application termination cannot guarantee restoration.

Linux uses all-thread ptrace ownership and controlled memory syscalls. It reports permission failures and never changes host ptrace policy, capabilities, or security settings. Existing Windows PDB and global keyboard integration gaps remain outside this feature. ARM, macOS, x86 targets, and dedicated Wine/Proton support are excluded.

## Focused validation

The roadmap permits two consolidated checkpoints: completed Windows workflows, then Linux parity/final packages. Use the [small controlled fixture and walkthrough](../validation/debugger/README.md). The separate validation export builds fixtures and harnesses without running them:

```bash
docker compose --profile debugger-validation run --build --rm debugger-validation
```

Do not invoke the full historical test suite or six-distribution matrix for these features. Actual results and package identities belong in `docs/DEBUGGER_VALIDATION.md`; source inspection or cross-compilation alone does not establish runtime acceptance.
