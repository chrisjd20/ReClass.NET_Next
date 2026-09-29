#!/bin/bash
set -euo pipefail
if [[ ! ${HOST_UID:-1000} =~ ^[0-9]+$ || ! ${HOST_GID:-1000} =~ ^[0-9]+$ ]]; then
    echo 'HOST_UID and HOST_GID must be numeric.' >&2
    exit 1
fi
mkdir -p /dist
chown "${HOST_UID:-1000}:${HOST_GID:-1000}" /dist
# Copy only this build's artifacts; leave any unrelated exports alone.
cp /artifacts/* /dist/
for artifact in /artifacts/*; do
    chown "${HOST_UID:-1000}:${HOST_GID:-1000}" "/dist/$(basename "$artifact")"
done
echo 'Windows/Linux x64 packages exported to dist/'
