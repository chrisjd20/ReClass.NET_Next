#pragma once
#include <cstdint>

// Additive x64 ABI. Existing plugin records/exports remain untouched.
// Execute: 0 success, -1 failure (request.error is a native error).
// Wait: 0 event, 1 timeout, -1 failure. All calls belong to one debugger worker.
namespace rcdebug {
enum Operation : uint32_t {
 Attach=1, Detach, Pause, Resume, GetContext, SetContext, Hardware,
 WriteCode, Allocate, Protect, Free, Step, Continue, Threads, Identity, ReadMemory, Recover
};
enum EventKind : uint32_t { None=0, Attached=1, Exception=2, ThreadCreated=3,
 ThreadExited=4, ProcessExited=5, Exec=6, Breakpoint=7, SingleStep=8, PauseEvent=9, ModuleChanged=10 };
enum Capability : uint64_t { Session=1, Context=2, Stepping=4, CodeWrite=8,
 Allocation=16, OperandContext=32 };
// Protection: 1 read, 2 write, 4 execute. Hardware flags: bit0 enable,
// bits8..9 trigger (0 execute,1 write,3 access), bits16..23 length (1,2,4,8).
// Continue flags: bit0 handled. Allocate.value: preferred address (0 any).
// Step flags: bit0 handles an owned pending breakpoint before single stepping.
// WriteCode flags: bit0 allows a one-byte owned breakpoint write at current RIP.
// Hardware.value: slot 0..3. Threads buffer: uint64_t IDs; result: count.
#pragma pack(push,8)
struct Request {
 uint32_t size, operation;
 uint64_t process, thread, address, value, length;
 uint32_t flags, error;
 uint64_t result;
};
struct ContextRecord {
 uint32_t size, available;
 uint64_t thread;
 uint64_t rax,rbx,rcx,rdx,rsi,rdi,rbp,rsp,r8,r9,r10,r11,r12,r13,r14,r15,rip,rflags,fsbase,gsbase;
};
struct EventRecord {
 uint32_t size,kind;
 uint64_t process,thread,address,code;
 uint32_t causedBy,firstChance;
 ContextRecord context;
};
#pragma pack(pop)
static_assert(sizeof(Request)==64,"Request ABI");
static_assert(sizeof(ContextRecord)==176,"Context ABI");
static_assert(sizeof(EventRecord)==224,"Event ABI");
}
extern "C" {
 uint64_t RcDebugQueryV1(uint32_t abiVersion);
 int32_t RcDebugExecuteV1(rcdebug::Request* request, rcdebug::ContextRecord* context, void* buffer, uint64_t bufferLength);
 int32_t RcDebugWaitV1(uint32_t timeoutMilliseconds, rcdebug::EventRecord* event);
}
