#!/bin/sh
set -eu
app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ "$(uname -m)" != x86_64 ]; then
    echo 'This package requires x86_64 Linux.' >&2
    exit 1
fi
if ! command -v mono >/dev/null 2>&1; then
    echo 'Mono is required. See README.txt for the Ubuntu/Debian/Fedora install commands.' >&2
    exit 1
fi
if [ -z "${DISPLAY:-}" ]; then
    echo 'An X11 display is required (enable XWayland on Wayland desktops).' >&2
    exit 1
fi
cd "$app_dir"
exec mono "$app_dir/ReClass.NET.exe" "$@"
