"""Exercise remote memory on a controlled child, not an unrelated host process."""
import ctypes as c
import json
import os
import pathlib
import subprocess
import sys

if len(sys.argv) > 1 and sys.argv[1] == '--fixture':
    buffer = c.create_string_buffer(b'initial-value', 64)
    print(json.dumps({'pid': os.getpid(), 'address': c.addressof(buffer)}), flush=True)
    input()
    print(buffer.value.decode(), flush=True)
    sys.exit(0)

library = c.CDLL(str(pathlib.Path(sys.argv[1]).resolve()))
library.OpenRemoteProcess.argtypes = [c.c_void_p, c.c_int]
library.OpenRemoteProcess.restype = c.c_void_p
library.IsProcessValid.argtypes = [c.c_void_p]
library.IsProcessValid.restype = c.c_bool
library.CloseRemoteProcess.argtypes = [c.c_void_p]
for name in ('ReadRemoteMemory', 'WriteRemoteMemory'):
    function = getattr(library, name)
    function.argtypes = [c.c_void_p, c.c_void_p, c.c_void_p, c.c_int, c.c_int]
    function.restype = c.c_bool
with subprocess.Popen([sys.executable, __file__, '--fixture'], stdin=subprocess.PIPE,
                      stdout=subprocess.PIPE, text=True) as child:
    try:
        fixture = json.loads(child.stdout.readline())
        handle = library.OpenRemoteProcess(fixture['pid'], 2)
        assert library.IsProcessValid(handle)
        buffer = c.create_string_buffer(64)
        assert library.ReadRemoteMemory(handle, fixture['address'], buffer, 0, 64)
        assert buffer.value == b'initial-value'
        replacement = c.create_string_buffer(b'--updated-value')
        assert library.WriteRemoteMemory(handle, fixture['address'], replacement, 2, 14)
        child.stdin.write('\n')
        child.stdin.flush()
        assert child.stdout.readline().strip() == 'updated-value'
        assert child.wait(timeout=5) == 0
        library.CloseRemoteProcess(handle)
    finally:
        if child.poll() is None:
            child.kill()
print('PASS: controlled child process memory read/write, including buffer offset')
