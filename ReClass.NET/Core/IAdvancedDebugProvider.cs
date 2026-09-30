using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ReClassNET.Core
{
    [Flags]
    public enum AdvancedCapabilities : ulong { None=0, Session=1, Context=2, Stepping=4, CodeWrite=8, Allocation=16, OperandContext=32 }
    public enum AdvancedOperation : uint { Attach=1, Detach, Pause, Resume, GetContext, SetContext, Hardware, WriteCode, Allocate, Protect, Free, Step, Continue, Threads, Identity, ReadMemory, Recover }
    public enum AdvancedEventKind : uint { None=0, Attached=1, Exception=2, ThreadCreated=3, ThreadExited=4, ProcessExited=5, Exec=6, Breakpoint=7, SingleStep=8, Pause=9, ModuleChanged=10 }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct AdvancedRequest
    {
        public uint Size, Operation;
        public ulong Process, Thread, Address, Value, Length;
        public uint Flags, Error;
        public ulong Result;
        public static AdvancedRequest Create(AdvancedOperation operation, ulong process) => new AdvancedRequest { Size=64, Operation=(uint)operation, Process=process };
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct AdvancedContext
    {
        public uint Size, Available;
        public ulong Thread;
        public ulong Rax,Rbx,Rcx,Rdx,Rsi,Rdi,Rbp,Rsp,R8,R9,R10,R11,R12,R13,R14,R15,Rip,Rflags,Fsbase,Gsbase;
        public static AdvancedContext Create() => new AdvancedContext { Size=176 };
        public Dictionary<string,ulong> Registers()
        {
            var r = new Dictionary<string,ulong>(StringComparer.OrdinalIgnoreCase);
            if ((Available & 1)==0) return r;
            var names = new[]{"rax","rbx","rcx","rdx","rsi","rdi","rbp","rsp","r8","r9","r10","r11","r12","r13","r14","r15","rip","rflags"};
            var values = new[]{Rax,Rbx,Rcx,Rdx,Rsi,Rdi,Rbp,Rsp,R8,R9,R10,R11,R12,R13,R14,R15,Rip,Rflags};
            for(int i=0;i<names.Length;i++) r[names[i]]=values[i];
            if ((Available&2)!=0) r["fsbase"]=Fsbase;
            if ((Available&4)!=0) r["gsbase"]=Gsbase;
            return r;
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct AdvancedEvent
    {
        public uint Size;
        public AdvancedEventKind Kind;
        public ulong Process,Thread,Address,Code;
        public uint CausedBy, FirstChance;
        public AdvancedContext Context;
        public static AdvancedEvent Create() => new AdvancedEvent { Size=224, Context=AdvancedContext.Create() };
    }
    // Optional additive contract: legacy process plugins need not implement it.
    public interface IAdvancedDebugProvider
    {
        AdvancedCapabilities Capabilities { get; }
        int Execute(ref AdvancedRequest request, ref AdvancedContext context, byte[] buffer);
        int Wait(uint timeoutMilliseconds, ref AdvancedEvent evt);
    }
}
