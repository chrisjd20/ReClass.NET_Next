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

# Opt-in focused debugger checks; this stage does not invoke the legacy suite.
FROM managed AS debugger-focused
RUN msbuild validation/debugger/FocusedChecks.csproj /p:Configuration=Release /p:AppOutputPath=/out/app /p:OutputPath=/out/validation/focused/ /verbosity:minimal \
    && msbuild validation/debugger/RuntimeWalkthrough.csproj /p:Configuration=Release /p:AppOutputPath=/out/app /p:OutputPath=/out/validation/focused/ /verbosity:minimal

FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS native
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends cmake g++ gcc make g++-mingw-w64-x86-64-posix ca-certificates curl xz-utils perl python3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY docker/mingw-x64.cmake docker/mingw-x64.cmake
COPY Dependencies/assembly-dependencies.json Dependencies/assembly-dependencies.json
COPY docker/build-assembler.sh docker/build-assembler.sh
RUN bash docker/build-assembler.sh
COPY NativeCore NativeCore
COPY validation/debugger validation/debugger
RUN bash validation/debugger/build-target.sh
RUN cmake -S NativeCore -B /out/linux -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /out/linux --parallel 2 \
    && cmake -S NativeCore -B /out/windows -DCMAKE_BUILD_TYPE=Release -DCMAKE_TOOLCHAIN_FILE=/src/docker/mingw-x64.cmake \
    && cmake --build /out/windows --parallel 2 \
    && g++ --version > /out/gcc-version.txt \
    && x86_64-w64-mingw32-g++-posix --version > /out/mingw-version.txt \
    && dpkg-query -W > /out/native-packages.txt

# Changes to the demo reuse the compiled native core and pinned assembler layer.
FROM native AS demo
RUN apt-get update && apt-get install -y --no-install-recommends libx11-dev libxrandr-dev libxinerama-dev libxcursor-dev libxi-dev libgl1-mesa-dev \
    && rm -rf /var/lib/apt/lists/*
COPY Dependencies/demo-dependencies.json Dependencies/demo-dependencies.json
COPY demo/ReClassBreakout demo/ReClassBreakout
COPY docker/build-demo.sh docker/build-demo.sh
RUN bash docker/build-demo.sh \
    && dpkg-query -W > /out/native-packages.txt

FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS packages
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-pefile binutils zip ca-certificates libx11-6 libxrandr2 libxinerama1 libxcursor1 libxi6 libgl1 libgl1-mesa-dri \
    && rm -rf /var/lib/apt/lists/*
COPY --from=managed /out /managed
COPY --from=demo /out /native
COPY Dependencies /dependencies
COPY LICENSE SOURCE.json /metadata/
COPY docs/DEBUGGER.md /metadata/DEBUGGER.md
COPY docker /scripts
ARG SOURCE_REVISION=working-tree
RUN python3 /scripts/package.py "$SOURCE_REVISION" \
    && python3 /scripts/verify-artifacts.py /artifacts

FROM packages AS export
ENTRYPOINT ["/bin/bash", "/scripts/export.sh"]

# Opt-in packaged game acceptance environment. Selecting it runs no checks.
FROM managed AS breakout-focused
RUN msbuild validation/breakout/RuntimeWalkthrough.csproj /p:Configuration=Release /p:AppOutputPath=/out/app /p:OutputPath=/out/validation/breakout/ /verbosity:minimal

FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS breakout-runtime
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends mono-devel libgdiplus xvfb xauth xdotool x11-utils fonts-liberation python3 python3-pil gcc libc6-dev libx11-6 libxrandr2 libxinerama1 libxcursor1 libxi6 libgl1 libgl1-mesa-dri \
    && rm -rf /var/lib/apt/lists/*
COPY --from=packages /artifacts /artifacts
COPY --from=breakout-focused /out/validation/breakout /validation/breakout
COPY --from=managed /out/app /validation/breakout
COPY --from=native /out/linux/out/NativeCore.so /validation/breakout/NativeCore.so
COPY --from=native /out/nasm/linux/nasm /validation/breakout/Tools/nasm
COPY Dependencies/ColorCode.dll Dependencies/Dia2Lib.dll Dependencies/Microsoft.ExceptionMessageBox.dll Dependencies/Iced.dll /validation/breakout/
COPY validation/breakout/launcher.c /validation/breakout/launcher.c
RUN gcc -std=gnu11 -O2 /validation/breakout/launcher.c -o /validation/breakout/breakout-launcher
WORKDIR /validation/breakout
ENTRYPOINT ["/bin/bash"]

# Native Linux walkthrough/desktop acceptance environment; no automatic test run.
FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS debugger-runtime
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends mono-devel libgdiplus xvfb xauth fonts-liberation \
    && rm -rf /var/lib/apt/lists/*
COPY --from=packages /artifacts /artifacts
COPY --from=debugger-focused /out/validation/focused /validation/focused
COPY --from=managed /out/app /validation/focused
COPY --from=native /out/linux/out/NativeCore.so /validation/focused/NativeCore.so
COPY --from=native /out/nasm/linux/nasm /validation/focused/Tools/nasm
COPY --from=native /out/validation/linux /validation/targets/linux
COPY Dependencies/ColorCode.dll Dependencies/Dia2Lib.dll Dependencies/Microsoft.ExceptionMessageBox.dll /validation/focused/
WORKDIR /validation/focused
ENTRYPOINT ["/bin/bash"]

# Explicitly selected validation export; builds the small fixture and focused batch,
# but runs neither the focused batch nor the historical test suite.
FROM mono:6.12.0.182@sha256:34d816779b1248b5cfd095770b64ecbaf1798e2aca693a91c11a018dce9c7ad5 AS debugger-validation-export
COPY --from=managed /out/app /validation/focused
COPY --from=debugger-focused /out/validation/focused /validation/focused
COPY --from=native /out/linux/out/NativeCore.so /validation/focused/NativeCore.so
COPY --from=native /out/windows/out/NativeCore.dll /validation/focused/NativeCore.dll
COPY Dependencies/ColorCode.dll Dependencies/Dia2Lib.dll Dependencies/Microsoft.ExceptionMessageBox.dll Dependencies/x64/symsrv.dll /validation/focused/
COPY --from=native /out/nasm/linux/nasm /validation/focused/Tools/nasm
COPY --from=native /out/nasm/windows/nasm.exe /validation/focused/Tools/nasm.exe
COPY --from=native /out/validation /validation/targets
COPY docker/export-debugger-validation.sh /scripts/export-debugger-validation.sh
ENTRYPOINT ["/bin/bash", "/scripts/export-debugger-validation.sh"]

FROM ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02 AS test
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends mono-devel libgdiplus xvfb xauth xdotool x11-utils fonts-liberation python3 python3-pefile binutils ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=packages /artifacts /artifacts
COPY --from=managed-tests /out/tests /tests
COPY --from=managed-tests /tools /tools
COPY docker /scripts
ENTRYPOINT ["/bin/bash", "/scripts/test.sh"]
