# ReClass: Breakout

ReClass: Breakout is a deliberately inspectable, single-player 2D robot game.
You play ROOKIE, breaking out of a memory-research facility one room at a time;
every room is impossible with the in-game controls alone. Its 12 rooms teach
scanning values, reconstructing native structures, following pointers,
discovering writers and readers, reading secrets out of code, editing assembly,
preparing longer hooks, filtering watch events and reading captured registers,
tracing, and saving reversible patches.

| # | Room | What you bend |
|---|---|---|
| 1 | Drone Swarm | ammo int32 (exact scan) |
| 2 | Cold Reactor | charge float32 (unknown / relative scans) |
| 3 | Collapsing Bridge | speed float32, not position |
| 4 | Blast Door | keycard byte and flag bits |
| 5 | Biometric Gate | structure + enum; read the accepted callsign from the scanner's code via an access watch |
| 6 | Armored Sentinels | pointer chain to the equipped weapon, across a reallocation |
| 7 | Turret Nest | NOP the health writer, then restore |
| 8 | Overclock | `dec` → `inc` assembly/hex edit |
| 9 | Shared Machinery | filtered instruction watch; read the player-only override code from `r9d` |
| 10 | Energy Siphon | conditional hook (three semantics) |
| 11 | Decision Vault | stepping, tracing, CSV export |
| 12 | Return Visit | saved definitions by module offset and signature across restarts |

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

The game view fills most of the window: a top-down facility room with pixel-art
sprites, an in-game HUD (health, ammo, room gauges, status line) and a hotbar of
the room's controlled actions, each with its Ctrl shortcut. The Mission drawer
on the right holds the objective, the current step, hints, solutions and restore
reminders; **Tab** collapses it for a larger game view.

Each room starts with its simulation paused. Values stay live while paused, so
an external edit is immediately observable: any value that changes from outside
the game flashes violet in the game view as **MEMORY WRITE DETECTED** (old → new
value) and is counted in the room banner. Fire once, Take one hit, Recharge once,
Swap weapon and similar hotbar actions are deliberate single actions; **Tick +1**
runs exactly one simulation step. Movement, timers and automatic damage stop
while paused; the game pauses on losing focus and stays paused when focus
returns.

Movement uses WASD or the arrow keys. Aim with the mouse; clicking or Space fires
inside the game view. Clicking hotbar or drawer controls never fires. Closed
doors in Rooms 4, 5 and 11 block movement until the door decision opens them.

**Show debug readout** (in the Mission drawer or Help) adds a **Memory** tab
with field types, exact values, **Reveal address** (current addresses, pointer
paths and module offsets) and **Hex values**. It is off by default so the lessons
lead with ReClass; using it has no penalty. Copy values for float scans instead
of rounding the displayed number. Hints and full solutions are available at any
time, and Back/Next never depend on an automatic detector.

Instructions use an embedded readable sans-serif font, numbers a monospace font,
and branding and headings keep their pixel style. The interface scales with
larger windows; A-/A+ adjusts the reading size. Help toggles the optional CRT
effect (scanlines, vignette and a glitch on memory edits).

Observed completion records an outcome, not proof of which editing method was
used. Structure saving (Room 5), trace export (Room 11) and the restart (Room 12)
include manual acknowledgements (Ctrl+M). Local progress stores tutorial
position, completion and display preferences; addresses and modified game state
are never saved as progress.

Normal recharge and passive regeneration in Room 2 stop at 60, below the reactor's
90 threshold. Room 4's **Invalidate keycard** and **Cut power bit** controls only
revoke access; granting the keycard and power requires editing memory. Room 5's
accepted callsign is not written anywhere in the lessons: it exists only as an
immediate operand in the scanner routine, which reads your callsign every tick
while you stand on the SCAN PAD. Room 9 generates a new four-digit override code
at every reset and passes it to the shared damage routine in `r9d` only for the
player's hits (the sentinel's hits carry a decoy); **Submit override** checks the
player's clearance against it, so the room no longer relies on self-reporting.

Loading older progress clears only badges whose rules changed: Rooms 2 and 4
(version 1) and Rooms 5 and 9 (version 2).

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

`--self-check` runs a small window-free sanity batch. For screenshots during
development set `RECLASS_BREAKOUT_CAPTURE=shot.ppm` (optionally
`RECLASS_BREAKOUT_CAPTURE_FRAME=N`, default 90): the game saves that frame as a
binary PPM and exits. Use it at the final integrated
checkpoint together with the focused real-provider walkthrough. The full legacy
suite, repeated debugger checks and distribution matrix are not required for this
demo. Actual runtime results belong in `docs/DEMO_VALIDATION.md`.
