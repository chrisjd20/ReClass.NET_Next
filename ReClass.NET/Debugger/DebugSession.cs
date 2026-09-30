using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReClassNET.Core;
using ReClassNET.Memory;
using ReClassNET.AssemblyEditing;

namespace ReClassNET.Debugger
{
    public enum DebugSessionState { Detached,Attaching,Running,Paused,Detaching,Faulted,Exited }
    public enum SnapshotPhase { Before,After,Unknown }
    public sealed class RegisterSnapshot
    {
        public Guid SessionId {get;internal set;}
        public ulong ThreadId {get;internal set;}
        public long EventId {get;internal set;}
        public DateTime Timestamp {get;internal set;}
        public AdvancedContext Context {get;internal set;}
        public SnapshotPhase Phase {get;internal set;}
        public IDictionary<string,ulong> Registers => Context.Registers();
    }
    public sealed class WatchHit
    {
        public Guid WatchId {get;internal set;}
        public ulong WatchedAddress {get;internal set;}
        public int WatchedLength {get;internal set;}
        public ulong EventRip {get;internal set;}
        public ulong Count {get;internal set;}
        public RegisterSnapshot Snapshot {get;internal set;}
        public IReadOnlyList<InstructionRecord> Candidates {get;internal set;}
        public bool Confirmed {get;internal set;}
        public string Status {get;internal set;}
        public bool Completed {get;internal set;}
    }
    public sealed class DebugWatch
    {
        public Guid Id {get;}=Guid.NewGuid();
        public ulong Address {get;internal set;}
        public int Length {get;internal set;}
        public bool Execution {get;internal set;}
        public bool PauseWhenMatched {get;set;}
        public string ConditionText {get;internal set;}
        internal WatchCondition Condition;
        internal readonly List<int> Slots=new List<int>();
        internal byte Original;
        internal bool Software;
        internal ulong Count;
        internal Action<WatchHit> Sink;
        internal readonly Dictionary<string,ulong> Counts=new Dictionary<string,ulong>();
        public string Error {get;internal set;}
    }
    public sealed class TraceEntry
    {
        public ulong Address {get;internal set;}
        public string Instruction {get;internal set;}
        public RegisterSnapshot Before {get;internal set;}
        public RegisterSnapshot After {get;internal set;}
    }
    public sealed class TraceResult
    {
        public IReadOnlyList<TraceEntry> Entries {get;internal set;}
        public string StopReason {get;internal set;}
    }
    public sealed class DebugSession : IDisposable
    {
        private readonly RemoteProcess process;
        private readonly IAdvancedDebugProvider provider;
        private readonly ICoreProcessFunctions selectedProvider;
        private readonly BlockingCollection<Action> commands=new BlockingCollection<Action>(256);
        private readonly Thread worker;
        private readonly Dictionary<Guid,DebugWatch> watches=new Dictionary<Guid,DebugWatch>();
        private readonly DebugWatch[] slots=new DebugWatch[4];
        private readonly HashSet<ulong> knownEntries=new HashSet<ulong>();
        private readonly InstructionService instructions=new InstructionService();
        private volatile bool shutdown;
        private long eventId;
        private int stopDepth,pauseDispatchDepth;
        private bool leaseWasRunning,userPaused,recoveryRequired;
        private string recoveryReason;
        private AdvancedEvent pending;
        private bool handledPending=true;
        private bool nativeAttached;
        public DebugSessionState State {get;private set;}=DebugSessionState.Detached;
        public Guid Id {get;}=Guid.NewGuid();
        public ulong ProcessId {get;}
        public ulong ProcessCreationMarker {get;private set;}
        public string ProviderIdentity {get;}
        public AdvancedCapabilities Capabilities => provider?.Capabilities??AdvancedCapabilities.None;
        public event Action<DebugSessionState> StateChanged;
        public event Action<string> Diagnostic;
        public event Action<ulong,bool> ModuleChanged;
        public Func<Task> RestoreOwnedPatchesAsync {get;set;}
        public Func<ulong,int,string> FindPatchOverlap {get;set;}
        public DebugSession(RemoteProcess process)
        {
            this.process=process??throw new ArgumentNullException(nameof(process));
            if(process.UnderlayingProcess==null) throw new InvalidOperationException("Open a process first.");
            ProcessId=unchecked((ulong)process.UnderlayingProcess.Id.ToInt64());
            selectedProvider=process.CoreFunctions.CurrentFunctions;
            provider=selectedProvider as IAdvancedDebugProvider;
            ProviderIdentity=process.CoreFunctions.CurrentFunctionsProvider;
            worker=new Thread(Run){IsBackground=true,Name="ReClass advanced debugger"};worker.Start();
        }
        private void SetState(DebugSessionState state)
        {
            State=state;
            if(state==DebugSessionState.Exited){watches.Clear();Array.Clear(slots,0,4);knownEntries.Clear();}
            var subscribers=StateChanged;
            if(subscribers!=null)foreach(Action<DebugSessionState> handler in subscribers.GetInvocationList())
                try{handler(state);}catch(Exception error){System.Diagnostics.Debug.WriteLine(error.Message);}
        }
        private void EmitDiagnostic(string message)
        {
            var subscribers=Diagnostic;
            if(subscribers!=null)foreach(Action<string> handler in subscribers.GetInvocationList())
                try{handler(message);}catch(Exception error){System.Diagnostics.Debug.WriteLine(error.Message);}
        }
        private static InvalidOperationException EventWaitFailure(string message,AdvancedEvent evt)=>
            new InvalidOperationException(message+" Native error "+evt.Code+" (0x"+evt.Code.ToString("X")+"), thread "+evt.Thread+", event "+evt.Kind+", address 0x"+evt.Address.ToString("X")+".");
        public Task<T> InvokeAsync<T>(Func<T> action,CancellationToken cancellation=default(CancellationToken))
        {
            if(Thread.CurrentThread==worker) {try{return Task.FromResult(action());}catch(Exception e){var failed=new TaskCompletionSource<T>();failed.SetException(e);return failed.Task;}}
            var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(shutdown){result.SetException(new ObjectDisposedException(nameof(DebugSession)));return result.Task;}
            try
            {
                if(!commands.TryAdd(()=>{if(shutdown){result.TrySetException(new ObjectDisposedException(nameof(DebugSession)));return;}if(cancellation.IsCancellationRequested){result.TrySetCanceled();return;}try{result.TrySetResult(action());}catch(Exception e){result.TrySetException(e);}}))
                    result.TrySetException(new InvalidOperationException("Debugger command queue is full; retry after the current operation completes."));
            }
            catch(Exception e){result.TrySetException(e);}
            return result.Task;
        }
        public T Invoke<T>(Func<T> action)=>Thread.CurrentThread==worker?action():InvokeAsync(action).GetAwaiter().GetResult();
        private void Run()
        {
            while(!shutdown)
            {
                Action command;
                if(commands.TryTake(out command,State==DebugSessionState.Running?0:20)) {command();continue;}
                if(State!=DebugSessionState.Running) continue;
                try
                {
                    var evt=AdvancedEvent.Create();int result=provider.Wait(20,ref evt);
                    if(result<0) throw EventWaitFailure("Native debugger event wait failed.",evt);
                    if(result==0) HandleEvent(evt);
                }
                catch(Exception e){recoveryRequired=true;recoveryReason=e.Message;SetState(DebugSessionState.Faulted);EmitDiagnostic(e.Message);}
            }
            Action abandoned;while(commands.TryTake(out abandoned))abandoned();
        }
        private void CheckIdentity()
        {
            if(!ReferenceEquals(selectedProvider,process.CoreFunctions.CurrentFunctions) || process.UnderlayingProcess==null || unchecked((ulong)process.UnderlayingProcess.Id.ToInt64())!=ProcessId)
                throw new InvalidOperationException("Process or selected provider changed; this session is stale.");
        }
        internal AdvancedRequest Native(AdvancedOperation op,ulong address=0,ulong value=0,ulong length=0,uint flags=0,ulong thread=0,byte[] bytes=null)
        {
            CheckIdentity();var request=AdvancedRequest.Create(op,ProcessId);
            request.Address=address;request.Value=value;request.Length=length;request.Flags=flags;request.Thread=thread;
            var context=AdvancedContext.Create();int status=provider.Execute(ref request,ref context,bytes);
            if(status<0)
            {
                var failure=new NativeDebugException(op,request.Error,request.Result);
                if(failure.RecoveryRequired)KeepStopped(failure.Message);
                throw failure;
            }
            return request;
        }
        public Task AttachAsync(CancellationToken cancellation=default(CancellationToken)) => InvokeAsync(()=>{
            if(State==DebugSessionState.Running||State==DebugSessionState.Paused)return true;
            if(State==DebugSessionState.Exited)throw new InvalidOperationException("This process session exited or changed address space. Reopen the process.");
            if(nativeAttached||recoveryRequired)throw new InvalidOperationException("The existing debugger session requires recovery before attachment.");
            if((Capabilities&AdvancedCapabilities.Session)==0)throw new NotSupportedException("Selected provider does not support advanced x64 debugging.");
            SetState(DebugSessionState.Attaching);
            try
            {
                ProcessCreationMarker=Native(AdvancedOperation.Identity).Result;
                var attachment=Native(AdvancedOperation.Attach,value:ProcessCreationMarker);nativeAttached=true;
                if(attachment.Result!=ProcessCreationMarker)throw new InvalidOperationException("Process creation identity changed during debugger attachment.");
                userPaused=false;pending=default(AdvancedEvent);handledPending=true;SetState(DebugSessionState.Running);
            }
            catch
            {
                if(nativeAttached&&!recoveryRequired)
                {
                    try{Native(AdvancedOperation.Detach);nativeAttached=false;}
                    catch(Exception cleanup){KeepStopped("Attachment cleanup failed: "+cleanup.Message);}
                }
                SetState(nativeAttached||recoveryRequired?DebugSessionState.Faulted:DebugSessionState.Detached);throw;
            }
            return true;
        },cancellation);
        public async Task PauseAsync(){if(State==DebugSessionState.Detached)await AttachAsync().ConfigureAwait(false);await InvokeAsync(()=>{PauseInternal();userPaused=true;return true;}).ConfigureAwait(false);}
        private void PauseInternal()
        {
            if(State==DebugSessionState.Running)
            {
                Native(AdvancedOperation.Pause);
                SetState(DebugSessionState.Paused);
                var evt=AdvancedEvent.Create();int result=provider.Wait(0,ref evt);
                if(result<0)throw EventWaitFailure("Could not obtain the process pause event.",evt);
                if(result==0)
                {
                    pauseDispatchDepth++;
                    try{HandleEvent(evt);}finally{pauseDispatchDepth--;}
                }
            }
            if(State!=DebugSessionState.Paused && !(State==DebugSessionState.Faulted&&recoveryRequired))throw new InvalidOperationException("Debugger must be attached and paused.");
        }
        public Task ResumeAsync()=>InvokeAsync(()=>{
            if(stopDepth!=0)throw new InvalidOperationException("A code transaction owns the process stop.");
            ResumeInternal();userPaused=false;return true;
        });
        private void ResumeInternal()
        {
            if(recoveryRequired)throw new InvalidOperationException("Recovery required: "+recoveryReason);
            if(State!=DebugSessionState.Paused)return;
            Native(AdvancedOperation.Resume,flags:handledPending?1U:0U);pending=default(AdvancedEvent);SetState(DebugSessionState.Running);
        }
        public Task<IDisposable> AcquireStopAsync(CancellationToken cancellation=default(CancellationToken))=>InvokeAsync(()=>{
            if(stopDepth==0){leaseWasRunning=State==DebugSessionState.Running&&!userPaused;PauseInternal();}
            stopDepth++;return (IDisposable)new StopLease(this);
        },cancellation);
        private sealed class StopLease:IDisposable
        {
            private DebugSession session;
            public StopLease(DebugSession session){this.session=session;}
            public void Dispose(){var owner=Interlocked.Exchange(ref session,null);if(owner!=null)owner.Invoke(()=>{if(--owner.stopDepth==0&&owner.leaseWasRunning&&!owner.recoveryRequired&&!owner.userPaused)owner.ResumeInternal();return true;});}
        }
        public void KeepStopped(string reason)=>Invoke(()=>{recoveryRequired=true;recoveryReason=reason;SetState(DebugSessionState.Faulted);return true;});
        public void RecoveryCompleted()=>Invoke(()=>{recoveryRequired=false;recoveryReason=null;if(State==DebugSessionState.Faulted)SetState(DebugSessionState.Paused);return true;});
        public Task RecoverAsync()=>InvokeAsync(()=>{Native(AdvancedOperation.Recover);RecoveryCompleted();userPaused=true;return true;});
        public byte[] ReadExact(ulong address,int length)=>Invoke(()=>{
            if(length<0||length>1024*1024)throw new ArgumentOutOfRangeException(nameof(length));
            checked{var end=address+(ulong)length;}
            var bytes=new byte[length];
            if(State==DebugSessionState.Detached)
            {
                CheckIdentity();Native(AdvancedOperation.Identity);
                if(!process.ReadExactForDebugger(address,bytes))throw new InvalidOperationException("Could not read the exact instruction bytes.");
            }
            else Native(AdvancedOperation.ReadMemory,address:address,length:(ulong)length,bytes:bytes);
            return bytes;
        });
        public AdvancedContext GetContext(ulong thread)=>Invoke(()=>{
            var req=AdvancedRequest.Create(AdvancedOperation.GetContext,ProcessId);req.Thread=thread;var ctx=AdvancedContext.Create();
            CheckIdentity();if(provider.Execute(ref req,ref ctx,null)<0)throw new NativeDebugException(AdvancedOperation.GetContext,req.Error,req.Result);return ctx;
        });
        public void SetContext(AdvancedContext context)=>Invoke(()=>{
            CheckIdentity();var req=AdvancedRequest.Create(AdvancedOperation.SetContext,ProcessId);req.Thread=context.Thread;
            if(provider.Execute(ref req,ref context,null)<0)throw new NativeDebugException(AdvancedOperation.SetContext,req.Error,req.Result);return true;
        });
        public IReadOnlyList<ulong> Threads()=>Invoke(()=>{
            var bytes=new byte[8192*8];var req=Native(AdvancedOperation.Threads,bytes:bytes);
            if(req.Result>8192)throw new InvalidOperationException("Thread enumeration exceeded capacity.");
            var ids=new ulong[(int)req.Result];for(int i=0;i<ids.Length;i++)ids[i]=BitConverter.ToUInt64(bytes,i*8);return (IReadOnlyList<ulong>)ids;
        });
        public void WriteCode(ulong address,byte[] bytes,bool breakpoint=false)=>Invoke(()=>{Native(AdvancedOperation.WriteCode,address:address,length:(ulong)bytes.Length,flags:breakpoint?1U:0U,bytes:bytes);return true;});
        public ulong Allocate(ulong near,int length)=>Invoke(()=>Native(AdvancedOperation.Allocate,value:near,length:(ulong)length,flags:3).Result);
        public void Protect(ulong address,int length,uint protection)=>Invoke(()=>{Native(AdvancedOperation.Protect,address:address,length:(ulong)length,flags:protection);return true;});
        public void Free(ulong address,int length)=>Invoke(()=>{Native(AdvancedOperation.Free,address:address,length:(ulong)length);return true;});
        public string FindBreakpointOverlap(ulong address,int length)=>Invoke(()=>{
            ulong end=checked(address+(ulong)length);
            var match=watches.Values.FirstOrDefault(w=>w.Execution&&w.Software&&w.Address<end&&checked(w.Address+(ulong)w.Length)>address);
            return match==null?null:"Instruction watch "+match.Id;
        });
        public IReadOnlyList<ulong> KnownExecutionEntries()=>Invoke(()=>(IReadOnlyList<ulong>)knownEntries.Concat(watches.Values.Where(w=>w.Execution).Select(w=>w.Address)).Distinct().ToArray());
        public void RecordInstructions(IEnumerable<InstructionRecord> decoded)=>Invoke(()=>{
            foreach(var record in decoded)
            {
                if((record.FlowControl==Iced.Intel.FlowControl.Call||record.FlowControl==Iced.Intel.FlowControl.ConditionalBranch||record.FlowControl==Iced.Intel.FlowControl.UnconditionalBranch)&&knownEntries.Count<10000)
                    knownEntries.Add(record.Instruction.NearBranchTarget);
            }
            return true;
        });
        public Task<DebugWatch> StartWatchAsync(ulong address,int length,bool writeOnly,Action<WatchHit> sink,string condition="",bool pause=false)=>InvokeAsync(()=>{
            if(stopDepth>0)throw new InvalidOperationException("A code transaction owns the process stop; retry after it finishes.");
            if(length<=0||length>32)throw new ArgumentOutOfRangeException(nameof(length));
            checked{var end=address+(ulong)length;}
            var segments=new List<Tuple<ulong,int>>();ulong cursor=address;int remaining=length;
            while(remaining>0){int n=new[]{8,4,2,1}.First(x=>x<=remaining&&cursor%(ulong)x==0);segments.Add(Tuple.Create(cursor,n));cursor=checked(cursor+(ulong)n);remaining-=n;}
            var free=Enumerable.Range(0,4).Where(i=>slots[i]==null).ToArray();
            if(segments.Count>free.Length)throw new InvalidOperationException("Entire watched range requires more available hardware slots.");
            var watch=new DebugWatch{Address=address,Length=length,Sink=sink,ConditionText=condition,Condition=WatchCondition.Parse(condition),PauseWhenMatched=pause};
            bool resume=State==DebugSessionState.Running;PauseInternal();
            try
            {
                for(int i=0;i<segments.Count;i++)
                {
                    int slot=free[i];Native(AdvancedOperation.Hardware,address:segments[i].Item1,value:(ulong)slot,flags:1U|((writeOnly?1U:3U)<<8)|((uint)segments[i].Item2<<16));
                    watch.Slots.Add(slot);slots[slot]=watch;
                }
                watches.Add(watch.Id,watch);
            }
            catch
            {
                foreach(int slot in watch.Slots){try{Native(AdvancedOperation.Hardware,value:(ulong)slot);}catch(Exception e){KeepStopped(e.Message);}slots[slot]=null;}
                throw;
            }
            finally{if(resume&&!recoveryRequired)ResumeInternal();}
            return watch;
        });
        public Task<DebugWatch> StartInstructionWatchAsync(ulong address,Action<WatchHit> sink,string condition="",bool pause=false,bool hardware=false)=>InvokeAsync(()=>{
            if(stopDepth>0)throw new InvalidOperationException("A code transaction owns the process stop; retry after it finishes.");
            if((Capabilities&(AdvancedCapabilities.Context|AdvancedCapabilities.Stepping))!=(AdvancedCapabilities.Context|AdvancedCapabilities.Stepping))throw new NotSupportedException("Instruction discovery requires thread context and instruction stepping from the selected provider.");
            var overlap=FindPatchOverlap?.Invoke(address,1);if(overlap!=null)throw new InvalidOperationException(overlap);
            if(watches.Values.Any(w=>w.Execution&&w.Address==address))throw new InvalidOperationException("Instruction already watched.");
            var watch=new DebugWatch{Address=address,Length=1,Execution=true,Sink=sink,ConditionText=condition,Condition=WatchCondition.Parse(condition),PauseWhenMatched=pause};
            bool resume=State==DebugSessionState.Running;PauseInternal();
            try
            {
                var first=ReadInstruction(address);var original=first.Bytes;
                if(hardware)
                {
                    int slot=Array.FindIndex(slots,x=>x==null);if(slot<0)throw new InvalidOperationException("No hardware execution slot available.");
                    Native(AdvancedOperation.Hardware,address:address,value:(ulong)slot,flags:1U|(1U<<16));slots[slot]=watch;watch.Slots.Add(slot);
                }
                else
                {
                    if(original[0]==0xcc)throw new InvalidOperationException("Instruction already contains INT3.");
                    watch.Software=true;watch.Original=original[0];WriteCode(address,new byte[]{0xcc},true);
                }
                watches.Add(watch.Id,watch);
                if(knownEntries.Count<10000)knownEntries.Add(address);
            }
            finally{if(resume&&!recoveryRequired)ResumeInternal();}
            return watch;
        });
        public Task StopWatchAsync(Guid id)=>InvokeAsync(()=>{StopWatchInternal(id);return true;});
        private void StopWatchInternal(Guid id)
        {
            DebugWatch watch;if(!watches.TryGetValue(id,out watch))return;
            if(State==DebugSessionState.Exited){watches.Remove(id);return;}
            bool resume=State==DebugSessionState.Running;PauseInternal();
            try
            {
                if(watch.Software)
                {
                    var current=ReadExact(watch.Address,1);
                    if(current[0]!=0xcc)throw new InvalidOperationException("Instruction watch byte changed externally; restore refused.");
                    WriteCode(watch.Address,new[]{watch.Original},true);
                }
                foreach(int slot in watch.Slots){Native(AdvancedOperation.Hardware,value:(ulong)slot);slots[slot]=null;}
                watches.Remove(id);
            }
            catch(Exception e){KeepStopped(e.Message);throw;}
            finally{if(resume&&!recoveryRequired)ResumeInternal();}
        }
        private RegisterSnapshot Snapshot(AdvancedContext ctx,SnapshotPhase phase)=>new RegisterSnapshot{SessionId=Id,ThreadId=ctx.Thread,Context=ctx,Phase=(ctx.Available&1)!=0?phase:SnapshotPhase.Unknown,Timestamp=DateTime.UtcNow,EventId=Interlocked.Increment(ref eventId)};
        private InstructionRecord ReadInstruction(ulong address)
        {
            var breakpoint=watches.Values.FirstOrDefault(w=>w.Software&&w.Address==address);
            for(int length=1;length<=15;length++)
            {
                var bytes=ReadExact(address,length);
                if(breakpoint!=null&&bytes[0]==0xcc)bytes[0]=breakpoint.Original;
                var decode=instructions.Decode(bytes,address);
                if(decode.Success&&decode.Instructions.Count==1)return decode.Instructions[0];
            }
            throw new InvalidOperationException("Instruction cannot be decoded at this origin.");
        }
        private void HandleModuleChange(AdvancedEvent evt)
        {
            bool unloaded=evt.Code==2;
            if(unloaded)process.InvalidateModuleInstance(evt.Address);
            process.UpdateProcessInformations();
            var subscribers=ModuleChanged;
            if(subscribers!=null)foreach(Action<ulong,bool> handler in subscribers.GetInvocationList())
                try{handler(evt.Address,unloaded);}catch(Exception error){System.Diagnostics.Debug.WriteLine(error.Message);}
        }
        private bool IsOwned(AdvancedEvent evt)=>evt.Kind==AdvancedEventKind.Attached||evt.Kind==AdvancedEventKind.Pause||evt.Kind==AdvancedEventKind.ThreadCreated||evt.Kind==AdvancedEventKind.ThreadExited||evt.Kind==AdvancedEventKind.ProcessExited||evt.Kind==AdvancedEventKind.ModuleChanged;
        private void HandleEvent(AdvancedEvent evt)
        {
            pending=evt;handledPending=IsOwned(evt);SetState(DebugSessionState.Paused);
            if(evt.Kind==AdvancedEventKind.ModuleChanged)HandleModuleChange(evt);
            if(evt.Kind==AdvancedEventKind.ProcessExited||evt.Kind==AdvancedEventKind.Exec)
            {
                watches.Clear();Array.Clear(slots,0,4);
                if(evt.Kind==AdvancedEventKind.Exec){Native(AdvancedOperation.Detach);nativeAttached=false;}
                else {Native(AdvancedOperation.Continue,flags:1);nativeAttached=false;}
                SetState(DebugSessionState.Exited);EmitDiagnostic(evt.Kind==AdvancedEventKind.Exec?"Target exec changed the address space; all previews invalidated.":"Target exited.");return;
            }
            var owned=watches.Values.FirstOrDefault(w=>w.Software&&evt.Kind==AdvancedEventKind.Breakpoint&&w.Address==evt.Address);
            if(owned!=null){handledPending=true;HandleSoftware(owned,evt);return;}
            var triggered=Enumerable.Range(0,4).Where(i=>(evt.CausedBy&(1U<<i))!=0&&slots[i]!=null).Select(i=>slots[i]).Distinct().ToArray();
            foreach(var watch in triggered.Where(w=>!w.Execution)) CaptureDataWatch(watch,evt);
            var execution=triggered.FirstOrDefault(w=>w.Execution);
            if(execution!=null){handledPending=true;HandleExecution(execution,evt);return;}
            if(pauseDispatchDepth==0&&!userPaused&&!recoveryRequired){Native(AdvancedOperation.Continue,flags:handledPending?1U:0U);SetState(DebugSessionState.Running);}
        }
        private InstructionRecord[] PreviousCandidates(ulong rip)
        {
            var result=new List<InstructionRecord>();
            for(int n=1;n<=15&&rip>=(ulong)n;n++)
            {
                try{var decoded=instructions.Decode(ReadExact(rip-(ulong)n,n),rip-(ulong)n);if(decoded.Success&&decoded.Instructions.Count==1&&decoded.Instructions[0].Length==n)result.Add(decoded.Instructions[0]);}catch{ }
            }
            return result.ToArray();
        }
        private void CaptureDataWatch(DebugWatch watch,AdvancedEvent evt)
        {
            if(!watches.ContainsKey(watch.Id))return;
            handledPending=true;watch.Count++;
            var snapshot=Snapshot(evt.Context,(evt.Context.Available&1)!=0?SnapshotPhase.After:SnapshotPhase.Unknown);
            try
            {
                if(!watch.Condition.Evaluate(snapshot.Registers,evt.Thread,watch.Count,ReadExact))return;
                var candidates=(evt.Context.Available&1)!=0?PreviousCandidates(evt.Context.Rip):new InstructionRecord[0];
                var key=evt.Context.Rip.ToString("X");ulong count;
                if(!watch.Counts.TryGetValue(key,out count)&&watch.Counts.Count>=10000){watch.Error="10,000 distinct instructions reached.";EmitDiagnostic(watch.Error);StopWatchInternal(watch.Id);return;}
                watch.Counts[key]=++count;
                watch.Sink?.Invoke(new WatchHit{WatchId=watch.Id,WatchedAddress=watch.Address,WatchedLength=watch.Length,EventRip=evt.Context.Rip,Count=count,Snapshot=snapshot,Candidates=candidates,Confirmed=false,Completed=true,Status="After event; preceding candidates need confirmation"});
                if(watch.PauseWhenMatched)userPaused=true;
            }
            catch(Exception error){watch.Error=error.Message;userPaused=true;EmitDiagnostic("Watch stopped: "+error.Message);StopWatchInternal(watch.Id);}
        }
        private void HandleSoftware(DebugWatch watch,AdvancedEvent evt)=>HandleExecution(watch,evt);
        private void HandleExecution(DebugWatch watch,AdvancedEvent evt)
        {
            var before=evt.Context;
            if(watch.Software)
            {
                if(ReadExact(watch.Address,1)[0]!=0xcc)throw new InvalidOperationException("Owned breakpoint byte changed.");
                WriteCode(watch.Address,new[]{watch.Original},true);
                before.Rip=watch.Address;SetContext(before);
            }
            var decoded=new[]{ReadInstruction(watch.Address)};RecordInstructions(decoded);
            watch.Count++;var snapshot=Snapshot(before,(before.Available&1)!=0&&before.Rip==watch.Address?SnapshotPhase.Before:SnapshotPhase.Unknown);
            bool match;
            try{match=watch.Condition.Evaluate(snapshot.Registers,evt.Thread,watch.Count,ReadExact);}
            catch(Exception error)
            {
                watch.Error=error.Message;userPaused=true;
                if(watch.Software)watches.Remove(watch.Id);else StopWatchInternal(watch.Id);
                EmitDiagnostic("Watch stopped: "+error.Message);return;
            }
            bool completed=false;
            try{StepInternal(evt.Thread,TimeSpan.FromSeconds(5),CancellationToken.None);completed=true;}
            catch(Exception error){userPaused=true;EmitDiagnostic("Instruction execution interrupted: "+error.Message);}
            finally
            {
                if(watch.Software&&State!=DebugSessionState.Exited&&watches.ContainsKey(watch.Id))
                {
                    if(ReadExact(watch.Address,1)[0]!=watch.Original){KeepStopped("Instruction changed during breakpoint step.");throw new InvalidOperationException("Instruction changed during breakpoint step.");}
                    WriteCode(watch.Address,new byte[]{0xcc},true);
                }
            }
            if(match)
            {
                watch.Sink?.Invoke(new WatchHit{WatchId=watch.Id,WatchedAddress=watch.Address,WatchedLength=1,EventRip=before.Rip,Count=watch.Count,Snapshot=snapshot,Candidates=decoded,Confirmed=true,Completed=completed,Status=completed?"Observed execution; Before context":"Attempted execution"});
                if(watch.PauseWhenMatched)userPaused=true;
            }
            if(pauseDispatchDepth==0&&!userPaused&&!recoveryRequired&&State!=DebugSessionState.Exited)ResumeInternal();
        }
        public Task<RegisterSnapshot> StepIntoAsync(ulong thread,CancellationToken cancellation=default(CancellationToken))=>InvokeAsync(()=>{
            if(stopDepth>0)throw new InvalidOperationException("Code transaction owns stop.");
            PauseInternal();userPaused=true;return Snapshot(StepInternal(thread,TimeSpan.FromSeconds(5),cancellation),SnapshotPhase.After);
        },cancellation);
        private AdvancedContext StepInternal(ulong thread,TimeSpan timeout,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var before=GetContext(thread);
            var watch=watches.Values.FirstOrDefault(w=>w.Software&&w.Address==before.Rip);
            var hardware=watches.Values.Where(w=>w.Execution&&!w.Software&&w.Address==before.Rip).ToArray();
            var disabled=new List<int>();
            bool unarmed=watch!=null&&ReadExact(watch.Address,1)[0]==0xcc;
            if(unarmed)WriteCode(watch.Address,new[]{watch.Original},true);
            try
            {
                foreach(var hw in hardware)foreach(var slot in hw.Slots){Native(AdvancedOperation.Hardware,value:(ulong)slot);disabled.Add(slot);}
                return StepCore(thread,timeout,cancellation);
            }
            finally
            {
                if(State!=DebugSessionState.Exited)
                {
                    foreach(var slot in disabled)
                    {
                        var owner=slots[slot];if(owner==null)continue;
                        try{Native(AdvancedOperation.Hardware,address:owner.Address,value:(ulong)slot,flags:1U|(1U<<16));}
                        catch(Exception error){KeepStopped("Execution breakpoint rearm failed: "+error.Message);throw;}
                    }
                    if(unarmed&&watches.ContainsKey(watch.Id))
                    {
                        if(ReadExact(watch.Address,1)[0]!=watch.Original){KeepStopped("Breakpoint original changed during manual step.");throw new InvalidOperationException("Breakpoint original changed during manual step.");}
                        WriteCode(watch.Address,new byte[]{0xcc},true);
                    }
                }
            }
        }
        private AdvancedContext StepCore(ulong thread,TimeSpan timeout,CancellationToken cancellation)
        {
            Native(AdvancedOperation.Step,thread:thread,flags:handledPending?1U:0U);var clock=System.Diagnostics.Stopwatch.StartNew();
            while(true)
            {
                var evt=AdvancedEvent.Create();int result=provider.Wait(20,ref evt);
                if(result<0)throw EventWaitFailure("Step event wait failed.",evt);
                if(result==0)
                {
                    pending=evt;handledPending=IsOwned(evt)||evt.Kind==AdvancedEventKind.SingleStep;
                    if(evt.Kind==AdvancedEventKind.ProcessExited){Native(AdvancedOperation.Continue,flags:1);nativeAttached=false;SetState(DebugSessionState.Exited);throw new InvalidOperationException("Target exited during step.");}
                    if(evt.Kind==AdvancedEventKind.Exec){Native(AdvancedOperation.Detach);nativeAttached=false;SetState(DebugSessionState.Exited);throw new InvalidOperationException("Target exec invalidated the traced address space.");}
                    if(evt.Thread==thread&&evt.Kind==AdvancedEventKind.ThreadExited){SetState(DebugSessionState.Paused);throw new InvalidOperationException("Selected thread exited during step.");}
                    if(evt.Thread==thread&&evt.Kind==AdvancedEventKind.SingleStep){SetState(DebugSessionState.Paused);
                        foreach(var data in Enumerable.Range(0,4).Where(i=>(evt.CausedBy&(1U<<i))!=0&&slots[i]!=null&&!slots[i].Execution).Select(i=>slots[i]).Distinct().ToArray())CaptureDataWatch(data,evt);
                        return evt.Context;}
                    if(evt.Kind==AdvancedEventKind.ModuleChanged){HandleModuleChange(evt);Native(AdvancedOperation.Continue,flags:1);continue;}
                    if(evt.Kind==AdvancedEventKind.ThreadCreated||evt.Kind==AdvancedEventKind.ThreadExited){Native(AdvancedOperation.Continue,flags:1);continue;}
                    SetState(DebugSessionState.Paused);userPaused=true;throw new InvalidOperationException("Step interrupted by target event "+evt.Kind+" code 0x"+evt.Code.ToString("X"));
                }
                if(cancellation.IsCancellationRequested||clock.Elapsed>=timeout)
                {
                    // A requested step may be blocked on another held thread. Regain the stop before returning.
                    Native(AdvancedOperation.Pause);SetState(DebugSessionState.Paused);userPaused=true;
                    throw new OperationCanceledException(cancellation.IsCancellationRequested?"Trace/step cancelled while waiting.":"Step timed out; selected thread may be waiting on a held thread.");
                }
            }
        }
        public Task<TraceResult> StartTraceAsync(ulong thread,int maxInstructions=1000,int maxMilliseconds=5000,string stopCondition="",CancellationToken cancellation=default(CancellationToken))=>InvokeAsync(()=>{
            if(maxInstructions<1||maxInstructions>100000||maxMilliseconds<1||maxMilliseconds>30000)throw new ArgumentOutOfRangeException("Trace limits");
            if(stopDepth>0)throw new InvalidOperationException("Code transaction owns stop.");
            PauseInternal();userPaused=true;var condition=string.IsNullOrWhiteSpace(stopCondition)?null:WatchCondition.Parse(stopCondition);
            var entries=new List<TraceEntry>();var clock=System.Diagnostics.Stopwatch.StartNew();string reason="Instruction limit";
            try
            {
                for(int i=0;i<maxInstructions;i++)
                {
                    cancellation.ThrowIfCancellationRequested();var remaining=TimeSpan.FromMilliseconds(maxMilliseconds)-clock.Elapsed;
                    if(remaining<=TimeSpan.Zero){reason="Time limit";break;}
                    var before=Snapshot(GetContext(thread),SnapshotPhase.Before);
                    if(condition!=null&&condition.Evaluate(before.Registers,thread,(ulong)i,ReadExact)){reason="Stop condition";break;}
                    var record=ReadInstruction(before.Context.Rip);
                    if(record==null||!record.IsValid){reason="Cannot decode current instruction";break;}
                    remaining=TimeSpan.FromMilliseconds(maxMilliseconds)-clock.Elapsed;
                    if(remaining<=TimeSpan.Zero){reason="Time limit";break;}
                    RecordInstructions(new[]{record});
                    var after=Snapshot(StepInternal(thread,remaining,cancellation),SnapshotPhase.After);
                    entries.Add(new TraceEntry{Address=before.Context.Rip,Instruction=record.Text,Before=before,After=after});
                }
            }
            catch(Exception e){reason=e.Message;}
            return new TraceResult{Entries=entries,StopReason=reason};
        },cancellation);
        public async Task DetachAsync()
        {
            if(RestoreOwnedPatchesAsync!=null&&State!=DebugSessionState.Exited)await RestoreOwnedPatchesAsync().ConfigureAwait(false);
            await InvokeAsync(()=>{
                if(State==DebugSessionState.Detached)return true;
                if(State==DebugSessionState.Exited)
                {
                    if(nativeAttached){Native(AdvancedOperation.Detach);nativeAttached=false;}
                    return true;
                }
                if(recoveryRequired)throw new InvalidOperationException("Detach refused; recovery required: "+recoveryReason);
                foreach(var id in watches.Keys.ToArray())StopWatchInternal(id);
                SetState(DebugSessionState.Detaching);try{Native(AdvancedOperation.Detach);nativeAttached=false;SetState(DebugSessionState.Detached);}catch{SetState(DebugSessionState.Faulted);throw;}
                return true;
            }).ConfigureAwait(false);
        }
        public void Dispose(){DetachAsync().GetAwaiter().GetResult();shutdown=true;if(Thread.CurrentThread!=worker)worker.Join();commands.Dispose();}
    }
    public sealed class NativeDebugException:Exception
    {
        public uint NativeError {get;}
        public bool RecoveryRequired {get;}
        public NativeDebugException(AdvancedOperation operation,uint error,ulong result):base(operation+" failed: "+(ReClassNET.Native.NativeMethods.IsUnix()?"Linux errno "+error:new Win32Exception((int)error).Message+" (native "+error+")"))
        {
            NativeError=error;RecoveryRequired=result==1&&(operation==AdvancedOperation.WriteCode||operation==AdvancedOperation.Allocate||operation==AdvancedOperation.Protect||operation==AdvancedOperation.Free||operation==AdvancedOperation.Recover||operation==AdvancedOperation.Hardware||operation==AdvancedOperation.Step);
        }
    }
}
