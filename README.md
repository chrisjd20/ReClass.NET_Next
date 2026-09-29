# ReClass.NET_Next

ReClass.NET with container builds that export **Windows x64** and **Linux x64** desktop packages. The frontend remains .NET Framework 4.7.2/WinForms; Linux runs it with Mono.

The starting source is the modified local checkout of [chrisjd20/ReClass.NET](https://github.com/chrisjd20/ReClass.NET), commit `a99973f33680efa4c0fb933da3f7750a49c91009`. The original upstream is [ReClassNET/ReClass.NET](https://github.com/ReClassNET/ReClass.NET). See [SOURCE.json](SOURCE.json), the [original README and credits](docs/UPSTREAM_README.md), and [MIT license](LICENSE).

## Build and export

Install Docker Engine/Desktop with Linux containers and Docker Compose. No host .NET SDK, Mono, GCC, MinGW, CMake or Visual Studio installation is needed. All compilation, dependency restoration, test tooling and packaging happen inside Docker.

```bash
docker compose run --build --rm build
```

The first build downloads the pinned images and their tools. Subsequent builds reuse Docker's cache. An x64 Linux container engine is the supported build host; another architecture would require Docker emulation and has not been validated.

Exports appear in `dist/`:

```text
ReClass.NET_Next-windows-x64.zip
ReClass.NET_Next-linux-x64.tar.gz
build-manifest.json
native-build-packages.txt
SHA256SUMS
```

Archives include the application, matching native library, managed dependency DLLs, an empty `Plugins` directory, license and runtime instructions. Windows also includes its existing x64 symbol-server DLL. Compiler runtimes are statically linked into the Windows native core; it needs no separate MinGW or Visual C++ runtime.

By default exports are owned by UID/GID 1000. On Linux, use your account's IDs and optionally record the current repository revision:

```bash
HOST_UID="$(id -u)" HOST_GID="$(id -g)" SOURCE_REVISION="$(git rev-parse HEAD)" \
  docker compose run --build --rm build
```

The manifest always records a hash of the actual build-context file contents, including uncommitted edits. `SOURCE_REVISION` is an optional label, not a claim that the tree was clean. Base image digests are pinned and installed native package/compiler versions are recorded. Distribution repositories supply other dependencies; timestamps and package updates mean archives are not promised to be byte-for-byte reproducible.

## Run the packages

**Windows 11 x64:** extract the complete ZIP and open `ReClass.NET.exe`. Windows 11's installed .NET Framework 4.8/4.8.1 satisfies the application's 4.7.2 target. Docker and build tools are unnecessary on the destination computer.

**Linux x64:** extract the archive and run `./run.sh`. Requires glibc 2.35 or newer, Mono 6.8 or newer with WinForms, libgdiplus, fonts and an X11 display. On Wayland desktops, enable XWayland.

Ubuntu 22.04/24.04 or Debian 12/13:

```bash
sudo apt-get install mono-devel libgdiplus fonts-liberation
```

Fedora 43/44:

```bash
sudo dnf install mono-devel mono-winforms libgdiplus liberation-fonts
```

`mono-devel` supplies the managed libraries and the compiler used by the address calculator. The modern .NET SDK alone cannot run this Mono WinForms application. These are archive packages with runtime prerequisites; Mono is not bundled.

The app may need additional permissions to inspect some processes. Launch scripts never change ptrace policy, capabilities, elevation settings or other system security controls. Only x64 processes and matching platform plugins are supported by these packages. The original launcher and x86 project settings remain in the source, but the release archives launch the x64 application directly.

## Verify

```bash
docker compose run --build --rm test
bash docker/check-compat.sh
```

`test` runs artifact/checksum checks, the original x64 xUnit suite, native and managed process/memory integration checks, a project save/load round-trip, a GUI startup check under Xvfb, and launcher path/argument checks. The build command itself checks binary architecture, native exports, package contents, glibc baseline and runtime dependencies before exporting.

`check-compat.sh` installs runtime prerequisites in pinned Ubuntu 22.04/24.04, Debian 12/13 and Fedora 43/44 containers, then exercises the **same exported Linux archive**. It requires an existing successful export. GUI tests use a virtual X11 display; native desktop appearance and Wayland integration still need manual review.

Windows packages are cross-compiled and statically inspected on Linux. This does **not** substitute for Windows runtime validation. See [validation results and manual checks](docs/VALIDATION.md).

## Existing limitations

Linux retains the upstream gaps: Windows PDB symbol loading and several desktop integrations are unavailable, global keyboard polling is stubbed, and debugger thread handling is incomplete. This work packages existing capabilities rather than adding feature parity. ARM, macOS, x86 releases, bundled Mono, UI modernization and installers are outside this build's scope.
