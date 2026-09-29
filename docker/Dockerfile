# syntax=docker/dockerfile:1
FROM mono:6.12.0.182@sha256:34d816779b1248b5cfd095770b64ecbaf1798e2aca693a91c11a018dce9c7ad5 AS managed
WORKDIR /src
COPY . .
RUN mkdir -p /out && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -d ' ' -f 1 > /out/source.sha256 \
    && msbuild ReClass.NET/ReClass.NET.csproj /p:Configuration=Release /p:Platform=x64 /p:SolutionDir=/src/ /p:OutputPath=/out/app/ /verbosity:minimal \
    && msbuild ReClass.NET_Launcher/ReClass.NET_Launcher.csproj /p:Configuration=Release /p:SolutionDir=/src/ /p:OutputPath=/out/launcher/ /verbosity:minimal \
    && mono --version > /out/mono-version.txt && msbuild -version -nologo > /out/msbuild-version.txt

FROM managed AS managed-tests
RUN msbuild ReClass.NET_Tests/ReClass.NET_Tests.csproj /t:Restore /p:Configuration=Release /p:Platform=x64 /p:SolutionDir=/src/ /verbosity:minimal \
    && msbuild ReClass.NET_Tests/ReClass.NET_Tests.csproj /p:Configuration=Release /p:Platform=x64 /p:SolutionDir=/src/ /p:OutputPath=/out/tests/ /verbosity:minimal \
    && mono Dependencies/nuget.exe install xunit.runner.console -Version 2.4.1 -OutputDirectory /tools -NonInteractive -Source https://api.nuget.org/v3/index.json

FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS native
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends cmake g++ gcc make g++-mingw-w64-x86-64-posix ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY NativeCore NativeCore
COPY docker/mingw-x64.cmake docker/mingw-x64.cmake
RUN cmake -S NativeCore -B /out/linux -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /out/linux --parallel 2 \
    && cmake -S NativeCore -B /out/windows -DCMAKE_BUILD_TYPE=Release -DCMAKE_TOOLCHAIN_FILE=/src/docker/mingw-x64.cmake \
    && cmake --build /out/windows --parallel 2 \
    && g++ --version > /out/gcc-version.txt \
    && x86_64-w64-mingw32-g++-posix --version > /out/mingw-version.txt \
    && dpkg-query -W > /out/native-packages.txt

FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS packages
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-pefile binutils zip ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=managed /out /managed
COPY --from=native /out /native
COPY Dependencies /dependencies
COPY LICENSE SOURCE.json /metadata/
COPY docker /scripts
ARG SOURCE_REVISION=working-tree
RUN python3 /scripts/package.py "$SOURCE_REVISION" \
    && python3 /scripts/verify-artifacts.py /artifacts

FROM packages AS export
ENTRYPOINT ["/bin/bash", "/scripts/export.sh"]

FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS test
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends mono-devel libgdiplus xvfb xauth xdotool x11-utils fonts-liberation python3 python3-pefile binutils ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=packages /artifacts /artifacts
COPY --from=managed-tests /out/tests /tests
COPY --from=managed-tests /tools /tools
COPY docker /scripts
ENTRYPOINT ["/bin/bash", "/scripts/test.sh"]
