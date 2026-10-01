# ReClass: Breakout

ReClass: Breakout is a deliberately inspectable, single-player 2D robot game.
You play ROOKIE, breaking out of a memory-research facility one room at a time;
every room is impossible with the in-game controls alone. Its 12 rooms teach
scanning values, reconstructing native structures, following pointers,
discovering writers and readers, reading secrets out of code, editing assembly,
preparing longer hooks, filtering watch events and reading captured registers,
tracing, and saving reversible patches.

Rooms 0–9 are the main path; rooms 10–13 are an optional Advanced chapter. Each
room teaches one technique, and later rooms reuse the ROOKIE class you build in
Room 3.

| # | Room | Chapter | Technique |
|---|---|---|---|
| 0 | Attach | Getting started | attach ReClass, open the Scanner |
| 1 | Drone Swarm | Scanning | exact-value int scan (ammo) |
| 2 | Cold Reactor | Scanning | `Is Between 5–100`, then increased/decreased/unchanged |
| 3 | Collapsing Bridge | Structures | find ammo, open a class at ammo − 4, read speed beside it |
| 4 | Blast Door | Structures | byte and bit-flag edits that preserve other bits |
| 5 | Armored Sentinels | Pointers | follow pointers across a reallocation |
| 6 | Static Root | Pointers | pointer scan to a module-static root; `[<module>+offset]` class |
| 7 | Turret Nest | Code | find what writes, NOP, restore |
| 8 | Overclock | Code | `dec` → `inc` instruction edit |
| 9 | Biometric Gate | Code | find what reads; read a secret immediate from code |
| 10 | Shared Machinery | Advanced (optional) | conditional watch, Pause on match, read `r9` |
| 11 | Energy Siphon | Advanced (optional) | conditional hook (Insert after) |
| 12 | Decision Vault | Advanced (optional) | step into, trace, CSV export |
| 13 | Return Visit | Advanced (optional) | saved patch definitions across a restart |

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

## Playing

The game runs in real time and nothing hurts you unless you walk into it. Move
with WASD, aim with the mouse, click or press Space to shoot, and press **E** at
glowing consoles. Pads act while you stand on them (charge pad, scan pad, the
press plate, the turret zone). Room 3's run starts with a 3-2-1 countdown when you
step onto its START pad. A failure respawns ROOKIE with a one-line reason.

The **Steps** drawer on the right (Tab hides it) shows the room's goal and a
numbered checklist. Each step is one action tagged **IN GAME** or **IN RECLASS**,
with exact ReClass labels shown as chips, one line on why it works, and what you
should see afterwards. Shooting works in every room, so ROOKIE can always be found
again through his ammo. Steps the game can observe (you fired,
walked, stood on a pad, or a value changed from outside) tick themselves off; the
others have a **Done** button. Finishing a room shows what you learned and a
**Next room** button. Rooms 4 and later include a collapsible **Find ROOKIE
again** recipe for a fresh ReClass session.

Rooms open in order: completing a room (which always takes real memory work:
an edit, a patch, a hook or a watch) opens the next one, and completed rooms stay
open. A completed room shows **Next room** in the drawer and the HUD. Every step
can be done with ReClass alone; the drawer's **Memory** tab shows live values,
addresses and teaching sites as an answer key. Values changed
from outside the process flash violet as **MEMORY WRITE DETECTED**. Escape opens
the room select (F1–F12 rooms 1–12, Shift+F1 room 13, Shift+F12 room 0; locked
rooms stay closed). Restart
room (Ctrl+R) resets gameplay data only.

## Restore patches before moving on

Reset room resets gameplay data. It never restores externally edited executable
code. Use ReClass's **Restore original** or **Restore all** before leaving a
patching exercise. Restarting the game creates a fresh process and discards its
old hook allocations.

Rooms 7, 8, 11 and 13 explicitly teach restoration as part of the exercise. Room
10 demonstrates that filtering a watch limits the events you see; a player-only
patch requires an object check in the replacement code (Room 11). Room 13 teaches that saved
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
binary PPM and exits. `--unlock-all` opens every room for development and the
acceptance harness. Use it at the final integrated
checkpoint together with the focused real-provider walkthrough. The full legacy
suite, repeated debugger checks and distribution matrix are not required for this
demo. Actual runtime results belong in `docs/DEMO_VALIDATION.md`.
