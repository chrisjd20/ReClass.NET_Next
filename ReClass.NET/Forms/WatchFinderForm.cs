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
using ReClassNET.Controls.Debugger;
using ReClassNET.Debugger;
using ReClassNET.Patching;
using ReClassNET.Properties;
using ReClassNET.UI;
using ReClassNET.UI.Debugger;

namespace ReClassNET.Forms
{
    // Capture aggregation happens on the session worker. The UI receives bounded dirty keys.
    public sealed class WatchFinderForm:IconForm,ISelfThemed
    {
        private sealed class Row
        {
            public string Key,Access,Status;
            public ulong Address,Code,Count;
            public int Width;
            public bool Confirmed;
            public WatchHit First,Latest,ConfirmedHit;
        }
        // What the grid paints for a row: its badge, coloured instruction and the count flash.
        private sealed class Display
        {
            public HitKind Kind;
            public List<AsmToken> Tokens=new List<AsmToken>();
            public ulong LastCount;
            public int FlashUntil;
        }
        private const int FlashMilliseconds=900;
        private readonly DebugWorkspace workspace;
        private readonly ulong address;
        private readonly int length;
        private readonly bool writeOnly,execution;
        // True when the watched instruction address is a proven instruction start (a confirmed row).
        private readonly bool boundaryKnown;
        private readonly Dictionary<string,Row> records=new Dictionary<string,Row>();
        private readonly ConcurrentQueue<string> dirty=new ConcurrentQueue<string>();
        private readonly HashSet<string> pendingKeys=new HashSet<string>();
        private readonly List<Guid> watchIds=new List<Guid>();
        private readonly SemaphoreSlim actions=new SemaphoreSlim(1,1);
        private readonly ConcurrentQueue<string> diagnostics=new ConcurrentQueue<string>();
        private readonly Dictionary<string,DataGridViewRow> displayed=new Dictionary<string,DataGridViewRow>();
        private readonly Dictionary<string,Display> looks=new Dictionary<string,Display>();
        private readonly DataGridView grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false};
        private readonly TextBox details=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false};
        private readonly TextBox condition=new TextBox{Width=DpiUtil.ScaleIntX(230)};
        private readonly CheckBox pauseMatch=new CheckBox{Text="Pause on match",AutoSize=true};
        private readonly StatusLine status=new StatusLine();
        private readonly DebuggerHeader header=new DebuggerHeader();
        private readonly CoachBar coach=new CoachBar();
        private readonly InspectorTabs inspector=new InspectorTabs{Dock=DockStyle.Fill};
        private readonly InstructionListView explainView=new InstructionListView{Dock=DockStyle.Fill};
        private readonly RegisterBoard registerBoard=new RegisterBoard{Dock=DockStyle.Fill};
        private readonly Dictionary<string,DarkButton> buttons=new Dictionary<string,DarkButton>();
        private readonly ToolTip tips=new ToolTip();
        private DarkButton pauseButton,resumeButton,stepButton,applyConditionButton;
        private string appliedCondition="";
        private bool appliedPause;
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=100};
        private CancellationTokenSource traceCancellation=new CancellationTokenSource();
        private long dropped,totalHits;
        // Row tallies kept by Aggregate, so the guidance never has to scan every row under the lock.
        private int confirmedRows,unlikelyRows,candidateRows;
        private int lastDetails;
        private volatile bool accepting;
        private bool closing,closePending,confirmPending,userPicked;
        private IDictionary<string,ulong> shownRegisters;
        private FlowLayoutPanel toolbar;
        public WatchFinderForm(DebugWorkspace workspace,ulong address,int length,bool writeOnly,bool execution=false,bool boundaryKnown=false)
        {
            this.workspace=workspace;this.address=address;this.length=length;this.writeOnly=writeOnly;this.execution=execution;this.boundaryKnown=boundaryKnown;
            Text=execution?"Find addresses accessed by instruction 0x"+address.ToString("X"):(writeOnly?"Find writes to ":"Find accesses to ")+"data 0x"+address.ToString("X");
            Width=DpiUtil.ScaleIntX(1280);Height=DpiUtil.ScaleIntY(860);MinimumSize=new Size(DpiUtil.ScaleIntX(900),DpiUtil.ScaleIntY(600));
            BackColor=DebuggerTheme.Background;ForeColor=DebuggerTheme.Text;Font=DebuggerTheme.UiFont;
            details.Font=DebuggerTheme.Mono;

            header.Icon=execution?Resources.B16x16_Find_Access:writeOnly?Resources.B16x16_Find_Write:Resources.B16x16_Find_Access;
            header.Title=execution?"Find addresses accessed by instruction":writeOnly?"Find what writes":"Find what accesses";
            header.Subtitle=execution
                ?"Watching the instruction at 0x"+address.ToString("X")+". Every address it reads or writes appears below."
                :"Watching "+length+" byte"+(length==1?"":"s")+" at 0x"+address.ToString("X")+". Every instruction that "+(writeOnly?"writes":"reads or writes")+" them appears below.";

            AddColumn("state","Attribution",13,140);AddColumn("count","Count",6,56);AddColumn("assembly","NASM instruction / candidates",24,200);
            AddColumn("bytes","Bytes",10,90);AddColumn("code","Instruction address",13,120);AddColumn("module","Module + offset",17,140);
            AddColumn("data","Data address",12,110);AddColumn("width","Width / access",9,90);AddColumn("thread","Thread / phase",14,110);
            grid.CellPainting+=PaintCell;grid.Paint+=(s,e)=>DarkGrid.PaintEmpty(grid,e.Graphics,EmptyGridText());
            grid.CellToolTipTextNeeded+=(s,e)=>{if(e.RowIndex>=0&&e.ColumnIndex==grid.Columns["state"].Index)e.ToolTipText=grid.Rows[e.RowIndex].Cells["state"].Value as string;};

            explainView.EmptyText="Select a row to see its instruction explained in plain English.";
            registerBoard.FollowRequested+=FollowRegisterTile;
            inspector.AddPage("Explain",explainView);inspector.AddPage("Registers",registerBoard);inspector.AddPage("Raw",Framed(details));
            var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterWidth=DpiUtil.ScaleIntY(6),BackColor=DebuggerTheme.Background};
            split.Panel1.Padding=new Padding(DpiUtil.ScaleIntX(8),DpiUtil.ScaleIntY(2),DpiUtil.ScaleIntX(8),0);split.Panel2.Padding=new Padding(DpiUtil.ScaleIntX(8),0,DpiUtil.ScaleIntX(8),DpiUtil.ScaleIntY(6));
            split.Panel1.Controls.Add(Framed(grid));split.Panel2.Controls.Add(Framed(inspector));

            var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,BackColor=DebuggerTheme.Background,Padding=new Padding(DpiUtil.ScaleIntX(6),DpiUtil.ScaleIntY(2),DpiUtil.ScaleIntX(6),DpiUtil.ScaleIntY(2))};
            var collect=Group(toolbar,"Collect");
            Add(collect,"Start",Resources.B16x16_Control_Play,DarkButtonStyle.Secondary,async()=>await StartAsync(),"Start watching again, clearing nothing.");
            Add(collect,"Stop",Resources.B16x16_Control_Stop,DarkButtonStyle.Secondary,StopAsync,"Stop watching. Rows stay so you can inspect them.");
            var filter=Group(toolbar,"Condition");
            var conditionLabel=new Label{Text="Condition",AutoSize=true,ForeColor=DebuggerTheme.Muted,Margin=new Padding(DpiUtil.ScaleIntX(3),DpiUtil.ScaleIntY(8),0,0)};
            condition.Margin=new Padding(DpiUtil.ScaleIntX(4),DpiUtil.ScaleIntY(5),DpiUtil.ScaleIntX(4),0);pauseMatch.Margin=new Padding(DpiUtil.ScaleIntX(4),DpiUtil.ScaleIntY(7),DpiUtil.ScaleIntX(4),0);
            tips.SetToolTip(condition,"Optional filter, checked on every hit. Example: mem32(rax + 0x1C) == 1");
            tips.SetToolTip(pauseMatch,"Freeze the game at the moment a hit matches, so you can read and step through the registers.");
            filter.Controls.Add(conditionLabel);filter.Controls.Add(condition);filter.Controls.Add(pauseMatch);
            applyConditionButton=Add(filter,"Apply condition",Resources.B16x16_Accept,DarkButtonStyle.Secondary,async()=>await StartAsync(),"Restart the watch with this condition and Pause on match setting.");
            var found=Group(toolbar,"Found code");
            Add(found,"Confirm next execution",Resources.B16x16_Accept,DarkButtonStyle.Primary,ConfirmAsync,"Checks the selected row really is the instruction, the next time it runs. A real instruction turns CONFIRMED.");
            Add(found,"Inspect / edit",Resources.B16x16_Page_Code,DarkButtonStyle.Secondary,async()=>{
                var row=Selected();if(row==null)return;
                var hit=row.ConfirmedHit??row.Latest;
                if(hit.Snapshot.Phase==SnapshotPhase.Before&&watchIds.Contains(hit.WatchId)){await workspace.Session.StopWatchAsync(hit.WatchId);watchIds.Remove(hit.WatchId);}
                new AssemblyEditorForm(workspace,row.Code,row.Confirmed?BoundarySource.Execution:BoundarySource.Uncertain,hit.Snapshot).Show();
            },"Open the selected instruction in the instruction editor to read or patch it.");
            Add(found,"Find accessed addresses",Resources.B16x16_Magnifier,DarkButtonStyle.Secondary,()=>{var row=Selected();if(row!=null)new WatchFinderForm(workspace,row.Code,1,false,true,row.Confirmed).Show();return Task.FromResult(true);},"Watch the selected instruction itself and list every address it touches (for all objects that run it).");
            var follow=Group(toolbar,"Follow");
            Add(follow,"Follow data",Resources.B16x16_Right_Button,DarkButtonStyle.Ghost,()=>{var row=Selected();if(row!=null)Follow(row.Address,row.Latest.Snapshot);return Task.FromResult(true);},"Open the watched data address in a class (current memory).");
            Add(follow,"Follow register…",Resources.B16x16_Pointer_Type,DarkButtonStyle.Ghost,FollowRegisterAsync,"Open the memory a captured register points at, as a class.");
            var run=Group(toolbar,"Run");
            pauseButton=Add(run,"Pause (F6)",Resources.B16x16_Control_Pause,DarkButtonStyle.Secondary,()=>workspace.Session.PauseAsync(),"Freeze the game.");
            resumeButton=Add(run,"Resume (F5)",Resources.B16x16_Control_Play,DarkButtonStyle.Secondary,()=>workspace.Session.ResumeAsync(),"Let the game run again.");
            stepButton=Add(run,"Step into (F11)",Resources.B16x16_Redo,DarkButtonStyle.Secondary,async()=>{
                var row=Selected();if(row==null)return;var snap=await workspace.Session.StepIntoAsync(row.Latest.Snapshot.ThreadId);details.Text=Registers(snap);
                ShowRegisters(snap,row,shownRegisters,true);inspector.SelectedIndex=1;
            },"Run exactly one instruction on the selected row's thread and show the registers afterwards.");
            var trace=Group(toolbar,"Trace");
            Add(trace,"Trace selected thread",Resources.B16x16_Text_List_Bullets,DarkButtonStyle.Secondary,TraceAsync,"Record every instruction the thread runs (with registers) and save it as a CSV.");
            Add(trace,"Cancel trace",Resources.B16x16_Button_Remove,DarkButtonStyle.Ghost,()=>{traceCancellation.Cancel();return Task.FromResult(true);},"Stop a running trace.",false);

            Controls.Add(split);Controls.Add(status);Controls.Add(toolbar);Controls.Add(coach);Controls.Add(header);
            this.toolbar=toolbar;ApplyTheme();
            Load+=(s,e)=>{try{split.SplitterDistance=Math.Max(DpiUtil.ScaleIntY(160),(int)(split.Height*0.46));}catch(InvalidOperationException){}};
            grid.SelectionChanged+=(s,e)=>ShowDetails();grid.MouseDown+=(s,e)=>userPicked=true;grid.KeyDown+=(s,e)=>userPicked=true;timer.Tick+=(s,e)=>{Flush();UpdateDebugButtons();UpdateGuidance();};timer.Start();
            condition.TextChanged+=(s,e)=>ConditionEdited();pauseMatch.CheckedChanged+=(s,e)=>ConditionEdited();
            workspace.Session.Diagnostic+=SessionDiagnostic;
            FormClosed+=(s,e)=>{workspace.Session.Diagnostic-=SessionDiagnostic;tips.Dispose();GlobalWindowManager.RemoveWindow(this);};
            FormClosing+=CloseWatchCollection;Shown+=async(s,e)=>{await this.actions.WaitAsync();try{if(!closePending)await StartAsync();}catch(Exception error){if(!IsDisposed)status.Text=error.Message;}finally{this.actions.Release();}};
            UpdateGuidance();
        }
        // Columns share the width by weight, so the grid fits the window without a horizontal scroll bar.
        // Also called by AppTheme on a live Light/Dark switch.
        public void ApplyTheme()
        {
            BackColor=DebuggerTheme.Background;ForeColor=DebuggerTheme.Text;
            DebuggerTheme.Style(toolbar);
            condition.BackColor=DebuggerTheme.Raised;condition.ForeColor=DebuggerTheme.Text;condition.BorderStyle=BorderStyle.FixedSingle;condition.Font=DebuggerTheme.Mono;
            details.BackColor=DebuggerTheme.Panel;details.ForeColor=DebuggerTheme.Text;details.BorderStyle=BorderStyle.None;
            DarkGrid.Style(grid);
            DebuggerTheme.UseDarkChrome(this);
        }
        private DataGridViewColumn AddColumn(string name,string text,float weight,int minimum)
        {
            var column=new DataGridViewTextBoxColumn{Name=name,HeaderText=text,SortMode=DataGridViewColumnSortMode.NotSortable,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=weight,MinimumWidth=DpiUtil.ScaleIntX(minimum)};
            grid.Columns.Add(column);return column;
        }
        private static Control Framed(Control content)
        {
            var frame=new Panel{Dock=DockStyle.Fill,Padding=new Padding(1),BackColor=DebuggerTheme.Border};
            content.Dock=DockStyle.Fill;frame.Controls.Add(content);return frame;
        }
        private static ToolGroup Group(FlowLayoutPanel toolbar,string caption){var group=new ToolGroup(caption);toolbar.Controls.Add(group);return group;}
        private DarkButton Add(ToolGroup panel,string text,Image icon,DarkButtonStyle style,Func<Task> action,string tip,bool exclusive=true)
        {
            var button=new DarkButton(text,icon,style);button.Click+=async(s,e)=>{
                button.Enabled=false;if(exclusive)await actions.WaitAsync();
                try{if(!closing&&!closePending)await action();}
                catch(Exception error){if(!IsDisposed)status.Text=error.Message;}
                finally{if(exclusive)actions.Release();if(!IsDisposed&&!closePending){button.Enabled=true;UpdateDebugButtons();}}
            };panel.Controls.Add(button);buttons[text]=button;tips.SetToolTip(button,tip);return button;
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
            var state=workspace.Session.State;bool idle=actions.CurrentCount!=0;bool hasRow=grid.SelectedRows.Count>0;
            pauseButton.Enabled=idle&&(state==DebugSessionState.Running||state==DebugSessionState.Detached);
            bool held=state==DebugSessionState.Paused&&workspace.Session.HeldPaused;
            resumeButton.Enabled=idle&&held;stepButton.Enabled=idle&&hasRow&&(state==DebugSessionState.Paused||state==DebugSessionState.Running);
            applyConditionButton.Enabled=idle&&(condition.Text!=appliedCondition||pauseMatch.Checked!=appliedPause);
            // Row actions already ignore clicks without a selection; disabling them makes that visible.
            foreach(var name in new[]{"Confirm next execution","Inspect / edit","Find accessed addresses","Follow data","Follow register…","Trace selected thread"})
                buttons[name].Enabled=idle&&hasRow;
        }
        // The header pills, the "what next" coach and the glowing button all follow the current state.
        private void UpdateGuidance()
        {
            if(IsDisposed||closePending)return;
            var state=workspace.Session.State;
            int rows,confirmed,unlikely,candidates;long hits;
            lock(records){rows=records.Count;confirmed=confirmedRows;unlikely=unlikelyRows;candidates=candidateRows;hits=totalHits;}
            bool held=state==DebugSessionState.Paused&&workspace.Session.HeldPaused;
            if(confirmed>0)confirmPending=false;
            var selected=Selected();
            var advice=WatchCoach.For(new WatchCoachState{Execution=execution,WriteOnly=writeOnly,Collecting=watchIds.Count>0,ConfirmPending=confirmPending,Paused=held,Detached=state==DebugSessionState.Detached,
                Rows=rows,Confirmed=confirmed,Unlikely=unlikely,Candidates=candidates,Selected=selected==null?(HitKind?)null:HitKinds.From(selected.Status,selected.Confirmed)});
            coach.Show(advice);
            foreach(var pair in buttons)pair.Value.Glow=pair.Key==advice.Button&&pair.Value.Enabled;
            header.SetPills(SessionPill(held?DebugSessionState.Paused:state==DebugSessionState.Paused?DebugSessionState.Running:state),new DebuggerHeader.HeaderPill{Text=rows+(rows==1?" ROW":" ROWS")+" · "+hits+(hits==1?" HIT":" HITS"),Severity=Severity.Neutral});
            bool animate=rows==0;
            foreach(var look in looks.Values)if(look.FlashUntil-Environment.TickCount>0){animate=true;break;}
            if(animate)grid.Invalidate();
        }
        private DebuggerHeader.HeaderPill SessionPill(DebugSessionState state)
        {
            switch(state)
            {
                case DebugSessionState.Running:return new DebuggerHeader.HeaderPill{Text=watchIds.Count>0?"WATCHING":"RUNNING",Severity=Severity.Success,Pulse=watchIds.Count>0};
                case DebugSessionState.Paused:return new DebuggerHeader.HeaderPill{Text="GAME PAUSED",Severity=Severity.Attention,Pulse=true,Solid=true};
                case DebugSessionState.Faulted:case DebugSessionState.Exited:return new DebuggerHeader.HeaderPill{Text=state==DebugSessionState.Exited?"TARGET EXITED":"FAULTED",Severity=Severity.Danger};
                default:return new DebuggerHeader.HeaderPill{Text=state.ToString().ToUpperInvariant(),Severity=Severity.Neutral};
            }
        }
        private string EmptyGridText()
        {
            if(execution)return watchIds.Count>0?"Waiting for this instruction to run… do the action in the game that uses it.":"Collection stopped. Start watches the instruction again.";
            return watchIds.Count>0?"Waiting for the first "+(writeOnly?"write":"access")+"… do something in the game that "+(writeOnly?"changes":"uses")+" this value.":"Collection stopped. Start watches the value again.";
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
            try{watch=execution?await workspace.Session.StartInstructionWatchAsync(address,CaptureHit,condition.Text,pauseMatch.Checked,boundaryKnown):await workspace.Session.StartWatchAsync(address,length,writeOnly,CaptureHit,condition.Text,pauseMatch.Checked);}
            catch{accepting=false;throw;}
            appliedCondition=condition.Text;appliedPause=pauseMatch.Checked;UpdateDebugButtons();
            watchIds.Add(watch.Id);status.Text=execution?"Before context; accesses are observed only after a completed step.":"After context; preceding instruction candidates require confirmation.";
        }
        private async Task StopAsync()
        {
            accepting=false;confirmPending=false;
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
                foreach(var candidate in hit.Candidates)
                {
                    // Several decodes can end at the same RIP; flag the ones that provably can't be the accessor.
                    bool? touches=hit.Candidates.Count>1?TouchesWatchedRange(candidate,hit):null;
                    Aggregate(hit,hit.WatchedAddress,hit.WatchedLength,writeOnly?"Write":"Access",touches==false?"Unlikely: its memory operand doesn't touch the watched data":hit.Status,candidate.Address);
                }
                if(hit.Candidates.Count==0)Aggregate(hit,hit.WatchedAddress,hit.WatchedLength,writeOnly?"Write":"Access","Writer unavailable",hit.EventRip);
            }
        }
        // Data hits only carry registers from after the instruction ran. A candidate that doesn't write the
        // registers its address is built from still saw those values, so its memory operands can be checked
        // against the watched range. Null means it can't be decided.
        private bool? TouchesWatchedRange(InstructionRecord candidate,WatchHit hit)
        {
            if(candidate.UsedMemory.Count==0)return false;
            foreach(var memory in candidate.UsedMemory)
            {
                if(memory.Base==Iced.Intel.Register.RIP||memory.Base==Iced.Intel.Register.EIP)return null;
                if(candidate.UsedRegisters.Any(r=>Writes(r.Access)&&(SameRegister(r.Register,memory.Base)||SameRegister(r.Register,memory.Index))))return null;
            }
            var ctx=hit.Snapshot.Context;
            var operands=workspace.Instructions.ResolveMemoryAddresses(candidate,hit.Snapshot.Registers,(ctx.Available&2)!=0?(ulong?)ctx.Fsbase:null,(ctx.Available&4)!=0?(ulong?)ctx.Gsbase:null,true);
            if(operands.Any(o=>!o.Available&&o.Memory.Access!=Iced.Intel.OpAccess.NoMemAccess))return null;
            ulong start=hit.WatchedAddress,end=start+(ulong)hit.WatchedLength;
            return operands.Any(o=>o.Available&&o.WidthBytes>0&&o.Address<end&&start<o.Address+(ulong)o.WidthBytes&&(!writeOnly||Writes(o.Memory.Access)));
        }
        private static bool Writes(Iced.Intel.OpAccess access)=>access==Iced.Intel.OpAccess.Write||access==Iced.Intel.OpAccess.CondWrite||access==Iced.Intel.OpAccess.ReadWrite||access==Iced.Intel.OpAccess.ReadCondWrite;
        private static bool SameRegister(Iced.Intel.Register register,Iced.Intel.Register used)=>used!=Iced.Intel.Register.None&&register!=Iced.Intel.Register.None&&Iced.Intel.RegisterExtensions.GetFullRegister(register)==Iced.Intel.RegisterExtensions.GetFullRegister(used);
        private void Aggregate(WatchHit hit,ulong data,int width,string access,string message,ulong? code=null)
        {
            ulong instruction=code??hit.Candidates.FirstOrDefault()?.Address??hit.EventRip;
            string key=instruction.ToString("X")+":"+data.ToString("X")+":"+width+":"+access;
            lock(records)
            {
                Row row;HitKind? before=null;
                if(!records.TryGetValue(key,out row))
                {
                    if(records.Count>=10000){accepting=false;_ = StopLimitedWatchAsync(hit.WatchId);return;}
                    records[key]=row=new Row{Key=key,Address=data,Code=instruction,Width=width,Access=access,First=hit};
                }
                else before=HitKinds.From(row.Status,row.Confirmed);
                // A confirmed attribution is sticky; later unconfirmed data hits only update counts.
                row.Count++;row.Latest=hit;totalHits++;
                if(hit.Confirmed){row.Confirmed=true;row.ConfirmedHit=hit;row.Status=message;}
                else if(!row.Confirmed)row.Status=message;
                var after=HitKinds.From(row.Status,row.Confirmed);
                if(before!=after){if(before.HasValue)Tally(before.Value,-1);Tally(after,1);}
                if(!pendingKeys.Contains(key))
                {
                    if(pendingKeys.Count<4096){pendingKeys.Add(key);dirty.Enqueue(key);}else Interlocked.Increment(ref dropped);
                }
            }
        }
        private void Tally(HitKind kind,int delta)
        {
            if(kind==HitKind.Confirmed)confirmedRows+=delta;else if(kind==HitKind.Unlikely)unlikelyRows+=delta;else if(kind==HitKind.Candidate)candidateRows+=delta;
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
            string selectedKey=grid.SelectedRows.Count>0?grid.SelectedRows[0].Tag as string:null;bool refreshSelection=false;
            foreach(var record in updates)
            {
                DataGridViewRow row;
                if(!displayed.TryGetValue(record.Key,out row)){int index=grid.Rows.Add();row=grid.Rows[index];row.Tag=record.Key;displayed[record.Key]=row;}
                var instruction=(record.ConfirmedHit??record.Latest).Candidates.FirstOrDefault(x=>x.Address==record.Code)??record.Latest.Candidates.FirstOrDefault(x=>x.Address==record.Code);
                var module=workspace.Process.Modules.FirstOrDefault(m=>SessionPatchTarget.Address(m.Start)<=record.Code&&SessionPatchTarget.Address(m.End)>record.Code);
                var moduleText=module==null?"":module.Name+" + 0x"+(record.Code-SessionPatchTarget.Address(module.Start)).ToString("X");
                Display look;if(!looks.TryGetValue(record.Key,out look)){look=new Display();looks[record.Key]=look;}
                look.Kind=HitKinds.From(record.Status,record.Confirmed);look.Tokens=instruction==null?new List<AsmToken>{new AsmToken("Unavailable",AsmTokenKind.Text)}:AsmTokens.Tokenize(instruction);
                if(record.Count!=look.LastCount){look.LastCount=record.Count;look.FlashUntil=Environment.TickCount+FlashMilliseconds;}
                row.Cells["state"].Value=record.Status;row.Cells["count"].Value=record.Count.ToString();row.Cells["assembly"].Value=instruction?.Text??"Unavailable";
                row.Cells["bytes"].Value=instruction==null?"":AssemblyService.FormatHex(instruction.Bytes);row.Cells["code"].Value="0x"+record.Code.ToString("X");row.Cells["module"].Value=moduleText;
                row.Cells["data"].Value=record.Address==0?"Unavailable":"0x"+record.Address.ToString("X");row.Cells["width"].Value=record.Width+" / "+record.Access;
                row.Cells["thread"].Value=record.Latest.Snapshot.ThreadId+" / "+record.Latest.Snapshot.Phase+" / "+record.Latest.Snapshot.Timestamp.ToLocalTime().ToString("T");
                if(look.Kind==HitKind.Confirmed){row.DefaultCellStyle.BackColor=DebuggerTheme.Mix(DebuggerTheme.Panel,DebuggerTheme.Teal,.13f);row.DefaultCellStyle.SelectionBackColor=DebuggerTheme.Mix(DebuggerTheme.Panel,DebuggerTheme.Teal,.32f);row.DefaultCellStyle.ForeColor=DebuggerTheme.Text;}
                else if(look.Kind==HitKind.Unlikely||look.Kind==HitKind.Unavailable)row.DefaultCellStyle.ForeColor=DebuggerTheme.Faint;
                if(record.Key==selectedKey)refreshSelection=true;
            }
            int count;lock(records)count=records.Count;
            if(count>=10000)status.Text="10,000 distinct rows reached; collection stopped.";
            else if(Interlocked.Read(ref dropped)>0)status.Text="Display samples dropped: "+Interlocked.Read(ref dropped)+". Captured counts continue to aggregate.";
            string message;while(diagnostics.TryDequeue(out message))status.Text=message;
            // Until the player picks a row, keep the most useful one selected: confirmed, then a candidate.
            if(!userPicked&&updates.Count>0&&grid.Rows.Count>0)
            {
                var best=grid.Rows.Cast<DataGridViewRow>().OrderBy(r=>{Display look;var k=r.Tag as string;return k!=null&&looks.TryGetValue(k,out look)?Rank(look.Kind):9;}).First();
                if(!best.Selected){grid.ClearSelection();best.Selected=true;}
                else if(unchecked(Environment.TickCount-lastDetails)>=1000)ShowDetails();
            }
            // New hits on the selected row refresh the inspector at most once a second; selecting a row is immediate.
            else if(selectedKey==null||refreshSelection&&unchecked(Environment.TickCount-lastDetails)>=1000)ShowDetails();
        }
        private static int Rank(HitKind kind){switch(kind){case HitKind.Confirmed:return 0;case HitKind.Candidate:return 1;case HitKind.Observed:return 2;case HitKind.Attempted:return 3;default:return 5;}}
        private void PaintCell(object sender,DataGridViewCellPaintingEventArgs e)
        {
            if(e.RowIndex<0||e.ColumnIndex<0)return;
            var key=grid.Rows[e.RowIndex].Tag as string;Display look;
            if(key==null||!looks.TryGetValue(key,out look))return;
            float flash=Math.Max(0,(look.FlashUntil-Environment.TickCount)/(float)FlashMilliseconds);
            var column=grid.Columns[e.ColumnIndex].Name;
            if(column=="state")DarkGrid.PaintBadge(e,look.Kind);
            else if(column=="assembly")DarkGrid.PaintTokens(e,look.Tokens,look.Kind==HitKind.Unlikely||look.Kind==HitKind.Unavailable?.6f:0f);
            else
            {
                // One line with an ellipsis: Mono ignores WrapMode and would wrap "module + offset" onto two lines.
                e.PaintBackground(e.CellBounds,true);
                bool selected=(e.State&DataGridViewElementStates.Selected)!=0;
                var color=selected?e.CellStyle.SelectionForeColor:e.CellStyle.ForeColor;
                var bounds=new Rectangle(e.CellBounds.X+DpiUtil.ScaleIntX(8),e.CellBounds.Y,e.CellBounds.Width-DpiUtil.ScaleIntX(12),e.CellBounds.Height);
                DebuggerTheme.DrawText(e.Graphics,e.FormattedValue as string??"",e.CellStyle.Font??DebuggerTheme.Mono,color,bounds,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
                e.Handled=true;
            }
            DarkGrid.PaintFlash(e,flash,look.Kind==HitKind.Confirmed?DebuggerTheme.Teal:DebuggerTheme.Amber);
        }
        private Row Selected(){if(grid.SelectedRows.Count==0)return null;var key=grid.SelectedRows[0].Tag as string;lock(records){Row row;return key!=null&&records.TryGetValue(key,out row)?Copy(row):null;}}
        private void ShowDetails()
        {
            var row=Selected();if(row==null)return;lastDetails=Environment.TickCount;var hit=row.Latest;var text=new StringBuilder();
            text.AppendLine("Code: 0x"+row.Code.ToString("X")+"   Watched data: 0x"+row.Address.ToString("X"));
            text.AppendLine("Event RIP: 0x"+hit.EventRip.ToString("X")+"   "+row.Status);
            var labels=workspace.Process.NamedAddresses.ToDictionary(p=>unchecked((ulong)p.Key.ToInt64()),p=>p.Value);
            var listed=new List<ListedInstruction>();var kind=HitKinds.From(row.Status,row.Confirmed);
            foreach(var candidate in hit.Candidates)
            {
                var explanation=workspace.Instructions.Explain(candidate,hit.Snapshot.Registers,labels,beforeInstruction:hit.Snapshot.Phase==SnapshotPhase.Before&&hit.Snapshot.Context.Rip==candidate.Address).Text;
                text.AppendLine("0x"+candidate.Address.ToString("X")+"  "+AssemblyService.FormatHex(candidate.Bytes)+"  "+candidate.Text);
                text.AppendLine(explanation);
                bool mine=candidate.Address==row.Code;
                var item=new ListedInstruction{Address=candidate.Address,Bytes=candidate.Bytes,Tokens=AsmTokens.Tokenize(candidate),Text=candidate.Text,Explanation=explanation,Dim=!mine,Accent=mine?HitKinds.Tone(kind):Severity.Neutral};
                if(mine)item.Badges.Add(new ListedBadge(HitKinds.Label(kind),HitKinds.Tone(kind)));
                else item.Badges.Add(new ListedBadge("OTHER ROW",Severity.Neutral));
                listed.Add(item);
            }
            text.AppendLine(Registers(hit.Snapshot));text.AppendLine("Following a captured pointer reads current memory, which may have changed.");details.Text=text.ToString();
            string where=hit.Candidates.Count>1?hit.Candidates.Count+" instructions could end at the reported spot ("+"0x"+hit.EventRip.ToString("X")+"). The highlighted one is this row.":"Event RIP 0x"+hit.EventRip.ToString("X")+" · "+hit.Snapshot.Phase+" context";
            explainView.SetItems(listed,where.ToUpperInvariant());
            inspector.SetBadge(0,HitKinds.Label(kind),HitKinds.Tone(kind));
            ShowRegisters(hit.Snapshot,row,null,false);
        }
        private void ShowRegisters(RegisterSnapshot snapshot,Row row,IDictionary<string,ulong> previous,bool stepped)
        {
            var instruction=row.Latest.Candidates.FirstOrDefault(c=>c.Address==row.Code);
            var used=instruction==null?Enumerable.Empty<string>():instruction.UsedRegisters.Where(r=>r.Register!=Iced.Intel.Register.None).Select(r=>Iced.Intel.RegisterExtensions.GetFullRegister(r.Register).ToString().ToLowerInvariant());
            var views=RegisterHighlights.Build(snapshot.Registers,stepped?null:used,row.Address,row.Width,previous);
            string caption=(stepped?"AFTER ONE STEP":snapshot.Phase.ToString().ToUpperInvariant()+" THE INSTRUCTION RAN")+" · THREAD "+snapshot.ThreadId+" · "+snapshot.Timestamp.ToLocalTime().ToString("T")+" · CLICK A TILE TO FOLLOW IT";
            if(snapshot.Phase==SnapshotPhase.After&&!stepped)caption="AFTER THE INSTRUCTION RAN · THREAD "+snapshot.ThreadId+" · "+snapshot.Timestamp.ToLocalTime().ToString("T")+" · CLICK A TILE TO FOLLOW IT";
            registerBoard.SetRegisters(views,caption);
            int watched=views.Count(v=>v.PointsAtWatched);
            inspector.SetBadge(1,stepped?"STEPPED":watched>0?watched+" -> DATA":null,stepped?Severity.Info:Severity.Attention);
            shownRegisters=snapshot.Registers;
        }
        private void FollowRegisterTile(RegisterView register)
        {
            var row=Selected();if(row==null)return;
            try{Follow(register.Value,row.Latest.Snapshot);status.Text="Following "+register.Name.ToUpperInvariant()+" = 0x"+register.Value.ToString("X")+" in a class. The memory shown is current and may differ from the captured moment.";}
            catch(Exception error){status.Text=error.Message;}
        }
        private static string Registers(RegisterSnapshot snapshot)=>snapshot.Phase+" context / thread "+snapshot.ThreadId+" / "+snapshot.Timestamp.ToString("O")+Environment.NewLine+string.Join(Environment.NewLine,snapshot.Registers.Select(r=>r.Key.ToUpperInvariant()+" = 0x"+r.Value.ToString("X16")));
        private async Task ConfirmAsync()
        {
            var row=Selected();if(row==null)return;
            var watchedStart=row.Address;var watchedWidth=row.Width;
            if(watchedWidth<=0||watchedStart==0)throw new InvalidOperationException("Select a row with an available watched data range.");
            accepting=true;int confirmedOnce=0;
            // Unless this was the only decode ending at the hit, the row may start mid-instruction: allow only
            // a hardware breakpoint, which then never fires instead of corrupting the real instruction.
            bool unambiguous=row.Confirmed||row.Latest.Candidates.Count==1;
            var watch=await workspace.Session.StartInstructionWatchAsync(row.Code,hit=>{
                var candidate=hit.Candidates.FirstOrDefault();if(candidate==null)return;
                var ctx=hit.Snapshot.Context;
                var operands=workspace.Instructions.ResolveMemoryAddresses(candidate,hit.Snapshot.Registers,(ctx.Available&2)!=0?(ulong?)ctx.Fsbase:null,(ctx.Available&4)!=0?(ulong?)ctx.Gsbase:null,hit.Snapshot.Phase==SnapshotPhase.Before&&hit.Snapshot.Context.Rip==candidate.Address);
                bool confirms=hit.Completed&&operands.Any(o=>o.Available&&o.WidthBytes>0&&o.Address<checked(watchedStart+(ulong)watchedWidth)&&watchedStart<checked(o.Address+(ulong)o.WidthBytes)&&(!writeOnly||o.Memory.Access==Iced.Intel.OpAccess.Write||o.Memory.Access==Iced.Intel.OpAccess.ReadWrite));
                if(confirms&&Interlocked.Exchange(ref confirmedOnce,1)==0){hit.WatchedAddress=watchedStart;hit.WatchedLength=watchedWidth;hit.Status="Confirmed access to watched range";CaptureHit(hit);_ = RemoveConfirmationWatchAsync(hit.WatchId);}
            },condition.Text,pauseMatch.Checked,unambiguous);
            watchIds.Add(watch.Id);confirmPending=true;status.Text="Confirm next execution: temporary instruction watch installed; waiting for the instruction to access the watched range. If this row isn't a real instruction start, it never turns green: pick another row.";
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
            using(var dialog=new Form{Text="Follow captured register (current memory)",Width=DpiUtil.ScaleIntX(360),Height=DpiUtil.ScaleIntY(160),StartPosition=FormStartPosition.CenterParent,Padding=new Padding(DpiUtil.ScaleIntX(10))})
            {
                var list=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList,Font=DebuggerTheme.Mono};list.Items.AddRange(row.Latest.Snapshot.Registers.Keys.Select(k=>(object)k).ToArray());if(list.Items.Count>0)list.SelectedIndex=0;
                var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,FlowDirection=FlowDirection.RightToLeft,Height=DpiUtil.ScaleIntY(40)};
                var ok=new DarkButton("Follow",null,DarkButtonStyle.Primary){DialogResult=DialogResult.OK};var cancel=new DarkButton("Cancel"){DialogResult=DialogResult.Cancel};
                buttons.Controls.Add(cancel);buttons.Controls.Add(ok);dialog.Controls.Add(list);dialog.Controls.Add(buttons);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                DebuggerTheme.StyleDialog(dialog);
                if(dialog.ShowDialog(this)==DialogResult.OK&&list.SelectedItem!=null)Follow(row.Latest.Snapshot.Registers[(string)list.SelectedItem],row.Latest.Snapshot);
            }
            return Task.FromResult(true);
        }
        private async Task TraceAsync()
        {
            var row=Selected();if(row==null)return;
            int count=1000,milliseconds=5000;string stopCondition="";
            using(var limits=new Form{Text="Trace limits (holds other threads)",Width=DpiUtil.ScaleIntX(460),Height=DpiUtil.ScaleIntY(270),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false})
            {
                var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(DpiUtil.ScaleIntX(10))};
                var instructions=new NumericUpDown{Minimum=1,Maximum=100000,Value=1000,Width=DpiUtil.ScaleIntX(150)};
                var time=new NumericUpDown{Minimum=1,Maximum=30000,Value=5000,Width=DpiUtil.ScaleIntX(150)};
                var expression=new TextBox{Width=DpiUtil.ScaleIntX(400),Font=DebuggerTheme.Mono};
                var ok=new DarkButton("Trace",Resources.B16x16_Text_List_Bullets,DarkButtonStyle.Primary){DialogResult=DialogResult.OK};var cancel=new DarkButton("Cancel"){DialogResult=DialogResult.Cancel};
                panel.Controls.Add(new Label{Text="Instruction limit",AutoSize=true,Margin=new Padding(3,6,3,0)},0,0);panel.Controls.Add(instructions,1,0);
                panel.Controls.Add(new Label{Text="Time limit (milliseconds)",AutoSize=true,Margin=new Padding(3,6,3,0)},0,1);panel.Controls.Add(time,1,1);
                var stopLabel=new Label{Text="Stop condition (Before context; optional)",AutoSize=true,Margin=new Padding(3,8,3,0)};panel.Controls.Add(stopLabel,0,2);panel.SetColumnSpan(stopLabel,2);
                panel.Controls.Add(expression,0,3);panel.SetColumnSpan(expression,2);
                var buttons=new FlowLayoutPanel{FlowDirection=FlowDirection.RightToLeft,AutoSize=true,Dock=DockStyle.Fill};buttons.Controls.Add(cancel);buttons.Controls.Add(ok);panel.Controls.Add(buttons,0,4);panel.SetColumnSpan(buttons,2);
                limits.Controls.Add(panel);limits.AcceptButton=ok;limits.CancelButton=cancel;
                DebuggerTheme.StyleDialog(limits);
                if(limits.ShowDialog(this)!=DialogResult.OK)return;
                count=(int)instructions.Value;milliseconds=(int)time.Value;stopCondition=expression.Text;
            }
            traceCancellation.Dispose();traceCancellation=new CancellationTokenSource();
            await workspace.Session.PauseAsync();status.Text="Trace holds other threads; scheduling changes. Limit: "+count+" instructions / "+milliseconds+" milliseconds.";
            var result=await workspace.Session.StartTraceAsync(row.Latest.Snapshot.ThreadId,count,milliseconds,stopCondition,traceCancellation.Token);
            if(closePending||IsDisposed)return;
            details.Text=string.Join(Environment.NewLine,result.Entries.Select(e=>"0x"+e.Address.ToString("X")+" "+e.Instruction))+Environment.NewLine+result.StopReason;
            inspector.SelectedIndex=2;inspector.SetBadge(2,"TRACE",Severity.Info);
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
