using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using ReClassNET.Debugger;

namespace ReClassNET.Forms
{
    public partial class MainForm
    {
        private void InstallAdvancedDebuggerMenu()
        {
            var menu=new ToolStripMenuItem("Debugger");
            var editor=new ToolStripMenuItem("Inspect / edit instructions…");
            editor.Click+=(s,e)=>{
                try
                {
                    if(!Program.RemoteProcess.IsValid)throw new InvalidOperationException("Attach to a process first.");
                    var input=PromptCodeAddress();if(!input.HasValue)return;
                    new AssemblyEditorForm(Program.RemoteProcess.DebugWorkspace,input.Value).Show();
                }
                catch(Exception error){MessageBox.Show(error.Message,"Instruction inspector");}
            };
            var patches=new ToolStripMenuItem("Saved and active patches…");
            patches.Click+=(s,e)=>{
                try
                {
                    if(!Program.RemoteProcess.IsValid)throw new InvalidOperationException("Open a process to preview saved patches. Definitions load inactive.");
                    new PatchManagerForm(Program.RemoteProcess.DebugWorkspace).Show();
                }
                catch(Exception error){MessageBox.Show(error.Message,"Patches");}
            };
            var pause=new ToolStripMenuItem("Pause");pause.Click+=async(s,e)=>{try{await Program.RemoteProcess.DebugWorkspace.Session.PauseAsync();}catch(Exception error){MessageBox.Show(error.Message,"Pause");}};
            var resume=new ToolStripMenuItem("Resume");resume.Click+=async(s,e)=>{try{await Program.RemoteProcess.DebugWorkspace.Session.ResumeAsync();}catch(Exception error){MessageBox.Show(error.Message,"Resume");}};
            var recover=new ToolStripMenuItem("Recover owned changes");recover.Click+=async(s,e)=>{
                try
                {
                    var workspace=Program.RemoteProcess.DebugWorkspace;
                    await workspace.Session.RecoverAsync();
                    var result=await workspace.Manager.RestoreAllAsync();if(!result.Success)throw new InvalidOperationException(result.Message);
                    MessageBox.Show("Owned code and context recovery verified. Target remains paused; Resume when ready.","Recovery");
                }
                catch(Exception error){MessageBox.Show(error.Message,"Recovery required");}
            };
            menu.DropDownItems.AddRange(new ToolStripItem[]{editor,patches,new ToolStripSeparator(),pause,resume,recover});
            mainMenuStrip.Items.Add(menu);
        }
        private ulong? PromptCodeAddress()
        {
            using(var dialog=new Form{Text="Code address (hexadecimal)",Width=380,Height=145,StartPosition=FormStartPosition.CenterParent})
            {
                var input=new TextBox{Dock=DockStyle.Top};
                var selected=memoryViewControl.GetSelectedNodes().FirstOrDefault();if(selected!=null)input.Text=unchecked((ulong)selected.Address.ToInt64()).ToString("X");
                var ok=new Button{Text="Inspect",Dock=DockStyle.Bottom,DialogResult=DialogResult.OK};dialog.Controls.Add(input);dialog.Controls.Add(ok);dialog.AcceptButton=ok;
                if(dialog.ShowDialog(this)!=DialogResult.OK)return null;
                string text=input.Text.Trim();if(text.StartsWith("0x",StringComparison.OrdinalIgnoreCase))text=text.Substring(2);
                ulong address;if(!ulong.TryParse(text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out address))throw new FormatException("Enter a hexadecimal code address.");
                return address;
            }
        }
    }
}
