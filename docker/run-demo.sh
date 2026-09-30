#!/bin/sh
set -eu
demo_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec "$demo_directory/ReClassBreakout" "$@"
