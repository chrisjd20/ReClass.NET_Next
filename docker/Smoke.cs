// Integration checks run against the packaged assembly, including its native ABI.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ReClassNET.Core;
using ReClassNET.DataExchange.ReClass;
using ReClassNET.Forms;
using ReClassNET.Logger;
using ReClassNET.Native;
using ReClassNET.Nodes;
using ReClassNET.Project;

class Smoke
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args[0] == "--gui") return Gui(args[1]);
            var library = NativeMethods.LoadLibrary(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                NativeMethods.IsUnix() ? "NativeCore.so" : "NativeCore.dll"));
            Check(library != IntPtr.Zero, "Native library did not load");
            try
            {
                var core = new NativeCoreWrapper(library);
                int pid = Process.GetCurrentProcess().Id;
                bool found = false;
                core.EnumerateProcesses(delegate(ref EnumerateProcessData process)
                {
                    if (process.Id.ToInt64() == pid) found = true;
                });
                Check(found, "Process enumeration did not find the test process");
                var handle = core.OpenRemoteProcess(new IntPtr(pid), ProcessAccess.Full);
                Check(core.IsProcessValid(handle), "Test process handle is invalid");
                try
                {
                    int modules = 0, sections = 0;
                    core.EnumerateRemoteSectionsAndModules(handle,
                        delegate(ref EnumerateRemoteSectionData section) { if (section.Size.ToInt64() > 0) sections++; },
                        delegate(ref EnumerateRemoteModuleData module) { if (!String.IsNullOrEmpty(module.Path)) modules++; });
                    Check(sections > 0 && modules > 0, "Module/section enumeration failed");
                    var address = Marshal.AllocHGlobal(8);
                    try
                    {
                        Marshal.WriteInt64(address, 0x1020304050607080L);
                        var data = new byte[8];
                        Check(core.ReadRemoteMemory(handle, address, ref data, 0, data.Length), "Memory read failed");
                        Check(BitConverter.ToInt64(data, 0) == 0x1020304050607080L, "Memory read returned wrong bytes");
                        data = BitConverter.GetBytes(0x0102030405060708L);
                        Check(core.WriteRemoteMemory(handle, address, ref data, 0, data.Length), "Memory write failed");
                        Check(Marshal.ReadInt64(address) == 0x0102030405060708L, "Memory write changed wrong bytes");
                    }
                    finally { Marshal.FreeHGlobal(address); }
                }
                finally { core.CloseRemoteProcess(handle); }
            }
            finally { NativeMethods.FreeLibrary(library); }
            // Loading the internal wrapper verifies its additional disassembly/input exports.
            var internalType = typeof(NativeCoreWrapper).Assembly.GetType("ReClassNET.Core.InternalCoreFunctions");
            using ((IDisposable)internalType.GetMethod("Create").Invoke(null, null)) { }
            using (var original = new ReClassNetProject())
            {
                var node = ClassNode.Create();
                node.Name = "DockerRoundTrip";
                node.AddNode(new Int32Node { Name = "Value", Comment = "Native package smoke test" });
                original.AddClass(node);
                new ReClassNetFile(original).Save(args[1], new NullLogger());
                using (var loaded = new ReClassNetProject())
                {
                    new ReClassNetFile(loaded).Load(args[1], new NullLogger());
                    Check(loaded.Classes.Count == 1 && loaded.Classes[0].Name == node.Name, "Project round-trip lost class");
                    Check(loaded.Classes[0].Nodes.Single() is Int32Node, "Project round-trip lost node type");
                }
            }
            Console.WriteLine("PASS: managed/native ABI, process/modules, memory read/write, project save/load");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static int Gui(string fixture)
    {
        bool passed = false;
        int ticks = 0;
        using (var timer = new System.Windows.Forms.Timer())
        {
            timer.Interval = 250;
            timer.Tick += delegate
            {
                try
                {
                    var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                    if (form == null)
                    {
                        if (++ticks > 80) throw new Exception("Main window did not open");
                        return;
                    }
                    Check(form.Visible && form.IsHandleCreated, "Main window is not visible");
                    Check(form.CurrentProject.Classes.Any(c => c.Name == "DockerRoundTrip"), "Startup argument did not load the fixture");
                    timer.Stop();
                    passed = true;
                    form.Close();
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }
            };
            timer.Start();
            var program = typeof(MainForm).Assembly.GetType("ReClassNET.Program");
            program.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new[] { fixture } });
        }
        Check(passed, "GUI startup returned before displaying the main window");
        Console.WriteLine("PASS: GUI startup and project argument containing spaces");
        return 0;
    }
}
