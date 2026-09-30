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
