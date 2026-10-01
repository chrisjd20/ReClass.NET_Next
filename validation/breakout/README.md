# Packaged Breakout acceptance

**Current acceptance scope:** the user explicitly removed ReClass GUI testing
against the demo from this task. Retain the completed native-service campaign
evidence and check the game itself. The ReClass GUI checklist below is an optional
future reference, not a delivery gate; do not launch another GUI integration pass.

Final acceptance is one consolidated session per packaged platform after the final integration build: run this bounded twelve-room service batch once, then check the remaining GUI behavior in the same acceptance record. Reuse the batch evidence for memory, debugger, patch and room outcomes; do not repeat a second full twelve-room gameplay walkthrough. Do not run the legacy debugger walkthrough or scanner stress campaign for this addition. `--from=N` is available to retry an affected failing tail; record the original failure and retry scope.

Lesson scans (rooms 1, 2, 3 and 6) run over the **whole process** with the Scanner's default settings, exactly as a player would, and fail if they don't narrow to a handful of results; `CONVERGE` lines record the counts.

The harness launches the **actual rendered packaged game**, attaches the real ReClass native provider, scans actual process memory, writes through ReClass, observes native hardware/execution watches, and uses the production patch planner/manager. Game actions are ordinary X11 keys (`xdotool`) or Windows foreground `SendInput`; there is no stdin command channel or demo-only memory-edit API. Game stdout supplies identity and action observations, not mutation requests.

It is service acceptance plus game GUI input. A successful batch does **not** certify a human walkthrough of ReClass GUI buttons, hints, guide readability, resizing, clipboard, or saved display preferences. Record the remaining GUI observations alongside the batch evidence using the checklist below.

Build against the packaged application (the root integration owns this build):

```sh
xbuild validation/breakout/RuntimeWalkthrough.csproj /p:Configuration=Release /p:AppOutputPath=/absolute/path/to/package
cc validation/breakout/launcher.c -o validation/breakout/bin/breakout-launcher
```

Copy the packaged native provider and runtime dependencies to the harness output directory, or use the integration's provider setup. On Linux run in X11/XWayland, with `xdotool` available. The native launcher remains the direct managed child so Mono never consumes the game debugger's ptrace wait events. It passes all game arguments through and leaves the existing debugger launcher unchanged.

```sh
mono validation/breakout/bin/RuntimeWalkthrough.exe /absolute/path/to/package/Demo/ReClassBreakout /tmp/breakout-linux
```

On native Windows run the same managed harness executable against `Demo/ReClassBreakout.exe`, with the application's native provider and dependencies present. The game window must be foregroundable in the interactive desktop; keyboard actions are actual input events. Use a fresh evidence prefix per platform.

Evidence includes stdout (startup PID/addresses, action sequence/results, edit readbacks and room outcomes), a named Actor/clearance project `.rcnet`, `-vault.csv`, and `-saved.rcnet` containing both persistence definitions. The saved project is loaded in a **new actual process**; both module offset and unique pattern resolve to its current ammo site, remain inactive until explicit application, produce three ammo increments, and restore normal decrement behavior.

## Coverage and limits

| Room | Batch evidence |
| --- | --- |
| 1 | Whole-process exact ammo scan narrows to the single real field (≤ 3 rounds), actual ammo edit, all twenty drones |
| 2 | Whole-process `Is Between 5–100` float scan, then increased/decreased/unchanged rounds narrow to ≤ 10 results with the real field; charge edit/reactor |
| 3 | Whole-process ammo scan to one result; class at ammo − 4 is ROOKIE; speed at +8 reads 60; speed edit and six movement ticks to exit |
| 4 | Alarm switch clears only bit 1; cleared maintenance bit rejected; keycard byte and preserving flags write open the door |
| 5 | Player→Inventory→equipped Weapon, array slots, changing replacement address, both armored sentinels |
| 6 | Whole-memory `Long` scan for the player pointer finds the room-6 World; second scan finds exactly one module-resident pointer (the advertised root); three relay links through the root across two relocations |
| 7 | Native writer observation and execution confirmation; shorter NOP padding; three unchanged hits; gameplay reset retains external code; exact restore and next damage |
| 8 | Execution-confirmed increment assembly/hex; longer in-place rejection; three increments; original decrement restore |
| 9 | Access watch on the callsign captures the scanner read before the `ENGINEER` immediate compare; unedited badge rejected; named structure/fixed text/clearance enum, archive save/reopen, gate opens |
| 10 | Shared instruction operands for both Actors/factions; enemy excluded by player condition; player Pause on match; override code read from captured `r9`; unedited clearance rejected, captured code accepted |
| 11 | InsertBefore and ReplaceSelection displacement/continuation previews; applied InsertAfter hook; player ammo consumption/healing; enemy no healing; restore |
| 12 | Short vault Before/After trace stopping at the endpoint; flags/register CSV; corrected inputs open the vault |
| 13 | Named definitions archive; matching executable SHA-256; fresh process; offset and unique pattern independently inactive→resolve→apply→three increments→restore |

For the remaining continuously running stop/resume observation, use the narrow `--resume-only` mode once per platform in the same final acceptance record. It engages the test freeze, starts a room 3 trial, releases the freeze, verifies its timer is running, suspends the actual process for 1.1 seconds through ReClass, checks the timer remains stable, then resumes without any input or focus change. The first changed timer sample must fall within resumed wall time plus 40 ms; the log records the stopped and resumed values. This mode performs no campaign or self-check batch and cannot be combined with `--from`.

Players never see a paused simulation. The harness uses a hidden test freeze (Ctrl+P) plus hidden Ctrl action hooks so every action is deterministic; these reach the same game actions as the in-world consoles, pads and shots. Room selection uses F1–F12, Shift+F1 (room 13) and Shift+F12 (room 0). `--resume-only` supplies the separate continuously running timer observation.

## Guided ReClass GUI evidence checklist

Create one row per platform in the delivery record with platform/package filename, image hash, date, tester, game PID, and evidence paths. Mark each item **pass**, **fail**, or **not performed**. Use the room rows to check the relevant visible controls against the existing batch evidence, without rerunning complete gameplay attempts, three-shot/hit sequences, or every saved locator exercise. Only repeat an outcome when GUI behavior leaves it uncertain. Preserve failures and describe any selective retry.

- [ ] Launch packaged game and packaged ReClass; Room 0 steps attach and open the Scanner. All 14 rooms selectable from Escape; the game runs in real time; E prompts appear at consoles; pads act while stood on.
- [ ] Steps drawer: one action per step with IN GAME / IN RECLASS tags and label chips; observable steps tick themselves; Done/Skip advance manual steps; finished steps collapse and can be revisited; room-complete card with Next room.
- [ ] Room 1: Integer (4 Bytes) First Scan, shoot, Next Scan, Change... to 100, clear twenty drones. Room 2: Unknown Initial Value + pads with Has Increased / Has Decreased / Has Not Changed; Change... to 95; reactor console.
- [ ] Room 3: X found by walking; Create class at address; class address `- 0x10`; row 0008 Change Type → Float shows 60; edit; START pad countdown and run. A slow run fails with a respawn reason.
- [ ] Room 4: row 0018 UInt32 reads 130; alarm switch → 128; set 129; row 0020 UInt32 then 0024 UInt8 = 1; door opens. Room 5: Pointer rows to inventory and equipped weapon; damage edits survive across the rack swap through the pointer.
- [ ] Room 6: Long + Is Hex scans; World identified by 6 at +0x18; green static result shows a `<module>+0x…` tooltip; class at `[<ReClassBreakout>+0x…]` follows three relay reboots.
- [ ] Room 7: find what writes on health, Confirm next execution (row turns green), NOP selection, Preview, Apply, three turret hits without damage, Restore original, one normal hit. Room 8: dec → inc with FF 08 → FF 00 preview; restore.
- [ ] Room 9: UTF8 Text callsign; find what accesses; the scanner row's count rises only on the pad; Inspect / edit shows `mov rcx, 0x5245454E49474E45`; ENGINEER + clearance 2 opens the gate.
- [ ] Room 10: Find accessed addresses; Condition `mem32(rax + 0x1C) == 1`; Pause on match; Apply condition; the press pauses on the player hit; read r9, Resume (F5), clearance = code; console accepts.
- [ ] Room 11: Hook / Insert after payload, Prepare hook, Apply; heal on player shot, none on sentinel shot; Restore original. Room 12: access watch on keycard, confirm, Find accessed addresses with Pause on match, Step into (F11), Trace selected thread and CSV; fixed inputs open the vault.
- [ ] Room 13: Module + offset Save definition, File → Save, restart, Debugger → Saved and active patches..., Preview, Apply, three increases, Restore original.
- [ ] Both packaged offline HTML and Markdown usable while game is debugger-suspended; content uses the exact current control names.
- [ ] Resize small/large; Tab collapses/expands the drawer; text size; Debug readout adds the Memory tab; external edits show MEMORY WRITE DETECTED; CRT toggle in Help. Restart preserves completion/step position/preferences.

Screenshots should show the paused live edit, writer confirmation plus hex preview, player/enemy hook evidence, endpoint trace/CSV, and inactive saved definitions after restart. Do not label service logs as screenshots or as a manual button walkthrough.
