using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ReClassNET.Core;
using ReClassNET.Debugger;
using ReClassNET.Memory;
using ReClassNET.Native;

namespace ReClassNET.Patching
{
    public sealed class SessionPatchTarget : IPatchTarget
    {
        private readonly RemoteProcess process;
        private readonly DebugSession session;
        private readonly Dictionary<string,Tuple<long,long,string>> fingerprints=new Dictionary<string,Tuple<long,long,string>>();
        public SessionPatchTarget(RemoteProcess process,DebugSession session){this.process=process;this.session=session;}
        public Guid SessionId=>session.Id;
        public string ProviderIdentity=>session.ProviderIdentity;
        public string Platform=>NativeMethods.IsUnix()?"Linux":"Windows";
        public bool IsAlive=>session.State!=DebugSessionState.Exited&&process.UnderlayingProcess!=null;
        public bool SupportsCodeTransactions=>(session.Capabilities&AdvancedCapabilities.CodeWrite)!=0;
        public bool SupportsAllocation=>(session.Capabilities&AdvancedCapabilities.Allocation)!=0;
        public IReadOnlyList<PatchModule> Modules
        {
            get
            {
                var sections=process.Sections.ToArray();
                return process.Modules.Select(m=>new PatchModule{
                    Name=m.Name,Path=m.Path,BaseAddress=Address(m.Start),Size=Address(m.Size),Sha256=Fingerprint(m.Path),
                    InstanceId=SessionId+":"+m.InstanceId,
                    ExecutableRanges=sections.Where(s=>(s.Protection&(SectionProtection.Read|SectionProtection.Execute))==(SectionProtection.Read|SectionProtection.Execute)&&Address(s.Start)>=Address(m.Start)&&Address(s.End)<=Address(m.End))
                        .Select(s=>new PatchRange{Address=Address(s.Start),Length=Address(s.Size)}).ToArray()
                }).ToArray();
            }
        }
        private string Fingerprint(string path)
        {
            try
            {
                var info=new FileInfo(path);if(!info.Exists)return null;
                lock(fingerprints)
                {
                    Tuple<long,long,string> cache;
                    if(fingerprints.TryGetValue(path,out cache)&&cache.Item1==info.Length&&cache.Item2==info.LastWriteTimeUtc.Ticks)return cache.Item3;
                    string hash;using(var file=info.OpenRead())using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
                    info.Refresh();fingerprints[path]=Tuple.Create(info.Length,info.LastWriteTimeUtc.Ticks,hash);return hash;
                }
            }
            catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
        }
        internal static ulong Address(IntPtr value)=>unchecked((ulong)value.ToInt64());
        public IReadOnlyList<ulong> StoppedInstructionPointers=>session.Threads().Select(t=>session.GetContext(t).Rip).ToArray();
        public IReadOnlyList<ulong> KnownIncomingTargets=>session.KnownExecutionEntries().Concat(process.NamedAddresses.Keys.Select(Address)).Distinct().ToArray();
        public async Task<IPatchStopLease> StopAsync(CancellationToken cancellation)
        {
            if(session.State==DebugSessionState.Detached)await session.AttachAsync(cancellation).ConfigureAwait(false);
            var lease=await session.AcquireStopAsync(cancellation).ConfigureAwait(false);
            try
            {
                var marker=session.Invoke(()=>session.Native(AdvancedOperation.Identity).Result);
                if(marker!=session.ProcessCreationMarker)throw new InvalidOperationException("Process creation identity changed.");
                return new Lease(session,lease);
            }
            catch{lease.Dispose();throw;}
        }
        private sealed class Lease:IPatchStopLease
        {
            private readonly DebugSession session;private IDisposable native;
            public Lease(DebugSession session,IDisposable native){this.session=session;this.native=native;}
            public void KeepStopped(string reason)=>session.KeepStopped(reason);
            public void RecoveryCompleted()=>session.RecoveryCompleted();
            public void Dispose()=>Interlocked.Exchange(ref native,null)?.Dispose();
        }
        public byte[] ReadExact(ulong address,int length)=>session.ReadExact(address,length);
        public PatchWriteResult WriteCode(ulong address,byte[] bytes)
        {
            try{session.WriteCode(address,bytes);return new PatchWriteResult{Success=true};}
            catch(NativeDebugException e){return new PatchWriteResult{Error=e.Message,NativeError=(int)e.NativeError,RecoveryRequired=e.RecoveryRequired};}
            catch(Exception e){return new PatchWriteResult{Error=e.Message};}
        }
        public PatchAllocation Allocate(ulong nearAddress,int size)=>new PatchAllocation{Address=session.Allocate(nearAddress,size),Size=size};
        public PatchWriteResult ProtectExecutable(PatchAllocation allocation)
        {
            try{session.Protect(allocation.Address,allocation.Size,5);return new PatchWriteResult{Success=true};}
            catch(NativeDebugException e){return new PatchWriteResult{Error=e.Message,NativeError=(int)e.NativeError,RecoveryRequired=e.RecoveryRequired};}
            catch(Exception e){return new PatchWriteResult{Error=e.Message};}
        }
        public void Free(PatchAllocation allocation)
        {
            if(allocation.Published)throw new InvalidOperationException("Published hook memory must remain until target exit.");
            session.Free(allocation.Address,allocation.Size);
        }
        public bool IsModuleCurrent(PatchModule module)=>module==null||Modules.Any(m=>m.InstanceId==module.InstanceId&&m.Sha256==module.Sha256);
        public string FindExternalOverlap(ulong address,int length)=>session.FindBreakpointOverlap(address,length);
    }
}
