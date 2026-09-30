# ReClass: Breakout

ReClass: Breakout is a deliberately inspectable, single-player robot training
game. Its 12 rooms teach scanning values, reconstructing native structures,
following pointers, discovering writers and accesses, editing assembly, preparing
longer hooks, filtering watch events, tracing, and saving reversible patches.

## Start

Both release archives include `Demo/`. On Windows, open
`Demo/ReClassBreakout.exe`. On Linux, run `Demo/run-demo.sh` from the extracted
archive. The game is native x64 and uses OpenGL 3.3; Linux needs an X11/XWayland
display and the package's documented graphics libraries. Docker is for building,
not playing.

Open ReClass.NET alongside the game and attach to the process name and PID shown
in the game. Every room is available from the room menu. For a direct launch use
`ReClassBreakout --room 8` (or `ReClassBreakout.exe --room 8`).

Read the included `Demo/GUIDE.html` or `Demo/GUIDE.md` for the complete walkthrough.
The game and these guides are generated from the same lesson content. Keep the
guide open when using debugger pause: suspending the process also stops its UI.

## Practice at your own pace

Each room starts with its simulation paused. Values remain visible and refresh
while paused, so an external edit is immediately observable. Fire once, Take one
hit, Recharge once, Swap weapon and Evaluate door are deliberate single actions.
Advance one tick runs exactly one simulation step. Movement, timers and automatic
damage stop while paused; the game pauses on losing focus and stays paused when
focus returns.

Movement uses WASD or the arrow keys. Aim with the mouse. Clicking or Space fires
only when interacting with the playfield; tutorial and control clicks do not fire.
Use the displayed buttons for deterministic lesson actions.

The values panel shows field types and exact values. Reveal address exposes
current addresses and pointer paths without penalty. Copy values for float scans
instead of rounding the displayed number. Hints and full solutions are available
at any time, and tutorial Back/Next never depend on an automatic detector.

Observed completion records an outcome, not proof of which editing method was
used. Structure saving, event filtering and trace export include manual
acknowledgments. Local progress stores tutorial position, completion and display
preferences; addresses and modified game state are never saved as progress.

## Restore patches before moving on

Reset room resets gameplay data. It never restores externally edited executable
code. Use ReClass's **Restore original** or **Restore all** before leaving a
patching exercise. Restarting the game creates a fresh process and discards its
old hook allocations.

Rooms 7, 8 and 10 explicitly teach restoration as part of the exercise. Room 9
demonstrates that filtering a watch limits the events you see; a player-only patch
requires an object check in the replacement code. Room 12 teaches that saved
definitions load inactive and must resolve and be explicitly applied again.

Windows and Linux executables have different code layouts and image identities.
Save separate patch definitions for each platform. Rebuilding an executable can
invalidate existing saved definitions even when a tutorial still looks the same.

## Build and focused validation

The normal repository build includes the game:

```sh
docker compose run --build --rm build
```

The generated memory layout supplies C++ structures, NASM offsets and the guide's
layout tables. The game exposes a module-resident `breakout_world_root` pointer;
Actor health and ammo begin at offsets zero and four. Teaching routines normalize
their data pointer into RAX so the assembly lessons use the same dialect on both
platforms. The game's Linux attachment allowance applies only to its own process;
it never changes the host's ptrace policy.

`--self-check` runs a small window-free sanity batch. Use it at the final integrated
checkpoint together with the focused real-provider walkthrough. The full legacy
suite, repeated debugger checks and distribution matrix are not required for this
demo. Actual runtime results belong in `docs/DEMO_VALIDATION.md`.
