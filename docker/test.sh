#!/bin/bash
set -euo pipefail
python3 /scripts/verify-artifacts.py /artifacts
xvfb-run -a timeout 180 mono /tools/xunit.runner.console.2.4.1/tools/net472/xunit.console.exe /tests/ReClass.NET_Tests.dll -noappdomain -noshadow
bash /scripts/runtime-test.sh /artifacts/ReClass.NET_Next-linux-x64.tar.gz
