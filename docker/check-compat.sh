#!/bin/bash
set -euo pipefail
repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repo_dir"
if [[ ! -f dist/ReClass.NET_Next-linux-x64.tar.gz ]]; then
    echo 'Run docker compose run --build --rm build first.' >&2
    exit 1
fi
runtimes=(
    'ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02'
    'ubuntu:24.04@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3'
    'debian:12-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251'
    'debian:13-slim@sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a'
    'fedora:43@sha256:a651ddf48ea28a06ed4e1e6519f51c9f47e7a5a138722ade87369b8fbb7e5b42'
    'fedora:44@sha256:43b29f65a41eb9c35e1cd5323e3bdf3b655c2357a9f4f1ff2f9c2798e5045d80'
)
for runtime in "${runtimes[@]}"; do
    printf '\nChecking %s\n' "${runtime%@*}"
    RUNTIME_IMAGE="$runtime" docker compose --profile compat run --build --rm compat
done
