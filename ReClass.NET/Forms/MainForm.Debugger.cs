using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ReClassNET.Debugger;
using ReClassNET.Patching;

namespace ReClassNET.Forms
{
    public partial class MainForm
    {
        private ToolStripMenuItem debuggerPauseItem,debuggerResumeItem,debuggerRecoverItem;
        private readonly ToolStripStatusLabel debuggerStateLabel=new ToolStripStatusLabel{Text="Debugger: Detached",Spring=true,TextAlign=ContentAlignment.MiddleRight};
        private DebugSession observedSession;
        private string titleExtra;
        private bool exitConfirmed;
        private void InstallAdvancedDebuggerMenu()
        {
            var menu=new ToolStripMenuItem("Debugger");
            var editor=new ToolStripMenuItem("Inspect / edit instructions…");
            editor.Click+=(s,e)=>{
                try
                {
                    if(!Program.RemoteProcess.IsValid)throw new InvalidOperationException("Attach to a process first.");
                    var workspace=Program.RemoteProcess.DebugWorkspace;
                    var input=PromptCodeAddress();if(!input.HasValue)return;
                    new AssemblyEditorForm(workspace,input.Value).Show();
                }
                catch(Exception error){ReClassNET.UI.ThemedMessageBox.Show(error.Message,"Instruction inspector");}
            };
            var patches=new ToolStripMenuItem("Saved and active patches…");
            patches.Click+=(s,e)=>{
                try
                {
                    if(!Program.RemoteProcess.IsValid)throw new InvalidOperationException("Open a process to preview saved patches. Definitions load inactive.");
                    new PatchManagerForm(Program.RemoteProcess.DebugWorkspace).Show();
                }
                catch(Exception error){ReClassNET.UI.ThemedMessageBox.Show(error.Message,"Patches");}
            };
            debuggerPauseItem=new ToolStripMenuItem("Pause"){ShortcutKeys=Keys.F6};debuggerPauseItem.Click+=async(s,e)=>{try{await Program.RemoteProcess.DebugWorkspace.Session.PauseAsync();}catch(Exception error){ReClassNET.UI.ThemedMessageBox.Show(error.Message,"Pause");}};
            debuggerResumeItem=new ToolStripMenuItem("Resume"){ShortcutKeys=Keys.F5};debuggerResumeItem.Click+=async(s,e)=>{try{var workspace=Program.RemoteProcess.ExistingDebugWorkspace;if(workspace!=null)await workspace.Session.ResumeAsync();}catch(Exception error){ReClassNET.UI.ThemedMessageBox.Show(error.Message,"Resume");}};
            debuggerRecoverItem=new ToolStripMenuItem("Recover owned changes");debuggerRecoverItem.Click+=async(s,e)=>{
                try
                {
                    var workspace=Program.RemoteProcess.ExistingDebugWorkspace;if(workspace==null)return;
                    await workspace.Session.RecoverAsync();
                    var result=await workspace.Manager.RestoreAllAsync();if(!result.Success)throw new InvalidOperationException(result.Message);
                    ReClassNET.UI.ThemedMessageBox.Show("Owned code and context recovery verified. Target remains paused; Resume when ready.","Recovery");
                }
                catch(Exception error){ReClassNET.UI.ThemedMessageBox.Show(error.Message,"Recovery required");}
            };
            menu.DropDownItems.AddRange(new ToolStripItem[]{editor,patches,new ToolStripSeparator(),debuggerPauseItem,debuggerResumeItem,debuggerRecoverItem});
            menu.DropDownOpening+=(s,e)=>UpdateDebuggerState();
            int help=mainMenuStrip.Items.IndexOf(helpToolStripMenuItem);
            if(help<0)mainMenuStrip.Items.Add(menu);else mainMenuStrip.Items.Insert(help,menu);
            statusStrip.Items.Add(debuggerStateLabel);
            Program.RemoteProcess.PatchConflictResolver=message=>PatchManagerForm.AskPatchConflict(this,message);
            Program.RemoteProcess.DebugWorkspaceChanged+=ObserveWorkspace;
            Program.RemoteProcess.ProcessAttached+=sender=>QueueDebuggerState();
            Program.RemoteProcess.ProcessClosed+=sender=>QueueDebuggerState();
            UpdateDebuggerState();
        }
        private void ObserveWorkspace(DebugWorkspace workspace)
        {
            if(observedSession!=null)observedSession.StateChanged-=SessionStateChanged;
            observedSession=workspace?.Session;
            if(observedSession!=null)observedSession.StateChanged+=SessionStateChanged;
            QueueDebuggerState();
        }
        private void SessionStateChanged(DebugSessionState state)=>QueueDebuggerState();
        private void QueueDebuggerState()
        {
            if(IsDisposed)return;
            if(!IsHandleCreated||!InvokeRequired){UpdateDebuggerState();return;}
            try{BeginInvoke(new Action(UpdateDebuggerState));}catch(InvalidOperationException){}
        }
        private void UpdateDebuggerState()
        {
            var process=Program.RemoteProcess;bool open=process.UnderlayingProcess!=null,advanced=open&&process.SupportsAdvancedDebugging;
            var session=process.ExistingDebugWorkspace?.Session;
            var state=session?.State??DebugSessionState.Detached;
            // The session is Paused for an instant while it handles each watch hit; only a held pause counts.
            if(state==DebugSessionState.Paused&&!session.HeldPaused)state=DebugSessionState.Running;
            string text;Color color;var theme=UI.AppTheme.Current;
            switch(state)
            {
                case DebugSessionState.Running:text="Running";color=theme.IsDark?theme.Teal:Color.DarkGreen;break;
                case DebugSessionState.Paused:text="Paused";color=theme.IsDark?theme.Amber:Color.DarkOrange;break;
                case DebugSessionState.Faulted:text="Faulted (Debugger → Recover owned changes)";color=theme.IsDark?theme.Red:Color.DarkRed;break;
                case DebugSessionState.Attaching:text="Attaching…";color=theme.IsDark?theme.Text:SystemColors.ControlText;break;
                case DebugSessionState.Detaching:text="Detaching…";color=theme.IsDark?theme.Text:SystemColors.ControlText;break;
                default:text=open&&!advanced&&process.Debugger.IsAttached?"Running (classic debugger)":"Detached";color=theme.IsDark?theme.Muted:SystemColors.GrayText;break;
            }
            debuggerStateLabel.Text="Debugger: "+text;debuggerStateLabel.ForeColor=color;
            if(debuggerPauseItem==null)return;
            debuggerPauseItem.Enabled=advanced&&(state==DebugSessionState.Running||state==DebugSessionState.Detached);
            debuggerResumeItem.Enabled=advanced&&state==DebugSessionState.Paused;
            debuggerRecoverItem.Enabled=advanced&&state==DebugSessionState.Faulted;
        }
        private bool TryCloseProcess(string title)
        {
            try{Program.RemoteProcess.Close();return true;}
            catch(OperationCanceledException){return false;}
            catch(Exception error){ReClassNET.UI.ThemedMessageBox.Show(error.Message,title,MessageBoxButtons.OK,MessageBoxIcon.Warning);return false;}
        }
        // Asks before discarding unsaved patch definitions or restoring live patches.
        private bool ConfirmReplaceProject(string action)
        {
            if(currentProject!=null&&currentProject.IsDirty)
            {
                var answer=ReClassNET.UI.ThemedMessageBox.Show("The current project has unsaved patch definitions. Save the project before you "+action+"?",Constants.ApplicationName,MessageBoxButtons.YesNoCancel,MessageBoxIcon.Warning);
                if(answer==DialogResult.Cancel)return false;
                if(answer==DialogResult.Yes){saveToolStripMenuItem_Click(this,EventArgs.Empty);if(currentProject.IsDirty)return false;}
            }
            int count=Program.RemoteProcess.ExistingDebugWorkspace?.ActivePatchCount??0;
            if(count>0&&ReClassNET.UI.ThemedMessageBox.Show(count+(count==1?" patch is":" patches are")+" applied in the target. Restore "+(count==1?"it":"them")+" and "+action+"?",Constants.ApplicationName,MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return false;
            return true;
        }
        private void ProjectDirtyChanged(object sender,EventArgs e)
        {
            if(IsHandleCreated&&InvokeRequired){try{BeginInvoke(new Action(()=>UpdateWindowTitle(titleExtra)));}catch(InvalidOperationException){}return;}
            UpdateWindowTitle(titleExtra);
        }
        private ulong? PromptCodeAddress()
        {
            using(var dialog=new Form{Text="Inspect code address",Width=460,Height=170,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false})
            {
                var hint=new Label{Dock=DockStyle.Top,Height=34,Text="Hex address (7FF6A1B21234) or module + offset (game.exe+0x1234, game+1234)."};
                var input=new TextBox{Dock=DockStyle.Top};
                var selected=memoryViewControl.GetSelectedNodes().FirstOrDefault();if(selected!=null)input.Text=unchecked((ulong)selected.Address.ToInt64()).ToString("X");
                var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,FlowDirection=FlowDirection.RightToLeft,Height=36};
                var ok=new Button{Text="Inspect",DialogResult=DialogResult.OK,AutoSize=true};var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true};
                buttons.Controls.Add(cancel);buttons.Controls.Add(ok);
                dialog.Controls.Add(input);dialog.Controls.Add(hint);dialog.Controls.Add(buttons);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                UI.AppTheme.Apply(dialog);
                while(true)
                {
                    if(dialog.ShowDialog(this)!=DialogResult.OK)return null;
                    ulong address;string error;
                    if(DebugWorkspace.TryParseCodeAddress(input.Text,Program.RemoteProcess.Modules,out address,out error))return address;
                    ReClassNET.UI.ThemedMessageBox.Show(error,"Inspect code address",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }
            }
        }
    }
}
