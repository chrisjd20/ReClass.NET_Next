# Breakout delivery and validation record

Date: 2026-09-30. Implementation: `demo/ReClassBreakout/`.

The deliverable is the complete 12-room native Windows/Linux x64 tutorial,
packaged alongside ReClass with generated offline HTML and Markdown guides.
The user's final instruction excludes testing the ReClass GUI against the game.
The completed native-service evidence below is retained; it is not described as
a manual ReClass GUI walkthrough. Final remaining checks concern the game only.

## Original campaign delivery and identity

- `dist/ReClass.NET_Next-windows-x64.zip`: `Demo/ReClassBreakout.exe`.
- `dist/ReClass.NET_Next-linux-x64.tar.gz`: `Demo/ReClassBreakout` and `Demo/run-demo.sh`.
- Both include `Demo/GUIDE.html`, `Demo/GUIDE.md`, `Demo/layout.md`, dependency
  records, raylib/GLFW licenses and bundled third-party notices.
- `dist/build-manifest.json` records build environment and component identities;
  `dist/SHA256SUMS` records archive identities.

Original campaign integration export: 2026-09-30 13:39:18 UTC. Build source-tree digest:
`a02362055bc60ab500fbbe0d87d73d520fc38af584e42eae5e7571c93c5b3957`.
This identifies the build input at compile time; this subsequent validation record
and roadmap completion annotations are documentation updates.

| Component | SHA-256 |
|---|---|
| Windows game | `86fe70a96d6df9b23f8b000ee1a73ddf573c113bbd6e52b08e0e787c745c570c` |
| Linux game | `881e3517f3ad34ec527f215fc3bab7a85407547e0ce7087d3fa53f2d1ea6bf40` |
| Shared HTML guide | `aad91dd9ff24d1309c381f2a3e63cf90affcb0ed6d2a68c8ad65817afbd7f5be` |
| Shared Markdown guide | `93479fc3ed9eb7cb4eb8aa8acac63c54dc3295c0b96ac0c7001d92d3d06d72a9` |

`docker compose run --build --rm build` builds and exports both platforms.
The final integrated artifact verifier passed checksums, package contents, x64
formats, native exports, graphics dependencies, guides and licenses. Evidence:
`dist/breakout-final-build.log`. No legacy test suite or distribution matrix ran.

## Runtime environments and scope

Windows ran natively on the user's Windows 11 x64 desktop with Intel OpenGL 3.3
graphics. Linux ran natively in an Ubuntu 22.04 x64 acceptance container under
WSL, with Mono, Xvfb/X11 and Mesa software rendering. The container's debugging
permissions were scoped to that container; host ptrace policy was not changed.
The game opts its own Linux process into sibling inspection.

The bounded `--self-check` ran once on each platform and passed all 12
initializers, controlled actions, data outcomes, pointer safety, fixed ticks and
teaching-layout assumptions. Evidence:
`dist/breakout-acceptance/{windows,linux}-self-check.log`.

The campaign harness launched the actual rendered packaged game. It used ordinary
keyboard input for game actions and the production ReClass native provider,
scanner, debugger, assembler, patch planner/manager, trace and project services
for memory operations. There is no game-only mutation protocol or mock provider.
See `validation/breakout/README.md` for its scope and narrow retry options.

## Campaign requirement evidence

Each row passed on native Windows and Linux. Manual acknowledgements record the
corresponding service operation, without claiming which GUI technique a learner
used. Every room remains freely selectable even without completion.

| Room | Verified behavior |
|---|---|
| 1 — Empty Magazine | Int32 scan/narrow and paused ammo edit; twenty targets destroyed without reload. |
| 2 — Battery Basics | Unknown float scan, increased/decreased/unchanged narrowing; edited charge activates reactor. |
| 3 — Speed Trial | Float speed found separately from adjacent position; edit and fixed movement ticks reach exit in time. |
| 4 — Security Clearance | Keycard byte and power/alarm bits edited while preserving unrelated bits; door opens. |
| 5 — Identity Check | Fixed string and clearance enum, named structure/project saved and reopened; ENGINEER identity accepted. |
| 6 — Replacement Parts | Root→Actor→Inventory→Weapon and pointer array inspected; actual replacement changes address; both armor targets defeated. |
| 7 — Damage Control | Writer captured and next execution confirmed; short NOP replacement padded; three hits leave health unchanged; reset leaves code patch intact; restore returns ten-point damage. |
| 8 — Reverse Engineering | Confirmed decrement converted to increment (`FF 08`→`FF 00`); oversized in-place replacement rejected; three shots reach 15; restore returns decrement. |
| 9 — Shared Machinery | Shared instruction accesses both Actors; faction/object condition excludes enemy and pauses on player match; captured pointers identify both objects. |
| 10 — Energy Siphon | Before/replace displacement and continuation previews; applied after-hook consumes one round and heals player by five; enemy firing adds no healing; restore returns ordinary firing. |
| 11 — The Decision Room | Bounded native vault trace ends at revealed endpoint; Before/After registers and flags exported to CSV; failing keycard corrected and door opens. |
| 12 — Return Visit | Definitions/project saved, actual process restarted; module offset and unique pattern independently resolve against matching image, remain inactive until explicit apply, increment three times and restore. |

Linux campaign evidence: `dist/breakout-acceptance/linux-walkthrough.log`.
Windows evidence combines `windows-walkthrough.log` (rooms 1–2),
`windows-walkthrough-final.log` (3–6), and `windows-walkthrough-restore.log`
(7–12), all in `dist/breakout-acceptance/`. Saved `.rcnet` projects and vault
CSV exports accompany those logs.

The original complete campaign preceded two frontend/status wording corrections:
the paused-refresh caption was shortened and reset status now states external
patches are unchanged. Simulation and teaching routines did not change. Only
room 12 was repeated against the changed executable identities:
`linux-final-image.log` and `windows-final-image.log` both passed both locators.
The later Mono editor layout correction changes only the managed application;
the final packaged game hashes remain those verified above.

Separate narrow resume checks proved a running trial timer stays stable through
a 1.1-second native debugger suspension, then advances by one fixed tick after
resume without intervening focus or input changes. No paused interval was replayed
as a movement/damage burst. Evidence: `linux-resume-final.log` and
`windows-resume.log`. Both observed a first resumed decrease of about 0.016667 s.

## Game interface and persistence

Linux game-only observations passed: paused live-value copying/refresh, exact
single mouse hit/recharge/tick actions, focus-loss pause with no automatic resume,
hint/solution/reveal/hex controls, tutorial Back/Next and complete instructions
clipboard, relocated module-offset copying, room menu, text size up to 22,
800×560 scrolling/help, room completion and display preference persistence.
An actual restart preserved tutorial settings while health/ammo and object
addresses were fresh. Progress contains only numeric room/step/preferences/badges,
not gameplay values, pointers or executable patches.

Evidence: `dist/breakout-acceptance/linux-gui-final/gui-results.json` and its game
screenshots. That earlier combined helper subsequently failed at a ReClass menu;
the game checks before that failure passed and are retained individually. This
is not an overall pass for the combined helper or the ReClass GUI.

Windows game-only checks passed: one paused Fire once mouse activation consumed
one round, tutorial controls did not fire, full instructions copied to clipboard,
all 12 rooms appeared in the menu, hint/solution/address/step preferences persisted,
and an actual restart restored fresh ammo. The reviewer inspected the screenshots
at small/large window sizes and the menu; panels remain readable and scrollable.
Evidence: `dist/breakout-acceptance/windows-game-only-final/` and
`windows-game-only-final-launch.log`. This helper launched no ReClass process.
The Linux final-caption screenshot at 800×560 with text size 22 was also inspected;
the shortened paused-refresh label fits its panel.

Guides are generated from the same 12 lesson records used in-game. Static review
confirmed numbered instructions, expected observations, hints, worked solutions,
restoration reminders, actual current control names, float/flag/string/pointer
exercises, short/long replacement explanations, three hook semantics, trace
endpoint, matching-image saved patch limits and offline access while suspended.
Both guides are ordinary packaged files independent of the running game.

## Corrections, retries and practical limits

- Initial audio disabling needed both raylib's audio build options; final binaries
  omit the audio module. Runtime acceptance image needed C development headers
  for its tiny native launcher. Both build issues were corrected before acceptance.
- Windows automation initially sent incorrect navigation scan codes and held
  post-stop keys too briefly. Its input helper was corrected, and only affected
  campaign tails were retried. Exact restored bytes and Running session state
  were recorded; no game/debugger logic change was needed for those timeouts.
- Linux mouse helper initially clicked too quickly for a frame to observe and
  lacked a clipboard owner. Bounded held clicks and a real X11 clipboard owner
  corrected the helper. Game UI observations then passed.
- The Windows game-only helper stopped after its first UI observations because
  PowerShell did not recognize a C#-style type alias. Replacing that alias with
  `uint16` allowed the same short game-only check to finish; no game change or
  campaign rerun was needed. The original failure log was retained.
- An incidental Mono assembly-editor defect was observed: native control creation
  collapsed multiline panes. A small post-show relayout correction was added.
  Compilation passed; per the user's scope correction, its ReClass/game GUI
  integration was not retested and no manual GUI pass is claimed.
- No macOS, ARM, Windows x86, Wayland-native, unrelated legacy-suite or broad
  performance acceptance is claimed. Linux uses X11/XWayland and the documented
  glibc 2.35/GCC 11 runtime baseline. Both games require OpenGL 3.3.
- Runtime logs/screenshots are local ignored evidence under `dist/`, not source
  assets. Rebuilding changes executable identities and may require resaving
  tutorial patch definitions. Gameplay reset never restores external code.

## Readable UI revision — 2026-09-30

This revision replaces dense pixel body text with embedded Liberation Sans regular
and bold, uses Liberation Mono for numeric values and revealed addresses, and
retains pixel branding/panel headings. Unmodified font files are pinned by SHA-256
and embedded during the build; both packages carry the Liberation font license.
No font installation, loose assets or runtime download is required.

Window resizing now scales text, geometry, mouse coordinates and scroll clipping
together (bounded logical UI scale from 1 to 2.5). The tutorial gets 41% of the
usable three-column area, and extreme widths use outer margins. A-/A+ remains
available for reading preferences. Larger line spacing, bold step titles, improved
contrast, panel shadows and an expandable controls/reference section reduce
density. Minimum-width focus indicators avoid heading collisions; delayed compact
tooltips avoid covering instructions immediately after clicks.

One focused game-only check per platform passed small, normal and large render
sizes and exactly one paused Fire once mouse activation at each size. Linux also
verified scaled hint/reveal/Next/Copy hit targets, clipboard content, and the
non-firing room menu. Screenshots from both platforms were inspected. Evidence:
`dist/breakout-readable-ui/linux-smoke.log`,
`dist/breakout-readable-ui/windows-smoke-launch.log`, and their platform screenshot
directories. A final minimum-size screenshot checks only the heading/tooltip
refinement discovered during that review, under
`dist/breakout-readable-ui/final-small/`.

No campaign, self-check, ReClass GUI integration, scanner suite or legacy suite
was repeated. The previous campaign remains historical evidence for the unchanged
simulation/teaching code, rather than a claim that saved definitions from the old
image match this rebuilt executable. Old saved definitions need the new matching
image identity, as explained by the guide.

Current UI package export: 2026-09-30T14:08:54.580098+00:00. Source-tree digest:
`bd34a103ec75343980ea89974aba96b13238a7c6e8d0491f5b99187f290881a5`.

| Current game | SHA-256 |
|---|---|
| Windows | `defb1a6f3111b84b0b711aef11f2637dd1ecb74f0f5988482bac97c07510a8a5` |
| Linux | `e5df10b87957b64afef353222a56e48a44ffbb4ede8078c699d3388637272843` |

The normal combined export and integrated artifact verifier passed for both
archives, including the new font license metadata. Build evidence:
`dist/breakout-readable-ui-export.log`.

## Ordinary-control bypass audit — 2026-09-30

The previous delivery allowed Room 2 to finish through repeated recharge or
passive regeneration, and Room 4 through its keycard/power/alarm toggles. These
were gameplay bypasses, despite the earlier campaign proving the memory-edit
solutions worked.

Normal charging now stops at 60, below the reactor threshold of 90. Charge edited
above 60 remains authoritative, including the guide's 95 → 100 recharge check.
Keycard/power controls now invalidate the card and cut power; they cannot grant
access. Alarm toggling remains available for scan practice. Room 11 shares the
keycard invalidation control. Generated in-game and offline lessons were updated.
Loading progress from version 1 clears only Room 2/4's recorded completion badges.

| Rooms | Audit result / regression coverage |
|---|---|
| 1 | Ordinary ammo runs out before twenty targets; reloading invalidates completion. |
| 2 | Repeated recharge, passive ticks, and combined controls cannot reach 90; external charge edits still activate the reactor. Reset removes completion. |
| 3 | Default speed cannot reach the exit before the deadline; edited speed succeeds. Start trial resets position and time together. |
| 4 | Diagnostic controls cannot grant a card or power. External edits open the door; revocation closes it while preserving unrelated flag bits. |
| 5 | Acknowledging project saving cannot replace the callsign/clearance edits. |
| 6 | Repeated ordinary shots and weapon swaps cannot penetrate either armored target; each replacement still needs an upgrade. |
| 7–8 | Ordinary hits/turret and shots cannot produce unchanged health or incrementing ammo. |
| 9 | Both hits require an additional manual acknowledgement. Debugger observations cannot be verified by the game; completion now explicitly says SELF-REPORTED. No memory write is required by this lesson. |
| 10 | Repeated normal player/enemy firing cannot produce the healing outcome. |
| 11 | Diagnostic controls plus trace acknowledgement cannot open the vault without edited inputs. |
| 12 | Restart acknowledgement and ordinary shots cannot replace the increment/restore behavior. |

Both Windows/Linux x64 packages compiled and passed integrated artifact checks;
exported archive checksums passed. The actual packaged Linux game's expanded
`--self-check` passed, covering all twelve room initializers, the above ordinary
control barriers, edited data outcomes, pointer safety, and fixed ticks. Windows
runtime, interactive GUI, and full external-provider campaign checks were not
repeated. Code-patch/restoration observations remain historical evidence, not a
new acceptance run. Progress migration was reviewed in source, not runtime-tested.

| Game in this audit's packages | SHA-256 |
|---|---|
| Windows | `73bf54c54dc2886a44670be4d430b613c97fd869a5b838cde7b4fecd5e39fa60` |
| Linux | `ed6aeef10aecb10695fd984d079edec984bddcbdf8f849089671d51b9473432f` |

This record was added after export; `build-manifest.json` identifies the actual
source snapshot used for the packages.

## Presentation and room revamp — 2026-09-30

The game view now owns most of the window: a 2D top-down facility with
procedurally generated pixel-art sprites, per-room set pieces, particle and
tracer effects, an in-view HUD and an action hotbar. Tutorial, objective and
outcome text moved into a collapsible Mission drawer (Tab); the live values
panel became an optional Memory tab behind **Show debug readout** (off by
default). Values changed from outside the process are detected between frames
and flash as **MEMORY WRITE DETECTED**. Everything visual is derived from
snapshots; no layout offsets, exported symbols, ACTION lines or controlled
actions changed except as listed below.

Room changes:

- Room 5 (Biometric Gate): the gate decision runs a new `breakout_scan_badge`
  routine, which the game also calls every tick while ROOKIE stands on the scan
  pad. The accepted callsign exists only as the immediate of
  `breakout_badge_compare` (`mov rcx, 'ENGINEER'`); the identity line gained a
  `badge_site=` token. The objective no longer names the callsign.
- Room 9 (Shared Machinery): each reset draws a four-digit override code and a
  distinct decoy. `breakout_apply_damage` receives the code as its second
  argument and copies it to `r9d` (player hits carry the code, enemy hits the
  decoy). **Submit override** (Ctrl+O) compares player clearance with the code.
  The manual acknowledgement and the SELF-REPORTED badge are gone.
- Closed doors in Rooms 4, 5 and 11 block movement; Room 3 keeps the robot on the
  bridge (y 160–200). Ctrl+U toggles the Room 7 turret.
- Progress version 3 clears only Room 5 and 9 badges.

Keyboard input now also reads raylib's per-frame press queue and holds Ctrl for
one extra frame, so taps shorter than a frame are not lost. Without this the
harness's `xdotool` taps were dropped under Mesa software rendering.

Linux evidence (Ubuntu 22.04 container, Xvfb + llvmpipe, container-scoped
ptrace; host policy unchanged): the packaged `--self-check` passed; the
twelve-room real-provider campaign passed, including the new Room 5 access watch
leading to the `ENGINEER` immediate and the Room 9 code read from captured `r9`;
`--resume-only` passed. Logs: `dist/breakout-revamp/{linux,linux-resume,export}.log`.
The combined Docker export and artifact verifier passed for both packages. The
Windows executable was run once through WSL interop and rendered correctly, but
the Windows campaign and the interactive ReClass GUI checklist were not run.

| Game in this revamp's packages | SHA-256 |
|---|---|
| Windows | `635796cd7c73b20a7b92634895ebc1fe3a4c5730b8e8d6e53cb843cd06de83fe` |
| Linux | `75494bbc533e6c0adbf760eaa30f02fb6667958e66af492631022513a0ee62c3` |

## Gameplay and lesson revamp (v2) — 2026-10-01

Play-testing showed rooms that could only be solved with the debug readout
(Room 3's speed never changes, so a value scan cannot narrow it), test-style
controls on a paused simulation, and wall-of-text lessons with hints that did not
help. The game was reworked:

- 14 rooms (0 Attach, 1–13) in four chapters. Each teaches one technique and
  every value is reachable with ReClass alone: by a scan driven by an in-game
  change, as a neighbour in a class, by following a pointer, or by a watch.
- Real-time play: WASD, mouse aim and shoot, **E** at consoles, pads that act
  while stood on, a 3-2-1 countdown for the Room 3 run, respawn with a reason.
  The hotbar and visible pause are gone; a hidden test freeze (Ctrl+P) and
  hidden Ctrl action hooks keep the harness deterministic.
- The drawer is a checklist: one action per step, IN GAME / IN RECLASS tags,
  ReClass labels as chips, auto-checked steps, no hints or solutions. Lesson
  schema and generator changed; guides are numbered checklists.
- New Room 6 (Static Root): the World is reallocated on every relay reboot;
  learners pointer-scan from the Actor to the World and then to the module-static
  `breakout_world_root`, and follow it with a `[<module>+offset]` class.
- Progress version 4 (rooms renumbered) clears earlier completion and steps.
- Rooms unlock in order: a room opens when the previous one is complete, and a
  room completes only on its game-verified outcome (the final step cannot be
  skipped). The Room 1 reload pad was removed. `--unlock-all` opens every room
  for development and the harness.

Two ReClass/Linux issues surfaced and were fixed:

- The Linux core typed every anonymous mapping (heap included) as **Mapped**,
  which the Scanner skips by default, so default scans could not find heap
  values on Linux. Anonymous **private** mappings are now **Private** (as on
  Windows); shared anonymous mappings stay Mapped.
- `breakout_world_root` was in `.bss`, whose tail is an anonymous mapping on
  Linux and therefore outside the module range. It now lives in `.data`, so it
  shows as a green module-static result. The Scanner also shows
  `<module>+0x…` as a tooltip on module-relative results.

Linux evidence (Ubuntu 22.04 container, Xvfb + llvmpipe, container-scoped
ptrace): packaged `--self-check` passed; the real-provider campaign passed rooms
1–13 (Room 6 static root found at `<ReClassBreakout>+0x28C020`); `--resume-only`
passed. Logs: `dist/breakout-v2/`. The combined export and artifact verifier
passed for both packages. The packaged Windows executable passed `--self-check`
and rendered through WSL interop; the Windows campaign and the interactive ReClass
GUI checklist were not run. `validation/scanner` and `validation/fixes` were not
re-run after the Linux section-type change.

| Game in these packages | SHA-256 |
|---|---|
| Windows | `972fb46bccb1775c3dba779983505dd23b2c365c6ca3d8a250883c45d7443882` |
| Linux | `8ce63adfb0753e58453be6f2875b0503c9c4ba3b18670799ff612f30e2879749` |

## Lesson pass 3: convergent scans — 2026-10-01

Play-testing found Room 3's walk-and-scan narrowing never converged. A
whole-process measurement showed why: render and animation floats move with
ROOKIE, and the game itself kept exact 4-byte copies of scanned fields (frame
snapshots, and integer/float locals in the deliberately unoptimised game code).
The earlier harness scanned only ROOKIE's 72 bytes and missed this.

- Presentation copies and game-code locals of scanned fields are now doubles, so
  a 4-byte scan finds only the real field. Reactor and HUD gauges use fixed
  segments instead of geometry that scales with the charge.
- Room 2 starts with `Is Between 5–100` (the remaining decoys were 0–1 colour
  floats in the software renderer's thread stacks). Room 3 finds ROOKIE through
  his ammo and reads speed from the class at ammo − 4; no relative scans.
  Shooting works in every room; Start run appears only on Room 3's last step.
- Steps carry a *why* line and a *you should see* line. Rooms 10–13 are an
  optional Advanced chapter; Room 9's completion reads "You escaped the facility".
- The harness runs the lesson scans over the whole process and fails unless they
  narrow to a handful of results.

Linux campaign (Xvfb + llvmpipe): Room 1 ammo 4,756 results → 1 after two shots;
Room 2 31,022 → 4 after one round → 2; Room 3 ammo → 1 after one shot; Room 6
static root `<ReClassBreakout>+0x292020`. Rooms 1–13 passed, `--resume-only`
passed, `--self-check` passed. Logs: `dist/breakout-v2/`. Windows counts were not
measured; GPU drivers keep less render data in process memory, so Windows should
narrow at least as well.

| Game in these packages | SHA-256 |
|---|---|
| Windows | `d0b7cf57cf3b49df38b643ecbb8c91f6630bb5a4a8a6d6d29cc628341cba0f5f` |
| Linux | `69ac74af43d6291adcbd012b62e4492b8b3147fcea9af894905f78241dcdf41e` |
