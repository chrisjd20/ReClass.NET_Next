# Instruction editing and debugging

These workflows target Windows x64 and native Linux x64 processes. The new debugger uses an optional versioned provider API. Existing process plugins remain loadable; a plugin without advanced capabilities can still use its original process operations, while the editor reports which new operations it cannot provide. The application always uses the selected provider.

## Find a writer and edit it

1. Open a process and use the existing scanner or structure view to locate a value.
2. Right-click the value and choose **Find out what writes to this address...** or **Find out what accesses this address...**. Change the value in the target to capture an event. A hardware watch covers at most 32 bytes; for a larger node ReClass offers to watch the first 8 bytes instead. Providers without advanced debugging (for example process plugins) keep using the classic finder.
3. The finder window (**Find writes to data 0x…** / **Find accesses to data 0x…**) shows separate instruction and data addresses, module offsets, bytes, NASM text, counts, threads, and snapshot phase. Hardware data watches capture **After** context. Several preceding instruction candidates may be plausible; these remain uncertain.
4. Select a candidate and choose **Confirm next execution**. Confirmation records a **Before** context and checks that the completed instruction accesses the watched range. The row gets a green **CONFIRMED** badge, its Attribution stays **Confirmed access to watched range** even when later unconfirmed hits arrive, and the temporary instruction watch removes itself after the first confirmed execution. Alternatively, type the exact code address in the editor and choose **Load selection**. An uncertain backward guess alone cannot authorize a patch; Preview then reports that an established instruction boundary is required.
5. Choose **Inspect / edit**. Edit NASM assembly or hexadecimal bytes, then choose **Preview**. The **Change preview** draws the original and replacement bytes column by column (or, for a prepared hook, the jump out to the hook code and back); **Details** shows the same as text. The last side you edit supplies the replacement. The unedited original selection retains its exact bytes.
6. Choose **Apply**, then **Restore original** when finished. A shorter replacement is padded with NOPs; an oversized replacement requires an explicit larger whole-instruction selection or a hook.

Both windows use a beginner-friendly layout that follows **Settings → General → Theme** (Dark by default, or Light for the classic look; switching applies to every open window at once):

- **Find writes / accesses:** a header shows what is being watched and whether the game is running or paused. A **NEXT** bar says what to do next and the matching button glows. Each row has an Attribution badge (**CONFIRMED**, **CANDIDATE**, **UNLIKELY**, **OBSERVED**), a coloured instruction, and a count that flashes on every hit. The inspector below has three tabs: **Explain** (every candidate instruction in plain English), **Registers** (tiles marking registers the instruction uses or that point into the watched data; hover a tile to see its value in decimal too, and click one that holds an address to follow it) and **Raw** (the classic text, also used for traces).
- **Instruction editor:** a step bar shows Inspect → Edit → Preview → Apply → Restore. The original selection is a listing with byte chips and a plain-English line per instruction (**Raw** shows the text, **Registers** the captured registers). Notes under the Assembly and hex panes show whether they assemble or decode, and a size meter shows whether the replacement fits in place. Patch mode, Hook semantics and Saved locator are segmented choices with a one-line explanation each. A red **PATCH ACTIVE** badge stays visible until **Restore original**.

In both windows the areas are separated by dividers with a small handle: drag one to give an area more room. In the instruction editor, the divider under the original selection can take space from the editors and then from the change preview; the divider beside **Registers** and the one between the Assembly and hex panes resize sideways. Divider positions are kept for windows opened later in the same session.

For example, `dec dword [rax]` encodes as `FF 08`; `inc dword [rax]` encodes as `FF 00`. The pointer is 64 bits and the data operand is 32 bits. A shared instruction may affect several objects, so changing it can affect more than the originally watched value.

The **Debugger** menu (before **Help**) has **Inspect / edit instructions…**, **Saved and active patches…**, **Pause** (F6), **Resume** (F5) and **Recover owned changes**; items are enabled only in the matching state. **Inspect / edit instructions…** accepts a hexadecimal address (`7FF6A1B21234`, `0x1234`) or a module plus hexadecimal offset (`game.exe+0x1234`, `game+1234`); the editor's **Code address (hex or module+offset)** box accepts the same forms. The status bar shows **Debugger: Detached**, **Running**, **Paused** or **Faulted (Debugger → Recover owned changes)**. With a provider that lacks advanced support, the Debugger menu explains that and no debugger session is created. Reading and conversion do not attach a debugger automatically. Applying or preparing a patch obtains a debugger session and a coordinated stop.

## Assembly and hexadecimal input

The editable dialect is **NASM Intel, BITS 64**. Use `inc dword [rax]`, numeric destinations, and local labels. The service supplies the origin and bitness. Includes, preprocessor commands, custom sections, external symbols, and build directives are unavailable. Hex input accepts separated bytes or contiguous pairs; patterns and incomplete bytes are errors.

Iced 1.21.0 decodes instructions, provides operand/register/flag metadata, and relocates displaced code. Bundled NASM 3.02 assembles text offline. Both packages include the executable, managed library, dependency hashes, and licenses. No host assembler or runtime download is required. Conversion is bounded to 64 KiB source/output and five seconds.

Explanations use local templates for common operations. Unsupported explanations and unavailable operands are labeled explicitly. A formatted instruction can assemble to an equivalent encoding with different bytes after an intentional assembly edit; text round trips do not promise byte identity.

## Longer replacements

Choose **Patch mode** **Hook** (the other mode is **In place**) and a **Hook semantics** mode:

- **Replace selection:** replacement body, additional instructions displaced for the entry jump, then continuation.
- **Insert before:** body, selected originals and extra displaced instructions, then continuation.
- **Insert after:** selected originals, body, extra displaced instructions, then continuation.

**Prepare hook** reserves memory without redirecting the original entry. It assembles at the actual allocated origin, relocates supported originals, verifies the code, and makes the allocation readable/executable. Review the selected and extra instructions, relocated mappings, body, entry jump, and return address before applying.

Near entries use a five-byte relative jump. A distant allocation needs a fourteen-byte indirect absolute jump. Overwrites always end on complete instruction boundaries. Known incoming entries into the interior, unsupported relocation, displaced returns/indirect transfers, and unsupported body control flow are rejected. Static inspection cannot prove that every computed branch is absent.

Generated jumps preserve registers and flags. Your body can intentionally change them. Extra instrumentation that calls functions must observe Windows x64 shadow space/alignment or System V AMD64 alignment/red-zone rules; the editor does not automatically preserve arbitrary state or make exception unwinding transparent.

Each prepared hook reserves 128 KiB. Only one unpublished preparation is retained per session. Cancelling an unpublished preparation frees it. Once published, its memory is never freed or reused while the process is alive, including after Restore or detach. Restored hooks are **Retired** so an invocation already inside one can finish. Published/retired code is capped at 16 MiB per attached session; restarting the target reclaims it. Repeated detach/reattach cannot reclaim allocations left by earlier sessions.

## Access discovery, conditions, and tracing

**Find accessed addresses** watches an instruction before execution, resolves supported scalar/stack operands, steps it, and distinguishes completed accesses from attempts interrupted by faults. It groups both object addresses by width and access kind. FS/GS addressing requires captured segment bases. Repeated strings, vector gather/scatter, and incomplete multi-address semantics are marked unavailable.

Follow a captured register or operand into a ReClass structure. Following reads current memory, which may have changed since capture. Only values that are addresses of memory the game has mapped can be followed; a plain number (for example a 4-digit code in `r9`) explains itself in its tile's tooltip and status message instead.

Conditions use a restricted interpreter with registers/subregisters, flag names, `threadId`, `hitCount`, arithmetic/bitwise/comparison/boolean operators, and `mem8/16/32/64(address)`. Hex requires `0x`; other literals are decimal. Comparisons are unsigned unless `signed32(value)` or `signed64(value)` is used. Boolean operations short-circuit. Read errors, unavailable registers, and division by zero stop the watch with a diagnostic. Conditions are limited to 2 KiB, 256 nodes, depth 32, and sixteen reads.

Choose filtering or **Pause on match** explicitly. Execution watches evaluate Before context; data watches evaluate After context. Changing **Condition** or **Pause on match** while a watch runs does not affect it until you click **Apply condition**, which restarts the watch with the new settings; the status line says so. A condition with a syntax error is not applied and the running watch keeps going. Applying a different condition clears the rows recorded under the old one, so every row left matches it. Signed division and modulo apply when either operand is `signed32(...)`/`signed64(...)`. **Pause (F6)**, **Resume (F5)** and **Step into (F11)** operate through the same session and are enabled only in the matching state; Step into needs the game paused first.

When the game exits, or ReClass attaches to a new process, open debugger windows show **TARGET EXITED** or **SESSION ENDED**: their rows stay readable, their actions are disabled, and they always close. Open new windows from the running game.

**Trace selected thread** asks for an **Instruction limit**, a **Time limit (milliseconds)** and an optional **Stop condition**; Cancel or Esc closes the dialog. A trace holds other target threads and records the selected thread's instructions and Before/After registers. This changes scheduling; a traced call waiting on another held thread can time out. Defaults are 1,000 instructions/five seconds, with maximums of 100,000 instructions/thirty seconds. Set a stop condition, cancel, or export the bounded result to CSV. A trace is not a complete memory snapshot or call stack.

Windows stepping rejects ICEBP/INT1 rather than claiming its target-owned debug exception. Target-owned trap flags and unrelated exceptions interrupt the requested step instead of being treated as ordinary completion.

## Saved definitions and restoration

Definitions are XML in the project under `ReClassNET.Next.Patches`, schema version 1. **Save definition**, then save the project file. Saving a definition keeps the current preview or prepared hook, so **Apply** still works. Unsaved definitions add ` *` to the window title; closing, opening or clearing a project asks whether to save first, and asks before restoring applied patches (`N patches are applied in the target. Restore them and open another project?`).

**Saved locator** is **Session address**, **Module + offset** or **Module pattern**. The default pattern masks displacements and immediates with `??` so it survives relocation; lengthen it or use **Module + offset** if Save reports several matches.

The **Patches** window (**Debugger → Saved and active patches…**) has **New…** (opens an empty editor), **Open editor**, **Preview** (resolves the selected definition and shows original → replacement bytes), **Apply** (previews if needed; hooks are prepared and applied in one step), **Restore original**, **Restore all**, **Cancel preparation** and **Delete definition**. The editor's **Restore original** restores this definition's live patch, or the live patch that owns the loaded selection. **Preview** after **Prepare hook** asks before releasing the reservation. Loading keeps them inactive and never attaches, allocates, or writes by itself. Unknown future schemas remain intact and read-only.

Persisted resolution uses an explicit module offset or a unique module-scoped byte pattern, together with the exact image SHA-256 and selected original bytes. ASLR changes the base, not the saved offset. No match, ambiguity, incomplete reads, changed images/mappings, and stale originals are separate failures. Session-only drafts require a new explicit address after restart. Definitions for Windows do not automatically target similarly named Linux binaries.

Applying and restoring recheck process creation/session identity, provider, module instance, original/installed bytes, overlapping ownership, and stopped instruction pointers. They coordinate executable writes, preserve protection, synchronize instruction execution, and verify readback. A thread inside the overwrite span requires resuming and retrying.

Ordinary close/detach restores owned patches and removes watches. **Restore all** continues past a failure and lists every patch it could not restore. When installed bytes were changed by another writer, a **Patch conflict** dialog offers **Force restore original bytes**, **Abandon ownership (leave bytes as they are)** or **Cancel**; the same choice appears on exit, detach, attaching another process, switching provider, and opening another project, so a conflict never dead-ends. If restoration still fails on exit, ReClass asks whether to exit anyway and leave the target as it is. An instruction watch overlapping a patch is reported as `Overlap with an active instruction watch at 0x… (Confirm next execution or Find accessed addresses). Press Stop in that Find writes/accesses window, or close it, then apply again.` Unverified recovery keeps the target stopped. **Recover owned changes** retries retained native bytes/context/protection first, then restores owned patches; Resume remains explicit after recovery. Unexpected application termination cannot guarantee restoration.

Linux uses all-thread ptrace ownership and controlled memory syscalls. Failures name the errno (for example `Linux errno 1 (EPERM, operation not permitted)`); a refused attach also reports `kernel.yama.ptrace_scope` and how an administrator can allow tracing. ReClass never changes host ptrace policy, capabilities, or security settings. Existing Windows PDB and global keyboard integration gaps remain outside this feature. ARM, macOS, x86 targets, and dedicated Wine/Proton support are excluded.

## Focused validation

The roadmap permits two consolidated checkpoints: completed Windows workflows, then Linux parity/final packages. Use the [small controlled fixture and walkthrough](../validation/debugger/README.md). The separate validation export builds fixtures and harnesses without running them:

```bash
docker compose --profile debugger-validation run --build --rm debugger-validation
```

The synthetic regression batch in [`validation/fixes`](../validation/fixes/README.md) covers the pattern scanner end-of-buffer match, signed condition division, Restore all continuing past a conflict, force restore/abandon, module+offset parsing and Linux errno text without a target process.

IDE builds copy `Tools/nasm(.exe)` into the output when `Dependencies/Tools/` (copied from a release package) or `dist/debugger-validation/focused/Tools/` contains it; otherwise assembly reports that NASM is missing.

Do not invoke the full historical test suite or six-distribution matrix for these features. Actual results and package identities belong in `docs/DEBUGGER_VALIDATION.md`; source inspection or cross-compilation alone does not establish runtime acceptance.
