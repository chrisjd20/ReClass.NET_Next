using System;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using ReClassNET.AssemblyEditing;
using ReClassNET.Memory;
using ReClassNET.Patching;
using ReClassNET.Project;

namespace ReClassNET.Debugger
{
    public sealed class DebugWorkspace : IDisposable
    {
        private Dictionary<Guid,int> savedRevisions=new Dictionary<Guid,int>();
        public RemoteProcess Process {get;}
        public DebugSession Session {get;}
        public SessionPatchTarget Target {get;}
        public InstructionService Instructions {get;}=new InstructionService();
        public AssemblyService Assembler {get;}=new AssemblyService();
        public PatchPlanner Planner {get;}
        public PatchManager Manager {get;}
        public ReClassNetProject Project {get;private set;}
        public PatchRepository Repository {get;private set;}
        public DebugWorkspace(RemoteProcess process,ReClassNetProject project)
        {
            Process=process;Session=new DebugSession(process);Target=new SessionPatchTarget(process,Session);
            Planner=new PatchPlanner(Instructions,Assembler);Manager=new PatchManager(Target,Planner);
            Project=project;Repository=new PatchRepository(project,project.MarkDirty);
            TrackRepository();
            Manager.IsDefinitionCurrent=(id,revision)=>{int current;return !Volatile.Read(ref savedRevisions).TryGetValue(id,out current)||current==revision;};
            Session.StateChanged+=state=>{if(state==DebugSessionState.Exited)Manager.InvalidateTarget();};
            Session.ModuleChanged+=(address,unloaded)=>{if(unloaded)Manager.InvalidateMappings(address);};
            Session.RestoreOwnedPatchesAsync=RestoreAsync;
            Session.FindPatchOverlap=(address,length)=>{
                var active=Manager.ActivePatches.FirstOrDefault(p=>new PatchRange{Address=p.Preview.Address,Length=(ulong)p.InstalledBytes.Length}.Overlaps(address,length));
                if(active!=null)return "Overlaps patch "+active.Preview.Definition.Name;
                var prepared=Manager.Preparation;
                if(prepared!=null&&new PatchRange{Address=prepared.Preview.Address,Length=(ulong)prepared.EntryBytes.Length}.Overlaps(address,length))return "Overlaps prepared hook";
                return null;
            };
        }
        private void TrackRepository()
        {
            var next=new Dictionary<Guid,int>(Volatile.Read(ref savedRevisions));
            foreach(var id in next.Keys.ToArray())next[id]=-1;
            foreach(var definition in Repository.Definitions)next[definition.Id]=definition.Revision;
            Volatile.Write(ref savedRevisions,next);
            Repository.Changed+=RepositoryChanged;
        }
        private void RepositoryChanged(object sender,EventArgs args)
        {
            if(!ReferenceEquals(sender,Repository))return;
            var next=new Dictionary<Guid,int>(Volatile.Read(ref savedRevisions));
            foreach(var id in next.Keys.ToArray())next[id]=-1;
            foreach(var definition in Repository.Definitions)next[definition.Id]=definition.Revision;
            Volatile.Write(ref savedRevisions,next);
        }
        private async Task RestoreAsync()
        {
            var result=await Manager.RestoreAllAsync().ConfigureAwait(false);
            if(!result.Success)throw new InvalidOperationException(result.Message);
        }
        // Restores every owned change. On failure the resolver chooses to force, abandon or cancel (throws).
        public void ReleaseOwnedCode(Func<string,PatchConflictChoice> resolve)
        {
            var result=Manager.RestoreAllAsync().GetAwaiter().GetResult();
            while(!result.Success)
            {
                var choice=resolve?.Invoke(result.Message)??PatchConflictChoice.Cancel;
                if(choice==PatchConflictChoice.Cancel)throw new OperationCanceledException(result.Message);
                if(choice==PatchConflictChoice.ForceRestore){result=Manager.ForceRestoreAllAsync().GetAwaiter().GetResult();continue;}
                result=Manager.AbandonAllAsync().GetAwaiter().GetResult();
                if(Session.State==DebugSessionState.Faulted)Session.RecoveryCompleted();
            }
        }
        public void SetProject(ReClassNetProject project,Func<string,PatchConflictChoice> resolve=null)
        {
            if(ReferenceEquals(Project,project))return;
            ReleaseOwnedCode(resolve);Repository.Save();Repository.Changed-=RepositoryChanged;Project=project;
            Repository=new PatchRepository(project,project.MarkDirty);TrackRepository();
        }
        public int ActivePatchCount=>Manager.ActivePatches.Count;
        public static bool TryParseCodeAddress(string text,IEnumerable<Module> modules,out ulong address,out string error)
        {
            address=0;error=null;text=(text??"").Trim();
            if(text.Length==0){error="Enter a code address.";return false;}
            int plus=text.LastIndexOf('+');
            if(plus<0)
            {
                if(ParseHex(text,out address))return true;
                var only=FindModule(text,modules);
                if(only!=null){address=unchecked((ulong)only.Start.ToInt64());return true;}
                error="Enter a hexadecimal address such as 7FF6A1B21234, or module+offset such as game.exe+0x1234.";return false;
            }
            string name=text.Substring(0,plus).Trim(),offsetText=text.Substring(plus+1).Trim();ulong offset;
            if(!ParseHex(offsetText,out offset)){error="The offset after '+' must be hexadecimal (for example game.exe+0x1234).";return false;}
            ulong baseAddress;var module=FindModule(name,modules);
            if(module!=null)baseAddress=unchecked((ulong)module.Start.ToInt64());
            else if(!ParseHex(name,out baseAddress)){error="Module '"+name+"' is not loaded in the target. Check the name (with or without its extension).";return false;}
            address=unchecked(baseAddress+offset);return true;
        }
        private static bool ParseHex(string text,out ulong value)
        {
            if(text.StartsWith("0x",StringComparison.OrdinalIgnoreCase))text=text.Substring(2);
            return ulong.TryParse(text,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out value);
        }
        private static Module FindModule(string name,IEnumerable<Module> modules)
        {
            if(modules==null||name.Length==0)return null;
            var all=modules.ToArray();
            return all.FirstOrDefault(m=>string.Equals(m.Name,name,StringComparison.OrdinalIgnoreCase))
                ??all.FirstOrDefault(m=>string.Equals(System.IO.Path.GetFileNameWithoutExtension(m.Name??""),name,StringComparison.OrdinalIgnoreCase));
        }
        public void Dispose(){Repository.Save();Session.Dispose();Repository.Changed-=RepositoryChanged;}
    }
}
