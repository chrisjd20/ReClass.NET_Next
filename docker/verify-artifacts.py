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
    print('PASS: checksums, package contents, x64 binaries, native exports and dependencies')

if __name__ == '__main__':
    verify(pathlib.Path(sys.argv[1]))
