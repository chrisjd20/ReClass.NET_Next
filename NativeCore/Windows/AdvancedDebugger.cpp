#include <windows.h>
#include <tlhelp32.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <map>
#include <vector>

#include "../Shared/AdvancedDebugger.hpp"

#if defined(_WIN64)

// This ABI deliberately does not share state with the legacy debugger. A single
// managed worker owns an advanced session and all of its Win32 debug events.
namespace {
using namespace rcdebug;
constexpr DWORD ThreadAccess = THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME | THREAD_QUERY_INFORMATION;
constexpr DWORD ProcessAccess = PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_CREATE_THREAD | SYNCHRONIZE;
constexpr DWORD64 TrapFlag = 0x100;
constexpr DWORD64 ResumeFlag = 0x10000;

struct Slot { bool active = false; uint64_t address = 0; unsigned trigger = 0, length = 0; };
struct SavedSlot { bool owned = false; DWORD64 address = 0, control = 0; };
struct Region { uint64_t address, length; DWORD oldProtection; bool changed = false; };
struct Thread {
    HANDLE handle = nullptr;
    bool suspended = false; // Exactly one suspend increment, owned by our step.
    std::array<SavedSlot, 4> saved{};
    bool needsSlots = false;
};
struct Session {
    DWORD pid = 0, worker = 0;
    HANDLE process = nullptr;
    uint64_t identity = 0, breakAddress = 0, breakinAddress = 0;
    std::map<DWORD, Thread> threads;
    std::array<Slot, 4> slots{};
    DEBUG_EVENT pending{};
    EventRecord record{};
    bool hasPending = false, reported = false, attaching = false;
    bool pauseRequested = false, faulted = false, exited = false;
    DWORD breakThread = 0, stepping = 0;
    bool priorTrap = false;
    uint64_t recoveryAddress = 0;
    std::vector<unsigned char> recoveryBytes;
    std::vector<Region> recoveryPages;
    std::map<DWORD, CONTEXT> recoveryContexts;
};
Session session;

DWORD lastError(DWORD fallback = ERROR_GEN_FAILURE) { const DWORD e = GetLastError(); return e ? e : fallback; }
int fail(Request& r, DWORD error) { r.error = error; SetLastError(error); return -1; }
int waitFail(EventRecord* event, DWORD error) {
    if (event && event->size >= offsetof(EventRecord, code) + sizeof(event->code)) event->code = error;
    SetLastError(error); return -1;
}
bool validRange(uint64_t address, uint64_t length) {
    return length && length <= std::numeric_limits<SIZE_T>::max() &&
        address <= std::numeric_limits<uintptr_t>::max() - (length - 1);
}
LPVOID pointer(uint64_t address) { return reinterpret_cast<LPVOID>(static_cast<uintptr_t>(address)); }
DWORD64& debugAddress(CONTEXT& c, unsigned slot) {
    switch (slot) { case 0: return c.Dr0; case 1: return c.Dr1; case 2: return c.Dr2; default: return c.Dr3; }
}
DWORD64 slotMask(unsigned slot) { return (DWORD64(3) << (2 * slot)) | (DWORD64(15) << (16 + 4 * slot)); }
unsigned lengthCode(unsigned length) { return length == 8 ? 2 : length == 4 ? 3 : length == 2 ? 1 : 0; }

bool getContext(Thread& thread, CONTEXT& c) {
    std::memset(&c, 0, sizeof(c));
    c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_DEBUG_REGISTERS;
    return GetThreadContext(thread.handle, &c) != FALSE;
}
bool canOwnSingleStep(uint64_t rip) {
    // ICEBP/INT1 intentionally generates the same Windows exception code as
    // TF stepping. Reject it before acquiring TF so a missing DR6 report cannot
    // turn the target's own debug exception into an owned completion event.
    for (unsigned i = 0; i != 15; ++i) {
        if (rip > std::numeric_limits<uint64_t>::max() - i) { SetLastError(ERROR_INVALID_ADDRESS); return false; }
        unsigned char opcode = 0; SIZE_T read = 0;
        if (!ReadProcessMemory(session.process, pointer(rip + i), &opcode, 1, &read) || read != 1) return false;
        const bool prefix = opcode == 0xf0 || opcode == 0xf2 || opcode == 0xf3 || opcode == 0x66 || opcode == 0x67 ||
            opcode == 0x2e || opcode == 0x36 || opcode == 0x3e || opcode == 0x26 || opcode == 0x64 || opcode == 0x65 ||
            (opcode >= 0x40 && opcode <= 0x4f);
        if (prefix) continue;
        if (opcode == 0xf1) { SetLastError(ERROR_NOT_SUPPORTED); return false; }
        return true;
    }
    SetLastError(ERROR_NOT_SUPPORTED); return false;
}
void copyContext(DWORD id, const CONTEXT& c, ContextRecord& output) {
    output = {};
    output.size = sizeof(output); output.available = 1; output.thread = id;
    output.rax = c.Rax; output.rbx = c.Rbx; output.rcx = c.Rcx; output.rdx = c.Rdx;
    output.rsi = c.Rsi; output.rdi = c.Rdi; output.rbp = c.Rbp; output.rsp = c.Rsp;
    output.r8 = c.R8; output.r9 = c.R9; output.r10 = c.R10; output.r11 = c.R11;
    output.r12 = c.R12; output.r13 = c.R13; output.r14 = c.R14; output.r15 = c.R15;
    output.rip = c.Rip; output.rflags = c.EFlags;
    // Win32 CONTEXT contains segment selectors, not usable FS/GS bases. Do not
    // claim those bases are available or substitute zero for an unknown base.
}
bool x64Identity(HANDLE process, uint64_t& identity) {
    DWORD state = WaitForSingleObject(process, 0);
    if (state != WAIT_TIMEOUT) { if (state != WAIT_FAILED) SetLastError(ERROR_PROCESS_ABORTED); return false; }
    BOOL wow64 = FALSE;
    SYSTEM_INFO system{}; GetNativeSystemInfo(&system);
    if (system.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64) { SetLastError(ERROR_NOT_SUPPORTED); return false; }
    if (!IsWow64Process(process, &wow64)) return false;
    if (wow64) { SetLastError(ERROR_NOT_SUPPORTED); return false; }
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(process, &creation, &exit, &kernel, &user)) return false;
    identity = (uint64_t(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
    return true;
}
void findSystemBreakpoints() {
    session.breakAddress = session.breakinAddress = 0;
    HMODULE local = GetModuleHandleW(L"ntdll.dll");
    if (!local) return;
    FARPROC breakpoint = GetProcAddress(local, "DbgBreakPoint");
    FARPROC breakin = GetProcAddress(local, "DbgUiRemoteBreakin");
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, session.pid);
    if (snapshot == INVALID_HANDLE_VALUE) return;
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (Module32FirstW(snapshot, &entry)) {
        do {
            if (_wcsicmp(entry.szModule, L"ntdll.dll") == 0) {
                uint64_t remote = reinterpret_cast<uintptr_t>(entry.modBaseAddr);
                if (breakpoint) session.breakAddress = remote + (reinterpret_cast<uintptr_t>(breakpoint) - reinterpret_cast<uintptr_t>(local));
                if (breakin) session.breakinAddress = remote + (reinterpret_cast<uintptr_t>(breakin) - reinterpret_cast<uintptr_t>(local));
                break;
            }
        } while (Module32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
}
void closeSession() {
    for (auto& item : session.threads) if (item.second.handle) CloseHandle(item.second.handle);
    if (session.process) CloseHandle(session.process);
    session = Session{};
}
bool suspendedThread(Thread& thread) {
    if (thread.suspended) return true;
    if (SuspendThread(thread.handle) == DWORD(-1)) return false;
    thread.suspended = true;
    return true;
}
bool releaseSuspends() {
    DWORD failure = 0;
    for (auto& item : session.threads) {
        Thread& thread = item.second;
        if (thread.suspended) {
            if (ResumeThread(thread.handle) == DWORD(-1)) { if (!failure) failure = lastError(); }
            else thread.suspended = false;
        }
    }
    if (failure) { SetLastError(failure); return false; }
    return true;
}
bool clearStep() {
    if (session.stepping) {
        auto it = session.threads.find(session.stepping);
        if (it != session.threads.end()) {
            CONTEXT c{};
            if (!getContext(it->second, c)) return false;
            if (!session.priorTrap) c.EFlags &= ~static_cast<DWORD>(TrapFlag);
            c.ContextFlags = CONTEXT_CONTROL;
            if (!SetThreadContext(it->second.handle, &c)) return false;
        }
        session.stepping = 0;
    }
    return releaseSuspends();
}
bool installThreadSlots(DWORD id, Thread& thread) {
    bool any = false;
    for (const Slot& slot : session.slots) if (slot.active) any = true;
    if (!any) return true;
    CONTEXT original{};
    if (!getContext(thread, original)) return false;
    CONTEXT changed = original;
    auto saved = thread.saved;
    for (unsigned i = 0; i != 4; ++i) {
        const Slot& slot = session.slots[i];
        if (!slot.active) continue;
        if (changed.Dr7 & (DWORD64(3) << (2 * i))) { SetLastError(ERROR_BUSY); return false; }
        saved[i] = {true, debugAddress(changed, i), changed.Dr7 & slotMask(i)};
        debugAddress(changed, i) = slot.address;
        changed.Dr7 = (changed.Dr7 & ~slotMask(i)) | (DWORD64(1) << (2 * i)) |
            (DWORD64(slot.trigger | (lengthCode(slot.length) << 2)) << (16 + 4 * i));
    }
    changed.ContextFlags = CONTEXT_DEBUG_REGISTERS;
    session.recoveryContexts.emplace(id, original); // Allocate before mutation.
    if (!SetThreadContext(thread.handle, &changed)) {
        DWORD error = lastError(); original.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (!SetThreadContext(thread.handle, &original)) session.faulted = true;
        else session.recoveryContexts.erase(id);
        SetLastError(error); return false;
    }
    session.recoveryContexts.erase(id);
    thread.saved = saved;
    return true;
}
bool addThread(DWORD id, HANDLE eventHandle) {
    if (session.threads.count(id)) return true;
    HANDLE own = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), eventHandle, GetCurrentProcess(), &own, 0, FALSE, DUPLICATE_SAME_ACCESS)) return false;
    Thread thread; thread.handle = own;
    decltype(session.threads)::iterator inserted;
    try { inserted = session.threads.emplace(id, thread).first; }
    catch (...) { CloseHandle(own); throw; }
    inserted->second.needsSlots = true;
    if (!installThreadSlots(id, inserted->second)) return false;
    inserted->second.needsSlots = false;
    // A new thread must not run through a temporarily uncovered INT3 site.
    if (session.stepping && session.stepping != id &&
        !(session.pauseRequested && session.breakThread == id) && !suspendedThread(inserted->second)) return false;
    return true;
}
DWORD normalDisposition() {
    if (session.pending.dwDebugEventCode != EXCEPTION_DEBUG_EVENT) return DBG_CONTINUE;
    if (session.record.kind == Attached || session.record.kind == PauseEvent ||
        session.record.kind == SingleStep || session.record.causedBy) return DBG_CONTINUE;
    return DBG_EXCEPTION_NOT_HANDLED;
}
bool continuePending(DWORD disposition, bool releaseStep) {
    if (!session.hasPending) { SetLastError(ERROR_INVALID_STATE); return false; }
    DEBUG_EVENT e = session.pending;
    const bool exiting = e.dwDebugEventCode == EXIT_PROCESS_DEBUG_EVENT;
    // Once the process has exited there are no live code bytes or suspend
    // counts to recover. Its exit event still needs its single continuation.
    if (!exiting && (session.faulted || !session.recoveryBytes.empty())) { SetLastError(ERROR_WRITE_FAULT); return false; }
    if (!exiting && releaseStep && !clearStep()) return false;
    if (!ContinueDebugEvent(e.dwProcessId, e.dwThreadId, disposition)) return false;
    session.hasPending = false; session.reported = false;
    if (e.dwDebugEventCode == EXIT_THREAD_DEBUG_EVENT) {
        auto it = session.threads.find(e.dwThreadId);
        if (it != session.threads.end()) { CloseHandle(it->second.handle); session.threads.erase(it); }
        if (session.breakThread == e.dwThreadId) session.breakThread = 0;
    }
    if (e.dwDebugEventCode == EXIT_PROCESS_DEBUG_EVENT) closeSession();
    return true;
}

// Receive owns the event before doing anything that can fail. Failure leaves it
// outstanding, so even a new-thread watchpoint failure cannot release that thread.
int receive(DWORD timeout) {
    if (session.hasPending) return 0;
    DEBUG_EVENT e{};
    if (!WaitForDebugEvent(&e, timeout)) {
        const DWORD error = GetLastError();
        if (error == ERROR_SEM_TIMEOUT) return 1;
        SetLastError(error); return -1;
    }
    session.pending = e; session.hasPending = true; session.reported = false;
    session.record = {}; session.record.size = sizeof(EventRecord);
    session.record.process = e.dwProcessId; session.record.thread = e.dwThreadId;
    session.record.context.size = sizeof(ContextRecord); session.record.context.thread = e.dwThreadId;
    if (e.dwProcessId != session.pid) { session.faulted = true; SetLastError(ERROR_INVALID_DATA); return -1; }
    switch (e.dwDebugEventCode) {
    case CREATE_PROCESS_DEBUG_EVENT:
        if (e.u.CreateProcessInfo.hFile) CloseHandle(e.u.CreateProcessInfo.hFile);
        if (!addThread(e.dwThreadId, e.u.CreateProcessInfo.hThread)) { session.faulted = true; return -1; }
        break;
    case CREATE_THREAD_DEBUG_EVENT:
        session.record.kind = ThreadCreated;
        session.record.address = reinterpret_cast<uintptr_t>(e.u.CreateThread.lpStartAddress);
        if ((session.attaching || session.pauseRequested) && session.breakinAddress && session.record.address == session.breakinAddress)
            session.breakThread = e.dwThreadId;
        if (!addThread(e.dwThreadId, e.u.CreateThread.hThread)) { session.faulted = true; return -1; }
        break;
    case EXIT_THREAD_DEBUG_EVENT:
        session.record.kind = ThreadExited; session.record.code = e.u.ExitThread.dwExitCode;
        if (session.stepping == e.dwThreadId) session.stepping = 0;
        break;
    case EXIT_PROCESS_DEBUG_EVENT:
        session.record.kind = ProcessExited; session.record.code = e.u.ExitProcess.dwExitCode;
        session.exited = true;
        break;
    case LOAD_DLL_DEBUG_EVENT:
        if (e.u.LoadDll.hFile) CloseHandle(e.u.LoadDll.hFile);
        session.record.kind = 10; // ModuleChanged (additive shared event kind).
        session.record.address = reinterpret_cast<uintptr_t>(e.u.LoadDll.lpBaseOfDll);
        session.record.code = 1;
        break;
    case UNLOAD_DLL_DEBUG_EVENT:
        session.record.kind = 10;
        session.record.address = reinterpret_cast<uintptr_t>(e.u.UnloadDll.lpBaseOfDll);
        session.record.code = 2;
        break;
    case EXCEPTION_DEBUG_EVENT: {
        session.record.kind = Exception;
        session.record.code = e.u.Exception.ExceptionRecord.ExceptionCode;
        session.record.address = reinterpret_cast<uintptr_t>(e.u.Exception.ExceptionRecord.ExceptionAddress);
        session.record.firstChance = e.u.Exception.dwFirstChance;
        auto it = session.threads.find(e.dwThreadId);
        CONTEXT c{};
        if (it == session.threads.end() || !getContext(it->second, c)) return -1;
        copyContext(e.dwThreadId, c, session.record.context);
        if (session.record.code == EXCEPTION_BREAKPOINT) {
            if (session.attaching && session.breakAddress && session.record.address == session.breakAddress) {
                session.record.kind = Attached;
            } else if (session.pauseRequested && e.dwThreadId == session.breakThread &&
                session.record.address == session.breakAddress) {
                session.record.kind = PauseEvent; session.pauseRequested = false;
            } else session.record.kind = Breakpoint;
        } else if (session.record.code == EXCEPTION_SINGLE_STEP) {
            const CONTEXT originalContext = c;
            DWORD64 enabledDr6 = 0;
            for (unsigned i = 0; i != 4; ++i)
                if (c.Dr7 & (DWORD64(3) << (2 * i))) enabledDr6 |= c.Dr6 & (DWORD64(1) << i);
            DWORD64 ownedDr6 = 0;
            for (unsigned i = 0; i != 4; ++i) {
                if (session.slots[i].active && it->second.saved[i].owned && (enabledDr6 & (DWORD64(1) << i))) {
                    session.record.causedBy |= 1u << i; ownedDr6 |= DWORD64(1) << i;
                }
            }
            // Windows can return DR6=0 for a TF exception when no hardware
            // breakpoint is active. The explicit outstanding TF request still
            // owns this selected-thread, first-chance trap at its event RIP.
            // ICEBP is rejected before starting the step; exceptions on other
            // threads and enabled unrelated hardware causes retain disposition.
            const bool nativeTrapWithoutDr6 = c.Dr6 == 0 && e.u.Exception.dwFirstChance && session.record.address == c.Rip;
            const bool ownedStep = session.stepping == e.dwThreadId && ((c.Dr6 & (DWORD64(1) << 14)) || nativeTrapWithoutDr6);
            // Intel specifies that B0..B3 may be set even for disabled slots.
            // DR6 alone cannot establish an unrelated hardware exception.
            const DWORD64 unknown = enabledDr6 & ~ownedDr6;
            const bool unrelatedStatus = unknown || (c.Dr6 & ((DWORD64(1) << 13) | (DWORD64(1) << 15))) ||
                ((c.Dr6 & (DWORD64(1) << 14)) && !ownedStep);
            if (unrelatedStatus) {
                if (std::getenv("RECLASS_NATIVE_DEBUG")) { std::fprintf(stderr,
                    "RcDebug step: thread=%lu selected=%lu dr6=%llx dr7=%llx rip=%llx flags=%lx unrelated=%llx\n",
                    static_cast<unsigned long>(e.dwThreadId), static_cast<unsigned long>(session.stepping),
                    static_cast<unsigned long long>(c.Dr6), static_cast<unsigned long long>(c.Dr7),
                    static_cast<unsigned long long>(c.Rip), static_cast<unsigned long>(c.EFlags), static_cast<unsigned long long>(unknown)); std::fflush(stderr); }
                // A target-owned debug cause can coincide with our watchpoint.
                // Give the target its normal exception rather than swallowing
                // it merely because an owned DR6 bit also happened to be set.
                session.record.causedBy = 0;
                break;
            }
            if (ownedStep) session.record.kind = SingleStep;
            else if (session.record.causedBy) session.record.kind = Breakpoint;
            if (std::getenv("RECLASS_NATIVE_DEBUG")) { std::fprintf(stderr,
                "RcDebug step: thread=%lu selected=%lu dr6=%llx dr7=%llx rip=%llx flags=%lx kind=%lu\n",
                static_cast<unsigned long>(e.dwThreadId), static_cast<unsigned long>(session.stepping),
                static_cast<unsigned long long>(c.Dr6), static_cast<unsigned long long>(c.Dr7),
                static_cast<unsigned long long>(c.Rip), static_cast<unsigned long>(c.EFlags), static_cast<unsigned long>(session.record.kind)); std::fflush(stderr); }
            // Preserve unrelated DR6 status; clear only causes we own. Hardware
            // execution breakpoints need RF to execute their instruction once.
            c.Dr6 &= ~ownedDr6;
            if (ownedStep && !session.priorTrap) { c.EFlags &= ~static_cast<DWORD>(TrapFlag); c.Dr6 &= ~(DWORD64(1) << 14); }
            for (unsigned i = 0; i != 4; ++i)
                if ((session.record.causedBy & (1u << i)) && session.slots[i].trigger == 0) c.EFlags |= static_cast<DWORD>(ResumeFlag);
            if (ownedDr6 || ownedStep) {
                session.recoveryContexts.emplace(e.dwThreadId, originalContext);
                c.ContextFlags = CONTEXT_CONTROL | CONTEXT_DEBUG_REGISTERS;
                if (!SetThreadContext(it->second.handle, &c)) { session.faulted = true; return -1; }
                session.recoveryContexts.erase(e.dwThreadId);
            }
        }
        break;
    }
    default: break;
    }
    return 0;
}

bool stopped() { return session.hasPending && !session.exited; }
bool pause() {
    if (stopped()) return true;
    if (session.exited) { SetLastError(ERROR_PROCESS_ABORTED); return false; }
    if (!session.pauseRequested) {
        findSystemBreakpoints();
        if (!session.breakAddress || !session.breakinAddress) { SetLastError(ERROR_NOT_SUPPORTED); return false; }
        if (!DebugBreakProcess(session.process)) return false;
        session.pauseRequested = true; session.breakThread = 0;
    }
    const ULONGLONG deadline = GetTickCount64() + 5000;
    while (GetTickCount64() < deadline) {
        int status = receive(50);
        if (status < 0) return false;
        if (status > 0) continue;
        if (session.record.kind == ProcessExited) { SetLastError(ERROR_PROCESS_ABORTED); return false; }
        if (session.pending.dwDebugEventCode == EXCEPTION_DEBUG_EVENT) return true;
        if (!continuePending(DBG_CONTINUE, false)) return false;
    }
    SetLastError(ERROR_TIMEOUT); return false;
}

int abortAttach(Request& request, DWORD originalError) {
    // Initial attachment has not published patches or debug-register state.
    // Roll back a failed attach rather than leaving a hidden native session
    // behind a managed "Detached" state. No target exception is swallowed.
    if (session.hasPending) {
        DWORD disposition = normalDisposition();
        if (!ContinueDebugEvent(session.pending.dwProcessId, session.pending.dwThreadId, disposition)) {
            session.faulted = true; request.result = 1; return fail(request, lastError());
        }
        session.hasPending = false;
    }
    if (session.pid && !DebugActiveProcessStop(session.pid)) {
        session.faulted = true; request.result = 1; return fail(request, lastError());
    }
    closeSession(); return fail(request, originalError);
}

bool setHardware(unsigned index, const Slot& proposed) {
    if (!session.recoveryContexts.empty()) { SetLastError(ERROR_WRITE_FAULT); return false; }
    struct Change { DWORD id; CONTEXT original, modified; SavedSlot save; };
    std::vector<Change> changes;
    for (auto& item : session.threads) {
        Change change{}; change.id = item.first;
        if (!getContext(item.second, change.original)) return false;
        change.modified = change.original;
        SavedSlot old = item.second.saved[index];
        change.save = old;
        if (proposed.active) {
            if (old.owned || (change.original.Dr7 & (DWORD64(3) << (2 * index)))) { SetLastError(ERROR_BUSY); return false; }
            change.save = {true, debugAddress(change.original, index), change.original.Dr7 & slotMask(index)};
            debugAddress(change.modified, index) = proposed.address;
            change.modified.Dr7 = (change.original.Dr7 & ~slotMask(index)) | (DWORD64(1) << (2 * index)) |
                (DWORD64(proposed.trigger | (lengthCode(proposed.length) << 2)) << (16 + 4 * index));
        } else if (old.owned) {
            const Slot& installed = session.slots[index];
            const DWORD64 expected = (DWORD64(1) << (2 * index)) |
                (DWORD64(installed.trigger | (lengthCode(installed.length) << 2)) << (16 + 4 * index));
            if (debugAddress(change.original, index) != installed.address || (change.original.Dr7 & slotMask(index)) != expected) {
                SetLastError(ERROR_BUSY); return false;
            }
            debugAddress(change.modified, index) = old.address;
            change.modified.Dr7 = (change.original.Dr7 & ~slotMask(index)) | old.control;
            change.modified.Dr6 &= ~(DWORD64(1) << index);
            change.save = {};
        }
        change.modified.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        changes.push_back(change);
    }
    size_t applied = 0;
    std::map<DWORD, CONTEXT> rollback;
    for (const Change& change : changes) rollback.emplace(change.id, change.original);
    for (; applied != changes.size(); ++applied) {
        if (!SetThreadContext(session.threads.at(changes[applied].id).handle, &changes[applied].modified)) break;
    }
    if (applied != changes.size()) {
        const DWORD error = lastError(); bool recovered = true;
        // Include the failing call: do not assume a failed context mutation was
        // guaranteed to leave every register untouched.
        for (size_t i = 0; i <= applied; ++i) {
            changes[i].original.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (!SetThreadContext(session.threads.at(changes[i].id).handle, &changes[i].original)) recovered = false;
        }
        if (!recovered) { session.faulted = true; session.recoveryContexts.swap(rollback); }
        SetLastError(recovered ? error : ERROR_WRITE_FAULT); return false;
    }
    for (const Change& change : changes) session.threads.at(change.id).saved[index] = change.save;
    session.slots[index] = proposed;
    return true;
}

DWORD protection(unsigned flags) {
    switch (flags) {
    case 0: return PAGE_NOACCESS;
    case 1: return PAGE_READONLY;
    case 2: case 3: return PAGE_READWRITE;
    case 4: return PAGE_EXECUTE;
    case 5: return PAGE_EXECUTE_READ;
    case 6: case 7: return PAGE_EXECUTE_READWRITE;
    default: return 0;
    }
}
unsigned abstractProtection(DWORD value) {
    switch (value & 0xff) {
    case PAGE_READONLY: return 1;
    case PAGE_READWRITE: case PAGE_WRITECOPY: return 3;
    case PAGE_EXECUTE: return 4;
    case PAGE_EXECUTE_READ: return 5;
    case PAGE_EXECUTE_READWRITE: case PAGE_EXECUTE_WRITECOPY: return 7;
    default: return 0;
    }
}
bool regions(uint64_t address, uint64_t length, std::vector<Region>& output) {
    uint64_t cursor = address, remaining = length;
    while (remaining) {
        MEMORY_BASIC_INFORMATION information{};
        if (!VirtualQueryEx(session.process, pointer(cursor), &information, sizeof(information))) return false;
        if (information.State != MEM_COMMIT || (information.Protect & (PAGE_GUARD | PAGE_NOACCESS))) { SetLastError(ERROR_NOACCESS); return false; }
        uint64_t base = reinterpret_cast<uintptr_t>(information.BaseAddress);
        if (cursor < base || information.RegionSize <= cursor - base) { SetLastError(ERROR_INVALID_ADDRESS); return false; }
        uint64_t chunk = std::min<uint64_t>(remaining, information.RegionSize - (cursor - base));
        output.push_back({cursor, chunk, information.Protect, false});
        remaining -= chunk;
        if (remaining) cursor += chunk;
    }
    return true;
}
bool restoreProtection(std::vector<Region>& pages) {
    bool success = true; DWORD first = 0;
    for (auto& page : pages) {
        if (!page.changed) continue;
        DWORD ignored = 0;
        if (!VirtualProtectEx(session.process, pointer(page.address), static_cast<SIZE_T>(page.length), page.oldProtection, &ignored)) {
            success = false; if (!first) first = lastError();
        } else page.changed = false;
    }
    if (!success) SetLastError(first);
    return success;
}
bool writable(std::vector<Region>& pages) {
    for (auto& page : pages) {
        DWORD ignored = 0;
        DWORD target = (abstractProtection(page.oldProtection) & 4) ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        if (!VirtualProtectEx(session.process, pointer(page.address), static_cast<SIZE_T>(page.length), target, &ignored)) return false;
        page.changed = true;
    }
    return true;
}
bool exactRead(uint64_t address, void* buffer, uint64_t length) {
    SIZE_T read = 0;
    if (!ReadProcessMemory(session.process, pointer(address), buffer, static_cast<SIZE_T>(length), &read)) return false;
    if (read != length) { SetLastError(ERROR_PARTIAL_COPY); return false; }
    return true;
}
bool verifyBytes(uint64_t address, const void* bytes, uint64_t length, std::vector<unsigned char>& readback) {
    if (!exactRead(address, readback.data(), length)) return false;
    if (std::memcmp(readback.data(), bytes, static_cast<size_t>(length)) != 0) { SetLastError(ERROR_WRITE_FAULT); return false; }
    return true;
}
bool writeCode(Request& r, const void* bytes) {
    const bool recovering = !session.recoveryBytes.empty();
    if (recovering && (r.address != session.recoveryAddress || r.length != session.recoveryBytes.size() ||
        std::memcmp(bytes, session.recoveryBytes.data(), static_cast<size_t>(r.length)) != 0)) {
        r.result = 1; SetLastError(ERROR_WRITE_FAULT); return false;
    }
    std::vector<Region> pages;
    if (!regions(r.address, r.length, pages)) return false;
    std::vector<unsigned char> original(static_cast<size_t>(r.length));
    // All potentially throwing allocations precede protection or byte changes.
    std::vector<unsigned char> readback(static_cast<size_t>(r.length));
    if (!exactRead(r.address, original.data(), r.length)) return false;
    bool changedBytes = false, success = writable(pages);
    DWORD error = success ? 0 : lastError();
    if (success) {
        SIZE_T written = 0; changedBytes = true;
        const BOOL writeSucceeded = WriteProcessMemory(session.process, pointer(r.address), bytes, static_cast<SIZE_T>(r.length), &written);
        success = writeSucceeded && written == r.length;
        if (!success) error = writeSucceeded ? ERROR_PARTIAL_COPY : lastError();
    }
    if (!restoreProtection(pages)) { if (!error) error = lastError(); success = false; }
    if (success && !FlushInstructionCache(session.process, pointer(r.address), static_cast<SIZE_T>(r.length))) { error = lastError(); success = false; }
    if (success && !verifyBytes(r.address, bytes, r.length, readback)) { error = lastError(); success = false; }
    if (success && recovering && !restoreProtection(session.recoveryPages)) { error = lastError(); success = false; }
    if (success) {
        if (recovering) { session.recoveryBytes.clear(); session.recoveryPages.clear(); session.recoveryAddress = 0; }
        r.result = r.length; return true;
    }
    // Any short write or cache/protection/readback failure is recovered while
    // the same global debug stop remains held. Cancellation cannot cut this off.
    bool recovered = true;
    if (changedBytes) {
        recovered = writable(pages);
        SIZE_T restored = 0;
        if (recovered) recovered = WriteProcessMemory(session.process, pointer(r.address), original.data(), static_cast<SIZE_T>(r.length), &restored) && restored == r.length;
    }
    if (!restoreProtection(pages)) recovered = false;
    if (changedBytes) {
        if (!FlushInstructionCache(session.process, pointer(r.address), static_cast<SIZE_T>(r.length))) recovered = false;
        if (!verifyBytes(r.address, original.data(), r.length, readback)) recovered = false;
    }
    if (!recovered || recovering) {
        if (!recovering) {
            session.recoveryAddress = r.address;
            session.recoveryBytes.swap(original); session.recoveryPages.swap(pages);
        }
        r.result = 1;
    }
    SetLastError(recovered ? (error ? error : ERROR_WRITE_FAULT) : ERROR_WRITE_FAULT);
    return false;
}

LPVOID allocateNear(uint64_t anchor, uint64_t length, DWORD protect) {
    SYSTEM_INFO info{}; GetSystemInfo(&info);
    const uint64_t granularity = info.dwAllocationGranularity;
    const uint64_t minAddress = reinterpret_cast<uintptr_t>(info.lpMinimumApplicationAddress);
    const uint64_t maxAddress = reinterpret_cast<uintptr_t>(info.lpMaximumApplicationAddress);
    if (!anchor || anchor < minAddress || anchor > maxAddress) return nullptr;
    const uint64_t reach = 0x7fff0000ULL;
    const uint64_t low = anchor > reach ? std::max(minAddress, anchor - reach) : minAddress;
    const uint64_t high = anchor < maxAddress - std::min(reach, maxAddress) ? anchor + reach : maxAddress;
    std::vector<uint64_t> candidates;
    uint64_t cursor = low;
    while (cursor <= high) {
        MEMORY_BASIC_INFORMATION region{};
        if (!VirtualQueryEx(session.process, pointer(cursor), &region, sizeof(region))) break;
        uint64_t base = reinterpret_cast<uintptr_t>(region.BaseAddress);
        if (!region.RegionSize || base > std::numeric_limits<uint64_t>::max() - region.RegionSize) break;
        uint64_t end = base + region.RegionSize;
        if (end <= cursor) break;
        if (region.State == MEM_FREE) {
            uint64_t first = std::max(base, low);
            first = (first + granularity - 1) & ~(granularity - 1);
            const uint64_t bound = std::min(end, high);
            if (first <= bound && length <= bound - first) {
                uint64_t last = (bound - length) & ~(granularity - 1);
                uint64_t preferred = anchor & ~(granularity - 1);
                candidates.push_back(std::max(first, std::min(last, preferred)));
            }
        }
        cursor = end;
    }
    auto distance = [anchor](uint64_t address) { return address > anchor ? address - anchor : anchor - address; };
    std::sort(candidates.begin(), candidates.end(), [&](uint64_t a, uint64_t b) { return distance(a) < distance(b); });
    for (uint64_t candidate : candidates) {
        LPVOID result = VirtualAllocEx(session.process, pointer(candidate), static_cast<SIZE_T>(length), MEM_RESERVE | MEM_COMMIT, protect);
        if (result) return result;
    }
    return nullptr;
}

bool recover() {
    if (!stopped() && !pause()) return false;
    if (!session.recoveryBytes.empty()) {
        std::vector<unsigned char> verified(session.recoveryBytes.size());
        bool restored = writable(session.recoveryPages);
        SIZE_T written = 0;
        if (restored) restored = WriteProcessMemory(session.process, pointer(session.recoveryAddress), session.recoveryBytes.data(), session.recoveryBytes.size(), &written) && written == session.recoveryBytes.size();
        if (!restoreProtection(session.recoveryPages)) restored = false;
        if (!FlushInstructionCache(session.process, pointer(session.recoveryAddress), session.recoveryBytes.size())) restored = false;
        if (!verifyBytes(session.recoveryAddress, session.recoveryBytes.data(), session.recoveryBytes.size(), verified)) restored = false;
        for (const Region& page : session.recoveryPages) {
            MEMORY_BASIC_INFORMATION information{};
            if (!VirtualQueryEx(session.process, pointer(page.address), &information, sizeof(information)) || information.Protect != page.oldProtection) restored = false;
        }
        if (!restored) { SetLastError(ERROR_WRITE_FAULT); return false; }
        session.recoveryBytes.clear(); session.recoveryPages.clear(); session.recoveryAddress = 0;
    }
    for (auto& item : session.recoveryContexts) {
        auto thread = session.threads.find(item.first);
        if (thread == session.threads.end()) continue;
        CONTEXT original = item.second;
        // Records include all debug registers and, for trap handling, control
        // state. Restore precisely those fields that the failed operation used.
        original.ContextFlags = item.second.ContextFlags & (CONTEXT_CONTROL | CONTEXT_DEBUG_REGISTERS);
        if (!(original.ContextFlags & 0x10)) original.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        CONTEXT verified{};
        if (!SetThreadContext(thread->second.handle, &original) || !getContext(thread->second, verified)) return false;
        if (verified.Dr0 != original.Dr0 || verified.Dr1 != original.Dr1 || verified.Dr2 != original.Dr2 ||
            verified.Dr3 != original.Dr3 || verified.Dr6 != original.Dr6 || verified.Dr7 != original.Dr7) { SetLastError(ERROR_WRITE_FAULT); return false; }
        if ((original.ContextFlags & 1) && (verified.Rip != original.Rip || verified.Rsp != original.Rsp || verified.EFlags != original.EFlags)) { SetLastError(ERROR_WRITE_FAULT); return false; }
    }
    session.recoveryContexts.clear();
    if (!clearStep()) return false;
    for (auto& item : session.threads) {
        if (!item.second.needsSlots) continue;
        if (!installThreadSlots(item.first, item.second)) return false;
        item.second.needsSlots = false;
    }
    session.faulted = false;
    return true;
}

int execute(Request& r, ContextRecord* context, void* buffer, uint64_t bufferLength) {
    r.error = 0; r.result = 0;
    if (session.pid && session.worker != GetCurrentThreadId()) return fail(r, ERROR_INVALID_THREAD_ID);
    if (r.process > std::numeric_limits<DWORD>::max() || r.thread > std::numeric_limits<DWORD>::max()) return fail(r, ERROR_INVALID_PARAMETER);
    if (r.operation == Identity) {
        HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | SYNCHRONIZE, FALSE, static_cast<DWORD>(r.process));
        if (!process) return fail(r, lastError());
        uint64_t identity = 0;
        const bool success = x64Identity(process, identity);
        DWORD error = success ? 0 : lastError(); CloseHandle(process);
        if (!success) return fail(r, error);
        r.result = identity; return 0;
    }
    if (r.operation == Attach) {
        if (session.pid) return fail(r, ERROR_BUSY);
        if (!r.process) return fail(r, ERROR_INVALID_PARAMETER);
        HANDLE process = OpenProcess(ProcessAccess, FALSE, static_cast<DWORD>(r.process));
        if (!process) return fail(r, lastError());
        uint64_t identity = 0;
        if (!x64Identity(process, identity)) { DWORD error = lastError(); CloseHandle(process); return fail(r, error); }
        if (r.value && r.value != identity) { CloseHandle(process); return fail(r, ERROR_INVALID_HANDLE); }
        if (!DebugActiveProcess(static_cast<DWORD>(r.process))) { DWORD error = lastError(); CloseHandle(process); return fail(r, error); }
        session.pid = static_cast<DWORD>(r.process); session.worker = GetCurrentThreadId();
        session.process = process; session.identity = identity; session.attaching = true;
        if (!DebugSetProcessKillOnExit(FALSE)) {
            DWORD error = lastError();
            return abortAttach(r, error);
        }
        findSystemBreakpoints();
        const ULONGLONG deadline = GetTickCount64() + 10000;
        while (GetTickCount64() < deadline) {
            int status = receive(50);
            if (status > 0) continue;
            if (status < 0) return abortAttach(r, lastError());
            if (session.record.kind == Attached) {
                session.attaching = false;
                if (!continuePending(DBG_CONTINUE, false)) return abortAttach(r, lastError());
                r.result = identity; return 0;
            }
            if (session.record.kind == ProcessExited) {
                if (!continuePending(DBG_CONTINUE, false)) return abortAttach(r, lastError());
                return fail(r, ERROR_PROCESS_ABORTED);
            }
            if (session.pending.dwDebugEventCode == EXCEPTION_DEBUG_EVENT) {
                // Unrelated exceptions retain their normal first/second-chance
                // disposition even during attachment; never swallow them.
                if (!continuePending(DBG_EXCEPTION_NOT_HANDLED, false)) return abortAttach(r, lastError());
            } else if (!continuePending(DBG_CONTINUE, false)) return abortAttach(r, lastError());
        }
        return abortAttach(r, ERROR_TIMEOUT);
    }
    if (!session.pid || (r.process && r.process != session.pid)) return fail(r, ERROR_INVALID_HANDLE);
    if (session.worker != GetCurrentThreadId()) return fail(r, ERROR_INVALID_THREAD_ID);
    if (session.exited && r.operation != Continue && r.operation != Resume && r.operation != Detach) return fail(r, ERROR_PROCESS_ABORTED);

    switch (r.operation) {
    case 17: // Recover: additive operation, same v1 request layout.
        if (!recover()) { r.result = 1; return fail(r, lastError()); }
        return 0;
    case Detach: {
        if (session.exited) {
            if (session.hasPending && !continuePending(DBG_CONTINUE, false)) return fail(r, lastError());
            return 0;
        }
        if (session.faulted || !session.recoveryBytes.empty()) return fail(r, ERROR_WRITE_FAULT);
        if (!pause()) return fail(r, lastError());
        if (!clearStep()) return fail(r, lastError());
        for (unsigned i = 0; i != 4; ++i)
            if (session.slots[i].active && !setHardware(i, Slot{})) return fail(r, lastError());
        // Continuing the event is mandatory before detaching. The managed
        // caller has already restored all its entry patches and INT3 bytes.
        if (session.hasPending && !continuePending(normalDisposition(), false)) return fail(r, lastError());
        // Consume an injected pause breakpoint that has not arrived yet before
        // detaching; a target exception might have been observed ahead of it.
        if (session.pauseRequested) {
            const ULONGLONG deadline = GetTickCount64() + 5000;
            while (session.pauseRequested && session.pid && GetTickCount64() < deadline) {
                int status = receive(50);
                if (status < 0) return fail(r, lastError());
                if (status > 0) continue;
                if (!continuePending(normalDisposition(), false)) return fail(r, lastError());
            }
            if (session.pauseRequested) return fail(r, ERROR_TIMEOUT);
            if (!session.pid) return 0;
        }
        if (!DebugActiveProcessStop(session.pid)) return fail(r, lastError());
        closeSession(); return 0;
    }
    case Pause:
        if (!pause()) return fail(r, lastError());
        return 0;
    case Resume:
        if (!session.hasPending) return session.faulted || !session.recoveryBytes.empty() ? fail(r, ERROR_WRITE_FAULT) : 0;
        if (!continuePending((r.flags & 1) ? DBG_CONTINUE : normalDisposition(), true)) return fail(r, lastError());
        return 0;
    case Continue:
        if (!session.hasPending) return fail(r, ERROR_INVALID_STATE);
        if (!continuePending(session.pending.dwDebugEventCode != EXCEPTION_DEBUG_EVENT || (r.flags & 1) ? DBG_CONTINUE : DBG_EXCEPTION_NOT_HANDLED,
            session.pending.dwDebugEventCode == EXCEPTION_DEBUG_EVENT || session.pending.dwDebugEventCode == EXIT_PROCESS_DEBUG_EVENT)) return fail(r, lastError());
        return 0;
    case Threads: {
        uint64_t count = session.threads.size(); r.result = count;
        if (!buffer && !bufferLength) return 0;
        if (!buffer || bufferLength / sizeof(uint64_t) < count) return fail(r, ERROR_INSUFFICIENT_BUFFER);
        uint64_t* output = static_cast<uint64_t*>(buffer);
        for (const auto& item : session.threads) *output++ = item.first;
        return 0;
    }
    case GetContext: case SetContext: {
        if (!context || context->size != sizeof(ContextRecord)) return fail(r, ERROR_INVALID_PARAMETER);
        if (!stopped()) return fail(r, ERROR_INVALID_STATE);
        auto it = session.threads.find(static_cast<DWORD>(r.thread));
        if (it == session.threads.end()) return fail(r, ERROR_INVALID_THREAD_ID);
        CONTEXT c{};
        if (!getContext(it->second, c)) return fail(r, lastError());
        if (r.operation == GetContext) { copyContext(it->first, c, *context); return 0; }
        if (!(context->available & 1) || context->thread != r.thread || context->rflags > std::numeric_limits<DWORD>::max()) return fail(r, ERROR_INVALID_PARAMETER);
        c.Rax = context->rax; c.Rbx = context->rbx; c.Rcx = context->rcx; c.Rdx = context->rdx;
        c.Rsi = context->rsi; c.Rdi = context->rdi; c.Rbp = context->rbp; c.Rsp = context->rsp;
        c.R8 = context->r8; c.R9 = context->r9; c.R10 = context->r10; c.R11 = context->r11;
        c.R12 = context->r12; c.R13 = context->r13; c.R14 = context->r14; c.R15 = context->r15;
        c.Rip = context->rip; c.EFlags = static_cast<DWORD>(context->rflags);
        c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
        if (!SetThreadContext(it->second.handle, &c)) return fail(r, lastError());
        return 0;
    }
    case Hardware: {
        if (!stopped()) return fail(r, ERROR_INVALID_STATE);
        if (r.value > 3) return fail(r, ERROR_INVALID_PARAMETER);
        Slot proposed;
        if (r.flags & 1) {
            proposed = {true, r.address, (r.flags >> 8) & 3u, (r.flags >> 16) & 255u};
            if ((proposed.trigger != 0 && proposed.trigger != 1 && proposed.trigger != 3) ||
                (proposed.length != 1 && proposed.length != 2 && proposed.length != 4 && proposed.length != 8) ||
                (proposed.trigger == 0 && proposed.length != 1) || (r.address % proposed.length) || !validRange(r.address, proposed.length)) return fail(r, ERROR_INVALID_PARAMETER);
        }
        if (!proposed.active && !session.slots[static_cast<unsigned>(r.value)].active) return 0;
        if (!setHardware(static_cast<unsigned>(r.value), proposed)) { if (session.faulted) r.result = 1; return fail(r, lastError()); }
        return 0;
    }
    case Step: {
        if (!stopped() || session.faulted || !session.recoveryBytes.empty()) return fail(r, session.faulted || !session.recoveryBytes.empty() ? ERROR_WRITE_FAULT : ERROR_INVALID_STATE);
        auto it = session.threads.find(static_cast<DWORD>(r.thread));
        if (it == session.threads.end()) return fail(r, ERROR_INVALID_THREAD_ID);
        if (session.stepping && !clearStep()) return fail(r, lastError());
        CONTEXT c{};
        if (!getContext(it->second, c)) return fail(r, lastError());
        if (c.EFlags & TrapFlag) return fail(r, ERROR_BUSY); // A target-owned TF cannot be claimed.
        if (!canOwnSingleStep(c.Rip)) return fail(r, lastError());
        for (auto& item : session.threads) {
            if (item.first != r.thread && !suspendedThread(item.second)) {
                DWORD error = lastError(); if (!releaseSuspends()) session.faulted = true;
                if (session.faulted) r.result = 1;
                return fail(r, session.faulted ? ERROR_WRITE_FAULT : error);
            }
        }
        session.priorTrap = false; session.stepping = static_cast<DWORD>(r.thread);
        c.EFlags |= static_cast<DWORD>(TrapFlag); c.ContextFlags = CONTEXT_CONTROL;
        if (!SetThreadContext(it->second.handle, &c)) {
            DWORD error = lastError(); if (!clearStep()) session.faulted = true;
            if (session.faulted) r.result = 1;
            return fail(r, session.faulted ? ERROR_WRITE_FAULT : error);
        }
        if (!continuePending((r.flags & 1) ? DBG_CONTINUE : normalDisposition(), false)) {
            DWORD error = lastError(); if (!clearStep()) session.faulted = true;
            if (session.faulted) r.result = 1;
            return fail(r, session.faulted ? ERROR_WRITE_FAULT : error);
        }
        return 0;
    }
    case ReadMemory:
        if (!buffer || r.length > bufferLength || !validRange(r.address, r.length)) return fail(r, ERROR_INVALID_PARAMETER);
        if (!exactRead(r.address, buffer, r.length)) return fail(r, lastError());
        r.result = r.length; return 0;
    case WriteCode:
        if (!stopped()) return fail(r, ERROR_INVALID_STATE);
        if (!buffer || r.length > bufferLength || !validRange(r.address, r.length)) return fail(r, ERROR_INVALID_PARAMETER);
        if ((r.flags & ~1u) || ((r.flags & 1) && r.length != 1)) return fail(r, ERROR_INVALID_PARAMETER);
        // The managed interval check covers every thread before calling this;
        // native code checks again for CPU-visible overwrite safety.
        for (auto& item : session.threads) {
            CONTEXT c{}; if (!getContext(item.second, c)) return fail(r, lastError());
            if (c.Rip >= r.address && c.Rip - r.address < r.length && !(r.flags & 1)) return fail(r, ERROR_BUSY);
        }
        if (!writeCode(r, buffer)) return fail(r, lastError());
        return 0;
    case Allocate: {
        if (!validRange(0, r.length) || (r.flags & ~7u)) return fail(r, ERROR_INVALID_PARAMETER);
        DWORD protect = protection(r.flags ? r.flags : 3);
        LPVOID allocated = allocateNear(r.value, r.length, protect);
        if (!allocated) allocated = VirtualAllocEx(session.process, nullptr, static_cast<SIZE_T>(r.length), MEM_RESERVE | MEM_COMMIT, protect);
        if (!allocated) return fail(r, lastError());
        r.result = reinterpret_cast<uintptr_t>(allocated); return 0;
    }
    case Protect: {
        if (!validRange(r.address, r.length) || (r.flags & ~7u)) return fail(r, ERROR_INVALID_PARAMETER);
        DWORD previous = 0;
        if (!VirtualProtectEx(session.process, pointer(r.address), static_cast<SIZE_T>(r.length), protection(r.flags), &previous)) return fail(r, lastError());
        r.result = abstractProtection(previous);
        if ((r.flags & 4) && !FlushInstructionCache(session.process, pointer(r.address), static_cast<SIZE_T>(r.length))) return fail(r, lastError());
        return 0;
    }
    case Free:
        if (!r.address || !VirtualFreeEx(session.process, pointer(r.address), 0, MEM_RELEASE)) return fail(r, r.address ? lastError() : ERROR_INVALID_PARAMETER);
        return 0;
    default: return fail(r, ERROR_NOT_SUPPORTED);
    }
}
} // namespace

extern "C" uint64_t RcDebugQueryV1(uint32_t version) {
    return version == 1 ? rcdebug::Session | rcdebug::Context | rcdebug::Stepping | rcdebug::CodeWrite | rcdebug::Allocation : 0;
}
extern "C" int32_t RcDebugExecuteV1(rcdebug::Request* request, rcdebug::ContextRecord* context, void* buffer, uint64_t bufferLength) {
    if (!request || request->size != sizeof(rcdebug::Request)) {
        if (request && request->size >= offsetof(rcdebug::Request, error) + sizeof(request->error)) request->error = ERROR_INVALID_PARAMETER;
        SetLastError(ERROR_INVALID_PARAMETER); return -1;
    }
    try { return execute(*request, context, buffer, bufferLength); }
    catch (const std::bad_alloc&) {
        if (session.attaching) return abortAttach(*request, ERROR_NOT_ENOUGH_MEMORY);
        return fail(*request, ERROR_NOT_ENOUGH_MEMORY);
    }
    catch (...) {
        if (session.attaching) return abortAttach(*request, ERROR_GEN_FAILURE);
        return fail(*request, ERROR_GEN_FAILURE);
    }
}
extern "C" int32_t RcDebugWaitV1(uint32_t timeout, rcdebug::EventRecord* event) {
    if (!event || event->size != sizeof(rcdebug::EventRecord) || !session.pid || session.worker != GetCurrentThreadId()) {
        return waitFail(event, ERROR_INVALID_PARAMETER);
    }
    try {
        const ULONGLONG start = GetTickCount64();
        for (;;) {
            if (session.hasPending) {
                // A received event is delivered once and must be explicitly
                // continued; waiting twice must not duplicate UI observations.
                if (session.reported) return 1;
            } else {
                DWORD remaining = timeout;
                if (timeout != INFINITE) {
                    const ULONGLONG elapsed = GetTickCount64() - start;
                    remaining = elapsed >= timeout ? 0 : timeout - static_cast<DWORD>(elapsed);
                }
                int status = receive(remaining);
                if (status < 0) return waitFail(event, lastError());
                if (status > 0) return status;
            }
            if (session.record.kind != rcdebug::None) {
                *event = session.record; session.reported = true; return 0;
            }
            if (!continuePending(DBG_CONTINUE, false)) return waitFail(event, lastError());
            if (timeout != INFINITE && GetTickCount64() - start >= timeout) return 1;
        }
    } catch (const std::bad_alloc&) {
        if (session.hasPending) session.faulted = true;
        return waitFail(event, ERROR_NOT_ENOUGH_MEMORY);
    } catch (...) {
        if (session.hasPending) session.faulted = true;
        return waitFail(event, ERROR_GEN_FAILURE);
    }
}
#else
extern "C" uint64_t RcDebugQueryV1(uint32_t) { return 0; }
extern "C" int32_t RcDebugExecuteV1(rcdebug::Request* request, rcdebug::ContextRecord*, void*, uint64_t) {
    if (request && request->size == sizeof(*request)) request->error = ERROR_NOT_SUPPORTED;
    SetLastError(ERROR_NOT_SUPPORTED); return -1;
}
extern "C" int32_t RcDebugWaitV1(uint32_t, rcdebug::EventRecord* event) {
    if (event && event->size == sizeof(*event)) event->code = ERROR_NOT_SUPPORTED;
    SetLastError(ERROR_NOT_SUPPORTED); return -1;
}
#endif
