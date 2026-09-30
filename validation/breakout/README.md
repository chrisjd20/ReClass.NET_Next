# Packaged Breakout acceptance

**Current acceptance scope:** the user explicitly removed ReClass GUI testing
against the demo from this task. Retain the completed native-service campaign
evidence and check the game itself. The ReClass GUI checklist below is an optional
future reference, not a delivery gate; do not launch another GUI integration pass.

Final acceptance is one consolidated session per packaged platform after the final integration build: run this bounded twelve-room service batch once, then check the remaining GUI behavior in the same acceptance record. Reuse the batch evidence for memory, debugger, patch and room outcomes; do not repeat a second full twelve-room gameplay walkthrough. Do not run the legacy debugger walkthrough or scanner stress campaign for this addition. `--from=N` is available to retry an affected failing tail; record the original failure and retry scope.

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
| 1 | Numeric scan/narrow, paused actual ammo edit, exactly twenty controlled shots without reload |
| 2 | Bounded unknown float scan; increased, decreased, unchanged narrowing; charge edit/reactor |
| 3 | Speed float scan, adjacent position reads, speed edit and six movement ticks to exit |
| 4 | Actual keycard byte and flags writes preserving unrelated bits, door result |
| 5 | Named structure/fixed text/clearance enum, actual archive save/reopen, ENGINEER door, acknowledgement |
| 6 | Root pointer and enemy array, Player→Inventory→equipped Weapon, array slots, changing replacement address, both armored targets |
| 7 | Native writer observation and execution confirmation; shorter NOP padding; three unchanged hits; gameplay reset retains external code; exact restore and next damage |
| 8 | Execution-confirmed increment assembly/hex; longer in-place rejection; three increments; original decrement restore |
| 9 | Shared instruction operands for both live Actors/factions; enemy excluded by player condition; player Pause on match; resume after stop |
| 10 | InsertBefore and ReplaceSelection displacement/continuation previews; applied InsertAfter hook; player ammo consumption/healing; enemy no healing; restore |
| 11 | Actual short vault Before/After trace stopping at revealed endpoint; flags/register CSV; diagnose keycard and open door; acknowledgement |
| 12 | Actual named project/definitions archive; matching executable SHA-256; fresh process; offset and unique pattern independently inactive→resolve→apply→three increments→restore |

For the remaining continuously running stop/resume observation, use the narrow `--resume-only` mode once per platform in the same final acceptance record. It starts a default room 3 trial, verifies its timer is running, suspends the actual process for 1.1 seconds through ReClass, checks the timer remains stable, then resumes without any input or focus change. The first changed timer sample must fall within resumed wall time plus 40 ms; the log records the stopped and resumed values. This mode performs no campaign or self-check batch and cannot be combined with `--from`.

Rooms 5, 9, 11 and 12 include visible manual acknowledgements. The harness activates those after corresponding service evidence; the acknowledgements do not prove GUI technique. The campaign preserves simulation pause across debugger stops; `--resume-only` supplies the separate continuously running timer observation. The GUI checklist checks Pause/Resume control access and explains focus-driven simulation pause without repeating that scenario.

## Guided ReClass GUI evidence checklist

Create one row per platform in the delivery record with platform/package filename, image hash, date, tester, game PID, and evidence paths. Mark each item **pass**, **fail**, or **not performed**. Use the room rows to check the relevant visible controls against the existing batch evidence, without rerunning complete gameplay attempts, three-shot/hit sequences, or every saved locator exercise. Only repeat an outcome when GUI behavior leaves it uncertain. Preserve failures and describe any selective retry.

- [ ] Launch packaged game and packaged ReClass; process name/PID visible; attach using process picker. All 12 rooms selectable, Back/Next work, pause on entry and focus loss, focus return stays paused.
- [ ] While simulation is paused, edit live ammo and a precise float in ReClass and observe the game update; click **Fire once**, **Take one hit**, **Recharge once**, **Advance one tick** and verify exactly one action each. Instructions and other UI clicks never fire.
- [ ] Room 1: use actual scanner type **Integer (4 Bytes)**, **First Scan**, controlled fire, **Next Scan**, result edit, and twenty-target outcome. Room 2: use **Unknown Initial Value**, **Has Increased**, **Has Decreased**, **Has Not Changed**; edit charge and **Activate reactor**. Room 3: find speed rather than X/Y, edit and win trial.
- [ ] Room 4: inspect keycard byte and flags/raw bytes together; preserve unrelated bits and open door. Room 5: create/rename the structure nodes, fixed 24-byte callsign and enum; save/reopen named project using GUI; acknowledge manual save and open ENGINEER door.
- [ ] Room 6: follow root and Inventory pointer path; inspect both array elements; upgrade one weapon, swap, reacquire current address, upgrade replacement and defeat second armor target; reveals update immediately.
- [ ] Room 7: **Find out what writes to this address...**, controlled hit, **Confirm next execution**, inspect Before context/operand, **Preview**, NOP-padding evidence, apply, three unchanged hits; **Reset room** explicitly means gameplay data and keeps the patch; **Restore original**, next hit subtracts ten.
- [ ] Room 8: confirm ammo writer; assembly `dec`→`inc`, compare hex preview, longer in-place edit rejected, three shots increase ammo to fifteen; **Restore original**, next shot decrements.
- [ ] Room 9: **Find accessed addresses**, separate player/enemy hits, follow captured pointers and faction fields, player condition and **Pause on match**; identify both Actors. Explain watch filtering does not constrain a patch. Resume safely.
- [ ] Room 10: select worked **Insert after** player-only healing, then **Prepare hook**; this produces the prepared hook preview. Inspect original displacement and continuation, then apply that preparation. Clicking **Preview** afterwards cancels the preparation, so prepare again if the definition changes. Correlate player ammo/healing, enemy exclusion, and restoration with the batch evidence; inspect the worked **Insert before** and **Replace selection** variants without repeating the full gameplay sequence.
- [ ] Room 11: pause at vault, step through failing input, inspect register/flags, **Trace** with endpoint stop condition, GUI **Export CSV**, inspect bounded rows, correct input and open door; acknowledge export.
- [ ] Room 12: GUI **Save definition** plus project save; close/reopen exact same game image; reload saved project and show definitions inactive; resolve module offset, explicitly apply and restore; repeat with unique module pattern; wrong image is described as unsupported by guide.
- [ ] Resume a previously running trial/turret after a debugger stop longer than its catch-up threshold; no movement/damage burst. Explain debugger pause freezes game rendering and input, unlike simulation pause.
- [ ] Both packaged offline HTML and Markdown usable while game is debugger-suspended; content uses exact current control names; restoration reminders visible before leaving patching rooms; restart explained as a fresh process.
- [ ] Resize small/large; wrapped and scrolled instruction/live-values panels remain readable; adjust text size; reveal/copy instruction, hint and full solution controls work. Restart preserves completion/tutorial position/preferences while gameplay pointers and patches reset. Return to menu works.

Screenshots should show the paused live edit, writer confirmation plus hex preview, player/enemy hook evidence, endpoint trace/CSV, and inactive saved definitions after restart. Do not label service logs as screenshots or as a manual button walkthrough.
