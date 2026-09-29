ARG RUNTIME_IMAGE=ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02
FROM ${RUNTIME_IMAGE}
RUN if command -v apt-get >/dev/null; then \
      export DEBIAN_FRONTEND=noninteractive; \
      apt-get update && apt-get install -y --no-install-recommends mono-devel libgdiplus fonts-liberation xvfb xauth python3 ca-certificates && rm -rf /var/lib/apt/lists/*; \
    else \
      dnf install -y mono-devel mono-winforms libgdiplus liberation-fonts xorg-x11-server-Xvfb xorg-x11-xauth python3 && dnf clean all; \
    fi
COPY docker/Smoke.cs docker/native-smoke.py docker/runtime-test.sh /scripts/
ENTRYPOINT ["/bin/bash", "/scripts/runtime-test.sh", "/dist/ReClass.NET_Next-linux-x64.tar.gz"]
