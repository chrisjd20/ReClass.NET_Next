"""Check archive integrity and the native ABI consumed by the managed frontend."""
import hashlib
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
                         'Microsoft.ExceptionMessageBox.dll', 'LICENSE', 'BUILD.json', 'README.txt', 'Plugins'):
                assert (root / name).exists(), (platform, name)
            app = pefile.PE(str(root / 'ReClass.NET.exe'))
            assert app.FILE_HEADER.Machine == 0x8664, 'Managed application must target x64'
            assert app.OPTIONAL_HEADER.DATA_DIRECTORY[14].VirtualAddress, 'Missing CLR header'
        windows = temp / 'ReClass.NET_Next-windows-x64'
        native = pefile.PE(str(windows / 'NativeCore.dll'))
        assert native.FILE_HEADER.Machine == 0x8664
        exports = {entry.name.decode() for entry in native.DIRECTORY_ENTRY_EXPORT.symbols if entry.name}
        assert EXPORTS <= exports, ('Missing Windows exports', EXPORTS - exports)
        imports = {entry.dll.decode().lower() for entry in native.DIRECTORY_ENTRY_IMPORT}
        allowed = {'kernel32.dll', 'msvcrt.dll', 'user32.dll', 'dinput8.dll', 'psapi.dll',
                   'ntdll.dll', 'advapi32.dll', 'ole32.dll'}
        assert imports <= allowed, ('Unexpected Windows runtime DLLs', imports - allowed)
        assert (windows / 'symsrv.dll').exists()
        linux = temp / 'ReClass.NET_Next-linux-x64'
        assert not (linux / 'symsrv.dll').exists()
        assert (linux / 'run.sh').stat().st_mode & 0o111
        library = str(linux / 'NativeCore.so')
        header = subprocess.check_output(['readelf', '-h', library], text=True)
        assert 'ELF64' in header and 'Advanced Micro Devices X86-64' in header
        symbols = subprocess.check_output(['nm', '-D', '--defined-only', library], text=True)
        assert EXPORTS <= {line.split()[-1] for line in symbols.splitlines()}
        versions = subprocess.check_output(['readelf', '--version-info', library], text=True)
        glibc = [tuple(map(int, value.split('.'))) for value in re.findall(r'\bGLIBC_([0-9.]+)', versions)]
        assert not glibc or max(glibc) <= (2, 35), ('glibc baseline exceeded', max(glibc))
        dependencies = subprocess.check_output(['ldd', library], text=True)
        assert 'not found' not in dependencies, dependencies
    print('PASS: checksums, package contents, x64 binaries, native exports and dependencies')

if __name__ == '__main__':
    verify(pathlib.Path(sys.argv[1]))
