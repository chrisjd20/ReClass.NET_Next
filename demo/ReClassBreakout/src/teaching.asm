; Tiny x64 teaching routines. The code is intentionally writable only by the
; external debugger; game reset never restores these bytes.
bits 64
default rel
%include "layout.inc"

section .text align=16
global breakout_decrement_ammo
global breakout_ammo_patchsite
global breakout_ammo_signature
global breakout_apply_damage
global breakout_damage_patchsite
global breakout_evaluate_vault
global breakout_vault_entry
global breakout_vault_endpoint

%macro ACTOR_TO_RAX 0
%ifidn __OUTPUT_FORMAT__, win64
    mov rax, rcx
%else
    mov rax, rdi
%endif
%endmacro

align 16
breakout_decrement_ammo:
    ACTOR_TO_RAX
    add rax, ACTOR_AMMO
    jmp short breakout_ammo_patchsite
; Unique inert signature is 16 bytes before the DEC and is never overwritten
; by a patch or hook at breakout_ammo_patchsite. Signature scan + 0x10.
breakout_ammo_signature:
    db 0x52,0x43,0x42,0x52,0x4b,0x41,0x4d,0x4d
    db 0x4f,0x35,0x35,0x21,0xa7,0x3c,0x6e,0x91
breakout_ammo_patchsite:
    dec dword [rax]
    times 24 nop
    ret

align 16
breakout_apply_damage:
    ACTOR_TO_RAX
breakout_damage_patchsite:
    sub dword [rax + ACTOR_HEALTH], byte 10
    times 24 nop
    ret

align 16
breakout_evaluate_vault:
    ACTOR_TO_RAX
breakout_vault_entry:
    cmp byte [rax + ACTOR_KEYCARD], 1
    jne .deny
    cmp dword [rax + ACTOR_CLEARANCE], ENGINEER_CLEARANCE
    jne .deny
    test dword [rax + ACTOR_FLAGS], ALARM_FLAG
    jnz .deny
    mov eax, 1
    jmp short breakout_vault_endpoint
.deny:
    xor eax, eax
breakout_vault_endpoint:
    nop
    ret

%ifidn __OUTPUT_FORMAT__, elf64
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
