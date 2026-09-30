bits 64
default rel
global fixture_modify
global fixture_hook
global fixture_bias

%ifidn __OUTPUT_FORMAT__, win64
%define object rcx
%else
%define object rdi
%endif

section .text
align 16
fixture_modify:
    dec dword [object]
    ret

align 16
fixture_hook:
    nop
    nop
    mov eax, [rel fixture_bias]
    test eax, eax
    jz .zero
    add dword [object], eax
    ret
.zero:
    inc dword [object]
    ret

section .data
align 4
fixture_bias: dd 3

%ifidn __OUTPUT_FORMAT__, elf64
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
