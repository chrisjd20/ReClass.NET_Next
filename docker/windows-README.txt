ReClass.NET_Next - Windows x64

Extract the entire archive, then run ReClass.NET.exe.
Windows 11 includes the required .NET Framework 4.8/4.8.1 runtime.
No Docker, Visual Studio, MinGW or separate C++ runtime installation is needed.
Use only x64 plugins compatible with the original ReClass.NET native API.
This package supports inspecting x64 processes; it contains no x86 application.
Elevation may be required to access processes running with higher privileges.

The Debugger menu provides assembly/hex inspection and saved patches. Scanner
and memory-node actions can find writers/accesses; an instruction can also
collect the data addresses it accesses. Edit assembly using the bundled NASM
Intel syntax assembler; no separate assembler installation is required.
Equal/shorter replacements are applied in place (with visible NOP padding).
Longer replacements require an explicit reviewed hook. Restore removes the
entry patch; published hook allocations remain reserved until target exit.
Saved patch definitions load inactive and require explicit resolution/Apply.
Read DEBUGGER.md for the workflow, conditions, trace and recovery instructions.

BUILD.json records the imported source commit, working-tree hash and toolchains.
Windows runtime validation is separate from Linux cross-compilation; consult the
repository's validation notes for the tested platforms and manual test steps.
