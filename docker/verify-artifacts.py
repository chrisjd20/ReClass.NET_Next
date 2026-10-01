"""Check archive integrity and the native ABI consumed by the managed frontend."""
import hashlib
import ctypes
import json
import pathlib
import re
import subprocess
import sys
import tarfile
import tempfile
import zipfile

import pefile

EXPORTS = {
    'IsProcessValid', 'OpenRemoteProcess', 'CloseRemoteProcess', 'ReadRemoteMemory',
    'WriteRemoteMemory', 'EnumerateProcesses', 'EnumerateRemoteSectionsAndModules',
    'DisassembleCode', 'ControlRemoteProcess', 'AttachDebuggerToProcess',
    'DetachDebuggerFromProcess', 'AwaitDebugEvent', 'HandleDebugEvent',
    'SetHardwareBreakpoint', 'InitializeInput', 'GetPressedKeys', 'ReleaseInput',
}
ADVANCED_EXPORTS = {'RcDebugQueryV1', 'RcDebugExecuteV1', 'RcDebugWaitV1'}

def verify_demo(root, platform):
    demo = root / 'Demo'
    build = json.loads((root / 'BUILD.json').read_text())['demo']
    assert build['rooms'] == 14
    dependencies = json.loads((demo / 'DEPENDENCIES.json').read_text())
    assert dependencies == build['dependencies']
    raylib = dependencies['raylib']
    fonts = dependencies['fonts']
    font_license = pathlib.Path(fonts['license'])
    assert (demo / font_license).read_bytes() == (root / font_license).read_bytes()
    assert hashlib.sha256((demo / font_license).read_bytes()).hexdigest() == fonts['license_sha256']
    assert set(fonts['files']) == {'LiberationSans-Regular.ttf', 'LiberationSans-Bold.ttf', 'LiberationMono-Regular.ttf'}
    assert raylib['version'] == '5.5'
    assert raylib['source_url'] == 'https://github.com/raysan5/raylib/archive/refs/tags/5.5.tar.gz'
    assert raylib['source_sha256'] == 'aea98ecf5bc5c5e0b789a76de0083a21a70457050ea4cc2aec7566935f5e258e'
    assert raylib['linkage'] == 'static' and not raylib['audio'] and not raylib['examples']
    for key in ('license', 'bundled_glfw_license', 'bundled_third_party_notices'):
        license_path = pathlib.Path(raylib[key])
        license_bytes = (demo / license_path).read_bytes()
        assert license_bytes == (root / license_path).read_bytes(), (platform, license_path)
        assert hashlib.sha256(license_bytes).hexdigest() == raylib[key + '_sha256']
    target = build['builds'][platform]
    executable = root / target['executable']
    assert hashlib.sha256(executable.read_bytes()).hexdigest() == target['sha256'], (platform, 'Demo checksum')
    for name in ('GUIDE.html', 'GUIDE.md', 'layout.md'):
        content = (demo / name).read_bytes()
        assert len(content) > 100, (platform, name)
        assert hashlib.sha256(content).hexdigest() == target['guides'][name], (platform, name)
    html = (demo / 'GUIDE.html').read_text()
    markdown = (demo / 'GUIDE.md').read_text()
    for room in range(0, 14):
        assert f'id="room-{room}"' in html, (platform, 'Missing HTML room', room)
        assert re.search(r'^## Room ' + str(room) + r': ', markdown, re.MULTILINE), (platform, 'Missing Markdown room', room)
    assert not re.search(r'<(?:script|link|img)\b[^>]*(?:src|href)\s*=\s*[\"\']https?://', html, re.IGNORECASE), 'Offline guide loads remote resources'
    if platform == 'windows':
        image = pefile.PE(str(executable))
        assert image.FILE_HEADER.Machine == 0x8664, 'Windows demo must be x64'
        assert not image.OPTIONAL_HEADER.DATA_DIRECTORY[14].VirtualAddress, 'Demo must be native'
        assert image.OPTIONAL_HEADER.DllCharacteristics & 0x40, 'Demo ASLR disabled'
        imports = {entry.dll.decode().lower() for entry in image.DIRECTORY_ENTRY_IMPORT}
        allowed = {'kernel32.dll', 'msvcrt.dll', 'user32.dll', 'gdi32.dll', 'opengl32.dll',
                   'winmm.dll', 'shell32.dll', 'advapi32.dll', 'ole32.dll', 'comdlg32.dll',
                   'imm32.dll', 'version.dll', 'ntdll.dll'}
        assert imports <= allowed, ('Unexpected demo runtime DLLs', imports - allowed)
    else:
        assert executable.stat().st_mode & 0o111, 'Linux demo is not executable'
        assert (demo / 'run-demo.sh').stat().st_mode & 0o111, 'Linux demo launcher is not executable'
        header = subprocess.check_output(['readelf', '-h', str(executable)], text=True)
        assert 'ELF64' in header and 'Advanced Micro Devices X86-64' in header, 'Linux demo must be x64'
        assert re.search(r'Type:\s+DYN', header), 'Linux demo must be a relocatable PIE'
        versions = subprocess.check_output(['readelf', '--version-info', str(executable)], text=True)
        glibc = [tuple(map(int, value.split('.'))) for value in re.findall(r'\bGLIBC_([0-9.]+)', versions)]
        assert not glibc or max(glibc) <= (2, 35), ('Demo glibc baseline exceeded', max(glibc))
        dynamic = subprocess.check_output(['readelf', '-d', str(executable)], text=True)
        needed = set(re.findall(r'\(NEEDED\).*?\[(.*?)\]', dynamic))
        allowed = {'libc.so.6', 'libm.so.6', 'libpthread.so.0', 'libdl.so.2', 'libstdc++.so.6',
                   'libgcc_s.so.1', 'libGL.so.1', 'libGLX.so.0', 'libOpenGL.so.0', 'libX11.so.6',
                   'libXrandr.so.2', 'libXinerama.so.1', 'libXcursor.so.1', 'libXi.so.6'}
        assert needed <= allowed, ('Unexpected demo runtime libraries', needed - allowed)
        resolved = subprocess.check_output(['ldd', str(executable)], text=True)
        assert 'not found' not in resolved, resolved

def verify(artifacts):
    for line in (artifacts / 'SHA256SUMS').read_text().splitlines():
        digest, name = line.split('  ', 1)
        assert hashlib.sha256((artifacts / name).read_bytes()).hexdigest() == digest, name
    with tempfile.TemporaryDirectory() as temp:
        temp = pathlib.Path(temp)
        with zipfile.ZipFile(artifacts / 'ReClass.NET_Next-windows-x64.zip') as archive:
            archive.extractall(temp)
        with tarfile.open(artifacts / 'ReClass.NET_Next-linux-x64.tar.gz') as archive:
            archive.extractall(temp)
        for platform in ('windows', 'linux'):
            root = temp / ('ReClass.NET_Next-' + platform + '-x64')
            for name in ('ReClass.NET.exe', 'ReClass.NET.exe.config', 'ColorCode.dll', 'Dia2Lib.dll',
                         'Microsoft.ExceptionMessageBox.dll', 'Iced.dll', 'LICENSE', 'BUILD.json', 'README.txt', 'DEBUGGER.md', 'Plugins',
                         'ASSEMBLY-DEPENDENCIES.json', 'Licenses/Iced-LICENSE.txt', 'Licenses/NASM-LICENSE.txt',
                         'Licenses/NASM-zlib-LICENSE.txt'):
                assert (root / name).exists(), (platform, name)
            dependencies = json.loads((root / 'ASSEMBLY-DEPENDENCIES.json').read_text())
            assert dependencies['iced']['version'] == '1.21.0'
            assert dependencies['nasm']['version'] == '3.02'
            assert hashlib.sha256((root / 'Iced.dll').read_bytes()).hexdigest() == dependencies['iced']['dll_sha256']
            iced = pefile.PE(str(root / 'Iced.dll'))
            assert iced.OPTIONAL_HEADER.DATA_DIRECTORY[14].VirtualAddress, 'Missing Iced CLR header'
            assembler = root / 'Tools' / ('nasm.exe' if platform == 'windows' else 'nasm')
            assert assembler.is_file(), (platform, 'Missing offline assembler')
            assert hashlib.sha256(assembler.read_bytes()).hexdigest() == dependencies['nasm']['builds'][platform]['sha256']
            app = pefile.PE(str(root / 'ReClass.NET.exe'))
            assert app.FILE_HEADER.Machine == 0x8664, 'Managed application must target x64'
            assert app.OPTIONAL_HEADER.DATA_DIRECTORY[14].VirtualAddress, 'Missing CLR header'
            verify_demo(root, platform)
        windows = temp / 'ReClass.NET_Next-windows-x64'
        native = pefile.PE(str(windows / 'NativeCore.dll'))
        assert native.FILE_HEADER.Machine == 0x8664
        exports = {entry.name.decode() for entry in native.DIRECTORY_ENTRY_EXPORT.symbols if entry.name}
        assert EXPORTS <= exports, ('Missing Windows exports', EXPORTS - exports)
        assert ADVANCED_EXPORTS <= exports, ('Missing Windows advanced exports', ADVANCED_EXPORTS - exports)
        imports = {entry.dll.decode().lower() for entry in native.DIRECTORY_ENTRY_IMPORT}
        allowed = {'kernel32.dll', 'msvcrt.dll', 'user32.dll', 'dinput8.dll', 'psapi.dll',
                   'ntdll.dll', 'advapi32.dll', 'ole32.dll'}
        assert imports <= allowed, ('Unexpected Windows runtime DLLs', imports - allowed)
        assert (windows / 'symsrv.dll').exists()
        assembler = pefile.PE(str(windows / 'Tools/nasm.exe'))
        assert assembler.FILE_HEADER.Machine == 0x8664, 'Windows NASM must be x64'
        assembler_imports = {entry.dll.decode().lower() for entry in assembler.DIRECTORY_ENTRY_IMPORT}
        assert assembler_imports <= {'kernel32.dll', 'msvcrt.dll'}, ('Unexpected assembler runtime DLLs', assembler_imports)
        linux = temp / 'ReClass.NET_Next-linux-x64'
        assert not (linux / 'symsrv.dll').exists()
        assert (linux / 'run.sh').stat().st_mode & 0o111
        library = str(linux / 'NativeCore.so')
        header = subprocess.check_output(['readelf', '-h', library], text=True)
        assert 'ELF64' in header and 'Advanced Micro Devices X86-64' in header
        symbols = subprocess.check_output(['nm', '-D', '--defined-only', library], text=True)
        assert EXPORTS <= {line.split()[-1] for line in symbols.splitlines()}
        assert ADVANCED_EXPORTS <= {line.split()[-1] for line in symbols.splitlines()}
        query = ctypes.CDLL(library).RcDebugQueryV1
        query.argtypes = [ctypes.c_uint32]
        query.restype = ctypes.c_uint64
        assert query(1) & 1, 'Linux advanced ABI v1 does not advertise session debugging'
        assert query(0) == 0, 'Unsupported advanced ABI must not advertise capabilities'
        versions = subprocess.check_output(['readelf', '--version-info', library], text=True)
        glibc = [tuple(map(int, value.split('.'))) for value in re.findall(r'\bGLIBC_([0-9.]+)', versions)]
        assert not glibc or max(glibc) <= (2, 35), ('glibc baseline exceeded', max(glibc))
        dependencies = subprocess.check_output(['ldd', library], text=True)
        assert 'not found' not in dependencies, dependencies
        assembler = str(linux / 'Tools/nasm')
        assert (linux / 'Tools/nasm').stat().st_mode & 0o111, 'Linux NASM is not executable'
        header = subprocess.check_output(['readelf', '-h', assembler], text=True)
        assert 'ELF64' in header and 'Advanced Micro Devices X86-64' in header
        versions = subprocess.check_output(['readelf', '--version-info', assembler], text=True)
        glibc = [tuple(map(int, value.split('.'))) for value in re.findall(r'\bGLIBC_([0-9.]+)', versions)]
        assert not glibc or max(glibc) <= (2, 35), ('Assembler glibc baseline exceeded', max(glibc))
        dependencies = subprocess.check_output(['ldd', assembler], text=True)
        assert 'not found' not in dependencies, dependencies
        version = subprocess.check_output([assembler, '--version'], text=True)
        assert re.search(r'NASM version 3\.02(?:\s|$)', version), version
    print('PASS: checksums, package contents, x64 binaries, native exports, demo guides/licenses and dependencies')

if __name__ == '__main__':
    verify(pathlib.Path(sys.argv[1]))
