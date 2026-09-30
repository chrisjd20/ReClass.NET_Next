#!/bin/bash
set -euo pipefail
if [[ ! ${HOST_UID:-1000} =~ ^[0-9]+$ || ! ${HOST_GID:-1000} =~ ^[0-9]+$ ]]; then
    echo 'HOST_UID and HOST_GID must be numeric.' >&2
    exit 1
fi
mkdir -p /dist/debugger-validation
cp -a /validation/. /dist/debugger-validation/
chown -R "${HOST_UID:-1000}:${HOST_GID:-1000}" /dist/debugger-validation
echo 'Controlled targets and focused checks exported to dist/debugger-validation/; no tests were run.'
