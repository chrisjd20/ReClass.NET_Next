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
        public void SetProject(ReClassNetProject project)
        {
            if(ReferenceEquals(Project,project))return;
            RestoreAsync().GetAwaiter().GetResult();Repository.Save();Repository.Changed-=RepositoryChanged;Project=project;
            Repository=new PatchRepository(project,project.MarkDirty);TrackRepository();
        }
        public void Dispose(){Repository.Save();Session.Dispose();Repository.Changed-=RepositoryChanged;}
    }
}
