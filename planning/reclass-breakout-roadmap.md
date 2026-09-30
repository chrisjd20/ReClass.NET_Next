# ReClass: Breakout — Interactive Memory Editing Tutorial

## 1\. Summary and decisions

Build a beginner-friendly, single-player 2D game inside this repository. Players guide a robot through 12 training rooms by inspecting and modifying the game with ReClass.NET.

The primary goal is to demonstrate existing scanning and structure tools alongside the new debugger, assembly editor, hooks, conditions, tracing, and saved patches. Ease of learning takes priority over difficulty.

**Chosen defaults:**

- **Stack:** C++17, CMake, NASM, and pinned [raylib 5.5](<https://github.com/raysan5/raylib/releases/tag/5.5>).
- **Platforms:** native Windows x64 and Linux x64, matching our ReClass packages.
- **Location:** a self-contained `demo/ReClassBreakout/` project.
- **Presentation:** top-down robot training facility, drawn with simple shapes, clear labels, and restrained animation. No external artwork or audio required.
- **Scope:** all 12 rooms, with a shared game engine and tutorial system.
- **Defaults:** visible values and types, accessible hints, optional address reveals, simulation paused when entering a room.
- **Integration:** the game runs as a separate process. Players perform memory operations through the existing ReClass interface.
- **Delivery:** game binaries and an offline tutorial accompany both ReClass packages.

This document is the implementation handoff and completion checklist. Implementation and native Windows/Linux acceptance have now been performed; see `docs/DEMO_VALIDATION.md` for evidence and scope. The user's final scope correction excludes ReClass GUI testing against the game. Retain native-service campaign evidence and focus final checks on the game itself.

## 2\. Player experience and teaching behavior

### Window and controls

Use a resizable window, initially 1280×800, with three areas:

- **Playfield:** robot, targets, doors, pickups, and the room objective.
- **Live values:** relevant fields, exact numeric values, types, and optional hexadecimal representations.
- **Instructions:** current step, expected observation, hints, and controls.

Support WASD/arrows for movement, mouse aiming, and clicking or Space to fire when the playfield has focus. UI interaction must never accidentally fire the weapon. Provide visible buttons for every action needed by a lesson.

Keep the UI readable when resized through wrapping and scrolling. Include a text-size setting.

### Simulation pause

Implement simulation pause independently of process suspension:

- Rendering, UI input, live-value display, and tutorial navigation continue while paused.
- Movement, automatic damage, enemy actions, recharge, and timers stop.
- **Fire once**, **Take one hit**, **Recharge once**, **Swap weapon**, and **Evaluate door** execute exactly one relevant gameplay action.
- **Advance one tick** performs one fixed 1/60-second simulation update.
- Pause automatically when the game loses focus. Returning focus does not automatically resume it.
- Use a fixed simulation timestep with bounded catch-up. Discard elapsed time after an extended debugger stop so resuming cannot cause a sudden movement or damage burst.
- Movement speed remains editable while paused; demonstrate its effect through stepping or resuming.

Explain explicitly that ReClass’s debugger pause suspends the whole process, including the game window.

### Values and instructions

Display authoritative game values every frame, including while paused. Show enough precision to distinguish float changes; provide a copyable round-trip representation where useful.

Each lesson contains:

1. Objective and starting values.
2. A short explanation of the memory concept.
3. Numbered instructions using actual ReClass control names.
4. A deterministic action that changes the target.
5. Expected observations and common mistakes.
6. Hints, a full solution, and a restoration step when code was patched.

Provide **Back**, **Next**, **Show hint**, **Reveal address**, **Show solution**, **Copy instructions**, and **Reset room**.

Address reveals include the current absolute address, field type, and relevant pointer path or module offset. Keep them current after object replacement or process restart. Revealing information never penalizes the player.

Generate the in-game text and an offline HTML guide from the same lesson content. The guide remains available when the debugger stops the game; include a plain Markdown version too.

### Progress and completion

- Make every room selectable from the start.
- Record observed completion where the game can reliably detect an outcome.
- Let players move between steps and rooms without satisfying an automatic detector.
- Explain that observable results do not prove which editing technique was used.
- Save room completion, tutorial position, and display preferences locally.
- Never persist raw pointers, active patches, or modified gameplay memory as progress.
- Label **Reset room** as resetting gameplay data. It does not restore externally patched code.
- Before leaving patching lessons, instruct the player to use **Restore original** or **Restore all** in ReClass.
- Provide a return-to-menu option and a clear instruction that restarting the executable creates a fresh process.

## 3\. Campaign and acceptance outcomes

| Room | Starting scenario and controlled actions | ReClass workflow | Observable completion |
|---|---|---|---|
| **1\. Empty Magazine** | Start with 12 rounds and 20 stationary targets. Provide Fire once and Reload to 12. | Scan an `int32`, narrow after firing, edit ammo. | Destroy all targets without reloading during the completion attempt. |
| **2\. Battery Basics** | Show charge as both a float value and a bar. Charge/drain buttons change it by 5. | Practice unknown initial value, increased, decreased, and unchanged scans despite the visible value. | Set charge to at least 90 and activate the reactor. |
| **3\. Speed Trial** | Show speed, position, remaining time, and corridor distance. Default speed cannot complete the trial in time. | Find and edit a `float32`; distinguish speed from position. | Reach the exit before the simulation timer expires. |
| **4\. Security Clearance** | Show a keycard byte and power flags. Separate controls expose normal changes. | Edit a boolean and set the required bit while preserving other flags. | Open the door with the keycard present, power bit set, and alarm bit clear. |
| **5\. Identity Check** | Show callsign `ROOKIE` and a clearance enum. Display a nearby collection of player fields. | Define a structure, identify a fixed-size string and enum, rename fields, save the project. | Door accepts callsign `ENGINEER` and engineer clearance. Project saving is a guided manual step. |
| **6\. Replacement Parts** | Two weapon slots with name, damage, projectile speed, and cooldown. Swapping replaces the equipped weapon object. | Follow Player → Inventory → equipped Weapon; inspect a pointer array and reacquire changing addresses. | Defeat an armored target, swap weapons, then upgrade the replacement and defeat another. |
| **7\. Damage Control** | Health starts at 100. Take one hit subtracts 10 through a dedicated routine. Optional turret fires slowly. | Find what writes, confirm the next execution, inspect, NOP the damage instruction, restore it. | Three hits leave health unchanged; after restoration, the next hit subtracts 10. |
| **8\. Reverse Engineering** | Ammo starts at 12. Fire once calls a known decrement instruction. | Confirm the writer; edit assembly from `dec` to `inc`; preview hex; apply; restore. | Three shots increase ammo to 15; after restoration, the next shot decreases it. |
| **9\. Shared Machinery** | Player and enemy health pass through the same damage routine. Hit player and Hit enemy are separate buttons. | Discover accessed addresses, follow captured pointers, inspect faction IDs, filter events, and use Pause on match. | Guided observation identifies both objects and captures a player-only event. Clearly explain that filtering a watch does not restrict a patch’s effects. |
| **10\. Energy Siphon** | Player and enemy use the same firing routine. Player starts with reduced health. | Prepare a longer hook that preserves ammo consumption and adds player-only healing. Teach Insert after first; include worked Insert before and Replace selection variants. | Player firing consumes one round and heals five; enemy firing provides no healing. Restore the original behavior afterward. |
| **11\. The Decision Room** | Door checks keycard, clearance, and alarm through a short assembly routine. Show all inputs and the decision result. | Pause at the routine, step, inspect flags/registers, trace to a marked endpoint, export CSV. | Diagnose the failing condition and make the door open. Trace/export completion is acknowledged manually. |
| **12\. Return Visit** | Reuse the ammo modification, then close and reopen the same executable. | Save patch definitions and the project; resolve a module offset after restart; explicitly apply and restore. Include a second exercise using a unique module byte pattern. | Reapplied patch produces increasing ammo in the new process, then restoration returns normal firing. |

Additional guided exercises belong within these rooms, rather than becoming more rooms:

- Inspect position as adjacent float fields.
- View flag bits and raw bytes together.
- Compare assembly and hexadecimal representations.
- Observe shorter replacements being NOP-padded and longer in-place replacements being rejected.
- Inspect hook displacement and continuation before applying.
- Save and reopen a named ReClass structure; optionally generate a C++ declaration.
- Explain that saved definitions are tied to the matching platform and executable image, and loading them does not apply them automatically.

## 4\. Implementation design and packaging

### Game architecture and memory

Separate simulation, rendering, tutorial presentation, lesson definitions, and teaching routines. Keep simulation and teaching actions on the main thread to make stepping predictable.

Use explicit, standard-layout native structures:

- **Actor:** `int32` health and ammo; `float` speed, charge, X, and Y; `uint32` flags, faction, and clearance; a `uint8` keycard; a 24-byte callsign; an Inventory pointer.
- **Inventory:** two Weapon pointers and an equipped Weapon pointer.
- **Weapon:** fixed-size name, `int32` damage, float projectile speed, and float cooldown.
- **World:** player pointer, a small enemy collection, and current room state.

Place health and ammo first in Actor at offsets 0 and 4. Use explicit padding where needed and compile-time size/offset assertions. Generate assembly offset constants and the tutorial’s layout descriptions from the same definitions.

Keep learner-facing fields readable from memory on each use; avoid cached gameplay copies and disable optimization/LTO for the small teaching-state module. Treat volatile access as a visibility tool, not thread synchronization.

Expose a module-resident root pointer for the structure lessons. Allocate lesson objects at stable addresses, changing weapon addresses only during the explicit swap exercise. Allocate replacements before retiring old objects so swapping actually demonstrates an address change.

Rendering must read the same values used by gameplay. Validate edited pointers against the game’s owned object pool before dereferencing them. Invalid pointers or non-finite floats should suspend the affected action and show the raw problematic value rather than crashing or silently overwriting it.

### Assembly teaching routines

Adapt the approach already used by the debugger fixture:

- Implement small NASM routines for decrementing ammo, applying damage, and evaluating the vault.
- Normalize the relevant pointer into RAX before each revealed teaching instruction, handling Windows and System V calling conventions in the entry wrapper.
- The ammo site uses `dec dword [rax]`, with RAX pointing at Actor’s ammo.
- Provide an identifiable patch-site symbol and enough straight-line padding after suitable sites for both near and far hook entry jumps without displacing a return.
- Do not introduce incoming branches into the middle of these patch spans.
- Keep tutorial hook bodies self-contained: local branches, no external function calls, and explicit preservation of any scratch state required by the routine.
- For the energy hook, derive the containing Actor from the ammo pointer using the generated offset, inspect faction, and heal only the player.
- Keep the vault routine short and deterministic, with a revealed endpoint before its return. The trace exercise stops there rather than tracing rendering or event handling.
- Give the persistence exercise a unique, stable signature outside the bytes being overwritten.
- Enable normal executable relocation/ASLR. Never teach hard-coded absolute addresses as persistent identifiers.

The game may report whether its known teaching bytes differ from startup originals, but it must not restore code behind ReClass’s patch manager.

### Lesson content and interfaces

Store lesson content as structured source data and generate compiled C++ tables plus the offline guides during the build. Runtime JSON parsing is unnecessary.

The minimum internal lesson interface supplies:

- Starting-state initialization.
- Available controlled actions.
- Visible field descriptors: label, type, and current address.
- Tutorial steps, hints, solutions, and expected observations.
- An outcome predicate and room-reset operation.

Provide these command-line options:

- `--room N`: start directly in a selected room.
- `--self-check`: run the bounded simulation/lesson sanity batch without opening a window.

Use the actual ReClass labels verified during implementation, including **Confirm next execution**, **Preview**, **Prepare hook**, **Find accessed addresses**, **Save definition**, and **Restore original**.

No new ReClass plugin API, remote-control protocol, or automatic patch application is required.

### Platform and build integration

- Add the game to the existing Docker/CMake toolchain, reusing native Linux compilation, MinGW cross-compilation, and NASM.
- Fetch a pinned raylib source artifact and record its checksum and license. Build it statically with examples and audio disabled.
- Use the desktop GLFW backend; target X11/XWayland on Linux.
- Retain the existing Linux glibc baseline. Statically link Windows compiler runtimes.
- Package the game and guides under `Demo/` in both existing release archives.
- Extend package manifests and the existing artifact verification to cover the game executable, architecture, runtime dependencies, guides, and licenses.
- The existing `docker compose run --build --rm build` command builds and exports everything.
- Display the game process name and PID prominently to simplify attachment.
- On Linux, let this intentionally inspectable demo opt into same-user sibling debugging through its own process-level ptrace allowance, following the fixture’s approach. Report failure in the attachment help; never modify host ptrace policy.
- Preserve the current scanner fix and other existing work while adding the demo.

## 5\. Execution order and minimal validation

Implement in four substantial chunks:

1. **Foundation:** game window, room framework, authoritative memory structures, live values, pause/step controls, instruction panel, and build integration.
2. **Core demonstration:** rooms 1, 3, 8, and 10, establishing scanning, float editing, an in-place assembly patch, and a longer hook.
3. **Complete campaign:** remaining rooms, shared-object discovery, tracing, persistence lessons, progress saving, hints, and generated guides.
4. **Delivery:** package both platforms and perform final acceptance.

The four-room milestone is an implementation checkpoint, not the final deliverable.

**Keep testing to one consolidated final acceptance pass:**

- Compile and package Windows/Linux x64; run the existing artifact checks once.
- Run the small `--self-check` batch once on each platform. Cover room initialization, controlled action counts, paused simulation behavior, outcomes, and teaching-layout assumptions.
- Perform one consolidated walkthrough per platform using the packaged game and ReClass. Cover the 12 rooms in the same session; reuse focused service evidence and check the remaining GUI behavior without a second full walkthrough.
- Explicitly verify visible edits while paused, exactly one action per button press, safe resumption after a debugger stop, boundary-confirmed increment/NOP patches, player-only hook behavior, trace termination, restoration, and saved-patch resolution after restart.
- Check that guide text remains usable outside the suspended game and that room reset never claims to restore code.
- Record outcomes and any limitations. Retry only failed or changed portions.

Do not run the full legacy test suite, the distribution matrix, repeated broad debugger checks, or a benchmark campaign for this addition. Do not add a multi-gigabyte scanner stress level; the scanner regression already has its own focused coverage.

Completion means both packages contain a playable 12-room tutorial whose documented workflows work with our current ReClass implementation on Windows and Linux.

## Implementation status

- [x] Foundation: native memory layouts, rendering, pause/step controls, tutorial panel.
- [x] Core demonstrations: rooms 1, 3, 8 and 10.
- [x] Complete campaign: all 12 rooms, hints, solutions, progress and offline guides.
- [x] Windows/Linux x64 build and package integration.
- [x] Final consolidated acceptance: one self-check per platform and real native-service campaign on both platforms. ReClass GUI testing excluded by the user.
- [x] Requirement-by-requirement completion audit and validation record in `docs/DEMO_VALIDATION.md`; final game-only UI observations passed, ReClass GUI testing excluded by the user.

Validation runs only at the integrated final checkpoint; affected failures may be retried.

## 6. Repository audit and execution boundaries

Planning audit date: 2026-09-30. Treat the working tree, rather than a hypothetical upstream checkout, as the starting point. Draft demo sources and packaging changes already exist here; their presence is not evidence that they compile or that a room has passed acceptance. Review and finish those files rather than creating a second game or overwriting existing work. Leave the unchecked items above unchecked until the relevant deliverables have been verified.

The existing ReClass functionality needed by the campaign is represented by:

| Area | Starting point | How the game should use it |
|---|---|---|
| Memory scanning | `ReClass.NET/Forms/ScannerForm.cs`, `ReClass.NET/MemoryScanner/` | Teach existing scan types and comparisons through actual process memory. |
| Structures and projects | `ReClass.NET/Nodes/`, existing project serialization | Teach native fields, fixed strings, enums, pointers, arrays and saved projects. |
| Writer/access discovery | `ReClass.NET/Forms/WatchFinderForm.cs`, `ReClass.NET/Debugger/` | Generate predictable single actions; let ReClass capture execution and operand addresses. |
| Assembly conversion | `ReClass.NET/Forms/AssemblyEditorForm.cs`, `ReClass.NET/Assembly/` | Show editable assembly and corresponding encoded bytes at the current origin. |
| In-place patches and hooks | `ReClass.NET/Patching/PatchPlanner.cs`, `PatchManager.cs` | Use production preview, prepare, apply and restore workflows. |
| Persistence | `ReClass.NET/Patching/PatchRepository.cs`, `Forms/PatchManagerForm.cs` | Save definitions, reload inactive, resolve against the same image and explicitly apply. |
| Existing release path | `compose.yaml`, `docker/Dockerfile`, `docker/package.py`, `docker/verify-artifacts.py` | Extend the two existing archives rather than introduce a separate release mechanism. |
| Debugger reference fixture | `validation/debugger/` | Reuse conventions for inspectable native routines and platform attachment; do not rerun its whole suite. |

Preserve the uncommitted scanner changes in `Scanner.cs`, `SimpleScannerWorker.cs`, and `validation/scanner/`. Keep them distinguishable from demo work during review. Do not remove existing dependency sources, rewrite Git history, commit, push, or expand platform scope as an incidental part of this plan.

The game is a native executable with its own address space. ReClass remains the tool used to inspect and modify it. Game buttons perform ordinary game actions, not scanner searches, external-memory edits or patch application. Completion indicators report observed outcomes and explicitly identify manual acknowledgments.

Specific follow-ups from source inspection: confirm the frontend exposes numeric module-relative offsets as well as absolute teaching addresses and pointer paths; synchronize the optional acceptance README with the correct hook-preparation sequence; compile the currently unverified game, generators and acceptance support during the execution phase. These are handoff items, not reported runtime failures. No game build or runtime pass has been performed by this planning audit.

## 7. Concrete file responsibilities

Use the current layout below. Generated outputs belong under the build directory and in packages, not as stale copies beside source headers.

| Path | Responsibility |
|---|---|
| `demo/ReClassBreakout/CMakeLists.txt` | Dependency pin, generators, native sources, NASM format, platform link options and install output. |
| `demo/ReClassBreakout/src/main.cpp` | CLI parsing, bounded self-check entry, process identity and Linux attachment allowance. |
| `demo/ReClassBreakout/src/game.h` / `game.cpp` | Owned objects, room initialization, fixed ticks, controlled actions, field/render snapshots and observable outcomes. |
| `demo/ReClassBreakout/src/teaching.asm` | Ammo, damage and vault routines with named sites, hook padding and trace endpoint. |
| `demo/ReClassBreakout/src/frontend.h` / `frontend.cpp` | Window/input, playfield, live fields, instructions, buttons, menus, scrolling and clipboard. |
| `demo/ReClassBreakout/src/progress.h` / `progress.cpp` | Local preferences and completion, with recoverable reads and temporary-file replacement on save. |
| `demo/ReClassBreakout/src/tutorial.h` | Compiled lesson/step interface shared with the frontend. |
| `demo/ReClassBreakout/tools/layout.json` | Sole native layout definition. |
| `demo/ReClassBreakout/tools/generate_layout.py` | C++ layout/assertions, NASM constants and readable layout tables. |
| `demo/ReClassBreakout/content/lessons.json` | All 12 objectives, instructions, expected observations, hints, solutions and restoration text. |
| `demo/ReClassBreakout/tools/generate-lessons.py` | Compiled C++ lesson tables plus self-contained HTML and Markdown. |
| `Dependencies/demo-dependencies.json`, `Dependencies/Licenses/raylib-*` | Pinned source identity, licenses and notices shipped with both builds. |
| `docker/build-demo.sh`, `docker/run-demo.sh` | Platform compilation and relocatable Linux launch wrapper. |
| `docker/Dockerfile`, `package.py`, `verify-artifacts.py` | Integrate the game into the existing build and archive checks. |
| `docs/DEMO.md`, package README files | Launch, attachment, pause/reset/restoration explanations and guide locations. |
| `validation/breakout/` | Optional focused acceptance support using actual game and production ReClass services. |
| `docs/DEMO_VALIDATION.md` | Actual final results, package identities, evidence, failures and narrowly scoped retries. Create during acceptance. |

Suggested interfaces are already present in the draft. Preserve their separation: `Game` owns authoritative state and exposes actions, live fields, render/outcome snapshots and current teaching sites; the frontend reads those snapshots and invokes actions; tutorial generation supplies text; progress stores presentation state only. Avoid routing gameplay through copied UI values.

## 8. Exact memory and simulation contract

All pointer fields are eight bytes. Generate the following layouts with explicit padding and compile-time assertions on both builds. Do not hand-maintain a second list of offsets in lesson text or assembly.

| Structure | Fields and offsets in decimal bytes | Size / alignment |
|---|---|---|
| Actor | health `int32` 0; ammo `int32` 4; speed `float32` 8; charge `float32` 12; X/Y `float32` 16/20; flags `uint32` 24; faction `uint32` 28; clearance `uint32` 32; keycard `uint8` 36; padding 37–39; callsign `char[24]` 40; inventory pointer 64 | 72 / 8 |
| Inventory | weapon slots `Weapon*[2]` 0; equipped `Weapon*` 16 | 24 / 8 |
| Weapon | name `char[24]` 0; damage `int32` 24; projectile speed `float32` 28; cooldown `float32` 32 | 36 / 4 |
| World | player pointer 0; two enemy pointers 8; lesson number `int32` 24; remaining time `float32` 28; corridor distance `float32` 32; door open `uint8` 36; trial running `uint8` 37; padding 38–39 | 40 / 8 |

The World lesson number is UI metadata, not a promise that editing that integer performs the complete room transition. Label it accordingly. Inputs used by the lessons, including timer and door conditions, must otherwise be the same memory read by simulation and displayed by the frontend.

Use player faction 1, enemy faction 2, engineer clearance 2, power bit `0x1`, and alarm bit `0x2`. Keycard acceptance means byte value exactly 1. Teach setting/clearing a bit with masks while preserving every unrelated bit.

Default player state: health 100, ammo 12, speed 80, charge 40, callsign `ROOKIE`, no keycard, alarm set and power clear. Room 3 overrides speed to 60; room 10 overrides health to 50. Reset initializes gameplay data and the room's outcome counters, but never writes executable bytes.

Allocate Actors and Inventory at stable owned addresses for the process lifetime. During a weapon swap, allocate the new object before retiring the previous one so the replacement cannot immediately reuse its address. Validate the complete pointer chain against owned objects before dereferencing. A readable but unowned address is still invalid for simulation. Display the offending raw pointer and suspend only the affected action; Reset room is the recovery path for altered data.

Read externally editable values on every relevant action. Handle non-finite floats without unsafe drawing or motion, and use wider intermediates for counters and comparisons near integer limits. Read fixed strings within their allocated length, even if a learner removes the terminating NUL. Do not repeatedly repair edited memory behind the player's back.

Use a fixed tick of 1/60 second, at most six catch-up ticks per render frame, and discard an elapsed interval longer than 0.25 seconds after a debugger stop. Clear accumulated time on pause transitions. **Advance one tick** bypasses simulation pause for exactly one tick and uses current movement input. Discrete lesson buttons bypass pause for their one named action; mouse-held firing must not repeat a controlled button press.

Show float values with up to nine significant digits and offer the same round-trip value for copying. Hex display is an optional aid alongside the semantic type, not the primary display. Reveal current absolute addresses and pointer paths; for executable/root sites also reveal the actual module-relative offset. Recompute offsets from the current process module mapping rather than from a prior session.

## 9. Room-by-room implementation recipes

These recipes refine the campaign table. Each room includes a visible objective, initial values, action buttons, expected changes, hints, a full worked solution and an outcome message. Common pause, menu, reset and tick controls remain available throughout.

1. **Empty Magazine:** start at ammo 12 with 20 targets. Teach an exact four-byte integer scan, one shot to 11, then a narrowing scan. Edit ammo to 30 while simulation is paused; 20 controlled hits should finish with ammo 10. Keep a reload count for the current attempt; reloading prevents the no-reload outcome until a data reset. The Fire once button deterministically chooses a remaining target so aiming skill cannot block the lesson.
2. **Battery Basics:** start charge at float 40. Recharge once adds 5; Drain once subtracts 5; a paused observation supplies an unchanged comparison. Teach unknown-initial, increased, decreased and unchanged scanning, even though the value is intentionally visible. Edit to 95 and press Activate reactor; charge at least 90 is sufficient. The game reports activation separately from charge editing.
3. **Speed Trial:** use an eight-second trial, a 580-unit corridor and initial speed 60, which permits only 480 units of travel. Show speed, X/Y, time and distance together. Instructions must say **Start trial**, then move through stepping or resume; editing speed alone does not start the timer. A worked value such as 120 makes success easy. Start trial preserves the edited speed. A data reset restores the default speed.
4. **Security Clearance:** show keycard as a byte and power/alarm as bits of a uint32. Toggle each independently to create obvious observations. Required door inputs are keycard 1, power set, alarm clear. Show raw flags beside decoded bits so clearing the entire integer is visibly different from clearing the alarm bit.
5. **Identity Check:** define Actor fields, a bounded 24-byte callsign and a clearance enum. Set callsign to `ENGINEER` and clearance to engineer value 2; evaluate the door. Save and reopen a named project through ReClass. Require a separate manual acknowledgment for the project step; the door predicate cannot prove that a project was saved. Saving a structure does not make its heap address persistent.
6. **Replacement Parts:** expose both weapon-slot pointers and the equipped pointer. Each armored target needs weapon damage at least 20. Upgrade the equipped weapon and destroy the first target, then swap to a newly allocated weapon. Reacquire the pointer, upgrade its damage and destroy the second target. Completion requires target destruction on both sides of a swap. Keep prior objects owned for safety, while making clear that editing an unequipped object no longer changes the equipped weapon.
7. **Damage Control:** trigger one subtract-10 hit, watch its writer and confirm the next execution boundary. NOP the dedicated three-byte subtraction through the existing editor. Three further controlled hits must leave health unchanged. Require Restore original and another hit that subtracts exactly 10. If demonstrating Reset room while patched, do it before the three-hit outcome sequence; a reset clears detector progress.
8. **Reverse Engineering:** capture the ammo writer, confirm the instruction, assemble `inc dword [rax]` in place of `dec dword [rax]`, compare bytes and apply. From a reset 12, three shots produce 15. Restore and fire once to reach 14. Offer a longer in-place replacement as an explicit rejected-preview exercise, without applying it. Teach NOP padding with a shorter replacement in the preceding damage lesson.
9. **Shared Machinery:** generate separate Hit player and Hit enemy events through the same damage site. Use Find accessed addresses to observe both operand addresses, then inspect the surrounding Actor and faction fields. Configure a player-only event condition and Pause on match. Completion combines evidence of both hits with a manual acknowledgment for discovery/filtering. State directly that an event filter does not turn an unconditional patch into a player-only patch.
10. **Energy Siphon:** start player health at 50. Use the shared ammo routine and InsertAfter as the primary longer-hook exercise. Player Fire once must consume one round and heal 5; Enemy fire must consume its own round without healing either Actor. Provide the two other hook-mode variants as worked previews, not three mandatory full apply/restore cycles. Restore and fire normally: ammo decreases, health stays unchanged. Do not reset between observing the hook and proving restoration because it clears the detector sequence.
11. **The Decision Room:** reveal vault entry and the endpoint NOP before return. Use the short routine to examine keycard, clearance and alarm checks; this door does not include room 4's separate power requirement. Pause, step and run a bounded Before/After trace to the endpoint, then export CSV. Set the required inputs and evaluate the door. A manual acknowledgment covers trace/export; opening the door proves only the input result.
12. **Return Visit:** save an increment definition and project, restore, close the game, and reopen the exact same platform executable. Reattach and load the saved project; definitions must be inactive. Resolve a module-offset locator and explicitly apply, verify three increments, then restore. Repeat resolution/application with the unique byte-pattern locator using the same short sequence. Do not require a different absolute address after restart; ASLR may reuse a mapping. Acknowledge restart after the fresh room reset, then verify restoration. Persist neither captured heap addresses nor an active patch.

## 10. Assembly, encoding and longer replacements

Teach that assembly and bytes are two representations of instructions **at a specific address in a specific mode**. Branch displacements and RIP-relative operands depend on their origin. Editing readable assembly is the default workflow; hex remains available. Decoding bytes cannot recover original labels, comments, variable names or high-level source code.

For the deliberately normalized teaching site:

```asm
; RAX points to Actor.ammo, not the start of Actor.
dec dword [rax]          ; FF 08
inc dword [rax]          ; FF 00
```

Never write a longer replacement directly over neighboring instructions. The editor must either fit the selected complete-instruction span, padding unused bytes with NOPs, or use its hook mechanism. A hook installs an entry jump, executes code in allocated executable memory, relocates displaced instructions as required, then continues at the first undisplaced instruction. The selected two-byte decrement can therefore require a larger entry overwrite than two bytes.

Reserve 24 straight-line NOP bytes after ammo and damage sites before return. No internal branch may land inside these entry spans. Keep normal Windows/System V entry handling outside the patch site and normalize the relevant pointer into RAX. Do not hard-code a near jump size or promise allocation distance; inspect the actual prepared entry jump, displaced span, body origin and continuation.

The worked healing body is:

```asm
pushfq
cmp dword [rax + 24], 1  ; Actor.faction relative to Actor.ammo
jne no_heal
add dword [rax - 4], byte 5 ; Actor.health relative to ammo
no_heal:
popfq
```

Generate those displacement numbers from the schema. This body preserves flags and uses no extra registers or external calls. The mode determines where the original decrement occurs:

| Hook mode | Worked source | Required behavior |
|---|---|---|
| InsertAfter | Healing body only | Original decrement precedes the body; do not include it again. |
| InsertBefore | Healing body only | Body runs first, then the relocated decrement. |
| ReplaceSelection | Explicit `dec dword [rax]` followed by the healing body | Selected decrement is replaced; source must preserve ammo consumption explicitly. Extra displaced padding is still handled by the planner. |

Actual current editor sequence for hooks: choose Hook and semantic mode, enter source, **Prepare hook**, read its generated preview, then **Apply**. `PrepareAsync` invokes preview internally. Clicking **Preview** afterward cancels the prepared allocation, so neither the guide nor the acceptance checklist should instruct that ordering. Source/selection edits invalidate preparation; prepare again after edits. For in-place edits use **Preview**, review, then **Apply**.

Use the inert signature `52 43 42 52 4B 41 4D 4D 4F 35 35 21 A7 3C 6E 91`, outside the overwritten ammo span. Pattern entry offset is **16 decimal** (0x10). ReClass's entry-offset input parses a signed decimal integer. Before saving, confirm uniqueness in the appropriate executable module. Saved definitions include matching platform, architecture, module identity/hash and expected original bytes; rebuilds can invalidate them.

The vault trace stops at the revealed endpoint **before** its return. A tutorial default of at most 20 instructions and three seconds is ample for this routine. Exported evidence should identify Before/After contexts, instruction addresses, relevant register/flag changes and the reached endpoint.

## 11. Tutorial presentation and recovery details

Every step uses this content contract: title, action instructions, expected observation, optional hint, full solution and any restoration instruction. Resolve schema-derived offsets during generation. Escape HTML and C++ strings correctly, including non-ASCII and embedded NULs; the offline HTML must need no network access.

Provide a permanently visible simulation-pause indicator and process/PID attachment help. Explain separately: **simulation pause keeps the UI live; debugger pause stops the whole window**. Never make a lesson require reading instructions only inside a suspended process; its equivalent guide section must remain accessible externally.

Keep playfield, values and tutorial interactions separate. Trigger controlled actions once on a complete button activation, clear stale mouse aim for deterministic target buttons, and suppress playfield fire when clicking controls. Clip and scroll long fields, action panels, lesson text and Help. Keep navigation/close controls reachable at a smaller window and increased text size. If using raylib's default bitmap font, normalize unsupported punctuation for rendering or supply a suitable font; preserve the original guide/clipboard text.

Use local progress files under Windows LocalAppData (AppData fallback) or Linux XDG state storage (home-directory fallback). Save selected room, per-room tutorial step/completion, text size and reveal preferences. Invalid/missing files fall back to defaults; write failures show a concise diagnostic while leaving the game usable. No networking, accounts or scoring penalties for hints.

Make recovery visible rather than implicit: restore in ReClass when code is patched; Reset room when gameplay data is invalid; restart when a fresh process is needed. Published hook allocations can remain until process exit even after restoration, matching the production manager's lifetime rules. Leaving a lesson must not secretly undo code or alter ReClass's active-patch records.

## 12. Build and delivery decisions

Keep raylib 5.5 as the selected dependency, pinned by the manifest's exact source URL and SHA-256. This is the project's chosen version, not a claim that it is the latest release. Build static raylib with GLFW/OpenGL 3.3, audio and examples disabled. Include raylib, bundled GLFW and applicable third-party notices.

Compile Linux natively in the existing container baseline and Windows with the existing x64 MinGW toolchain. Use NASM elf64/win64 respectively. Keep PIE/ASLR enabled and LTO disabled for teaching code; compile the state module without optimization/inlining. Static Windows compiler runtimes avoid requiring separate GCC DLLs. Linux still needs the documented display/OpenGL libraries: a build container is not the player's graphical desktop.

The archive contract is:

```text
ReClass.NET_Next-{platform}-x64/
  ...existing ReClass files...
  Demo/
    ReClassBreakout.exe        # Windows
    ReClassBreakout            # Linux
    run-demo.sh                # Linux
    GUIDE.html
    GUIDE.md
    layout.md
    DEPENDENCIES.json
    Licenses/
```

Use `docker compose run --build --rm build` for the normal combined export. Record demo executable and guide identities in build metadata; extend existing verification for x64 executable format, relocations/ASLR, expected runtime dependencies, Linux glibc baseline, guide room coverage and bundled notices. Ignore local/generated build trees while retaining source, content, generators and dependency records.

Keep `PR_SET_PTRACER` opt-in restricted to the intentionally inspectable Linux game itself, subject to platform support and user permissions. It does not override every security policy. A failure should point to attachment help rather than attempt a machine-wide setting change.

## 13. Implementation handoff and minimal acceptance budget

The executing AI should first read this document and inspect the current diff. Inventory which draft components are already present, preserve unrelated work, and complete missing requirements. Do not interpret existing source as permission to skip runtime proof, or unchecked milestones as a request to rewrite working components.

Follow the four implementation chunks in section 5. Use compiler feedback as needed to make the integrated build work. Do not introduce a test-after-every-edit loop. Defer the runtime acceptance work to the completed integration checkpoint.

The final checkpoint has **one** build/package verification, **one** small self-check per native platform, and **one** consolidated real-game/native-service campaign per platform, with only affected failure/change retries. These are the planned validation budget. The user's subsequent scope correction excludes ReClass GUI testing against this game. Reuse completed service evidence and check only remaining game behavior; do not run another full campaign or ReClass GUI walkthrough. Automated service evidence must not be labeled a manual GUI walkthrough.

The consolidated sessions should establish:

- Successful native launch and attachment to the packaged game, all 12 rooms accessible, initial/focus-loss pause behavior, live paused edits and exactly one controlled action per activation.
- Integer/float scanning, boolean/bit edits, named structure/project save, changed weapon pointer and reacquisition.
- Execution-confirmed writer/access captures, assembly/hex correspondence, rejected oversized in-place edit, increment/NOP effects and exact restoration.
- Shared player/enemy event discovery, player-only event filtering, correct player-only longer hook, and ordinary restored firing.
- Bounded vault trace and CSV, saved inactive definitions, actual close/reopen, current offset/pattern resolution, explicit application and restoration.
- No catch-up burst after a debugger stop, offline guide usability while suspended, readable resize/text settings, clipboard/reveals, and progress persistence that excludes memory/patch state.

Use actual Windows for Windows runtime evidence and actual Linux for Linux evidence; cross-compilation alone proves neither. A rendered Linux game can run in an isolated graphical acceptance environment, but record that environment honestly. Keep artifacts and screenshots under ignored local output directories, with stable paths referenced in `docs/DEMO_VALIDATION.md`.

If a failure occurs, record the failed requirement, fix its cause and retry only the affected room/build portion. Do not repeat passed legacy suites, scanners, full campaign batches or platform matrices by default. If validation cannot be performed, label it **not performed** and leave that acceptance requirement open rather than presenting source inspection as a pass.

The validation record includes date, platform/runtime, source/package identity, launch/attachment result, room outcomes, restoration/restart evidence, game UI observations, and failures/selective retries. Both packages and the game requirements have been accepted; ReClass GUI integration remains outside the user's requested validation scope. The original planning audit ran no build or runtime tests; subsequent implementation evidence is recorded in `docs/DEMO_VALIDATION.md`.
