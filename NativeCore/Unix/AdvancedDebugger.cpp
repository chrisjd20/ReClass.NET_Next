#include <sys/ptrace.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <sys/user.h>
#include <sys/uio.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <unistd.h>
#include <dirent.h>
#include <elf.h>
#include <signal.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <fstream>
#include <limits>
#include <map>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "../Shared/AdvancedDebugger.hpp"

// No signal handlers, process-wide timers, or waitpid(-1): this worker consumes
// only stops belonging to its seized target threads.
namespace {
using namespace rcdebug;
using Clock = std::chrono::steady_clock;
constexpr unsigned long Options = PTRACE_O_TRACECLONE | PTRACE_O_TRACEEXEC | PTRACE_O_TRACEEXIT;
constexpr uint64_t TrapFlag = 0x100, ResumeFlag = 0x10000;
struct Slot { bool active = false; uint64_t address = 0; unsigned trigger = 0, length = 0; };
struct SavedSlot { bool owned = false; uint64_t address = 0, control = 0; };
struct HardwareRepair { pid_t tid; unsigned index; uint64_t address,control,status; };
struct Thread {
    bool stopped = false, interrupted = false, newborn = false, exiting = false;
    bool groupStopped = false, listening = false, foreign = false;
    int signal = 0;
    std::array<SavedSlot,4> saved{};
    bool needsSlots = false;
    bool statusNeedsProcessing = false;
    int unprocessedStatus = 0;
};
struct StopEvent { EventRecord record{}; int signal = 0; bool owned = false; };
struct SessionState {
    pid_t pid = 0, worker = 0, stepping = 0;
    uint64_t identity = 0;
    bool paused = false, attaching = false, faulted = false, exited = false;
    std::map<pid_t,Thread> threads;
    std::array<Slot,4> slots{};
    std::deque<StopEvent> events;
    StopEvent current{};
    bool pending = false, reported = false;
    uint64_t recoveryAddress = 0;
    std::vector<unsigned char> recoveryBytes;
    pid_t recoveryThread = 0;
    user_regs_struct recoveryRegisters{};
    long recoverySyscall = 0;
    std::array<uint64_t,6> recoveryArguments{};
    uint64_t recoveryResult = 0;
    bool recoveryCompleted = false;
    std::vector<HardwareRepair> recoveryHardware;
    std::vector<std::pair<uint64_t,uint64_t>> orphanAllocations;
    bool recoveryProtection = false;
    uint64_t protectionAddress = 0, protectionLength = 0;
    unsigned protectionFlags = 0;
    pid_t diagnosticThread = 0;
    int diagnosticStatus = 0;
    const char* diagnosticStage = "wait";
};
SessionState session;
std::map<uint64_t,uint64_t> allocations;
int error(int fallback = EIO) { return errno ? errno : fallback; }
int fail(Request& request, int code) { request.error = static_cast<uint32_t>(code); errno = code; return -1; }
int waitFail(EventRecord* event, int code) {
    if (event && event->size >= offsetof(EventRecord,code)+sizeof(event->code)) event->code = static_cast<uint64_t>(code);
    if(event&&event->size==sizeof(EventRecord)){
        event->process=static_cast<uint64_t>(session.pid);event->thread=static_cast<uint64_t>(session.diagnosticThread);
        event->kind=std::strcmp(session.diagnosticStage,"clone-slot-install")==0?ThreadCreated:None;
    }
    if(std::getenv("RECLASS_NATIVE_DEBUG")){
        std::fprintf(stderr,"RcDebug wait: errno=%d (%s) tid=%d status=0x%x stage=%s\n",code,std::strerror(code),
            static_cast<int>(session.diagnosticThread),static_cast<unsigned>(session.diagnosticStatus),session.diagnosticStage);
        std::fflush(stderr);
    }
    errno = code; return -1;
}
bool validRange(uint64_t address, uint64_t length) {
    return length && length <= static_cast<uint64_t>(std::numeric_limits<ssize_t>::max()) &&
        address <= std::numeric_limits<uintptr_t>::max() - (length - 1);
}
void* pointer(uint64_t address) { return reinterpret_cast<void*>(static_cast<uintptr_t>(address)); }
bool trace(enum __ptrace_request operation, pid_t tid, uint64_t address = 0, uint64_t value = 0) {
    session.diagnosticThread=tid;
    if(ptrace(operation,tid,pointer(address),pointer(value))!=-1)return true;
    int failure=error();
    if(std::getenv("RECLASS_NATIVE_DEBUG")){
        std::fprintf(stderr,"RcDebug ptrace: operation=0x%x tid=%d address=0x%llx value=0x%llx errno=%d (%s)\n",
            static_cast<unsigned>(operation),static_cast<int>(tid),static_cast<unsigned long long>(address),
            static_cast<unsigned long long>(value),failure,std::strerror(failure));std::fflush(stderr);
    }
    errno=failure;return false;
}
bool getRegisters(pid_t tid,user_regs_struct& registers) {
    return ptrace(PTRACE_GETREGS,tid,nullptr,&registers) != -1;
}
bool setRegisters(pid_t tid,const user_regs_struct& registers) {
    return ptrace(PTRACE_SETREGS,tid,nullptr,&registers) != -1;
}
bool peekRegister(pid_t tid,unsigned index,uint64_t& value) {
    errno = 0;
    const long result = ptrace(PTRACE_PEEKUSER,tid,pointer(offsetof(user,u_debugreg)+index*sizeof(uint64_t)),nullptr);
    if (result == -1 && errno) return false;
    value = static_cast<uint64_t>(result); return true;
}
bool pokeRegister(pid_t tid,unsigned index,uint64_t value) {
    return trace(PTRACE_POKEUSER,tid,offsetof(user,u_debugreg)+index*sizeof(uint64_t),value);
}
uint64_t slotMask(unsigned slot) { return (uint64_t(3)<<(slot*2)) | (uint64_t(15)<<(16+slot*4)); }
unsigned lengthCode(unsigned length) { return length == 8 ? 2 : length == 4 ? 3 : length == 2 ? 1 : 0; }
void copyContext(pid_t tid,const user_regs_struct& registers,ContextRecord& output) {
    output = {}; output.size = sizeof(output); output.thread = static_cast<uint64_t>(tid); output.available = 7;
    output.rax=registers.rax;output.rbx=registers.rbx;output.rcx=registers.rcx;output.rdx=registers.rdx;
    output.rsi=registers.rsi;output.rdi=registers.rdi;output.rbp=registers.rbp;output.rsp=registers.rsp;
    output.r8=registers.r8;output.r9=registers.r9;output.r10=registers.r10;output.r11=registers.r11;
    output.r12=registers.r12;output.r13=registers.r13;output.r14=registers.r14;output.r15=registers.r15;
    output.rip=registers.rip;output.rflags=registers.eflags;output.fsbase=registers.fs_base;output.gsbase=registers.gs_base;
}
bool processIdentity(pid_t pid,uint64_t& identity) {
    std::ifstream executable("/proc/"+std::to_string(pid)+"/exe",std::ios::binary);
    Elf64_Ehdr header{};
    if (!executable.read(reinterpret_cast<char*>(&header),sizeof(header))) { errno=ESRCH;return false; }
    if (std::memcmp(header.e_ident,ELFMAG,SELFMAG) || header.e_ident[EI_CLASS]!=ELFCLASS64 ||
        header.e_ident[EI_DATA]!=ELFDATA2LSB || header.e_machine!=EM_X86_64) { errno=ENOTSUP;return false; }
    std::ifstream stat("/proc/"+std::to_string(pid)+"/stat");std::string line;
    if (!std::getline(stat,line)) { errno=ESRCH;return false; }
    const size_t end=line.rfind(')');if(end==std::string::npos){errno=EPROTO;return false;}
    std::istringstream fields(line.substr(end+2));std::string value;
    // Fields begin with state (field 3); creation marker is starttime (field 22).
    for(unsigned field=3;field<=22;++field) if(!(fields>>value)){errno=EPROTO;return false;}
    char* tail=nullptr;errno=0;identity=std::strtoull(value.c_str(),&tail,10);
    if(errno || !tail || *tail || !identity){errno=EPROTO;return false;}return true;
}
bool taskIds(std::vector<pid_t>& ids) {
    const std::string path="/proc/"+std::to_string(session.pid)+"/task";
    DIR* directory=opendir(path.c_str());if(!directory)return false;
    errno=0;
    try {
        while(dirent* entry=readdir(directory)) {
            char* end=nullptr;long value=std::strtol(entry->d_name,&end,10);
            if(end && !*end && value>0 && value<=std::numeric_limits<pid_t>::max())ids.push_back(static_cast<pid_t>(value));
        }
    } catch(...) {closedir(directory);throw;}
    const int failure=errno;closedir(directory);
    if(failure){errno=failure;return false;}
    std::sort(ids.begin(),ids.end());return true;
}
pid_t statusValue(pid_t tid,const char* name) {
    std::ifstream status("/proc/"+std::to_string(tid)+"/status");std::string line;
    while(std::getline(status,line))if(line.compare(0,std::strlen(name),name)==0){
        std::istringstream value(line.substr(std::strlen(name)));pid_t id=0;value>>id;return id;
    }
    return 0;
}
bool seizeMissing() {
    std::vector<pid_t> ids;if(!taskIds(ids))return false;
    for(pid_t tid:ids) {
        if(session.threads.count(tid))continue;
        Thread thread;
        auto inserted=session.threads.emplace(tid,thread).first;
        if(!trace(PTRACE_SEIZE,tid,0,Options)) {
            const int failure=error();
            if(failure==ESRCH){session.threads.erase(inserted);continue;}
            // Clone tracing may have attached this thread between enumeration
            // and seizure. Verify the tracer thread; do not steal another owner.
            if(failure==EPERM && statusValue(tid,"TracerPid:")==session.worker)inserted->second.newborn=true;
            else{session.threads.erase(inserted);errno=failure;return false;}
        }
    }
    return true;
}
bool installSlots(pid_t tid,Thread& thread) {
    uint64_t dr7=0;if(!peekRegister(tid,7,dr7))return false;
    uint64_t changed=dr7;auto saved=thread.saved;
    std::array<uint64_t,4> addresses{};
    std::array<bool,4> install{};
    for(unsigned i=0;i<4;++i) {
        if(!session.slots[i].active)continue;
        if(!peekRegister(tid,i,addresses[i]))return false;
        const Slot& slot=session.slots[i];
        const uint64_t control=(uint64_t(1)<<(2*i))|(uint64_t(slot.trigger|(lengthCode(slot.length)<<2))<<(16+4*i));
        // Clone inherits the parent's debug registers. Its old saved state is
        // supplied by the clone event, so inherited owned bits are recognized.
        if(saved[i].owned) {
            if(addresses[i]==slot.address && (dr7&slotMask(i))==control)continue;
            if(addresses[i]==0 && (dr7&slotMask(i))==control){
                // Linux copies ptrace_dr7 on clone but clears ptrace_bps, so
                // PEEKUSER may expose our inherited enable bits with address0.
                // The recorded clone lineage proves these bits are ours. Build
                // the missing perf breakpoint before the newborn can run.
                saved[i]={true,0,saved[i].control};install[i]=true;
                changed=(changed&~slotMask(i))|control;continue;
            }
            // Some kernels clear a clone's debug registers rather than copying
            // them. In that case its actual empty state is the saved baseline.
            if(dr7&(uint64_t(3)<<(2*i))){errno=EBUSY;return false;}
            saved[i]={};
        }
        if(dr7&(uint64_t(3)<<(2*i))){errno=EBUSY;return false;}
        saved[i]={true,addresses[i],dr7&slotMask(i)};
        install[i]=true;
        changed=(changed&~slotMask(i))|control;
    }
    unsigned applied=0;
    uint64_t oldStatus=0;if(!peekRegister(tid,6,oldStatus))return false;
    std::vector<HardwareRepair> rollback;
    for(unsigned i=0;i<4;++i)if(install[i])rollback.push_back({tid,i,addresses[i],dr7,oldStatus});
    for(;applied<4;++applied)if(install[applied] &&
        !pokeRegister(tid,applied,session.slots[applied].address))break;
    if(applied!=4 || !pokeRegister(tid,7,changed)) {
        int failure=error();bool recovered=pokeRegister(tid,7,dr7);
        for(unsigned i=0;i<4;++i)if(install[i] && !pokeRegister(tid,i,addresses[i]))recovered=false;
        if(!recovered){session.faulted=true;session.recoveryHardware.swap(rollback);}errno=recovered?failure:EIO;return false;
    }
    thread.saved=saved;return true;
}
StopEvent makeEvent(pid_t tid,uint32_t kind,uint64_t code=0) {
    StopEvent event;event.record.size=sizeof(EventRecord);event.record.kind=kind;
    event.record.process=static_cast<uint64_t>(session.pid);event.record.thread=static_cast<uint64_t>(tid);event.record.code=code;
    event.record.context.size=sizeof(ContextRecord);event.record.context.thread=static_cast<uint64_t>(tid);
    return event;
}
bool handleStatus(pid_t tid,int status) {
    session.diagnosticThread=tid;session.diagnosticStatus=status;session.diagnosticStage="status";
    auto found=session.threads.find(tid);
    if(found==session.threads.end()){errno=ESRCH;return false;}
    if(WIFEXITED(status)||WIFSIGNALED(status)) {
        bool foreign=found->second.foreign;
        if(std::getenv("RECLASS_NATIVE_DEBUG")){
            std::fprintf(stderr,"RcDebug exit: tid=%d status=0x%x exit=%d signal=%d remaining=%zu foreign=%d\n",
                static_cast<int>(tid),static_cast<unsigned>(status),WIFEXITED(status)?WEXITSTATUS(status):-1,
                WIFSIGNALED(status)?WTERMSIG(status):0,session.threads.size()-1,foreign?1:0);
            std::fflush(stderr);
        }
        session.threads.erase(found);
        if(session.stepping==tid)session.stepping=0;
        if(foreign)return true;
        uint32_t kind=session.threads.empty()?ProcessExited:ThreadExited;
        uint64_t code=WIFEXITED(status)?WEXITSTATUS(status):static_cast<uint64_t>(128+WTERMSIG(status));
        StopEvent event=makeEvent(tid,kind,code);event.owned=true;session.events.push_back(event);
        if(kind==ProcessExited)session.exited=true;
        return true;
    }
    if(!WIFSTOPPED(status)){errno=EPROTO;return false;}
    Thread& thread=found->second;thread.stopped=true;thread.listening=false;
    const int signal=WSTOPSIG(status);const unsigned event=static_cast<unsigned>(status)>>16;
    if(thread.foreign) {
        if(!trace(PTRACE_DETACH,tid,0,0))return false;
        session.threads.erase(tid);return true;
    }
    if(event==PTRACE_EVENT_CLONE) {
        unsigned long child=0;
        if(ptrace(PTRACE_GETEVENTMSG,tid,nullptr,&child)==-1)return false;
        pid_t childTid=static_cast<pid_t>(child);
        Thread newborn;newborn.newborn=true;newborn.saved=thread.saved;
        const pid_t owner=statusValue(childTid,"Tgid:");
        newborn.foreign=owner&&owner!=session.pid;
        auto inserted=session.threads.emplace(childTid,newborn);
        if(!inserted.second && !inserted.first->second.stopped) {
            inserted.first->second.newborn=true;inserted.first->second.saved=thread.saved;
        }
        thread.interrupted=false;thread.signal=0;return true;
    }
    if(event==PTRACE_EVENT_EXEC) {
        unsigned long former=0;
        if(ptrace(PTRACE_GETEVENTMSG,tid,nullptr,&former)==-1)return false;
        auto formerThread=session.threads.find(static_cast<pid_t>(former));
        if(formerThread!=session.threads.end() && formerThread->first!=tid) {
            thread.saved=formerThread->second.saved;session.threads.erase(formerThread);
        }
        // Exec invalidates every previous mapping and transient stop snapshot.
        // Only restore owned DR bits; no old code bytes are written into exec.
        uint64_t dr7=0;if(!peekRegister(tid,7,dr7))return false;
        for(unsigned i=0;i<4;++i)if(thread.saved[i].owned) {
            if(!pokeRegister(tid,i,thread.saved[i].address))return false;
            dr7=(dr7&~slotMask(i))|thread.saved[i].control;
        }
        if(!pokeRegister(tid,7,dr7))return false;
        thread.saved={};thread.interrupted=false;thread.signal=0;
        session.slots={};session.stepping=0;session.events.clear();session.pending=false;
        session.recoveryBytes.clear();session.recoveryAddress=0;
        allocations.clear();
        StopEvent execution=makeEvent(tid,Exec);execution.owned=true;
        user_regs_struct registers{};
        if(getRegisters(tid,registers)){copyContext(tid,registers,execution.record.context);execution.record.address=registers.rip;}
        session.events.push_back(execution);return true;
    }
    if(event==PTRACE_EVENT_EXIT) {
        if(std::getenv("RECLASS_NATIVE_DEBUG")){
            const int previousError=errno;unsigned long exitStatus=0;
            const long result=ptrace(PTRACE_GETEVENTMSG,tid,nullptr,&exitStatus);
            std::fprintf(stderr,"RcDebug exit-stop: tid=%d status=0x%lx message=%ld\n",static_cast<int>(tid),exitStatus,result);
            std::fflush(stderr);errno=previousError;
        }
        thread.exiting=true;thread.interrupted=false;thread.signal=0;
        if(!trace(PTRACE_CONT,tid,0,0))return false;
        thread.stopped=false;return true;
    }
    if(event==PTRACE_EVENT_STOP) {
        bool created=thread.newborn;thread.newborn=false;thread.interrupted=false;thread.signal=0;
        if(signal!=SIGTRAP)thread.groupStopped=true;
        if(created) {
            thread.needsSlots=true;
            session.diagnosticStage="clone-slot-install";
            if(!installSlots(tid,thread)){session.faulted=true;return false;}
            session.diagnosticStage="status";
            thread.needsSlots=false;
            if(!session.attaching){StopEvent creation=makeEvent(tid,ThreadCreated);creation.owned=true;session.events.push_back(creation);}
        }
        return true;
    }
    if(event){errno=ENOTSUP;return false;}
    thread.interrupted=false;thread.signal=signal;
    siginfo_t info{};
    if(ptrace(PTRACE_GETSIGINFO,tid,nullptr,&info)==-1)return false;
    if(signal==SIGCONT)thread.groupStopped=false;
    user_regs_struct registers{};
    if(!getRegisters(tid,registers))return false;
    StopEvent stop=makeEvent(tid,Exception,static_cast<uint64_t>(signal));
    stop.signal=signal;stop.record.firstChance=1;stop.record.address=registers.rip;
    copyContext(tid,registers,stop.record.context);
    if(signal==SIGTRAP) {
        uint64_t dr6=0,dr7=0;if(!peekRegister(tid,6,dr6)||!peekRegister(tid,7,dr7))return false;
        uint64_t enabledBits=0;
        for(unsigned i=0;i<4;++i)if(dr7&(uint64_t(3)<<(2*i)))enabledBits|=dr6&(uint64_t(1)<<i);
        uint64_t ownedBits=0;
        for(unsigned i=0;i<4;++i)if(session.slots[i].active&&thread.saved[i].owned&&(enabledBits&(uint64_t(1)<<i))){
            ownedBits|=uint64_t(1)<<i;stop.record.causedBy|=1u<<i;
        }
        const bool ownedStep=session.stepping==tid&&info.si_code==TRAP_TRACE;
        const bool unknown=(enabledBits&~ownedBits) || (dr6&((uint64_t(1)<<13)|(uint64_t(1)<<15))) ||
            ((dr6&(uint64_t(1)<<14))&&!ownedStep&&info.si_code==TRAP_HWBKPT);
        if(info.si_code==TRAP_HWBKPT&&ownedBits&&!unknown) {
            stop.record.kind=Breakpoint;stop.owned=true;
            for(unsigned i=0;i<4;++i)if((ownedBits&(uint64_t(1)<<i))&&session.slots[i].trigger==0)registers.eflags|=ResumeFlag;
            if(!pokeRegister(tid,6,dr6&~ownedBits)||!setRegisters(tid,registers))return false;
        } else if(ownedStep&&!unknown) {
            stop.record.kind=SingleStep;stop.owned=true;
            registers.eflags&=~TrapFlag;
            if(!pokeRegister(tid,6,dr6&~(uint64_t(1)<<14))||!setRegisters(tid,registers))return false;
            copyContext(tid,registers,stop.record.context);
        } else if(info.si_code==TRAP_BRKPT||info.si_code==SI_KERNEL) {
            stop.record.kind=Breakpoint;stop.record.address=registers.rip?registers.rip-1:0;
            stop.record.causedBy=0;
        } else stop.record.causedBy=0;
        if(std::getenv("RECLASS_NATIVE_DEBUG")){
            std::fprintf(stderr,"RcDebug trap: tid=%d si_code=%d dr6=0x%llx dr7=0x%llx enabled=0x%llx owned=0x%llx step=%d unknown=%d rip=0x%llx kind=%u handled=%d\n",
                static_cast<int>(tid),info.si_code,static_cast<unsigned long long>(dr6),static_cast<unsigned long long>(dr7),
                static_cast<unsigned long long>(enabledBits),static_cast<unsigned long long>(ownedBits),ownedStep?1:0,unknown?1:0,
                static_cast<unsigned long long>(registers.rip),stop.record.kind,stop.owned?1:0);
            std::fflush(stderr);
        }
    }else if(std::getenv("RECLASS_NATIVE_DEBUG")){
        std::fprintf(stderr,"RcDebug signal: tid=%d signal=%d si_code=%d rip=0x%llx\n",static_cast<int>(tid),signal,
            info.si_code,static_cast<unsigned long long>(registers.rip));std::fflush(stderr);
    }
    session.events.push_back(stop);return true;
}
// One polling turn. Do not reap unrelated child processes of the application.
int pollStatuses() {
    bool received=false;
    std::vector<pid_t> ids;ids.reserve(session.threads.size());
    for(const auto& item:session.threads)ids.push_back(item.first);
    for(pid_t tid:ids) {
        auto current=session.threads.find(tid);
        if(current==session.threads.end()||current->second.stopped)continue;
        int status=0;pid_t waited=waitpid(tid,&status,__WALL|WNOHANG);
        if(waited==-1) {
            session.diagnosticThread=tid;session.diagnosticStage="waitpid";
            if(errno==EINTR)continue;
            if(errno==ECHILD||errno==ESRCH) {
                if(statusValue(tid,"Tgid:")==0) {
                    session.threads.erase(tid);received=true;
                    if(session.threads.empty()){session.exited=true;StopEvent exit=makeEvent(tid,ProcessExited);exit.owned=true;session.events.push_back(exit);}
                    continue;
                }
            }
            return -1;
        }
        if(waited>0){
            received=true;
            current=session.threads.find(waited);
            if(current!=session.threads.end()){current->second.unprocessedStatus=status;current->second.statusNeedsProcessing=true;}
            if(!handleStatus(waited,status)){session.faulted=true;return -1;}
            current=session.threads.find(waited);if(current!=session.threads.end())current->second.statusNeedsProcessing=false;
        }
    }
    return received?0:1;
}
bool allStopped() {
    for(const auto& item:session.threads)if(!item.second.stopped)return false;
    return !session.threads.empty();
}
bool stopWorld(unsigned milliseconds=5000,bool discover=true) {
    const auto deadline=Clock::now()+std::chrono::milliseconds(milliseconds);
    for(;;) {
        if(discover&&!seizeMissing()) {
            if(errno!=ENOENT && errno!=ESRCH)return false;
            if(session.threads.empty()){session.exited=true;errno=ESRCH;return false;}
        }
        for(auto& item:session.threads) {
            Thread& thread=item.second;
            if(thread.stopped||thread.interrupted||thread.newborn||thread.exiting)continue;
            if(!trace(PTRACE_INTERRUPT,item.first)) {
                if(errno==ESRCH)continue;
                return false;
            }
            thread.interrupted=true;
        }
        if(pollStatuses()<0)return false;
        if(session.exited){errno=ESRCH;return false;}
        if(allStopped()) {
            // Rescan after every known thread is stopped. Clone events are
            // collected before this condition, closing the snapshot race.
            const size_t count=session.threads.size();if(discover&&!seizeMissing())return false;
            if(count==session.threads.size()&&allStopped()){session.paused=true;return true;}
        }
        if(Clock::now()>=deadline){
            session.diagnosticStage="all-stop-timeout";
            if(std::getenv("RECLASS_NATIVE_DEBUG")){
                for(const auto& item:session.threads){
                    const Thread& thread=item.second;siginfo_t info{};errno=0;
                    const long result=ptrace(PTRACE_GETSIGINFO,item.first,nullptr,&info);const int infoError=errno;
                    std::fprintf(stderr,"RcDebug all-stop: tid=%d stopped=%d interrupted=%d newborn=%d exiting=%d group=%d listening=%d signal=%d getsiginfo=%ld errno=%d si_signo=%d si_code=0x%x\n",
                        static_cast<int>(item.first),thread.stopped?1:0,thread.interrupted?1:0,thread.newborn?1:0,
                        thread.exiting?1:0,thread.groupStopped?1:0,thread.listening?1:0,thread.signal,result,infoError,
                        info.si_signo,static_cast<unsigned>(info.si_code));
                }
                std::fflush(stderr);
            }
            errno=ETIMEDOUT;return false;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
}
bool resumeThread(pid_t tid,Thread& thread,bool single=false) {
    if(!thread.stopped)return true;
    if(thread.signal&&std::getenv("RECLASS_NATIVE_DEBUG")){
        std::fprintf(stderr,"RcDebug resume: tid=%d signal=%d single=%d group=%d\n",static_cast<int>(tid),thread.signal,
            single?1:0,thread.groupStopped?1:0);std::fflush(stderr);
    }
    if(thread.groupStopped) {
        if(single){errno=EBUSY;return false;}
        if(!trace(PTRACE_LISTEN,tid))return false;
        thread.listening=true;
    } else if(!trace(single?PTRACE_SINGLESTEP:PTRACE_CONT,tid,0,static_cast<uint64_t>(thread.signal)))return false;
        thread.stopped=false;thread.interrupted=false;thread.signal=0;return true;
}
bool resumeWorld() {
    if(session.faulted||!session.recoveryBytes.empty()){errno=EIO;return false;}
    if(!session.events.empty())return true; // Every queued exception is disposed first.
    bool success=true;int failure=0;
    for(auto& item:session.threads)if(!resumeThread(item.first,item.second)){success=false;if(!failure)failure=error();}
    if(!success){
        if(!stopWorld())session.faulted=true;
        errno=failure;return false;
    }
    session.paused=false;return true;
}
bool choosePending() {
    if(session.pending)return true;
    if(session.events.empty())return false;
    session.current=session.events.front();session.events.pop_front();session.pending=true;session.reported=false;return true;
}
bool disposePending(bool handled,bool clearStep) {
    if(!session.pending){errno=EINVAL;return false;}
    auto found=session.threads.find(static_cast<pid_t>(session.current.record.thread));
    if(found!=session.threads.end()&&session.current.signal)found->second.signal=handled?0:session.current.signal;
    if(clearStep)session.stepping=0;
    session.pending=false;session.reported=false;
    if(session.exited){session=SessionState{};allocations.clear();return true;}
    if(session.stepping) {
        auto step=session.threads.find(session.stepping);
        if(step==session.threads.end()){errno=ESRCH;return false;}
        if(session.events.empty()&&!resumeThread(step->first,step->second,true))return false;
        session.paused=false;return true;
    }
    return resumeWorld();
}
bool pause() {
    if(session.paused&&allStopped())return true;
    if(!stopWorld())return false;
    if(!choosePending()) {
        pid_t tid=session.stepping?session.stepping:session.threads.begin()->first;
        StopEvent event=makeEvent(tid,PauseEvent);event.owned=true;
        user_regs_struct registers{};
        if(!getRegisters(tid,registers))return false;
        copyContext(tid,registers,event.record.context);event.record.address=registers.rip;
        session.current=event;session.pending=true;session.reported=false;
    }
    return true;
}
bool ptraceRead(pid_t tid,uint64_t address,void* output,uint64_t length) {
    auto* destination=static_cast<unsigned char*>(output);
    while(length) {
        const uint64_t aligned=address&~uint64_t(sizeof(long)-1);
        const size_t offset=static_cast<size_t>(address-aligned);
        const size_t count=static_cast<size_t>(std::min<uint64_t>(length,sizeof(long)-offset));
        errno=0;long word=ptrace(PTRACE_PEEKTEXT,tid,pointer(aligned),nullptr);
        if(word==-1&&errno)return false;
        std::memcpy(destination,reinterpret_cast<unsigned char*>(&word)+offset,count);
        destination+=count;length-=count;if(length)address+=count;
    }
    return true;
}
bool ptraceWrite(pid_t tid,uint64_t address,const void* input,uint64_t length) {
    const auto* source=static_cast<const unsigned char*>(input);
    while(length) {
        const uint64_t aligned=address&~uint64_t(sizeof(long)-1);
        const size_t offset=static_cast<size_t>(address-aligned);
        const size_t count=static_cast<size_t>(std::min<uint64_t>(length,sizeof(long)-offset));
        errno=0;long word=ptrace(PTRACE_PEEKTEXT,tid,pointer(aligned),nullptr);
        if(word==-1&&errno)return false;
        std::memcpy(reinterpret_cast<unsigned char*>(&word)+offset,source,count);
        if(!trace(PTRACE_POKETEXT,tid,aligned,static_cast<uint64_t>(word)))return false;
        source+=count;length-=count;if(length)address+=count;
    }
    // x86-64's coherent instruction cache requires no separate syscall; the
    // ptrace stop/return serializes execution, and this is the explicit cache
    // synchronization point of the code-write provider contract.
    std::atomic_thread_fence(std::memory_order_seq_cst);return true;
}
pid_t stoppedThread() {
    for(const auto& item:session.threads)if(item.second.stopped&&!item.second.exiting&&!item.second.foreign)return item.first;
    errno=ESRCH;return 0;
}
bool exactRead(uint64_t address,void* buffer,uint64_t length) {
    iovec local{buffer,static_cast<size_t>(length)},remote{pointer(address),static_cast<size_t>(length)};
    ssize_t read=process_vm_readv(session.pid,&local,1,&remote,1,0);
    if(read==static_cast<ssize_t>(length))return true;
    if(session.paused) {
        pid_t tid=stoppedThread();if(tid)return ptraceRead(tid,address,buffer,length);
    }
    if(read>=0)errno=EIO;return false;
}
bool writeCode(Request& request,const void* buffer) {
    if(session.recoveryThread){request.result=1;errno=EIO;return false;}
    const bool recovering=!session.recoveryBytes.empty();
    if(recovering&&(request.address!=session.recoveryAddress||request.length!=session.recoveryBytes.size()||
        std::memcmp(buffer,session.recoveryBytes.data(),static_cast<size_t>(request.length)))) {
        request.result=1;errno=EIO;return false;
    }
    pid_t tid=stoppedThread();if(!tid)return false;
    std::vector<unsigned char> original(static_cast<size_t>(request.length)),verification(static_cast<size_t>(request.length));
    if(!ptraceRead(tid,request.address,original.data(),request.length))return false;
    bool success=ptraceWrite(tid,request.address,buffer,request.length);
    int failure=success?0:error();
    if(success)success=ptraceRead(tid,request.address,verification.data(),request.length)&&
        !std::memcmp(buffer,verification.data(),static_cast<size_t>(request.length));
    if(success){
        if(recovering){session.recoveryBytes.clear();session.recoveryAddress=0;}
        request.result=request.length;return true;
    }
    if(!failure)failure=error();
    bool recovered=ptraceWrite(tid,request.address,original.data(),request.length)&&
        ptraceRead(tid,request.address,verification.data(),request.length)&&
        !std::memcmp(original.data(),verification.data(),static_cast<size_t>(request.length));
    if(!recovered||recovering){
        if(!recovering){session.recoveryAddress=request.address;session.recoveryBytes.swap(original);}
        request.result=1;
    }
    errno=recovered?failure:EIO;return false;
}
bool setHardware(unsigned index,const Slot& proposed) {
    if(!session.recoveryHardware.empty()){errno=EIO;return false;}
    struct Change{pid_t tid;uint64_t address,control,dr6,newAddress,newControl;SavedSlot saved;};
    std::vector<Change> changes;
    for(auto& item:session.threads) {
        if(item.second.exiting||item.second.foreign)continue;
        Change change{};change.tid=item.first;change.saved=item.second.saved[index];
        if(!item.second.stopped||!peekRegister(item.first,index,change.address)||!peekRegister(item.first,7,change.control)||
            !peekRegister(item.first,6,change.dr6)){if(!item.second.stopped)errno=EBUSY;return false;}
        change.newAddress=change.address;change.newControl=change.control;
        if(proposed.active){
            if(change.saved.owned||(change.control&(uint64_t(3)<<(index*2)))){errno=EBUSY;return false;}
            change.saved={true,change.address,change.control&slotMask(index)};
            change.newAddress=proposed.address;
            change.newControl=(change.control&~slotMask(index))|(uint64_t(1)<<(index*2))|
                (uint64_t(proposed.trigger|(lengthCode(proposed.length)<<2))<<(16+index*4));
        }else if(change.saved.owned){
            const Slot& installed=session.slots[index];
            const uint64_t expected=(uint64_t(1)<<(index*2))|(uint64_t(installed.trigger|(lengthCode(installed.length)<<2))<<(16+index*4));
            if(change.address!=installed.address||(change.control&slotMask(index))!=expected){errno=EBUSY;return false;}
            change.newAddress=change.saved.address;change.newControl=(change.control&~slotMask(index))|change.saved.control;
            change.saved={};
        }
        changes.push_back(change);
    }
    size_t applied=0;
    std::vector<HardwareRepair> rollback;rollback.reserve(changes.size());
    for(const Change& change:changes)rollback.push_back({change.tid,index,change.address,change.control,change.dr6});
    for(;applied<changes.size();++applied){
        const Change& change=changes[applied];
        if(!pokeRegister(change.tid,index,change.newAddress)||!pokeRegister(change.tid,7,change.newControl)||
            (!proposed.active&&!pokeRegister(change.tid,6,change.dr6&~(uint64_t(1)<<index))))break;
    }
    if(applied<changes.size()){
        int failure=error();bool recovered=true;
        for(size_t i=0;i<=applied;++i){const Change& change=changes[i];
            if(!pokeRegister(change.tid,7,change.control)||!pokeRegister(change.tid,index,change.address)||!pokeRegister(change.tid,6,change.dr6))recovered=false;
        }
        if(!recovered){session.faulted=true;session.recoveryHardware.swap(rollback);}errno=recovered?failure:EIO;return false;
    }
    for(const Change& change:changes)session.threads.at(change.tid).saved[index]=change.saved;
    session.slots[index]=proposed;return true;
}
struct Mapping { uint64_t start,end; unsigned protection; };
bool mappings(std::vector<Mapping>& output) {
    std::ifstream input("/proc/"+std::to_string(session.pid)+"/maps");
    if(!input){errno=ESRCH;return false;}
    std::string line;
    while(std::getline(input,line)){
        unsigned long long start=0,end=0;char permissions[5]{};
        if(std::sscanf(line.c_str(),"%llx-%llx %4s",&start,&end,permissions)!=3||start>=end){errno=EPROTO;return false;}
        unsigned protect=(permissions[0]=='r'?1u:0u)|(permissions[1]=='w'?2u:0u)|(permissions[2]=='x'?4u:0u);
        output.push_back({static_cast<uint64_t>(start),static_cast<uint64_t>(end),protect});
    }
    return true;
}
bool mappedProtection(uint64_t address,uint64_t length,unsigned& previous) {
    std::vector<Mapping> maps;if(!mappings(maps))return false;
    uint64_t cursor=address,remaining=length;bool first=true;
    for(const Mapping& map:maps){
        if(cursor<map.start){errno=EFAULT;return false;}
        if(cursor>=map.end)continue;
        if(first){previous=map.protection;first=false;}
        else if(previous!=map.protection){errno=ENOTSUP;return false;}
        uint64_t count=std::min(remaining,map.end-cursor);remaining-=count;
        if(!remaining)return true;cursor+=count;
    }
    errno=EFAULT;return false;
}
// Remote syscalls run only one stopped, owned thread. No function call, target
// stack write, red-zone use, signal handler, or remote thread is involved.
bool remoteSyscall(long number,const std::array<uint64_t,6>& arguments,uint64_t& result,
    uint64_t excludedAddress=0,uint64_t excludedLength=0) {
    if(!session.paused||!allStopped()){errno=EBUSY;return false;}
    if(session.faulted||!session.recoveryBytes.empty()){errno=EIO;return false;}
    pid_t tid=0;user_regs_struct original{};
    std::vector<unsigned char> saved(3),readback(3);
    if(number==SYS_mmap)session.orphanAllocations.reserve(session.orphanAllocations.size()+1);
    for(auto& item:session.threads){
        Thread& thread=item.second;
        bool ownedSignal=session.pending&&session.current.owned&&session.current.record.thread==static_cast<uint64_t>(item.first);
        if(thread.exiting||thread.groupStopped||thread.foreign||(thread.signal&&!ownedSignal))continue;
        user_regs_struct candidate{};if(!getRegisters(item.first,candidate))continue;
        if(!validRange(candidate.rip,3))continue;
        if(excludedLength && candidate.rip<excludedAddress+excludedLength && candidate.rip+3>excludedAddress)continue;
        uint64_t dr7=0;if(!peekRegister(item.first,7,dr7))continue;
        bool debugConflict=false;
        for(unsigned slot=0;slot<4;++slot)if((dr7&(uint64_t(3)<<(2*slot)))&&((dr7>>(16+4*slot))&3)==0){
            uint64_t address=0;if(!peekRegister(item.first,slot,address)){debugConflict=true;break;}
            if(address>=candidate.rip&&address-candidate.rip<3){debugConflict=true;break;}
        }
        if(debugConflict||!ptraceRead(item.first,candidate.rip,saved.data(),3))continue;
        tid=item.first;original=candidate;break;
    }
    if(!tid){errno=ENOTSUP;return false;}
    const std::array<unsigned char,3> injected{{0x0f,0x05,0xcc}};
    user_regs_struct call=original;
    call.rax=static_cast<uint64_t>(number);call.orig_rax=static_cast<uint64_t>(-1);
    call.rdi=arguments[0];call.rsi=arguments[1];call.rdx=arguments[2];call.r10=arguments[3];call.r8=arguments[4];call.r9=arguments[5];
    call.eflags&=~TrapFlag;call.eflags|=ResumeFlag;
    bool installed=ptraceWrite(tid,original.rip,injected.data(),injected.size());
    int failure=installed?0:error();bool running=false,completion=false;int unexpectedStatus=0;bool unexpected=false;
    if(installed && !setRegisters(tid,call)){installed=false;failure=error();}
    if(installed){
        if(!trace(PTRACE_CONT,tid)){failure=error();}
        else{session.threads.at(tid).stopped=false;running=true;}
    }
    const auto deadline=Clock::now()+std::chrono::seconds(5);
    while(running && Clock::now()<deadline){
        int status=0;pid_t waited=waitpid(tid,&status,__WALL|WNOHANG);
        if(waited==-1){if(errno==EINTR)continue;failure=error();break;}
        if(!waited){std::this_thread::sleep_for(std::chrono::milliseconds(1));continue;}
        running=false;session.threads.at(tid).stopped=WIFSTOPPED(status);
        if(WIFSTOPPED(status)&&WSTOPSIG(status)==SIGTRAP&&(static_cast<unsigned>(status)>>16)==0){
            user_regs_struct returned{};siginfo_t info{};
            if(getRegisters(tid,returned)&&ptrace(PTRACE_GETSIGINFO,tid,nullptr,&info)!=-1 && returned.rip==original.rip+3 &&
                (info.si_code==TRAP_BRKPT||info.si_code==SI_KERNEL)){
                result=returned.rax;completion=true;break;
            }
        }
        unexpected=true;unexpectedStatus=status;failure=EINTR;break;
    }
    if(running){
        if(!failure)failure=ETIMEDOUT;
        if(trace(PTRACE_INTERRUPT,tid)){
            const auto recoveryDeadline=Clock::now()+std::chrono::seconds(5);
            while(Clock::now()<recoveryDeadline){
                int status=0;pid_t waited=waitpid(tid,&status,__WALL|WNOHANG);
                if(waited==tid){running=false;session.threads.at(tid).stopped=WIFSTOPPED(status);unexpected=true;unexpectedStatus=status;break;}
                if(waited==-1&&errno!=EINTR)break;
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        }
    }
    // Restore borrowed text through any still-stopped thread if the borrowed
    // thread died. Its dead register context no longer needs restoration.
    pid_t restoringTid=running?0:stoppedThread();
    bool textRestored=restoringTid&&ptraceWrite(restoringTid,original.rip,saved.data(),3)&&
        ptraceRead(restoringTid,original.rip,readback.data(),3)&&!std::memcmp(saved.data(),readback.data(),3);
    bool alive=session.threads.count(tid)&&session.threads.at(tid).stopped;
    bool contextRestored=!alive;
    if(alive){
        user_regs_struct verified{};
        contextRestored=setRegisters(tid,original)&&getRegisters(tid,verified)&&!std::memcmp(&original,&verified,sizeof(original));
    }
    if(!textRestored||!contextRestored||running){
        // Retain the exact borrowed text and context for an explicit retry.
        session.recoveryAddress=original.rip;session.recoveryBytes.swap(saved);
        session.recoveryThread=tid;session.recoveryRegisters=original;
        session.recoverySyscall=number;session.recoveryArguments=arguments;session.recoveryResult=result;session.recoveryCompleted=completion;
        if(unexpected&&session.threads.count(tid)){
            session.threads.at(tid).unprocessedStatus=unexpectedStatus;session.threads.at(tid).statusNeedsProcessing=true;
        }
        if(completion&&number==SYS_mmap&&result<static_cast<uint64_t>(-4095LL))session.orphanAllocations.emplace_back(result,arguments[1]);
        // A failed context restore is kept separate from code recovery below.
        // Do not allow continuation on an unknown register/borrowed-code state.
        session.faulted=true;errno=EIO;return false;
    }
    if(unexpected){
        pid_t priorStep=session.stepping;session.stepping=0;
        bool handled=handleStatus(tid,unexpectedStatus);session.stepping=priorStep;
        if(!handled){session.faulted=true;errno=EIO;return false;}
        session.paused=allStopped();
        errno=failure?failure:EINTR;return false;
    }
    if(!completion){errno=failure?failure:EIO;return false;}
    if(result>=static_cast<uint64_t>(-4095LL)){errno=static_cast<int>(-static_cast<int64_t>(result));return false;}
    return true;
}
uint64_t pageRounded(uint64_t length) {
    const uint64_t page=static_cast<uint64_t>(sysconf(_SC_PAGESIZE));
    if(!page||length>std::numeric_limits<uint64_t>::max()-(page-1)){errno=EOVERFLOW;return 0;}
    return (length+page-1)&~(page-1);
}
bool remoteAllocate(Request& request) {
    uint64_t length=pageRounded(request.length);if(!length)return false;
    std::vector<Mapping> maps;if(!mappings(maps))return false;
    const uint64_t anchor=request.value,page=static_cast<uint64_t>(sysconf(_SC_PAGESIZE));
    std::vector<uint64_t> candidates;
    constexpr uint64_t MaximumUserAddress=0x00007fffffffffffULL,Reach=0x7fff0000ULL;
    if(anchor&&anchor<=MaximumUserAddress){
        uint64_t low=anchor>Reach?std::max(page,anchor-Reach):page;
        uint64_t high=anchor<=MaximumUserAddress-Reach?anchor+Reach:MaximumUserAddress;
        uint64_t cursor=low;
        for(const Mapping& map:maps){
            if(map.end<=cursor)continue;
            if(map.start>high)break;
            uint64_t end=std::min(map.start,high);
            uint64_t first=(cursor+page-1)&~(page-1);
            if(end>=first&&length<=end-first){
                uint64_t last=(end-length)&~(page-1);uint64_t preferred=anchor&~(page-1);
                candidates.push_back(std::max(first,std::min(last,preferred)));
            }
            cursor=std::max(cursor,map.end);
        }
        uint64_t first=(cursor+page-1)&~(page-1);
        if(first<high&&length<=high-first)candidates.push_back(std::max(first,std::min((high-length)&~(page-1),anchor&~(page-1))));
        auto distance=[anchor](uint64_t address){return address>anchor?address-anchor:anchor-address;};
        std::sort(candidates.begin(),candidates.end(),[&](uint64_t a,uint64_t b){return distance(a)<distance(b);});
    }
    uint64_t allocated=0;bool success=false;
    // mmap hints are non-replacing on every supported kernel. The returned
    // address is checked instead of relying on MAP_FIXED_NOREPLACE support.
    for(uint64_t candidate:candidates){
        std::array<uint64_t,6> arguments{{candidate,length,request.flags?request.flags:3,MAP_PRIVATE|MAP_ANONYMOUS,static_cast<uint64_t>(-1),0}};
        if(!remoteSyscall(SYS_mmap,arguments,allocated)){
            if(errno==ENOMEM||errno==EEXIST||errno==EINVAL)continue;return false;
        }
        uint64_t distance=allocated>anchor?allocated-anchor:anchor-allocated;
        if(distance<=Reach){success=true;break;}
        uint64_t ignored=0;
        if(!remoteSyscall(SYS_munmap,{{allocated,length,0,0,0,0}},ignored,allocated,length))return false;
    }
    if(!success){
        if(!remoteSyscall(SYS_mmap,{{0,length,request.flags?request.flags:3,MAP_PRIVATE|MAP_ANONYMOUS,static_cast<uint64_t>(-1),0}},allocated))return false;
    }
    try{allocations.emplace(allocated,length);}
    catch(...){uint64_t ignored=0;remoteSyscall(SYS_munmap,{{allocated,length,0,0,0,0}},ignored,allocated,length);throw;}
    request.result=allocated;return true;
}
bool detachAll() {
    int failure=0;
    for(auto iterator=session.threads.begin();iterator!=session.threads.end();){
        pid_t tid=iterator->first;Thread& thread=iterator->second;
        if(!thread.stopped){if(!failure)failure=EBUSY;++iterator;continue;}
        // Seized group stops retain kernel job-control state when detached;
        // inject only actual pending signal-delivery signals, never SIGCONT.
        if(!trace(PTRACE_DETACH,tid,0,static_cast<uint64_t>(thread.signal))){
            if(errno!=ESRCH){if(!failure)failure=error();++iterator;continue;}
        }
        iterator=session.threads.erase(iterator);
    }
    if(failure){errno=failure;return false;}
    session=SessionState{};allocations.clear();return true;
}

bool recover() {
    if(!session.paused||!allStopped())if(!stopWorld())return false;
    if(!session.recoveryBytes.empty()){
        pid_t tid=stoppedThread();if(!tid)return false;
        std::vector<unsigned char> verified(session.recoveryBytes.size());
        if(!ptraceWrite(tid,session.recoveryAddress,session.recoveryBytes.data(),session.recoveryBytes.size())||
            !ptraceRead(tid,session.recoveryAddress,verified.data(),verified.size())||
            std::memcmp(verified.data(),session.recoveryBytes.data(),verified.size())){errno=EIO;return false;}
        auto borrowed=session.threads.find(session.recoveryThread);
        if(session.recoveryThread&&borrowed!=session.threads.end()&&!borrowed->second.exiting){
            user_regs_struct context{};
            if(!setRegisters(borrowed->first,session.recoveryRegisters)||!getRegisters(borrowed->first,context)||
                std::memcmp(&context,&session.recoveryRegisters,sizeof(context))){errno=EIO;return false;}
        }
        session.recoveryBytes.clear();session.recoveryAddress=0;session.recoveryThread=0;
    }
    for(const HardwareRepair& repair:session.recoveryHardware){
        auto thread=session.threads.find(repair.tid);if(thread==session.threads.end()||thread->second.exiting)continue;
        if(!pokeRegister(repair.tid,7,repair.control&~(uint64_t(3)<<(2*repair.index)))||
            !pokeRegister(repair.tid,repair.index,repair.address)||!pokeRegister(repair.tid,6,repair.status)||!pokeRegister(repair.tid,7,repair.control))return false;
    }
    for(const HardwareRepair& repair:session.recoveryHardware){
        auto thread=session.threads.find(repair.tid);if(thread==session.threads.end()||thread->second.exiting)continue;
        uint64_t address=0,control=0,status=0;
        if(!peekRegister(repair.tid,repair.index,address)||!peekRegister(repair.tid,7,control)||!peekRegister(repair.tid,6,status)||
            address!=repair.address||control!=repair.control||status!=repair.status){errno=EIO;return false;}
    }
    session.recoveryHardware.clear();
    std::vector<pid_t> retry;
    for(const auto& item:session.threads)if(item.second.statusNeedsProcessing)retry.push_back(item.first);
    for(pid_t tid:retry){
        auto thread=session.threads.find(tid);if(thread==session.threads.end())continue;
        int status=thread->second.unprocessedStatus;
        if(!handleStatus(tid,status))return false;
        thread=session.threads.find(tid);if(thread!=session.threads.end())thread->second.statusNeedsProcessing=false;
    }
    if(session.stepping){
        auto thread=session.threads.find(session.stepping);
        if(thread!=session.threads.end()&&!thread->second.exiting){
            user_regs_struct registers{};uint64_t dr6=0;
            if(!getRegisters(thread->first,registers)||!peekRegister(thread->first,6,dr6))return false;
            registers.eflags&=~TrapFlag;
            if(!setRegisters(thread->first,registers)||!pokeRegister(thread->first,6,dr6&~(uint64_t(1)<<14)))return false;
        }
        session.stepping=0;
    }
    for(auto& item:session.threads)if(item.second.needsSlots){
        if(!installSlots(item.first,item.second))return false;item.second.needsSlots=false;
    }
    session.faulted=false;
    if(session.recoveryProtection){
        uint64_t ignored=0;
        if(!remoteSyscall(SYS_mprotect,{{session.protectionAddress,session.protectionLength,session.protectionFlags,0,0,0}},ignored,
            session.protectionAddress,session.protectionLength))return false;
        session.recoveryProtection=false;
    }
    while(!session.orphanAllocations.empty()){
        const auto allocation=session.orphanAllocations.back();uint64_t ignored=0;
        if(!remoteSyscall(SYS_munmap,{{allocation.first,allocation.second,0,0,0,0}},ignored,allocation.first,allocation.second))return false;
        session.orphanAllocations.pop_back();
    }
    return true;
}
int abortAttach(Request& request,int originalError) {
    // Every successful seizure is rolled back, including partial enumeration.
    // Regain stops first so PTRACE_DETACH is valid and target signals survive.
    bool stopped=session.threads.empty()||stopWorld(5000,false);
    if(!stopped&&!session.threads.empty()){session.faulted=true;request.result=1;return fail(request,error());}
    if(!detachAll()){session.faulted=true;request.result=1;return fail(request,error());}
    return fail(request,originalError);
}
int execute(Request& request,ContextRecord* context,void* buffer,uint64_t bufferLength) {
    request.error=0;request.result=0;
    if(request.process>static_cast<uint64_t>(std::numeric_limits<pid_t>::max())||
        request.thread>static_cast<uint64_t>(std::numeric_limits<pid_t>::max()))return fail(request,EINVAL);
    if(session.pid&&session.worker!=static_cast<pid_t>(syscall(SYS_gettid)))return fail(request,EPERM);
    if(request.operation==Identity){
        uint64_t identity=0;if(!processIdentity(static_cast<pid_t>(request.process),identity))return fail(request,error());
        request.result=identity;return 0;
    }
    if(request.operation==Attach){
        if(session.pid)return fail(request,EBUSY);
        if(!request.process)return fail(request,EINVAL);
        uint64_t identity=0;if(!processIdentity(static_cast<pid_t>(request.process),identity))return fail(request,error());
        if(request.value&&request.value!=identity)return fail(request,ESRCH);
        allocations.clear();
        session.pid=static_cast<pid_t>(request.process);session.worker=static_cast<pid_t>(syscall(SYS_gettid));session.identity=identity;session.attaching=true;
        if(!seizeMissing()||!stopWorld())return abortAttach(request,error());
        session.attaching=false;
        // Initial target signals stay queued for normal event delivery; only
        // our interrupt/newborn stops are continued automatically.
        if(!resumeWorld())return abortAttach(request,error());
        request.result=identity;return 0;
    }
    if(!session.pid||(request.process&&request.process!=static_cast<uint64_t>(session.pid)))return fail(request,ESRCH);
    if(session.exited&&request.operation!=Continue&&request.operation!=Resume&&request.operation!=Detach)return fail(request,ESRCH);
    switch(request.operation){
    case 17:
        if(!recover()){request.result=1;session.faulted=true;return fail(request,error());}return 0;
    case Pause:
        if(!pause())return fail(request,error());return 0;
    case Resume:
        if(!session.pending){session.stepping=0;if(!resumeWorld())return fail(request,error());return 0;}
        if(!disposePending((request.flags&1)||session.current.owned,true))return fail(request,error());return 0;
    case Continue:{
        bool lifecycle=session.pending&&(session.current.record.kind==ThreadCreated||session.current.record.kind==ThreadExited);
        if(!disposePending((request.flags&1)!=0,!lifecycle))return fail(request,error());return 0;
    }
    case Detach:
        if(session.exited){session=SessionState{};allocations.clear();return 0;}
        if(session.faulted||!session.recoveryBytes.empty())return fail(request,EIO);
        if(!pause())return fail(request,error());
        session.stepping=0;
        for(unsigned i=0;i<4;++i)if(session.slots[i].active&&!setHardware(i,Slot{}))return fail(request,error());
        // Owned debugger traps are suppressed; unrelated target signals remain
        // in each thread's signal-delivery stop for the detach operation.
        if(session.pending&&session.current.owned){auto found=session.threads.find(static_cast<pid_t>(session.current.record.thread));if(found!=session.threads.end())found->second.signal=0;}
        for(const StopEvent& event:session.events)if(event.owned){auto found=session.threads.find(static_cast<pid_t>(event.record.thread));if(found!=session.threads.end())found->second.signal=0;}
        if(!detachAll())return fail(request,error());return 0;
    case Threads:{
        uint64_t count=0;for(const auto& item:session.threads)if(!item.second.foreign&&!item.second.exiting)++count;
        request.result=count;if(!buffer&&!bufferLength)return 0;
        if(!buffer||bufferLength/sizeof(uint64_t)<count)return fail(request,ENOBUFS);
        auto* ids=static_cast<uint64_t*>(buffer);for(const auto& item:session.threads)if(!item.second.foreign&&!item.second.exiting)*ids++=static_cast<uint64_t>(item.first);
        return 0;
    }
    case GetContext:case SetContext:{
        if(!context||context->size!=sizeof(ContextRecord)||!session.paused||!allStopped())return fail(request,EINVAL);
        pid_t tid=static_cast<pid_t>(request.thread);auto found=session.threads.find(tid);
        if(found==session.threads.end()||!found->second.stopped||found->second.exiting)return fail(request,ESRCH);
        user_regs_struct registers{};if(!getRegisters(tid,registers))return fail(request,error());
        if(request.operation==GetContext){copyContext(tid,registers,*context);return 0;}
        if(!(context->available&1)||context->thread!=request.thread)return fail(request,EINVAL);
        registers.rax=context->rax;registers.rbx=context->rbx;registers.rcx=context->rcx;registers.rdx=context->rdx;
        registers.rsi=context->rsi;registers.rdi=context->rdi;registers.rbp=context->rbp;registers.rsp=context->rsp;
        registers.r8=context->r8;registers.r9=context->r9;registers.r10=context->r10;registers.r11=context->r11;
        registers.r12=context->r12;registers.r13=context->r13;registers.r14=context->r14;registers.r15=context->r15;
        if(registers.rip!=context->rip)registers.orig_rax=static_cast<uint64_t>(-1);
        registers.rip=context->rip;registers.eflags=context->rflags;
        if(context->available&2)registers.fs_base=context->fsbase;
        if(context->available&4)registers.gs_base=context->gsbase;
        if(!setRegisters(tid,registers))return fail(request,error());return 0;
    }
    case Hardware:{
        if(!session.paused||!allStopped()||request.value>3)return fail(request,EINVAL);
        Slot proposed;
        if(request.flags&1){
            proposed={true,request.address,(request.flags>>8)&3u,(request.flags>>16)&255u};
            if((proposed.trigger!=0&&proposed.trigger!=1&&proposed.trigger!=3)||
                (proposed.length!=1&&proposed.length!=2&&proposed.length!=4&&proposed.length!=8)||
                (proposed.trigger==0&&proposed.length!=1)||request.address%proposed.length||!validRange(request.address,proposed.length))return fail(request,EINVAL);
        }
        if(!proposed.active&&!session.slots[static_cast<unsigned>(request.value)].active)return 0;
        if(!setHardware(static_cast<unsigned>(request.value),proposed)){if(session.faulted)request.result=1;return fail(request,error());}return 0;
    }
    case Step:{
        if(!session.paused||!allStopped()||session.faulted||!session.recoveryBytes.empty())return fail(request,EBUSY);
        auto found=session.threads.find(static_cast<pid_t>(request.thread));if(found==session.threads.end()||found->second.exiting)return fail(request,ESRCH);
        if(!session.events.empty())return fail(request,EBUSY);
        user_regs_struct registers{};if(!getRegisters(found->first,registers))return fail(request,error());
        if(registers.eflags&TrapFlag)return fail(request,EBUSY);
        if(session.pending&&session.current.signal&&session.current.record.thread==request.thread)
            found->second.signal=(request.flags&1)||session.current.owned?0:session.current.signal;
        if(!resumeThread(found->first,found->second,true))return fail(request,error());
        session.stepping=found->first;session.pending=false;session.reported=false;session.paused=false;return 0;
    }
    case ReadMemory:
        if(!buffer||request.length>bufferLength||!validRange(request.address,request.length))return fail(request,EINVAL);
        if(!exactRead(request.address,buffer,request.length))return fail(request,error());request.result=request.length;return 0;
    case WriteCode:
        if(!session.paused||!allStopped())return fail(request,EBUSY);
        if(!buffer||request.length>bufferLength||!validRange(request.address,request.length)||(request.flags&~1u)||
            ((request.flags&1)&&request.length!=1))return fail(request,EINVAL);
        for(const auto& item:session.threads)if(!item.second.exiting&&!item.second.foreign){
            user_regs_struct registers{};if(!getRegisters(item.first,registers))return fail(request,error());
            if(registers.rip>=request.address&&registers.rip-request.address<request.length&&!(request.flags&1))return fail(request,EBUSY);
        }
        if(!writeCode(request,buffer))return fail(request,error());return 0;
    case Allocate:
        if(!validRange(0,request.length)||(request.flags&~7u))return fail(request,EINVAL);
        if(!remoteAllocate(request)){if(session.faulted||!session.recoveryBytes.empty())request.result=1;return fail(request,error());}return 0;
    case Protect:{
        if(!validRange(request.address,request.length)||(request.flags&~7u))return fail(request,EINVAL);
        uint64_t page=static_cast<uint64_t>(sysconf(_SC_PAGESIZE));uint64_t start=request.address&~(page-1);
        uint64_t length=pageRounded(request.length+request.address-start);if(!length)return fail(request,error());
        unsigned previous=0;if(!mappedProtection(start,length,previous))return fail(request,error());
        uint64_t result=0;
        if(!remoteSyscall(SYS_mprotect,{{start,length,request.flags,0,0,0}},result,start,length)){
            if(session.faulted||!session.recoveryBytes.empty()){
                session.recoveryProtection=true;session.protectionAddress=start;session.protectionLength=length;session.protectionFlags=previous;
            }
            if(session.faulted||!session.recoveryBytes.empty())request.result=1;return fail(request,error());
        }
        request.result=previous;std::atomic_thread_fence(std::memory_order_seq_cst);return 0;
    }
    case Free:{
        auto allocation=allocations.find(request.address);
        if(allocation==allocations.end())return fail(request,EINVAL);
        uint64_t result=0;
        if(!remoteSyscall(SYS_munmap,{{request.address,allocation->second,0,0,0,0}},result,request.address,allocation->second)){
            if(session.faulted||!session.recoveryBytes.empty())request.result=1;return fail(request,error());
        }
        allocations.erase(allocation);return 0;
    }
    default:return fail(request,ENOTSUP);
    }
}
} // namespace
extern "C" uint64_t RcDebugQueryV1(uint32_t version) {
    return version==1?rcdebug::Session|rcdebug::Context|rcdebug::Stepping|rcdebug::CodeWrite|rcdebug::Allocation|rcdebug::OperandContext:0;
}
extern "C" int32_t RcDebugExecuteV1(rcdebug::Request* request,rcdebug::ContextRecord* context,void* buffer,uint64_t bufferLength) {
    if(!request||request->size!=sizeof(rcdebug::Request)){
        if(request&&request->size>=offsetof(rcdebug::Request,error)+sizeof(request->error))request->error=EINVAL;
        errno=EINVAL;return -1;
    }
    try{return execute(*request,context,buffer,bufferLength);}
    catch(const std::bad_alloc&){if(session.attaching)return abortAttach(*request,ENOMEM);if(session.faulted||!session.recoveryBytes.empty())request->result=1;return fail(*request,ENOMEM);}
    catch(...){if(session.attaching)return abortAttach(*request,EIO);if(session.faulted||!session.recoveryBytes.empty())request->result=1;return fail(*request,EIO);}
}
extern "C" int32_t RcDebugWaitV1(uint32_t timeout,rcdebug::EventRecord* event) {
    if(!event||event->size!=sizeof(rcdebug::EventRecord)||!session.pid||session.worker!=static_cast<pid_t>(syscall(SYS_gettid)))return waitFail(event,EINVAL);
    try{
        const auto deadline=Clock::now()+std::chrono::milliseconds(timeout);
        for(;;){
            if(session.pending){
                if(session.reported)return 1;
                *event=session.current.record;session.reported=true;return 0;
            }
            if(!session.events.empty()){
                if(!session.exited&&!allStopped()){
                    // The stop barrier may reap the final thread and queue a
                    // terminal event. Deliver that event instead of its ESRCH.
                    if(!stopWorld()&&!session.exited)return waitFail(event,error());
                }
                if(!session.exited)session.paused=true;
                choosePending();continue;
            }
            if(session.paused)return 1;
            const int status=pollStatuses();if(status<0)return waitFail(event,error());
            if(status==0){
                if(!session.exited){
                    if(!stopWorld()&&!session.exited)return waitFail(event,error());
                }
                if(session.events.empty()){
                    if(session.stepping){
                        auto found=session.threads.find(session.stepping);
                        if(found!=session.threads.end()&&!resumeThread(found->first,found->second,true))return waitFail(event,error());
                        session.paused=false;
                    }else if(!resumeWorld())return waitFail(event,error());
                }
                continue;
            }
            if(Clock::now()>=deadline)return 1;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }catch(const std::bad_alloc&){session.faulted=true;return waitFail(event,ENOMEM);}
     catch(...){session.faulted=true;return waitFail(event,EIO);}
}
