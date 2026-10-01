using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReClassNET.Core;
using ReClassNET.Debugger;
using ReClassNET.Memory;
using ReClassNET.MemoryScanner;
using ReClassNET.MemoryScanner.Comparer;
using ReClassNET.Util.Conversion;
using ReClassNET.Patching;
using ReClassNET.DataExchange.ReClass;
using ReClassNET.Project;
using ReClassNET.Nodes;
using ReClassNET.Logger;

// One bounded acceptance batch against the packaged, rendered game. All game
// actions are ordinary platform keyboard input. All edits use actual ReClass
// services and the actual native provider; there is no substitute game model.
internal static class RuntimeWalkthrough
{
    const int Timeout = 15000;
    const ulong Health = 0, Ammo = 4, Speed = 8, Charge = 12, X = 16, Y = 20,
        Flags = 24, Faction = 28, Clearance = 32, Keycard = 36, Callsign = 40, Inventory = 64;
    static readonly LittleEndianBitConverter Endian = new LittleEndianBitConverter();
    static bool Windows => Environment.OSVersion.Platform == PlatformID.Win32NT;
    static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    static async Task<T> Timed<T>(Task<T> task, string name) { await Timed((Task)task, name); return await task; }
    static async Task Timed(Task task, string name) { if (await Task.WhenAny(task, Task.Delay(Timeout)) != task) throw new TimeoutException(name); await task; }
    static async Task Until(Func<bool> ready, string name) { var timer = Stopwatch.StartNew(); while (!ready()) { if (timer.ElapsedMilliseconds > Timeout) throw new TimeoutException(name); await Task.Delay(20); } }
    static IntPtr Ptr(ulong address) => new IntPtr(unchecked((long)address));
    static ulong Address(string value) => ulong.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    static int Int(DebugWorkspace w, ulong address) => BitConverter.ToInt32(w.Target.ReadExact(address, 4), 0);
    static float Float(DebugWorkspace w, ulong address) => BitConverter.ToSingle(w.Target.ReadExact(address, 4), 0);
    static ulong Pointer(DebugWorkspace w, ulong address) => BitConverter.ToUInt64(w.Target.ReadExact(address, 8), 0);
    static async Task Write(DebugWorkspace w, ulong address, byte[] bytes)
    {
        using (var stop = await w.Target.StopAsync(CancellationToken.None)) {
            Check(w.Process.WriteRemoteMemory(Ptr(address), bytes), "Actual ReClass write failed at " + address.ToString("X"));
            Check(w.Target.ReadExact(address, bytes.Length).SequenceEqual(bytes), "Actual ReClass write readback differs.");
        }
        Console.WriteLine("EDIT address=0x" + address.ToString("X") + " bytes=" + BitConverter.ToString(bytes));
    }
    static Task WriteInt(DebugWorkspace w, ulong address, int value) => Write(w, address, BitConverter.GetBytes(value));
    static Task WriteFloat(DebugWorkspace w, ulong address, float value) => Write(w, address, BitConverter.GetBytes(value));
    static int Main(string[] args)
    {
        try {
            int from = 1;
            bool resumeOnly = args.Contains("--resume-only");
            Check(!resumeOnly || !args.Any(a => a.StartsWith("--from=")), "--resume-only cannot be combined with --from.");
            foreach (var option in args.Where(a => a.StartsWith("--from="))) Check(int.TryParse(option.Substring(7), out from) && from >= 1 && from <= 13, "--from must be 1..13");
            var positional = args.Where(a => a != "--resume-only" && !a.StartsWith("--from=")).ToArray();
            Check(positional.Length >= 1 && positional.Length <= 2 && positional.All(a => !a.StartsWith("--")), "Usage: RuntimeWalkthrough [--from=N | --resume-only] <packaged-game> [evidence-prefix]");
            var evidence = positional.Length == 2 ? Path.GetFullPath(positional[1]) : Path.Combine(Path.GetTempPath(), "breakout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(evidence));
            if (resumeOnly) RunResumeOnly(Path.GetFullPath(positional[0])).GetAwaiter().GetResult();
            else Run(Path.GetFullPath(positional[0]), evidence, from).GetAwaiter().GetResult();
            Console.WriteLine(resumeOnly ? "PASS focused continuously running Breakout debugger resume; campaign not repeated" : "PASS packaged Breakout actual ReClass service acceptance rooms=" + from + "..13");
            Console.WriteLine("SCOPE: platform game input and native ReClass services; manual ReClass GUI controls/readability require the companion checklist.");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine("FAIL Breakout acceptance: " + e); return 1; }
    }
    static async Task RunResumeOnly(string executable)
    {
        using (var core = new CoreFunctionsManager())
        using (var c = new Child(executable, 3))
        using (var process = new RemoteProcess(core))
        using (var project = new ReClassNetProject()) {
            await c.Identity();
            process.Open(new ProcessInfo(Ptr((ulong)c.Pid), Path.GetFileName(executable), executable));
            await process.UpdateProcessInformationsAsync();
            using (var w = new DebugWorkspace(process, project)) {
                w.Session.Diagnostic += message => Console.Error.WriteLine("Debugger: " + message);
                await Timed(w.Session.AttachAsync(), "Focused resume attach");
                await c.Freeze(); await c.Room(3); await c.Action("y"); await c.Action("p");
                Check(c.Last["paused"] == "0", "Focused trial simulation did not resume.");
                ulong timerAddress = Pointer(w, c.Root) + 28;
                float runningStart = Float(w, timerAddress);
                await Task.Delay(150);
                float runningAfter = Float(w, timerAddress);
                Check(runningAfter < runningStart, "Trial timer was not continuously running before the debugger stop.");
                await Timed(w.Session.PauseAsync(), "Focused native debugger pause");
                Check(w.Session.State == DebugSessionState.Paused, "Native debugger did not pause.");
                float stopped = Float(w, timerAddress);
                var stopClock = Stopwatch.StartNew(); await Task.Delay(1100);
                float stillStopped = Float(w, timerAddress);
                Check(stillStopped == stopped, "Trial timer changed while the whole process was debugger-suspended.");
                long stoppedMilliseconds = stopClock.ElapsedMilliseconds;
                // No input, foreground change or simulation toggle between native
                // Resume and sampling: this specifically tests running catch-up.
                var resumedClock = Stopwatch.StartNew();
                await Timed(w.Session.ResumeAsync(), "Focused native debugger resume");
                float firstChanged = stopped;
                while (firstChanged == stopped) {
                    Check(resumedClock.ElapsedMilliseconds < 2000, "Trial simulation did not resume within two seconds.");
                    firstChanged = Float(w, timerAddress);
                    if (firstChanged == stopped) await Task.Delay(2);
                }
                double sampledMilliseconds = resumedClock.Elapsed.TotalMilliseconds;
                double timerDecline = stopped - firstChanged;
                Check(timerDecline > 0 && timerDecline <= sampledMilliseconds / 1000.0 + 0.04,
                    "Debugger resume produced catch-up: timer decline=" + timerDecline.ToString("R", CultureInfo.InvariantCulture) + "s; resumed wall=" + sampledMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + "ms.");
                Console.WriteLine("RESUME running-before=" + runningStart.ToString("R", CultureInfo.InvariantCulture) + " running-after=" + runningAfter.ToString("R", CultureInfo.InvariantCulture) + " stopped=" + stopped.ToString("R", CultureInfo.InvariantCulture) + " stable=" + stillStopped.ToString("R", CultureInfo.InvariantCulture) + " stopped-ms=" + stoppedMilliseconds + " first-resumed=" + firstChanged.ToString("R", CultureInfo.InvariantCulture) + " resumed-wall-ms=" + sampledMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " timer-decline=" + timerDecline.ToString("R", CultureInfo.InvariantCulture) + " focus-input-during-resume=none");
                await c.Action("p"); Check(c.Last["paused"] == "1", "Focused simulation cleanup did not pause.");
                await Timed(w.Session.DetachAsync(), "Focused resume detach"); await c.Exit();
            }
        }
    }
    static async Task Run(string executable, string evidence, int from)
    {
        using (var core = new CoreFunctionsManager()) {
            byte[] archive;
            using (var child = new Child(executable, from))
            using (var process = new RemoteProcess(core))
            using (var project = new ReClassNetProject()) {
                await child.Identity();
                process.Open(new ProcessInfo(Ptr((ulong)child.Pid), Path.GetFileName(executable), executable));
                await process.UpdateProcessInformationsAsync();
                using (var w = new DebugWorkspace(process, project)) {
                    w.Session.Diagnostic += s => Console.Error.WriteLine("Debugger: " + s);
                    await Timed(w.Session.AttachAsync(), "Attach actual game");
                    Check(w.Session.State == DebugSessionState.Running, "Attached game not Running.");
                    Check(Pointer(w, Pointer(w, child.Root)) == child.Player, "World root/player pointer path differs from advertised identity.");
                    Check(Pointer(w, Pointer(w, child.Root) + 8) == child.Enemy, "World enemy pointer array differs.");
                    Check(Int(w, child.Player + Faction) == 1 && Int(w, child.Enemy + Faction) == 2, "Actor factions/layout differ.");
                    await child.Freeze();
                    if (from <= 1) await Room1(w, child);
                    if (from <= 2) await Room2(w, child);
                    if (from <= 3) await Room3(w, child);
                    if (from <= 4) await Room4(w, child);
                    if (from <= 5) await Room5(w, child);
                    if (from <= 6) await Room6(w, child);
                    if (from <= 7) await Room7(w, child);
                    if (from <= 8) await Room8(w, child);
                    if (from <= 9) await Room9(w, child, evidence);
                    if (from <= 10) await Room10(w, child);
                    if (from <= 11) await Room11(w, child);
                    if (from <= 12) await Room12(w, child, evidence);
                    await child.Room(13);
                    archive = await SaveDefinitions(w, child, evidence);
                    await Timed(w.Session.DetachAsync(), "Detach before restart");
                    await child.Exit();
                }
            }
            await Restart(core, executable, archive);
        }
    }
    static Scanner Scan(DebugWorkspace w, Child child, ScanValueType type)
    {
        // Deliberately bound to the live Actor neighborhood; the unknown scan
        // exercises real scanner clipping without broad historical stress work.
        return new Scanner(w.Process, new ScanSettings { StartAddress = Ptr(child.Player), StopAddress = Ptr(child.Player + 71), ValueType = type, FastScanAlignment = 4, ScanCopyOnWriteMemory = SettingState.Indeterminate, ScanMappedMemory = true });
    }
    static async Task Search(Scanner scanner, IScanComparer comparer)
    {
        // Whole-process scans can take far longer than the UI-action timeout.
        var pass = scanner.Search(comparer, null, CancellationToken.None);
        if (await Task.WhenAny(pass, Task.Delay(180000)) != pass) throw new TimeoutException("Real scanner pass");
        Check(await pass, "Real scanner did not finish successfully.");
    }
    static void Found(Scanner scanner, ulong address) { Check(scanner.GetResults().Any(r => unchecked((ulong)r.Address.ToInt64()) == address), "Scanner did not retain expected actual field."); Check(scanner.GetResults().All(r => unchecked((ulong)r.Address.ToInt64()) >= unchecked((ulong)scanner.Settings.StartAddress.ToInt64()) && unchecked((ulong)r.Address.ToInt64()) <= unchecked((ulong)scanner.Settings.StopAddress.ToInt64())), "Scan escaped requested target range."); }
    static FloatMemoryComparer FC(ScanCompareType mode, float value = 0) => new FloatMemoryComparer(mode, ScanRoundMode.Strict, 6, value, value, Endian);
    static async Task Complete(Child child, int room) { Check(child.Last.ContainsKey("complete") && child.Last["complete"] == "1", "Room " + room + " outcome not observed: " + child.LastLine); Console.WriteLine("PASS room=" + room + " " + child.LastLine); await Task.CompletedTask; }
    // Lesson scans run over the whole process with the Scanner's default settings, exactly
    // as a player would, so decoys created by the game itself are counted.
    static Scanner WholeProcess(DebugWorkspace w, ScanValueType type) => new Scanner(w.Process, new ScanSettings { ValueType = type });
    static int Count(Scanner scanner) => scanner.TotalResultCount;
    static async Task<ulong> FindAmmo(DebugWorkspace w, Child c, string room)
    {
        // The "Find ROOKIE" recipe: shoot, exact scan, shoot, next scan -> one result.
        using (var scanner = WholeProcess(w, ScanValueType.Integer)) {
            await c.Action("f"); int ammo = Int(w, c.Player + Ammo);
            await Search(scanner, new IntegerMemoryComparer(ScanCompareType.Equal, ammo, ammo, Endian));
            int first = Count(scanner);
            ulong[] left = new ulong[0]; int rounds = 0;
            // The lesson: shoot and Next Scan again until one result is left (at most three rounds).
            do {
                await c.Action("f"); ammo = Int(w, c.Player + Ammo); ++rounds;
                await Search(scanner, new IntegerMemoryComparer(ScanCompareType.Equal, ammo, ammo, Endian));
                left = scanner.GetResults().Select(r => unchecked((ulong)r.Address.ToInt64())).ToArray();
            } while (left.Length > 1 && rounds < 3);
            Console.WriteLine("CONVERGE room=" + room + " recipe=ammo-exact first=" + first + " rounds=" + rounds + " left=" + left.Length);
            foreach (var address in left) {
                var section = w.Process.Sections.FirstOrDefault(x => address >= unchecked((ulong)x.Start.ToInt64()) && address < unchecked((ulong)x.End.ToInt64()));
                Console.WriteLine("  RESULT 0x" + address.ToString("X") + (address == c.Player + Ammo ? " (real)" : "") + " section=" + (section == null ? "none" : section.Type + "/" + section.Category + "/" + section.ModuleName + " " + section.Start.ToString("X")) +
                    " around=" + BitConverter.ToString(w.Target.ReadExact(address - 16, 32)));
            }
            Check(left.Length == 1 && left[0] == c.Player + Ammo, "Ammo scan did not converge to the single real field: " + left.Length + " results.");
            return left[0];
        }
    }
    static async Task Room1(DebugWorkspace w, Child c)
    {
        await c.Room(1);
        ulong ammo = await FindAmmo(w, c, "1");
        await WriteInt(w, ammo, 30);
        await Task.Delay(100); Check(Int(w, c.Player + Ammo) == 30 && c.Last["paused"] == "1", "Paused edit was overwritten.");
        for (int i = 0; i < 20; ++i) await c.Action("f");
        Check(Int(w, c.Player + Ammo) == 10, "Twenty exact actions did not consume twenty rounds.");
        await Complete(c, 1);
    }
    static async Task Room2(DebugWorkspace w, Child c)
    {
        await c.Room(2);
        using (var scanner = WholeProcess(w, ScanValueType.Float)) {
            await Search(scanner, new FloatMemoryComparer(ScanCompareType.Between, ScanRoundMode.Strict, 6, 5, 100, Endian)); Found(scanner, c.Player + Charge);
            var counts = new List<int> { Count(scanner) };
            for (int round = 0; round < 3; ++round) {
                await c.Action("b"); await Search(scanner, FC(ScanCompareType.Increased)); Found(scanner, c.Player + Charge);
                await c.Action("d"); await Search(scanner, FC(ScanCompareType.Decreased)); Found(scanner, c.Player + Charge);
                await Search(scanner, FC(ScanCompareType.NotChanged)); Found(scanner, c.Player + Charge);
                counts.Add(Count(scanner));
            }
            Console.WriteLine("CONVERGE room=2 recipe=between-then-relative results-per-round=" + string.Join(",", counts));
            foreach (var address in scanner.GetResults().Select(r => unchecked((ulong)r.Address.ToInt64())).Take(40)) {
                var section = w.Process.Sections.FirstOrDefault(x => address >= unchecked((ulong)x.Start.ToInt64()) && address < unchecked((ulong)x.End.ToInt64()));
                Console.WriteLine("  RESULT 0x" + address.ToString("X") + (address == c.Player + Charge ? " (real)" : "") + " value=" + Float(w, address) + " section=" + (section == null ? "none" : section.Type + "/" + section.Category + "/" + section.ModuleName + " " + section.Start.ToString("X") + "-" + section.End.ToString("X")));
            }
            Check(counts.Last() <= 10, "Room 2 relative scans did not converge to a handful of results.");
            await WriteFloat(w, c.Player + Charge, 95); await c.Action("o");
        }
        await Complete(c, 2);
    }
    static async Task Room3(DebugWorkspace w, Child c)
    {
        // The lesson: find ammo, open a class at ammo - 4, read speed at +8.
        await c.Room(3);
        ulong ammo = await FindAmmo(w, c, "3");
        ulong rookie = ammo - 4;
        Check(rookie == c.Player && Int(w, rookie + Health) == 100, "Class at ammo - 4 is not ROOKIE.");
        Check(Float(w, rookie + Speed) == 60, "Row 0008 of the class is not speed 60.");
        await WriteFloat(w, rookie + Speed, 6000); await c.Action("y");
        await c.HoldRight(true);
        try { for (int i = 0; i < 6; ++i) await c.Action("t"); } finally { await c.HoldRight(false); }
        Check(Float(w, c.Player + X) >= 650, "Edited speed did not move player through trial."); await Complete(c, 3);
    }
    static async Task Room4(DebugWorkspace w, Child c)
    {
        await c.Room(4); Check(Int(w, c.Player + Flags) == 0x82, "Blast door flags do not start with maintenance and alarm bits.");
        await c.Action("a"); Check(Int(w, c.Player + Flags) == 0x80, "Alarm switch changed more than bit 1.");
        await c.Action("o"); Check(c.Last["complete"] == "0", "Door opened without edits.");
        await WriteInt(w, c.Player + Flags, 1); await c.Action("o"); Check(c.Last["complete"] == "0", "Door ignored the cleared maintenance bit.");
        await Write(w, c.Player + Keycard, new byte[] { 1 }); await WriteInt(w, c.Player + Flags, 0x81);
        await c.Action("o"); await Complete(c, 4);
    }
    static async Task Room9(DebugWorkspace w, Child c, string evidence)
    {
        await c.Room(9);
        var clearance = new EnumDescription { Name = "Clearance" }; clearance.SetData(false, EnumDescription.UnderlyingTypeSize.FourBytes, new[] { new KeyValuePair<string,long>("Rookie", 0), new KeyValuePair<string,long>("Engineer", 2) }); w.Project.AddEnum(clearance);
        var actor = ClassNode.Create(); actor.Name = "BreakoutActor"; actor.AddressFormula = c.Player.ToString("X");
        var enumNode = new EnumNode { Name = "clearance" }; enumNode.ChangeEnum(clearance);
        actor.AddNodes(new BaseNode[] { new Int32Node { Name = "health" }, new Int32Node { Name = "ammo" }, new FloatNode { Name = "speed" }, new FloatNode { Name = "charge" }, new FloatNode { Name = "x" }, new FloatNode { Name = "y" }, new UInt32Node { Name = "flags" }, new UInt32Node { Name = "faction" }, enumNode, new UInt8Node { Name = "keycard" }, new UInt8Node { Name = "pad0" }, new UInt8Node { Name = "pad1" }, new UInt8Node { Name = "pad2" }, new Utf8TextNode { Name = "callsign", Length = 24 }, new UInt64Node { Name = "inventory" } });
        w.Project.AddClass(actor); Check(actor.MemorySize == 72 && (ulong)enumNode.Offset == Clearance, "Named native structure layout differs.");
        using (var file = File.Create(evidence + ".rcnet")) new ReClassNetFile(w.Project).Save(file, new NullLogger());
        using (var reopened = new ReClassNetProject()) { using (var file = File.OpenRead(evidence + ".rcnet")) new ReClassNetFile(reopened).Load(file, new NullLogger()); Check(reopened.Classes.Single().Name == "BreakoutActor" && reopened.Classes.Single().MemorySize == 72 && reopened.Enums.Any(e => e.Name == "Clearance"), "Named project/enum reopen differs."); }
        // The accepted callsign is only an immediate operand; an access watch on the
        // callsign leads to the scanner's read, directly before that compare.
        var reads = new ConcurrentBag<WatchHit>(); var access = await w.Session.StartWatchAsync(c.Player + Callsign, 8, false, reads.Add);
        try { await c.Action("o"); await Until(() => reads.Any(h => h.Candidates.Any(i => i.Address == c.BadgeSite - 4)), "Badge scanner read"); }
        finally { await w.Session.StopWatchAsync(access.Id); }
        var compare = w.Instructions.Decode(w.Target.ReadExact(c.BadgeSite, 10), c.BadgeSite); Check(compare.Success && compare.Instructions.Count == 1 && compare.Instructions[0].Text.ToUpperInvariant().Contains("5245454E49474E45"), "Badge compare immediate differs.");
        Check(c.Last["primary"] == "0", "Unedited badge opened the gate.");
        byte[] text = new byte[24]; Encoding.ASCII.GetBytes("ENGINEER").CopyTo(text, 0); await Write(w, c.Player + Callsign, text); await WriteInt(w, c.Player + Clearance, 2);
        await c.Action("o"); await Complete(c, 9);
    }
    static async Task Room5(DebugWorkspace w, Child c)
    {
        await c.Room(5); ulong inventory = Pointer(w, c.Player + Inventory), equipped = Pointer(w, inventory + 16);
        Check(new[] { Pointer(w, inventory), Pointer(w, inventory + 8) }.Contains(equipped), "Equipped pointer is not one of inventory array slots.");
        await WriteInt(w, equipped + 24, 50); await c.Action("f"); await c.Action("w");
        ulong replacement = Pointer(w, inventory + 16); Check(replacement != equipped && new[] { Pointer(w, inventory), Pointer(w, inventory + 8) }.Contains(replacement), "Explicit swap did not replace/reacquire array object.");
        await WriteInt(w, replacement + 24, 50); await c.Action("f"); await Complete(c, 5);
    }
    static async Task Room6(DebugWorkspace w, Child c)
    {
        // Pointer scan: Actor -> World (heap) -> module-static root, then follow the root across relocations.
        await c.Room(6);
        ulong world;
        using (var scanner = new Scanner(w.Process, new ScanSettings { ValueType = ScanValueType.Long, FastScanAlignment = 8 })) {
            await Search(scanner, new LongMemoryComparer(ScanCompareType.Equal, (long)c.Player, (long)c.Player, Endian));
            var worlds = scanner.GetResults().Select(r => unchecked((ulong)r.Address.ToInt64())).Where(a => { try { return Int(w, a + 24) == 6; } catch { return false; } }).ToArray();
            ulong expected = Pointer(w, c.Root);
            if (!worlds.Contains(expected)) {
                var section = w.Process.Sections.FirstOrDefault(x => expected >= unchecked((ulong)x.Start.ToInt64()) && expected < unchecked((ulong)x.End.ToInt64()));
                Console.Error.WriteLine("ROOM6 results=" + scanner.TotalResultCount + " candidates=" + string.Join(",", worlds.Select(a => "0x" + a.ToString("X"))) + " expected=0x" + expected.ToString("X") +
                    " section=" + (section == null ? "none" : section.Type + "/" + section.Protection + "/" + section.Category + " " + section.Start.ToString("X") + "-" + section.End.ToString("X")));
            }
            Check(worlds.Contains(expected), "The pointer scan did not find the World that the root points at.");
            world = expected;
        }
        using (var scanner = new Scanner(w.Process, new ScanSettings { ValueType = ScanValueType.Long, FastScanAlignment = 8 })) {
            await Search(scanner, new LongMemoryComparer(ScanCompareType.Equal, (long)world, (long)world, Endian));
            var module = w.Target.Modules.Single(m => c.Root >= m.BaseAddress && c.Root - m.BaseAddress < m.Size);
            var statics = scanner.GetResults().Select(r => unchecked((ulong)r.Address.ToInt64())).Where(a => a >= module.BaseAddress && a - module.BaseAddress < module.Size).ToArray();
            Check(statics.Length == 1 && statics[0] == c.Root, "Module-static scan did not find exactly the advertised root.");
            Console.WriteLine("STATIC root=<" + module.Name + ">+0x" + (c.Root - module.BaseAddress).ToString("X"));
        }
        var seen = new HashSet<ulong>();
        for (int link = 1; link <= 3; ++link) {
            ulong current = Pointer(w, c.Root); Check(seen.Add(current), "Relay reboot did not move the control block.");
            await WriteFloat(w, current + 32, 100); await c.Action("t");
            Check(c.Last["links"] == link.ToString(CultureInfo.InvariantCulture), "Relay link not counted: " + c.LastLine);
            if (link < 3) await c.Action("n");
        }
        await Complete(c, 6);
    }
    static PatchDefinition Definition(DebugWorkspace w, ulong site, int length, string assembly, BoundarySource boundary = BoundarySource.Execution) => new PatchDefinition { Name = "Breakout acceptance", SourceKind = PatchSourceKind.Assembly, Assembly = assembly, LocatorKind = PatchLocatorKind.SessionAddress, SessionId = w.Session.Id, SessionAddress = site, Platform = w.Target.Platform, SelectionLength = length, ExpectedBytes = w.Target.ReadExact(site, length), Boundary = boundary };
    static async Task Confirm(DebugWorkspace w, Child c, ulong data, ulong site, string action)
    {
        var writes = new ConcurrentBag<WatchHit>(); var watch = await w.Session.StartWatchAsync(data, 4, true, writes.Add);
        try { await c.Action(action); await Until(() => writes.Count != 0, "Actual writer capture"); Check(writes.Any(h => h.Snapshot.Phase == SnapshotPhase.After && h.Candidates.Any(i => i.Address == site)), "Expected writer candidate not captured."); }
        finally { await w.Session.StopWatchAsync(watch.Id); }
        var hit = new TaskCompletionSource<WatchHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        watch = await w.Session.StartInstructionWatchAsync(site, h => hit.TrySetResult(h));
        try { await c.Action(action); var confirmed = await Timed(hit.Task, "Confirm next execution"); Check(confirmed.Confirmed && confirmed.Completed && confirmed.Snapshot.Phase == SnapshotPhase.Before && w.Instructions.ResolveMemoryAddresses(confirmed.Candidates.Single(), confirmed.Snapshot.Registers).Any(m => m.Available && m.Address == data && m.WidthBytes == 4), "Execution did not confirm actual writer boundary and operand."); }
        finally { await w.Session.StopWatchAsync(watch.Id); }
        Console.WriteLine("CONFIRMED site=0x" + site.ToString("X") + " data=0x" + data.ToString("X"));
    }
    static async Task Apply(DebugWorkspace w, PatchPreview preview) { Check(preview.CanApply, "Preview rejected: " + preview.Message); var result = await w.Manager.ApplyAsync(preview); Check(result.Success, "Apply failed: " + result.Message); Check(w.Session.State == DebugSessionState.Running, "Patch Apply left native session " + w.Session.State + " instead of Running."); }
    static async Task Restore(DebugWorkspace w, PatchDefinition definition) {
        Console.WriteLine("RESTORE begin session-state=" + w.Session.State + " site=0x" + definition.SessionAddress.ToString("X"));
        var result = await w.Manager.RestoreAsync(definition.Id);
        var returnedState = w.Session.State;
        byte[] readback = w.Target.ReadExact(definition.SessionAddress, definition.ExpectedBytes.Length);
        Console.WriteLine("RESTORE returned-status=" + result.Status + " returned-state=" + returnedState + " readback-state=" + w.Session.State + " code=" + BitConverter.ToString(readback) + " expected=" + BitConverter.ToString(definition.ExpectedBytes) + " message=" + result.Message);
        Check(result.Success, "Restore failed: " + result.Message);
        Check(readback.SequenceEqual(definition.ExpectedBytes), "Exact startup instruction bytes not restored.");
        Check(w.Session.State == DebugSessionState.Running, "Restore left native debugger " + w.Session.State + " instead of Running; no synthetic input was sent.");
    }
    static async Task Room7(DebugWorkspace w, Child c)
    {
        await c.Room(7); await Confirm(w, c, c.Player + Health, c.Damage, "h"); await c.Action("r");
        var decoded = w.Instructions.Decode(w.Target.ReadExact(c.Damage, 3), c.Damage); Check(decoded.Success && decoded.Instructions.Count == 1, "Damage writer not one three-byte instruction.");
        var definition = Definition(w, c.Damage, 3, "nop"); var preview = await w.Planner.PreviewAsync(definition, w.Target); Check(preview.PaddingLength == 2, "Short replacement was not NOP padded."); await Apply(w, preview);
        try { for (int i = 0; i < 3; ++i) await c.Action("h"); Check(Int(w, c.Player) == 100, "NOP did not suppress three actual game hits."); await c.Action("r"); Check(w.Target.ReadExact(c.Damage, 3).SequenceEqual(preview.ReplacementBytes), "Gameplay reset restored externally patched code."); for (int i = 0; i < 3; ++i) await c.Action("h"); }
        finally { await Restore(w, definition); }
        try { await c.Action("h"); }
        catch (Exception e) { Console.Error.WriteLine("RESTORED-HIT failure session-state=" + w.Session.State + " health=" + Int(w, c.Player) + " code=" + BitConverter.ToString(w.Target.ReadExact(c.Damage, 3)) + " error=" + e.Message); throw; }
        Check(Int(w, c.Player) == 90, "Restored next hit did not subtract ten."); await Complete(c, 7);
    }
    static async Task Room8(DebugWorkspace w, Child c)
    {
        await c.Room(8); await Confirm(w, c, c.Player + Ammo, c.AmmoSite, "f"); await c.Action("r");
        var definition = Definition(w, c.AmmoSite, 2, "inc dword [rax]"); var preview = await w.Planner.PreviewAsync(definition, w.Target);
        Check(preview.ReplacementBytes.SequenceEqual(new byte[] { 0xff, 0x00 }), "Increment hex preview differs.");
        var longer = await w.Planner.PreviewAsync(Definition(w, c.AmmoSite, 2, "inc dword [rax]\nnop"), w.Target); Check(!longer.CanApply, "Long in-place edit accepted.");
        await Apply(w, preview); try { for (int i = 0; i < 3; ++i) await c.Action("f"); Check(Int(w, c.Player + Ammo) == 15, "Three actual shots did not increase ammo to fifteen."); } finally { await Restore(w, definition); }
        await c.Action("f"); Check(Int(w, c.Player + Ammo) == 14, "Restored decrement failed."); await Complete(c, 8);
    }
    static async Task Room10(DebugWorkspace w, Child c)
    {
        await c.Room(10); var hits = new ConcurrentBag<WatchHit>(); var watch = await w.Session.StartInstructionWatchAsync(c.Damage, hits.Add);
        try { await c.Action("h"); await c.Action("j"); await Until(() => hits.Count >= 2, "Shared writer operands"); var addresses = hits.SelectMany(h => w.Instructions.ResolveMemoryAddresses(h.Candidates.Single(), h.Snapshot.Registers)).Where(m => m.Available).Select(m => m.Address).Distinct().ToArray(); Check(addresses.Contains(c.Player) && addresses.Contains(c.Enemy), "Shared code did not discover both actual actor addresses."); }
        finally { await w.Session.StopWatchAsync(watch.Id); }
        uint code = 0;
        var pause = new TaskCompletionSource<WatchHit>(TaskCreationOptions.RunContinuationsAsynchronously); watch = await w.Session.StartInstructionWatchAsync(c.Damage, h => pause.TrySetResult(h), "rax == 0x" + c.Player.ToString("X"), true);
        try {
            await c.Action("j"); Check(!pause.Task.IsCompleted, "Player condition matched enemy.");
            var action = c.Action("h"); var hit = await Timed(pause.Task, "Player-only pause on match"); await Until(() => w.Session.State == DebugSessionState.Paused, "Debugger paused");
            Check(hit.Snapshot.Context.Rax == c.Player && Int(w, c.Player + Faction) == 1, "Captured pointer/faction differs.");
            code = (uint)hit.Snapshot.Context.R9; Check(code >= 1000 && code <= 9999, "Player override code not in r9d.");
            await Task.Delay(1100); await w.Session.StopWatchAsync(watch.Id); watch = null; await w.Session.ResumeAsync(); await Timed(action, "Resume actual game action");
            Check(c.Last["paused"] == "1", "Simulation pause was lost across debugger stop.");
        } finally { if (watch != null) await w.Session.StopWatchAsync(watch.Id); if (w.Session.State == DebugSessionState.Paused) await w.Session.ResumeAsync(); }
        await c.Action("o"); Check(c.Last["complete"] == "0", "Unedited clearance unlocked the press.");
        await WriteInt(w, c.Player + Clearance, (int)code); await c.Action("o"); await Complete(c, 10);
    }
    static async Task Room11(DebugWorkspace w, Child c)
    {
        await c.Room(11); await Confirm(w, c, c.Player + Ammo, c.AmmoSite, "f"); await c.Action("r");
        string healing = "pushfq\ncmp dword [rax + 24], 1\njne no_heal\nadd dword [rax - 4], byte 5\nno_heal:\npopfq";
        foreach (var mode in new[] { HookSemanticMode.InsertBefore, HookSemanticMode.ReplaceSelection }) {
            var variant = Definition(w, c.AmmoSite, 2, mode == HookSemanticMode.ReplaceSelection ? "dec dword [rax]\n" + healing : healing); variant.Mode = PatchMode.Hook; variant.HookMode = mode;
            var prepared = await w.Planner.PrepareHookAsync(await w.Planner.PreviewAsync(variant, w.Target), w.Target, w.Manager);
            Check(prepared.ReturnAddress == c.AmmoSite + (ulong)prepared.Preview.OriginalBytes.Length && prepared.DisplacedInstructions.Count > 0, "Worked hook variant displaced/continuation preview differs.");
            Check((await w.Manager.CancelPreparationAsync()).Success, "Variant preparation cancellation failed.");
            Console.WriteLine("PREVIEW hook-mode=" + mode + " displacement=" + prepared.Preview.OriginalBytes.Length + " continuation=0x" + prepared.ReturnAddress.ToString("X"));
        }
        var definition = Definition(w, c.AmmoSite, 2, healing); definition.Mode = PatchMode.Hook; definition.HookMode = HookSemanticMode.InsertAfter;
        var hook = await w.Planner.PrepareHookAsync(await w.Planner.PreviewAsync(definition, w.Target), w.Target, w.Manager); Check(hook.DisplacedInstructions.Any(i => i.Text.StartsWith("dec ")), "Insert-after lost original ammo consumption.");
        Check((await w.Manager.ApplyAsync(hook)).Success, "Real game player hook publication failed.");
        try {
            await c.Action("f"); Check(Int(w, c.Player + Ammo) == 11 && Int(w, c.Player) == 55, "Hook did not consume one ammo and heal player five.");
            int enemyAmmo = Int(w, c.Enemy + Ammo), enemyHealth = Int(w, c.Enemy), playerHealth = Int(w, c.Player);
            await c.Action("e"); Check(Int(w, c.Enemy + Ammo) == enemyAmmo - 1 && Int(w, c.Enemy) == enemyHealth && Int(w, c.Player) == playerHealth, "Enemy firing leaked healing.");
        } finally { await Restore(w, definition); }
        await c.Action("f"); Check(Int(w, c.Player) == 55 && Int(w, c.Player + Ammo) == 10, "Restored shared fire behavior differs."); await Complete(c, 11);
    }
    static async Task Room12(DebugWorkspace w, Child c, string evidence)
    {
        await c.Room(12); var captured = new TaskCompletionSource<WatchHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = await w.Session.StartInstructionWatchAsync(c.VaultEntry, h => captured.TrySetResult(h), "", true);
        var action = c.Action("o");
        try {
            var hit = await Timed(captured.Task, "Vault entry pause"); await Until(() => w.Session.State == DebugSessionState.Paused, "Vault paused"); await w.Session.StopWatchAsync(watch.Id); watch = null;
            Check(hit.Snapshot.Context.Rax == c.Player && w.Target.ReadExact(c.Player + Keycard, 1)[0] == 0, "Starting vault failing keycard diagnosis differs.");
            var trace = await Timed(w.Session.StartTraceAsync(hit.Snapshot.ThreadId, 20, 3000, "rip == 0x" + c.VaultEnd.ToString("X")), "Short vault trace");
            Check(trace.StopReason == "Stop condition" && trace.Entries.Count > 0 && trace.Entries.All(t => t.Before.Phase == SnapshotPhase.Before && t.After.Phase == SnapshotPhase.After && t.Address >= c.VaultEntry && t.Address < c.VaultEnd), "Vault trace escaped endpoint or lost contexts.");
            File.WriteAllLines(evidence + "-vault.csv", new[] { "address,instruction,beforeRax,afterRax,beforeFlags,afterFlags" }.Concat(trace.Entries.Select(t => "0x" + t.Address.ToString("X") + ",\"" + t.Instruction.Replace("\"", "\"\"") + "\",0x" + t.Before.Context.Rax.ToString("X") + ",0x" + t.After.Context.Rax.ToString("X") + ",0x" + t.Before.Context.Rflags.ToString("X") + ",0x" + t.After.Context.Rflags.ToString("X"))));
            Console.WriteLine("TRACE entries=" + trace.Entries.Count + " stop=" + trace.StopReason + " endpoint=0x" + c.VaultEnd.ToString("X") + " csv=" + evidence + "-vault.csv");
            await w.Session.ResumeAsync(); await Timed(action, "Complete traced door action");
        } finally { if (watch != null) await w.Session.StopWatchAsync(watch.Id); if (w.Session.State == DebugSessionState.Paused) await w.Session.ResumeAsync(); }
        await Write(w, c.Player + Keycard, new byte[] { 1 }); await WriteInt(w, c.Player + Clearance, 2); await WriteInt(w, c.Player + Flags, Int(w, c.Player + Flags) & ~2); await c.Action("o"); await Complete(c, 12);
    }
    static async Task<byte[]> SaveDefinitions(DebugWorkspace w, Child c, string evidence)
    {
        await Confirm(w, c, c.Player + Ammo, c.AmmoSite, "f"); await c.Action("r");
        var module = w.Target.Modules.Single(m => c.AmmoSite >= m.BaseAddress && c.AmmoSite - m.BaseAddress < m.Size);
        var offset = Definition(w, c.AmmoSite, 2, "inc dword [rax]"); offset.Name = "Breakout ammo module offset"; offset.LocatorKind = PatchLocatorKind.ModuleOffset; offset.ModuleName = module.Name; offset.ImageSha256 = new PatchTargetResolver().Fingerprint(module.Path); offset.Offset = c.AmmoSite - module.BaseAddress;
        var pattern = offset.Clone(); pattern.Id = Guid.NewGuid(); pattern.Name = "Breakout ammo unique signature"; pattern.LocatorKind = PatchLocatorKind.ModulePattern; pattern.Pattern = string.Join(" ", w.Target.ReadExact(c.Signature, 16).Select(b => b.ToString("X2"))); pattern.EntryOffset = checked((long)(c.AmmoSite - c.Signature));
        w.Repository.Upsert(offset); w.Repository.Upsert(pattern); w.Repository.Save();
        byte[] archive; using (var stream = new MemoryStream()) { new ReClassNetFile(w.Project).Save(stream, new NullLogger()); archive = stream.ToArray(); }
        File.WriteAllBytes(evidence + "-saved.rcnet", archive);
        Check(w.Manager.ActivePatches.Count == 0, "Saving definitions applied code.");
        Console.WriteLine("SAVED module=" + module.Name + " offset=0x" + offset.Offset.ToString("X") + " sha256=" + offset.ImageSha256 + " pattern=" + pattern.Pattern);
        await Task.CompletedTask; return archive;
    }
    static async Task Restart(CoreFunctionsManager core, string executable, byte[] archive)
    {
        using (var c = new Child(executable, 13))
        using (var process = new RemoteProcess(core))
        using (var project = new ReClassNetProject()) {
            await c.Identity(); using (var stream = new MemoryStream(archive)) new ReClassNetFile(project).Load(stream, new NullLogger());
            process.Open(new ProcessInfo(Ptr((ulong)c.Pid), Path.GetFileName(executable), executable)); await process.UpdateProcessInformationsAsync();
            using (var w = new DebugWorkspace(process, project)) {
                await Timed(w.Session.AttachAsync(), "Attach restarted same executable"); await c.Freeze(); await c.Room(13); Check(w.Repository.Definitions.Count == 2 && w.Manager.ActivePatches.Count == 0 && w.Target.ReadExact(c.AmmoSite, 2).SequenceEqual(new byte[] { 0xff, 0x08 }), "Loaded definitions auto-applied or archive lost definitions.");
                var resolver = new PatchTargetResolver();
                foreach (var saved in w.Repository.Definitions.ToArray()) {
                    var resolved = resolver.Resolve(saved, w.Target, CancellationToken.None); Check(resolved.Status == PatchResolutionStatus.Resolved && resolved.Address == c.AmmoSite, "Saved locator did not resolve actual restarted teaching site: " + resolved.Message);
                    await c.Action("r"); var preview = await w.Planner.PreviewAsync(saved, w.Target); await Apply(w, preview);
                    try { for (int i = 0; i < 3; ++i) await c.Action("f"); Check(Int(w, c.Player + Ammo) == 15, "Saved patch did not produce incremented ammo."); }
                    finally { Check((await w.Manager.RestoreAsync(saved.Id)).Success, "Saved patch restoration failed."); }
                    await c.Action("f"); Check(Int(w, c.Player + Ammo) == 14 && w.Target.ReadExact(c.AmmoSite, 2).SequenceEqual(saved.ExpectedBytes), "Saved restoration did not return original firing."); await Complete(c, 13);
                    Console.WriteLine("RESOLVED restarted pid=" + c.Pid + " locator=" + saved.LocatorKind + " address=0x" + resolved.Address.ToString("X") + " inactive-before-explicit-apply=1");
                }
                await Timed(w.Session.DetachAsync(), "Final detach"); await c.Exit();
            }
        }
    }
    sealed class Child : IDisposable
    {
        readonly Process launcher;
        readonly BlockingCollection<string> lines = new BlockingCollection<string>();
        readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pid { get; private set; }
        public ulong Root, Player, Enemy, AmmoSite, Damage, VaultEntry, VaultEnd, Signature, BadgeSite;
        public Dictionary<string,string> Last = new Dictionary<string,string>();
        public string LastLine = "";
        string window;
        bool exited;
        long sequence;
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        public Child(string executable, int room)
        {
            string launched = executable, arguments = "--room " + room + " --unlock-all";
            if (!Windows) {
                launched = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "breakout-launcher");
                if (!File.Exists(launched)) launched = Path.Combine(Path.GetDirectoryName(executable), "breakout-launcher");
                Check(File.Exists(launched), "breakout-launcher must be next to harness or game; Mono must not own game ptrace waits.");
                arguments = Quote(executable) + " " + arguments;
            }
            launcher = new Process { StartInfo = new ProcessStartInfo(launched, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = false } };
            launcher.OutputDataReceived += (s, e) => { if (e.Data == null) closed.TrySetResult(true); else { Console.WriteLine("GAME " + e.Data); lines.Add(e.Data); } };
            launcher.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine("GAME stderr " + e.Data); };
            Check(launcher.Start(), "Could not start packaged game."); launcher.BeginOutputReadLine(); launcher.BeginErrorReadLine();
        }
        static Dictionary<string,string> Tokens(string line)
        {
            return line.Split(' ').Where(s => s.Contains("=")).Select(s => s.Split(new[] { '=' }, 2)).GroupBy(s => s[0]).ToDictionary(g => g.Key, g => g.Last()[1]);
        }
        async Task<string> Next(Func<string,bool> accepts, string name)
        {
            return await Task.Run(() => {
                var timer = Stopwatch.StartNew(); string line;
                while (timer.ElapsedMilliseconds < Timeout) {
                    if (lines.TryTake(out line, 100) && accepts(line)) return line;
                    if (closed.Task.IsCompleted && lines.Count == 0) throw new InvalidOperationException("Game stdout closed while waiting for " + name + ".");
                }
                throw new TimeoutException("Game stdout: " + name);
            });
        }
        public async Task Identity()
        {
            var data = Tokens(await Next(s => s.StartsWith("BREAKOUT "), "startup identity"));
            Pid = int.Parse(data["pid"], CultureInfo.InvariantCulture); Root = Address(data["root"]); Player = Address(data["player"]); Enemy = Address(data["enemy"]); AmmoSite = Address(data["ammo_site"]); Damage = Address(data["damage_site"]); VaultEntry = Address(data["vault_entry"]); VaultEnd = Address(data["vault_end"]); Signature = Address(data["signature"]); BadgeSite = Address(data["badge_site"]);
            // A GUI window can map after the startup line; give it one bounded wait.
            if (Windows) await Until(() => { IntPtr found = FindWindowForPid(Pid); if (found != IntPtr.Zero) window = found.ToInt64().ToString(); return found != IntPtr.Zero; }, "Game window");
            else {
                var timer = Stopwatch.StartNew();
                while (window == null) {
                    string matches = await Xdotool("search --onlyvisible --pid " + Pid, false);
                    window = matches.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (timer.ElapsedMilliseconds > Timeout) throw new TimeoutException("Mapped game X11 window");
                    if (window == null) await Task.Delay(50);
                }
            }
        }
        async Task Record(string expectedName)
        {
            LastLine = await Next(s => s.StartsWith("ACTION "), "ordinary GUI action diagnostic"); Last = Tokens(LastLine);
            Check(Last.ContainsKey("seq") && Last.ContainsKey("room"), "GUI action record lacks sequence/room.");
            Check(long.Parse(Last["seq"], CultureInfo.InvariantCulture) == ++sequence && Last["name"] == expectedName, "Unexpected extra/mismatched GUI action: " + LastLine);
        }
        // Rooms 1-12 are F1-F12; Shift+F1 is room 13 and Shift+F12 room 0.
        public async Task Room(int room) {
            if (room >= 1 && room <= 12) await SendKey("F" + room, false); else await SendKey(room == 13 ? "F1" : "F12", false, true);
            await Record("room"); Check(Last["room"] == room.ToString(CultureInfo.InvariantCulture) && Last["paused"] == "1", "Room input did not select the room under the test freeze.");
        }
        // The hidden test freeze makes every following action deterministic.
        public async Task Freeze() { await Action("p"); if (Last["paused"] != "1") await Action("p"); Check(Last["paused"] == "1", "Test freeze did not engage."); }
        public async Task Action(string key) {
            var names = new Dictionary<string,string> { {"r","reset"}, {"f","fire"}, {"h","hit_player"}, {"j","hit_enemy"}, {"e","enemy_fire"}, {"b","charge"}, {"d","drain"}, {"w","swap"}, {"t","tick"}, {"l","reload"}, {"p","pause"}, {"y","trial"}, {"a","alarm"}, {"n","relay"} };
            string expected = key == "o" ? (Last.ContainsKey("room") && Last["room"] == "2" ? "reactor" : "door") : names[key];
            await SendKey(key, true); await Record(expected);
        }
        async Task SendKey(string key, bool control, bool shift = false)
        {
            if (!Windows) {
                string prefix = "windowfocus --sync " + window + " ";
                string commands = (control ? "keydown Control_L " : "") + (shift ? "keydown Shift_L " : "") + "keydown " + key + " sleep 0.08 keyup " + key + (shift ? " keyup Shift_L" : "") + (control ? " keyup Control_L" : "");
                await Xdotool(prefix + commands);
            } else {
                Check(SetForegroundWindow(new IntPtr(long.Parse(window))), "Could not foreground game for ordinary SendInput.");
                await Task.Delay(40);
                ushort code = key.StartsWith("F", StringComparison.Ordinal) && key.Length > 1 ? (ushort)(0x70 + int.Parse(key.Substring(1)) - 1) : (ushort)char.ToUpperInvariant(key[0]);
                if (control) InputKey(0x11, false); if (shift) InputKey(0x10, false); InputKey(code, false); await Task.Delay(250); InputKey(code, true); if (shift) InputKey(0x10, true); if (control) InputKey(0x11, true); await Task.Delay(40);
            }
        }
        public async Task HoldRight(bool down)
        {
            if (!Windows) await Xdotool((down ? "keydown" : "keyup") + " Right"); else InputKey(0x27, !down);
        }
        static async Task<string> Xdotool(string arguments, bool requireSuccess = true)
        {
            using (var tool = new Process { StartInfo = new ProcessStartInfo("xdotool", arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } }) {
                Check(tool.Start(), "Cannot start xdotool."); var stdout = tool.StandardOutput.ReadToEndAsync(); var stderr = tool.StandardError.ReadToEndAsync();
                await Timed(Task.Run(() => tool.WaitForExit()), "xdotool ordinary platform input"); string result = await stdout; string error = await stderr;
                if (requireSuccess) Check(tool.ExitCode == 0, "xdotool: " + error); return result;
            }
        }
        public async Task Exit() { if (exited) return; await SendKey("q", true); await Timed(closed.Task, "Graceful UI Quit stdout EOF"); exited = true; }
        public void Dispose()
        {
            if (!exited && Pid != 0) {
                try { Exit().GetAwaiter().GetResult(); }
                catch (Exception e) { Console.Error.WriteLine("Cleanup UI quit failed: " + e.Message); }
            }
            // No managed HasExited/WaitForExit on the game while native debugger
            // owns its ptrace statuses. Launcher disposal does not wait or reap.
            launcher.Dispose();
        }
        [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit, Size = 32)] struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
        [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mode);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] input, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        static IntPtr FindWindowForPid(int pid) { IntPtr found = IntPtr.Zero; EnumWindows((h, p) => { uint owner; GetWindowThreadProcessId(h, out owner); if (owner == (uint)pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero); return found; }
        static void InputKey(ushort code, bool up) { var inputs = new[] { new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = code, Scan = (ushort)MapVirtualKey(code, 0), Flags = (up ? 2u : 0u) | (code >= 0x21 && code <= 0x2e ? 1u : 0u) } } } }; Check(SendInput(1, inputs, Marshal.SizeOf(typeof(Input))) == 1, "Windows ordinary SendInput failed: " + Marshal.GetLastWin32Error()); }
    }
}
