"""Package the container-built application without bundling host runtimes."""
import datetime
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys

artifacts = pathlib.Path('/artifacts')
artifacts.mkdir()
source = json.loads(pathlib.Path('/metadata/SOURCE.json').read_text())
assembly_dependencies = json.loads(pathlib.Path('/dependencies/assembly-dependencies.json').read_text())
assert hashlib.sha256(pathlib.Path('/dependencies/Iced.dll').read_bytes()).hexdigest() == assembly_dependencies['iced']['dll_sha256']
assembly_dependencies['nasm'] = json.loads(pathlib.Path('/native/nasm/build.json').read_text())
manifest = {
    'source': source,
    'repository_revision': sys.argv[1],
    'source_tree_sha256': pathlib.Path('/managed/source.sha256').read_text().strip(),
    'built_at_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'architectures': ['x86_64'],
    'assembly_dependencies': assembly_dependencies,
    'tools': {
        'mono': pathlib.Path('/managed/mono-version.txt').read_text(),
        'msbuild': pathlib.Path('/managed/msbuild-version.txt').read_text(),
        'gcc': pathlib.Path('/native/gcc-version.txt').read_text(),
        'mingw': pathlib.Path('/native/mingw-version.txt').read_text(),
    },
    'images': {
        'managed': 'mono:6.12.0.182@sha256:34d816779b1248b5cfd095770b64ecbaf1798e2aca693a91c11a018dce9c7ad5',
        'native': 'ubuntu:22.04@sha256:b8b6ee6aa931ecd9d0d952abc34dc0e5f7c6a30c6bb71b079fe399fde0329c02',
    },
    'runtime_requirements': {
        'windows': 'Windows 11 x64, installed .NET Framework 4.8 or 4.8.1',
        'linux': 'x86_64 glibc >= 2.35, Mono >= 6.8 with WinForms, libgdiplus, X11/XWayland',
    },
}
(artifacts / 'build-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
shutil.copy('/native/native-packages.txt', artifacts / 'native-build-packages.txt')
for platform, native_name in [('windows', 'NativeCore.dll'), ('linux', 'NativeCore.so')]:
    name = 'ReClass.NET_Next-' + platform + '-x64'
    root = pathlib.Path('/packages') / name
    shutil.copytree('/managed/app', root)
    for dependency in ('ColorCode.dll', 'Dia2Lib.dll', 'Microsoft.ExceptionMessageBox.dll'):
        shutil.copy('/dependencies/' + dependency, root / dependency)
    shutil.copy('/dependencies/Iced.dll', root / 'Iced.dll')
    shutil.copytree('/dependencies/Licenses', root / 'Licenses')
    (root / 'ASSEMBLY-DEPENDENCIES.json').write_text(json.dumps(assembly_dependencies, indent=2) + '\n')
    (root / 'Tools').mkdir()
    assembler = 'nasm.exe' if platform == 'windows' else 'nasm'
    shutil.copy('/native/nasm/' + platform + '/' + assembler, root / 'Tools' / assembler)
    (root / 'Tools' / assembler).chmod(0o755 if platform == 'linux' else 0o644)
    shutil.copy('/native/' + platform + '/out/' + native_name, root / native_name)
    shutil.copy('/metadata/LICENSE', root / 'LICENSE')
    shutil.copy('/metadata/DEBUGGER.md', root / 'DEBUGGER.md')
    shutil.copy(artifacts / 'build-manifest.json', root / 'BUILD.json')
    shutil.copy('/scripts/' + platform + '-README.txt', root / 'README.txt')
    (root / 'Plugins').mkdir()
    for item in root.iterdir():
        if item.is_file():
            item.chmod(0o644)
    if platform == 'windows':
        shutil.copy('/dependencies/x64/symsrv.dll', root / 'symsrv.dll')
        subprocess.run(['zip', '-qr', str(artifacts / (name + '.zip')), name], cwd='/packages', check=True)
    else:
        shutil.copy('/scripts/run.sh', root / 'run.sh')
        (root / 'run.sh').chmod(0o755)
        subprocess.run(['tar', '-czf', str(artifacts / (name + '.tar.gz')), '-C', '/packages', name], check=True)
lines = []
for item in sorted(artifacts.iterdir()):
    lines.append(hashlib.sha256(item.read_bytes()).hexdigest() + '  ' + item.name)
(artifacts / 'SHA256SUMS').write_text('\n'.join(lines) + '\n')
print('Created Windows/Linux x64 archives, manifest and SHA256SUMS')
