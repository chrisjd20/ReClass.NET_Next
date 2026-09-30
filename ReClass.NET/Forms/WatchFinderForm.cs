using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReClassNET.AssemblyEditing;
using ReClassNET.Debugger;
using ReClassNET.Patching;
using ReClassNET.UI;

namespace ReClassNET.Forms
{
    // Capture aggregation happens on the session worker. The UI receives bounded dirty keys.
    public sealed class WatchFinderForm:IconForm
    {
        private sealed class Row
        {
            public string Key,Access,Status;
            public ulong Address,Code,Count;
            public int Width;
            public bool Confirmed;
            public WatchHit First,Latest,ConfirmedHit;
        }
        private readonly DebugWorkspace workspace;
        private readonly ulong address;
        private readonly int length;
        private readonly bool writeOnly,execution;
        private readonly Dictionary<string,Row> records=new Dictionary<string,Row>();
        private readonly ConcurrentQueue<string> dirty=new ConcurrentQueue<string>();
        private readonly HashSet<string> pendingKeys=new HashSet<string>();
        private readonly List<Guid> watchIds=new List<Guid>();
        private readonly SemaphoreSlim actions=new SemaphoreSlim(1,1);
        private readonly ConcurrentQueue<string> diagnostics=new ConcurrentQueue<string>();
        private readonly Dictionary<string,DataGridViewRow> displayed=new Dictionary<string,DataGridViewRow>();
        private readonly DataGridView grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false};
        private readonly TextBox details=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false};
        private readonly TextBox condition=new TextBox{Width=190};
        private readonly CheckBox pauseMatch=new CheckBox{Text="Pause on match",AutoSize=true};
        private readonly Label status=new Label{Dock=DockStyle.Bottom,Height=36,AutoEllipsis=true};
        private Button pauseButton,resumeButton,stepButton,applyConditionButton;
        private string appliedCondition="";
        private bool appliedPause;
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=100};
        private CancellationTokenSource traceCancellation=new CancellationTokenSource();
        private long dropped;
        private volatile bool accepting;
        private bool closing,closePending;
        public WatchFinderForm(DebugWorkspace workspace,ulong address,int length,bool writeOnly,bool execution=false)
        {
            this.workspace=workspace;this.address=address;this.length=length;this.writeOnly=writeOnly;this.execution=execution;
            Text=execution?"Find addresses accessed by instruction 0x"+address.ToString("X"):(writeOnly?"Find writes to ":"Find accesses to ")+"data 0x"+address.ToString("X");
            Width=1100;Height=750;
            details.Font=new Font(FontFamily.GenericMonospace,9);
            grid.Columns.Add("count","Count");grid.Columns.Add("code","Instruction address");grid.Columns.Add("module","Module + offset");grid.Columns.Add("data","Data address");
            grid.Columns.Add("width","Width / access");grid.Columns.Add("bytes","Bytes");grid.Columns.Add("assembly","NASM instruction / candidates");grid.Columns.Add("thread","Thread / phase");grid.Columns.Add("state","Attribution");
            foreach(DataGridViewColumn c in grid.Columns)c.AutoSizeMode=DataGridViewAutoSizeColumnMode.DisplayedCells;
            var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=310};split.Panel1.Controls.Add(grid);split.Panel2.Controls.Add(details);
            var actions=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,WrapContents=true};
            actions.Controls.Add(new Label{Text="Condition",AutoSize=true});actions.Controls.Add(condition);actions.Controls.Add(pauseMatch);
            applyConditionButton=Add(actions,"Apply condition",async()=>await StartAsync());
            Add(actions,"Start",async()=>await StartAsync());Add(actions,"Stop",StopAsync);
            Add(actions,"Inspect / edit",async()=>{
                var row=Selected();if(row==null)return;
                var hit=row.ConfirmedHit??row.Latest;
                if(hit.Snapshot.Phase==SnapshotPhase.Before&&watchIds.Contains(hit.WatchId)){await workspace.Session.StopWatchAsync(hit.WatchId);watchIds.Remove(hit.WatchId);}
                new AssemblyEditorForm(workspace,row.Code,row.Confirmed?BoundarySource.Execution:BoundarySource.Uncertain,hit.Snapshot).Show();
            });
            Add(actions,"Confirm next execution",ConfirmAsync);
            Add(actions,"Find accessed addresses",()=>{var row=Selected();if(row!=null)new WatchFinderForm(workspace,row.Code,1,false,true).Show();return Task.FromResult(true);});
            Add(actions,"Follow data",()=>{var row=Selected();if(row!=null)Follow(row.Address,row.Latest.Snapshot);return Task.FromResult(true);});
            Add(actions,"Follow register…",FollowRegisterAsync);
            pauseButton=Add(actions,"Pause (F6)",()=>workspace.Session.PauseAsync());resumeButton=Add(actions,"Resume (F5)",()=>workspace.Session.ResumeAsync());
            stepButton=Add(actions,"Step into (F11)",async()=>{var row=Selected();if(row==null)return;var snap=await workspace.Session.StepIntoAsync(row.Latest.Snapshot.ThreadId);details.Text=Registers(snap);});
            Add(actions,"Trace selected thread",TraceAsync);Add(actions,"Cancel trace",()=>{traceCancellation.Cancel();return Task.FromResult(true);},false);
            Controls.Add(split);Controls.Add(actions);Controls.Add(status);
            grid.SelectionChanged+=(s,e)=>ShowDetails();timer.Tick+=(s,e)=>{Flush();UpdateDebugButtons();};timer.Start();
            condition.TextChanged+=(s,e)=>ConditionEdited();pauseMatch.CheckedChanged+=(s,e)=>ConditionEdited();
            workspace.Session.Diagnostic+=SessionDiagnostic;
            FormClosed+=(s,e)=>{workspace.Session.Diagnostic-=SessionDiagnostic;GlobalWindowManager.RemoveWindow(this);};
            FormClosing+=CloseWatchCollection;Shown+=async(s,e)=>{await this.actions.WaitAsync();try{if(!closePending)await StartAsync();}catch(Exception error){if(!IsDisposed)status.Text=error.Message;}finally{this.actions.Release();}};
        }
        private Button Add(FlowLayoutPanel panel,string text,Func<Task> action,bool exclusive=true)
        {
            var button=new Button{Text=text,AutoSize=true};button.Click+=async(s,e)=>{
                button.Enabled=false;if(exclusive)await actions.WaitAsync();
                try{if(!closing&&!closePending)await action();}
                catch(Exception error){if(!IsDisposed)status.Text=error.Message;}
                finally{if(exclusive)actions.Release();if(!IsDisposed&&!closePending){button.Enabled=true;UpdateDebugButtons();}}
            };panel.Controls.Add(button);return button;
        }
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
        {
            var button=keyData==Keys.F5?resumeButton:keyData==Keys.F6?pauseButton:keyData==Keys.F11?stepButton:null;
            if(button!=null){if(button.Enabled)button.PerformClick();return true;}
            return base.ProcessCmdKey(ref msg,keyData);
        }
        private void UpdateDebugButtons()
        {
            if(IsDisposed||closePending||pauseButton==null)return;
            var state=workspace.Session.State;bool idle=actions.CurrentCount!=0;
            pauseButton.Enabled=idle&&(state==DebugSessionState.Running||state==DebugSessionState.Detached);
            resumeButton.Enabled=idle&&state==DebugSessionState.Paused;stepButton.Enabled=idle&&(state==DebugSessionState.Paused||state==DebugSessionState.Running);
            applyConditionButton.Enabled=idle&&(condition.Text!=appliedCondition||pauseMatch.Checked!=appliedPause);
        }
        private void ConditionEdited()
        {
            UpdateDebugButtons();
            if(watchIds.Count!=0&&(condition.Text!=appliedCondition||pauseMatch.Checked!=appliedPause))status.Text="Condition or Pause on match changed. The running watch still uses the old settings; click Apply condition to restart it.";
        }
        private void SessionDiagnostic(string message)
        {
            if(diagnostics.Count<64)diagnostics.Enqueue(message);
        }
        private async Task StartAsync()
        {
            await StopAsync();await workspace.Session.AttachAsync();accepting=true;
            DebugWatch watch;
            try{watch=execution?await workspace.Session.StartInstructionWatchAsync(address,CaptureHit,condition.Text,pauseMatch.Checked):await workspace.Session.StartWatchAsync(address,length,writeOnly,CaptureHit,condition.Text,pauseMatch.Checked);}
            catch{accepting=false;throw;}
            appliedCondition=condition.Text;appliedPause=pauseMatch.Checked;UpdateDebugButtons();
            watchIds.Add(watch.Id);status.Text=execution?"Before context; accesses are observed only after a completed step.":"After context; preceding instruction candidates require confirmation.";
        }
        private async Task StopAsync()
        {
            accepting=false;
            foreach(var id in watchIds.ToArray()){await workspace.Session.StopWatchAsync(id);watchIds.Remove(id);}
            status.Text="Collection stopped.";
        }
        private void CaptureHit(WatchHit hit)
        {
            if(!accepting||hit==null||hit.Snapshot==null)return;
            if(execution&&hit.Candidates.Count>0)
            {
                var ctx=hit.Snapshot.Context;
                var operands=workspace.Instructions.ResolveMemoryAddresses(hit.Candidates[0],hit.Snapshot.Registers,(ctx.Available&2)!=0?(ulong?)ctx.Fsbase:null,(ctx.Available&4)!=0?(ulong?)ctx.Gsbase:null,hit.Snapshot.Phase==SnapshotPhase.Before&&hit.Snapshot.Context.Rip==hit.Candidates[0].Address);
                foreach(var operand in operands)
                {
                    if(operand.Available)Aggregate(hit,operand.Address,operand.WidthBytes,operand.Memory.Access.ToString(),hit.Completed?"Observed":"Attempted");
                    else Aggregate(hit,0,0,"Unavailable",operand.Reason);
                }
                if(operands.Count==0)Aggregate(hit,0,0,"None","Instruction has no memory access");
            }
            else
            {
                foreach(var candidate in hit.Candidates)Aggregate(hit,hit.WatchedAddress,hit.WatchedLength,writeOnly?"Write":"Access",hit.Status,candidate.Address);
                if(hit.Candidates.Count==0)Aggregate(hit,hit.WatchedAddress,hit.WatchedLength,writeOnly?"Write":"Access","Writer unavailable",hit.EventRip);
            }
        }
        private void Aggregate(WatchHit hit,ulong data,int width,string access,string message,ulong? code=null)
        {
            ulong instruction=code??hit.Candidates.FirstOrDefault()?.Address??hit.EventRip;
            string key=instruction.ToString("X")+":"+data.ToString("X")+":"+width+":"+access;
            lock(records)
            {
                Row row;
                if(!records.TryGetValue(key,out row))
                {
                    if(records.Count>=10000){accepting=false;_ = StopLimitedWatchAsync(hit.WatchId);return;}
                    records[key]=row=new Row{Key=key,Address=data,Code=instruction,Width=width,Access=access,First=hit};
                }
                // A confirmed attribution is sticky; later unconfirmed data hits only update counts.
                row.Count++;row.Latest=hit;
                if(hit.Confirmed){row.Confirmed=true;row.ConfirmedHit=hit;row.Status=message;}
                else if(!row.Confirmed)row.Status=message;
                if(!pendingKeys.Contains(key))
                {
                    if(pendingKeys.Count<4096){pendingKeys.Add(key);dirty.Enqueue(key);}else Interlocked.Increment(ref dropped);
                }
            }
        }
        private async Task StopLimitedWatchAsync(Guid id)
        {
            try{await workspace.Session.StopWatchAsync(id).ConfigureAwait(false);}
            catch(Exception error){SessionDiagnostic("Collection limit cleanup failed: "+error.Message);}
        }
        private static Row Copy(Row row)=>new Row{Key=row.Key,Access=row.Access,Status=row.Status,Address=row.Address,Code=row.Code,Count=row.Count,Width=row.Width,First=row.First,Latest=row.Latest,Confirmed=row.Confirmed,ConfirmedHit=row.ConfirmedHit};
        private void Flush()
        {
            var updates=new List<Row>();string key;
            lock(records)
            {
                for(int i=0;i<256&&dirty.TryDequeue(out key);i++){pendingKeys.Remove(key);Row record;if(records.TryGetValue(key,out record))updates.Add(Copy(record));}
            }
            foreach(var record in updates)
            {
                DataGridViewRow row;
                if(!displayed.TryGetValue(record.Key,out row)){int index=grid.Rows.Add();row=grid.Rows[index];row.Tag=record.Key;displayed[record.Key]=row;}
                if(record.Confirmed)row.DefaultCellStyle.ForeColor=Color.DarkGreen;
                var instruction=(record.ConfirmedHit??record.Latest).Candidates.FirstOrDefault(x=>x.Address==record.Code)??record.Latest.Candidates.FirstOrDefault(x=>x.Address==record.Code);
                var module=workspace.Process.Modules.FirstOrDefault(m=>SessionPatchTarget.Address(m.Start)<=record.Code&&SessionPatchTarget.Address(m.End)>record.Code);
                var moduleText=module==null?"":module.Name+" + 0x"+(record.Code-SessionPatchTarget.Address(module.Start)).ToString("X");
                row.SetValues(record.Count.ToString(),"0x"+record.Code.ToString("X"),moduleText,record.Address==0?"Unavailable":"0x"+record.Address.ToString("X"),record.Width+" / "+record.Access,instruction==null?"":AssemblyService.FormatHex(instruction.Bytes),instruction?.Text??"Unavailable",record.Latest.Snapshot.ThreadId+" / "+record.Latest.Snapshot.Phase+" / "+record.Latest.Snapshot.Timestamp.ToLocalTime().ToString("T"),record.Status);
            }
            int count;lock(records)count=records.Count;
            if(count>=10000)status.Text="10,000 distinct rows reached; collection stopped.";
            else if(Interlocked.Read(ref dropped)>0)status.Text="Display samples dropped: "+Interlocked.Read(ref dropped)+". Captured counts continue to aggregate.";
            string message;while(diagnostics.TryDequeue(out message))status.Text=message;
        }
        private Row Selected(){if(grid.SelectedRows.Count==0)return null;var key=grid.SelectedRows[0].Tag as string;lock(records){Row row;return key!=null&&records.TryGetValue(key,out row)?Copy(row):null;}}
        private void ShowDetails()
        {
            var row=Selected();if(row==null)return;var hit=row.Latest;var text=new StringBuilder();
            text.AppendLine("Code: 0x"+row.Code.ToString("X")+"   Watched data: 0x"+row.Address.ToString("X"));
            text.AppendLine("Event RIP: 0x"+hit.EventRip.ToString("X")+"   "+row.Status);
            foreach(var candidate in hit.Candidates)
            {
                text.AppendLine("0x"+candidate.Address.ToString("X")+"  "+AssemblyService.FormatHex(candidate.Bytes)+"  "+candidate.Text);
                text.AppendLine(workspace.Instructions.Explain(candidate,hit.Snapshot.Registers,workspace.Process.NamedAddresses.ToDictionary(p=>unchecked((ulong)p.Key.ToInt64()),p=>p.Value),beforeInstruction:hit.Snapshot.Phase==SnapshotPhase.Before&&hit.Snapshot.Context.Rip==candidate.Address).Text);
            }
            text.AppendLine(Registers(hit.Snapshot));text.AppendLine("Following a captured pointer reads current memory, which may have changed.");details.Text=text.ToString();
        }
        private static string Registers(RegisterSnapshot snapshot)=>snapshot.Phase+" context / thread "+snapshot.ThreadId+" / "+snapshot.Timestamp.ToString("O")+Environment.NewLine+string.Join(Environment.NewLine,snapshot.Registers.Select(r=>r.Key.ToUpperInvariant()+" = 0x"+r.Value.ToString("X16")));
        private async Task ConfirmAsync()
        {
            var row=Selected();if(row==null)return;
            var watchedStart=row.Address;var watchedWidth=row.Width;
            if(watchedWidth<=0||watchedStart==0)throw new InvalidOperationException("Select a row with an available watched data range.");
            accepting=true;int confirmedOnce=0;
            var watch=await workspace.Session.StartInstructionWatchAsync(row.Code,hit=>{
                var candidate=hit.Candidates.FirstOrDefault();if(candidate==null)return;
                var ctx=hit.Snapshot.Context;
                var operands=workspace.Instructions.ResolveMemoryAddresses(candidate,hit.Snapshot.Registers,(ctx.Available&2)!=0?(ulong?)ctx.Fsbase:null,(ctx.Available&4)!=0?(ulong?)ctx.Gsbase:null,hit.Snapshot.Phase==SnapshotPhase.Before&&hit.Snapshot.Context.Rip==candidate.Address);
                bool confirms=hit.Completed&&operands.Any(o=>o.Available&&o.WidthBytes>0&&o.Address<checked(watchedStart+(ulong)watchedWidth)&&watchedStart<checked(o.Address+(ulong)o.WidthBytes)&&(!writeOnly||o.Memory.Access==Iced.Intel.OpAccess.Write||o.Memory.Access==Iced.Intel.OpAccess.ReadWrite));
                if(confirms&&Interlocked.Exchange(ref confirmedOnce,1)==0){hit.WatchedAddress=watchedStart;hit.WatchedLength=watchedWidth;hit.Status="Confirmed access to watched range";CaptureHit(hit);_ = RemoveConfirmationWatchAsync(hit.WatchId);}
            },condition.Text,pauseMatch.Checked);
            watchIds.Add(watch.Id);status.Text="Confirm next execution: temporary instruction watch installed; waiting for the instruction to access the watched range.";
        }
        // Runs off the session worker so the stop is queued after the current event completes.
        private async Task RemoveConfirmationWatchAsync(Guid id)
        {
            string message="Confirmed. The temporary instruction watch was removed; choose Inspect / edit to patch this instruction.";
            try{await Task.Run(()=>workspace.Session.StopWatchAsync(id)).ConfigureAwait(false);}
            catch(Exception error){message="Confirmed, but the temporary instruction watch could not be removed: "+error.Message;}
            if(IsDisposed||!IsHandleCreated)return;
            try{BeginInvoke(new Action(()=>{watchIds.Remove(id);if(!IsDisposed)status.Text=message;}));}catch(InvalidOperationException){}
        }
        private void Follow(ulong pointer,RegisterSnapshot snapshot)
        {
            if(snapshot.SessionId!=workspace.Session.Id||workspace.Session.State==DebugSessionState.Exited)throw new InvalidOperationException("Captured session is stale.");
            if(pointer==0)throw new InvalidOperationException("Captured pointer is zero/unavailable.");
            workspace.Target.ReadExact(pointer,1);LinkedWindowFeatures.CreateClassAtAddress(new IntPtr(unchecked((long)pointer)),true);
        }
        private Task FollowRegisterAsync()
        {
            var row=Selected();if(row==null)return Task.FromResult(true);
            using(var dialog=new Form{Text="Follow captured register (current memory)",Width=340,Height=150,StartPosition=FormStartPosition.CenterParent})
            {
                var list=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};list.Items.AddRange(row.Latest.Snapshot.Registers.Keys.Select(k=>(object)k).ToArray());if(list.Items.Count>0)list.SelectedIndex=0;
                var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,FlowDirection=FlowDirection.RightToLeft,Height=36};
                var ok=new Button{Text="Follow",AutoSize=true,DialogResult=DialogResult.OK};var cancel=new Button{Text="Cancel",AutoSize=true,DialogResult=DialogResult.Cancel};
                buttons.Controls.Add(cancel);buttons.Controls.Add(ok);dialog.Controls.Add(list);dialog.Controls.Add(buttons);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                if(dialog.ShowDialog(this)==DialogResult.OK&&list.SelectedItem!=null)Follow(row.Latest.Snapshot.Registers[(string)list.SelectedItem],row.Latest.Snapshot);
            }
            return Task.FromResult(true);
        }
        private async Task TraceAsync()
        {
            var row=Selected();if(row==null)return;
            int count=1000,milliseconds=5000;string stopCondition="";
            using(var limits=new Form{Text="Trace limits (holds other threads)",Width=440,Height=250,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false})
            {
                var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(8)};
                var instructions=new NumericUpDown{Minimum=1,Maximum=100000,Value=1000,Width=150};
                var time=new NumericUpDown{Minimum=1,Maximum=30000,Value=5000,Width=150};
                var expression=new TextBox{Width=390};
                var ok=new Button{Text="Trace",DialogResult=DialogResult.OK,AutoSize=true};var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true};
                panel.Controls.Add(new Label{Text="Instruction limit",AutoSize=true,Margin=new Padding(3,6,3,0)},0,0);panel.Controls.Add(instructions,1,0);
                panel.Controls.Add(new Label{Text="Time limit (milliseconds)",AutoSize=true,Margin=new Padding(3,6,3,0)},0,1);panel.Controls.Add(time,1,1);
                var stopLabel=new Label{Text="Stop condition (Before context; optional)",AutoSize=true,Margin=new Padding(3,8,3,0)};panel.Controls.Add(stopLabel,0,2);panel.SetColumnSpan(stopLabel,2);
                panel.Controls.Add(expression,0,3);panel.SetColumnSpan(expression,2);
                var buttons=new FlowLayoutPanel{FlowDirection=FlowDirection.RightToLeft,AutoSize=true,Dock=DockStyle.Fill};buttons.Controls.Add(cancel);buttons.Controls.Add(ok);panel.Controls.Add(buttons,0,4);panel.SetColumnSpan(buttons,2);
                limits.Controls.Add(panel);limits.AcceptButton=ok;limits.CancelButton=cancel;
                if(limits.ShowDialog(this)!=DialogResult.OK)return;
                count=(int)instructions.Value;milliseconds=(int)time.Value;stopCondition=expression.Text;
            }
            traceCancellation.Dispose();traceCancellation=new CancellationTokenSource();
            await workspace.Session.PauseAsync();status.Text="Trace holds other threads; scheduling changes. Limit: "+count+" instructions / "+milliseconds+" milliseconds.";
            var result=await workspace.Session.StartTraceAsync(row.Latest.Snapshot.ThreadId,count,milliseconds,stopCondition,traceCancellation.Token);
            if(closePending||IsDisposed)return;
            details.Text=string.Join(Environment.NewLine,result.Entries.Select(e=>"0x"+e.Address.ToString("X")+" "+e.Instruction))+Environment.NewLine+result.StopReason;
            using(var save=new SaveFileDialog{Filter="Trace CSV (*.csv)|*.csv",FileName="trace.csv"})
            {
                if(save.ShowDialog(this)==DialogResult.OK)
                {
                    var csv=new StringBuilder("address,instruction,thread,before_registers,after_registers\n");
                    foreach(var entry in result.Entries)csv.AppendLine("0x"+entry.Address.ToString("X")+","+Csv(entry.Instruction)+","+entry.Before.ThreadId+","+Csv(Registers(entry.Before))+","+Csv(Registers(entry.After)));
                    File.WriteAllText(save.FileName,csv.ToString());
                }
            }
        }
        private static string Csv(string text)=>"\""+text.Replace("\"","\"\"")+"\"";
        private async void CloseWatchCollection(object sender,FormClosingEventArgs e)
        {
            if(closing)return;e.Cancel=true;if(closePending)return;closePending=true;accepting=false;traceCancellation.Cancel();
            await actions.WaitAsync();
            try{await StopAsync();closing=true;timer.Stop();timer.Dispose();traceCancellation.Dispose();Close();}
            catch(Exception error){closePending=false;status.Text="Close cancelled: "+error.Message;}
            finally{actions.Release();}
        }
        protected override void OnLoad(EventArgs e){base.OnLoad(e);GlobalWindowManager.AddWindow(this);}
    }
}
