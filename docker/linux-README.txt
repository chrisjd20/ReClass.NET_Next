ReClass.NET_Next - Linux x64

Extract the archive and run ./run.sh from a graphical desktop.
Requires x86_64, glibc >= 2.35, Mono >= 6.8, WinForms, libgdiplus and X11.
Wayland desktops need XWayland enabled. No Docker or compiler is needed to run.

Ubuntu 22.04/24.04 and Debian 12/13:
  sudo apt-get install mono-devel libgdiplus fonts-liberation

Fedora 43/44:
  sudo dnf install mono-devel mono-winforms libgdiplus liberation-fonts

mono-devel is a convenient distro package supplying all required managed
libraries, including the compiler used by the application's address calculator.
The .NET SDK alone does not provide Mono WinForms.

You may need additional permission to inspect another process. This application
does not change ptrace policy, capabilities or other system settings for you.
Only x64 processes and matching Linux plugins are supported in this package.

The Debugger menu provides assembly/hex inspection and saved patches. Scanner
and memory-node actions can find writers/accesses; an instruction can also
collect the data addresses it accesses. The package includes the NASM Intel
syntax assembler. Equal/shorter replacements use reviewed in-place edits;
longer replacements require an explicit hook. Published hook allocations
remain reserved until target exit, even after Restore or detach. Saved patch
definitions load inactive and require explicit resolution/Apply.
Read DEBUGGER.md for the workflow, conditions, trace and recovery instructions.

Existing Linux limitations: Windows PDB support and several desktop integrations
are unavailable; global keyboard polling is stubbed. The optional advanced core
provides the native all-thread ptrace debugger. These archives do not promise
full parity for unrelated Windows integrations or legacy plugin providers.

BUILD.json records the source and toolchain versions. The repository documents
automated compatibility checks and manual desktop validation steps.

ReClass: Breakout is the bundled 12-room memory editing tutorial. Launch
./Demo/run-demo.sh from a graphical desktop, then attach ReClass to the displayed
ReClassBreakout process/PID. Open Demo/GUIDE.html in your browser for the offline
guide, or read Demo/GUIDE.md and Demo/layout.md. The guide remains available when
the debugger suspends the whole game process. Use --room N to start a room;
--self-check runs the bounded lesson sanity batch without opening a window.

The game uses statically built raylib 5.5 with the GLFW X11 backend and no audio.
It requires OpenGL 3.3, a compatible graphics driver, libstdc++ from GCC 11 or
newer, and X11/XWayland libraries. On Ubuntu/Debian the desktop dependencies are:
  sudo apt-get install libgl1 libgl1-mesa-dri libx11-6 libxrandr2 libxinerama1 libxcursor1 libxi6 libstdc++6
Only this demo opts into same-user sibling inspection using a process-level
ptrace allowance. Its attachment help reports failure; host ptrace policy is
never changed. Simulation starts paused. Reset room resets gameplay data;
restore external patches through ReClass before leaving patching rooms.
Demo/DEPENDENCIES.json and Demo/Licenses record the raylib pin and notices.
