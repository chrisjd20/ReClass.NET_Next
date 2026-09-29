#!/bin/bash
set -euo pipefail
archive=${1:?Usage: runtime-test.sh linux-package.tar.gz}
sha256sum "$archive"
test_dir=$(mktemp -d '/tmp/ReClass package with spaces.XXXXXX')
trap 'rm -rf "$test_dir"' EXIT
tar -xzf "$archive" -C "$test_dir"
app_dir="$test_dir/ReClass.NET_Next-linux-x64"
fixture="$test_dir/Project with spaces.rcnet"
python3 /scripts/native-smoke.py "$app_dir/NativeCore.so"
mcs -platform:x64 -out:"$app_dir/Smoke.exe" -r:"$app_dir/ReClass.NET.exe" -r:System.Windows.Forms -r:System.Drawing -r:System.Xml.Linq /scripts/Smoke.cs
xvfb-run -a timeout 60 mono "$app_dir/Smoke.exe" --check "$fixture"
xvfb-run -a timeout 60 mono "$app_dir/Smoke.exe" --gui "$fixture"
# Test the distributed shell entry point itself and verify argv/path handling.
mkdir "$test_dir/mock"
cat > "$test_dir/mock/mono" <<'MOCK'
#!/bin/sh
test "$#" -eq 2
test -f "$1"
test "$2" = "$EXPECTED_FIXTURE"
test "$PWD/ReClass.NET.exe" = "$1"
echo 'PASS: run.sh directory resolution and argument forwarding'
MOCK
chmod +x "$test_dir/mock/mono"
EXPECTED_FIXTURE="$fixture" DISPLAY=:99 PATH="$test_dir/mock:$PATH" "$app_dir/run.sh" "$fixture"
