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

Existing Linux limitations: Windows PDB support and several desktop integrations
are unavailable; global keyboard polling is stubbed; debugger thread handling
has upstream limitations. These archives do not promise full Windows parity.

BUILD.json records the source and toolchain versions. The repository documents
automated compatibility checks and manual desktop validation steps.
