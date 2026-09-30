using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReClassNET.AssemblyEditing;
using ReClassNET.Core;
using ReClassNET.DataExchange.ReClass;
using ReClassNET.Debugger;
using ReClassNET.Logger;
using ReClassNET.Memory;
using ReClassNET.Patching;
using ReClassNET.Project;

// Exercises the real selected native provider against a child created solely for this walkthrough.
// The separate FocusedChecks console covers simulated partial writes; this executable uses no mocks.
internal static class RuntimeWalkthrough
{
    private const int Timeout = 10000;
    private static int Main(string[] args)
    {
        try
        {
            bool smoke = args.Contains("--smoke");
            var startOptions = args.Where(a => a.StartsWith("--from=", StringComparison.Ordinal)).ToArray();
            int from = 1;
            if (startOptions.Length > 1 || (startOptions.Length == 1 && !int.TryParse(startOptions[0].Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out from)) || from < 1 || from > 5 || (smoke && from != 1)) throw new ArgumentException("--from=N must select a group from 1 to 5; --smoke starts at group 1.");
            var positional = args.Where(a => a != "--smoke" && !a.StartsWith("--from=", StringComparison.Ordinal)).ToArray();
            if (positional.Length > 2 || positional.Any(a => a.StartsWith("--", StringComparison.Ordinal))) throw new ArgumentException("Usage: RuntimeWalkthrough [--smoke | --from=N] [target-path] [trace-output]");
            string target = positional.Length == 0 ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Environment.OSVersion.Platform == PlatformID.Win32NT ? "debugger-target.exe" : "debugger-target") : Path.GetFullPath(positional[0]);
            Console.WriteLine("Runtime=" + Environment.OSVersion + "; CLR=" + Environment.Version + "; target=" + target);
            Console.WriteLine("Application=" + typeof(DebugWorkspace).Assembly.Location + "; mode=" + (smoke ? "smoke" : "walkthrough"));
            Run(target, positional.Length > 1 ? positional[1] : Path.Combine(Path.GetTempPath(), "reclass-debugger-trace-" + Guid.NewGuid().ToString("N") + ".csv"), smoke, from).GetAwaiter().GetResult();
            Console.WriteLine(smoke ? "PASS real native package smoke: conversion, increment apply and original restore" : "PASS real native walkthrough: groups " + from + " through 5"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL real native walkthrough: " + e); return 1; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Timed(Task task, string operation)
    {
        if (await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false) != task) throw new TimeoutException(operation + " timed out.");
        await task.ConfigureAwait(false);
    }
    private static async Task<T> Timed<T>(Task<T> task, string operation)
    {
        await Timed((Task)task, operation).ConfigureAwait(false); return await task.ConfigureAwait(false);
    }
    private static async Task Until(Func<bool> ready, string operation)
    {
        var started = Stopwatch.StartNew();
        while (!ready()) { if (started.ElapsedMilliseconds >= Timeout) throw new TimeoutException(operation + " timed out."); await Task.Delay(20).ConfigureAwait(false); }
    }
    private static async Task Run(string executable, string traceOutput, bool smoke, int from)
    {
        using (var core = new CoreFunctionsManager())
        using (var child = new Child(executable))
        using (var process = new RemoteProcess(core))
        using (var project = new ReClassNetProject())
        {
            await child.ReadIdentity();
            process.Open(new ProcessInfo(new IntPtr(child.Pid), Path.GetFileName(executable), executable));
            await process.UpdateProcessInformationsAsync();
            var workspace = new DebugWorkspace(process, project);
            Exception primaryFailure = null;
            try
            {
                workspace.Session.Diagnostic += message => Console.Error.WriteLine("Debugger: " + message);
                child.NativeWaitOwned = true;
                await Timed(workspace.Session.AttachAsync(), "Attach");
                Check(workspace.Session.State == DebugSessionState.Running, "Attach completion was not Running.");
                if (from <= 1) { await Conversion(workspace, child); Console.WriteLine("PASS 1 conversion and exact boundaries"); }
                if (smoke)
                {
                    await SmokeApplyRestore(workspace, child);
                    await Timed(workspace.Session.DetachAsync(), "Smoke detach");
                    child.NativeWaitOwned = false;
                    await child.Exit();
                    return;
                }
                if (from <= 2) { await DiscoveryAndEditing(workspace, child); Console.WriteLine("PASS 2 native discovery, apply, NOP, stale/overlap and restore"); }
                if (from <= 3) { await HookBehavior(workspace, child); Console.WriteLine("PASS 3 native allocation, RIP-relative/direct-branch relocation and retirement"); }
                if (from <= 4) { await Inspection(workspace, child, traceOutput); Console.WriteLine("PASS 4 shared instruction operands, conditions, bounded trace, new thread"); }
                var saved = await SaveAndExit(workspace, child);
                await Timed(workspace.Session.DetachAsync(), "Release exited native session");
                child.NativeWaitOwned = false;
                await RestartAndResolve(core, executable, saved);
                Console.WriteLine("PASS 5 archive definitions inactive, restart resolution, ambiguity/identity rejection and exit");
            }
            catch (Exception error)
            {
                primaryFailure = error;
                Console.Error.WriteLine("PRIMARY walkthrough failure: " + error);
                throw;
            }
            finally
            {
                try { workspace.Dispose(); child.NativeWaitOwned = false; }
                catch (Exception cleanup) when (primaryFailure != null)
                {
                    Console.Error.WriteLine("CLEANUP failure after primary walkthrough error: " + cleanup);
                }
            }
        }
    }
    private static PatchDefinition Definition(DebugWorkspace w, Child child, ulong address, int length, string assembly)
    {
        return new PatchDefinition { Name = "Controlled fixture", SourceKind = PatchSourceKind.Assembly, Assembly = assembly,
            LocatorKind = PatchLocatorKind.SessionAddress, SessionId = w.Session.Id, SessionAddress = address, Platform = w.Target.Platform,
            SelectionLength = length, ExpectedBytes = w.Target.ReadExact(address, length), Boundary = BoundarySource.ExplicitOrigin };
    }
    private static string PointerRegister(DebugWorkspace w) => w.Target.Platform == "Windows" ? "rcx" : "rdi";
    private static async Task Conversion(DebugWorkspace w, Child child)
    {
        var original = w.Instructions.Decode(w.Target.ReadExact(child.Modify, 2), child.Modify);
        Check(original.Success && original.Instructions.Single().Text.StartsWith("dec ", StringComparison.Ordinal), "The stable writer was not a two-byte decrement.");
        var assembly = await w.Assembler.AssembleAsync("inc dword [" + PointerRegister(w) + "]", child.Modify);
        Check(assembly.Success && assembly.Bytes.Length == 2 && w.Instructions.Decode(assembly.Bytes, child.Modify).Instructions.Single().Text.StartsWith("inc ", StringComparison.Ordinal), "Origin-aware assembly failed. " + string.Join("; ", assembly.Diagnostics.Select(d => d.ToString())));
        Check(!w.Instructions.Decode(new byte[] { 0x0F }, child.Modify).Success, "Incomplete instruction was accepted.");
        var longer = await w.Planner.PreviewAsync(Definition(w, child, child.Modify, 2, "inc dword [" + PointerRegister(w) + "]\nnop"), w.Target);
        Check(!longer.CanApply, "Longer in-place replacement became applicable.");
        var branch = await w.Assembler.AssembleAsync("jmp 0x" + child.Hook.ToString("X"), child.Modify);
        Check(branch.Success && branch.Preview.Instructions.Single().Instruction.NearBranchTarget == child.Hook, "Absolute branch destination changed with origin. " + string.Join("; ", branch.Diagnostics.Select(d => d.ToString())));
    }
    private static async Task SmokeApplyRestore(DebugWorkspace w, Child child)
    {
        var baseline = await child.Command("values");
        var definition = Definition(w, child, child.Modify, 2, "inc dword [" + PointerRegister(w) + "]");
        var preview = await w.Planner.PreviewAsync(definition, w.Target);
        Check(preview.CanApply, "Smoke preview failed: " + preview.Status + "; " + preview.Message);
        var applied = await w.Manager.ApplyAsync(preview);
        Check(applied.Success, "Smoke Apply failed: " + applied.Status + "; " + applied.Message);
        try
        {
            var incremented = await child.Command("modify");
            Check(incremented[0] == baseline[0] + 1 && incremented[1] == baseline[1] + 1, "Smoke increment did not update both objects.");
        }
        finally
        {
            var restored = await w.Manager.RestoreAsync(definition.Id);
            Check(restored.Success, "Smoke Restore failed: " + restored.Status + "; " + restored.Message);
            Check(w.Target.ReadExact(child.Modify, preview.OriginalBytes.Length).SequenceEqual(preview.OriginalBytes), "Smoke Restore did not recover the exact original code bytes.");
        }
        var decremented = await child.Command("modify");
        Check(decremented.SequenceEqual(baseline), "Smoke restored writer did not decrement both objects.");
        Console.WriteLine("PASS smoke increment applied and original behavior restored");
    }
    private static async Task DiscoveryAndEditing(DebugWorkspace w, Child child)
    {
        var writes = new ConcurrentBag<WatchHit>();
        var watch = await Timed(w.Session.StartWatchAsync(child.Object1, 4, true, writes.Add), "Install write watch");
        var values = await child.Command("modify");
        await Until(() => writes.Count != 0, "Writer observation");
        Check(values[0] == 99 && values[1] == 199, "Controlled decrement did not update both objects.");
        Check(writes.All(h => h.Snapshot.Phase == SnapshotPhase.After) && writes.Any(h => h.Candidates.Any(i => i.Address == child.Modify)), "Writer capture lost After phase or known instruction candidate.");
        await Timed(w.Session.StopWatchAsync(watch.Id), "Remove write watch");
        var definition = Definition(w, child, child.Modify, 2, "inc dword [" + PointerRegister(w) + "]");
        var preview = await w.Planner.PreviewAsync(definition, w.Target);
        Check(preview.CanApply, preview.Message);
        Check((await w.Manager.ApplyAsync(preview)).Success, "Native increment Apply failed.");
        values = await child.Command("modify"); Check(values[0] == 100 && values[1] == 200, "Applied writer did not increment both objects.");
        var overlapDefinition = Definition(w, child, child.Modify, 2, "nop");
        var overlap = await w.Planner.PreviewAsync(overlapDefinition, w.Target);
        Check((await w.Manager.ApplyAsync(overlap)).Status == PatchStatus.Conflict, "Overlapping active patch was accepted.");
        Check((await w.Manager.RestoreAsync(definition.Id)).Success, "Increment restoration failed.");
        values = await child.Command("modify"); Check(values[0] == 99 && values[1] == 199, "Original decrement was not restored.");
        var nop = await w.Planner.PreviewAsync(Definition(w, child, child.Modify, 2, "nop"), w.Target);
        Check(nop.CanApply && nop.PaddingLength == 1 && (await w.Manager.ApplyAsync(nop)).Success, "Short NOP replacement failed.");
        values = await child.Command("modify"); Check(values[0] == 99 && values[1] == 199, "NOP patch still decremented objects.");
        Check((await w.Manager.RestoreAsync(nop.Definition.Id)).Success, "NOP restore failed.");
        // Simulate a known external fixture writer through a separate code transaction, then clean it up.
        var stale = await w.Planner.PreviewAsync(Definition(w, child, child.Modify, 2, "nop"), w.Target);
        try
        {
            await WriteFixture(w, child.Modify, new byte[] { 0x90, 0x90 });
            Check((await w.Manager.ApplyAsync(stale)).Status == PatchStatus.Conflict, "A stale snapshot was written over.");
        }
        finally { await WriteFixture(w, child.Modify, stale.OriginalBytes); }
        var breakpointPreview = await w.Planner.PreviewAsync(Definition(w, child, child.Modify, 2, "nop"), w.Target);
        var execution = await w.Session.StartInstructionWatchAsync(child.Modify, _ => { });
        Check((await w.Manager.ApplyAsync(breakpointPreview)).Status == PatchStatus.Conflict, "Software-breakpoint overlap was accepted.");
        await w.Session.StopWatchAsync(execution.Id);
    }
    private static async Task WriteFixture(DebugWorkspace w, ulong address, byte[] bytes)
    {
        using (var stop = await w.Target.StopAsync(CancellationToken.None))
        {
            var result = w.Target.WriteCode(address, bytes);
            if (!result.Success || !w.Target.ReadExact(address, bytes.Length).SequenceEqual(bytes)) { stop.KeepStopped("Controlled fixture write failed."); throw new InvalidOperationException(result.Error ?? "Fixture readback failed."); }
        }
    }
    private static async Task HookBehavior(DebugWorkspace w, Child child)
    {
        await child.Command("reset");
        var definition = Definition(w, child, child.Hook, 2, "inc dword [" + PointerRegister(w) + "]\nnop\nnop\nnop"); definition.Mode = PatchMode.Hook;
        var preview = await w.Planner.PreviewAsync(definition, w.Target);
        var hook = await Timed(w.Planner.PrepareHookAsync(preview, w.Target, w.Manager), "Prepare RIP hook");
        Check(hook.Preview.OriginalBytes.Length >= hook.JumpLength && hook.DisplacedInstructions.Any(i => i.Instruction.IsIPRelativeMemoryOperand), "Prepared hook lost its RIP-relative suffix.");
        Check((await w.Manager.ApplyAsync(hook)).Success, "Native hook publication failed.");
        var values = await child.Command("hook"); Check(values[0] == 104 && values[1] == 204, "Replacement body plus displaced bias load did not execute correctly.");
        Check((await w.Manager.RestoreAsync(definition.Id)).Success, "Hook entry restore failed.");
        values = await child.Command("hook"); Check(values[0] == 107 && values[1] == 207, "Restored hook site did not use original behavior.");
        Check(hook.Allocation.Retired && w.Manager.RetainedAllocations.Contains(hook.Allocation) && w.Manager.PublishedBytes >= PatchPlanner.HookReservationSize, "Retired code was freed or lost from accounting.");
        // Hook the TEST preceding the direct JZ; TEST body preserves the branch's ZF input.
        definition = Definition(w, child, child.Hook + 8, 2, "test eax, eax\nnop\nnop\nnop"); definition.Mode = PatchMode.Hook;
        preview = await w.Planner.PreviewAsync(definition, w.Target);
        hook = await Timed(w.Planner.PrepareHookAsync(preview, w.Target, w.Manager), "Prepare direct-branch hook");
        Check(hook.DisplacedInstructions.Any(i => i.FlowControl == Iced.Intel.FlowControl.ConditionalBranch), "Direct branch was not included in relocation case.");
        Check((await w.Manager.ApplyAsync(hook)).Success, "Direct-branch hook publication failed.");
        using (var stop = await w.Target.StopAsync(CancellationToken.None)) Check(w.Process.WriteRemoteMemory(new IntPtr(unchecked((long)child.Bias)), BitConverter.GetBytes(0)), "Cannot set fixture branch input.");
        values = await child.Command("hook"); Check(values[0] == 108 && values[1] == 208, "Relocated JZ changed its original external destination.");
        Check((await w.Manager.RestoreAsync(definition.Id)).Success, "Direct-branch hook restore failed.");
        await child.Command("reset");
    }
    private static async Task Inspection(DebugWorkspace w, Child child, string traceOutput)
    {
        var executionHits = new ConcurrentBag<WatchHit>();
        var execution = await w.Session.StartInstructionWatchAsync(child.Modify, executionHits.Add);
        await child.Command("modify"); await Until(() => executionHits.Count >= 2, "Shared instruction observations");
        var addresses = executionHits.SelectMany(hit => w.Instructions.ResolveMemoryAddresses(hit.Candidates.Single(), hit.Snapshot.Registers)).Where(m => m.Available).Select(m => m.Address).Distinct().ToArray();
        Check(addresses.Contains(child.Object1) && addresses.Contains(child.Object2) && executionHits.All(h => h.Confirmed && h.Completed && h.Snapshot.Phase == SnapshotPhase.Before), "Execution watch did not observe both object addresses with completed Before context.");
        await w.Session.StopWatchAsync(execution.Id);
        var stoppedHit = new TaskCompletionSource<WatchHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        string condition = PointerRegister(w) + " == 0x" + child.Object1.ToString("X");
        execution = await w.Session.StartInstructionWatchAsync(child.Modify, hit => stoppedHit.TrySetResult(hit), condition, true);
        var command = child.Command("modify");
        var stopped = await Timed(stoppedHit.Task, "Condition pause");
        await Until(() => w.Session.State == DebugSessionState.Paused, "Condition paused state");
        await w.Session.StopWatchAsync(execution.Id);
        var trace = await Timed(w.Session.StartTraceAsync(stopped.Snapshot.ThreadId, 8, 2000), "Bounded trace");
        Check(trace.Entries.Count > 0 && trace.Entries.Count <= 8 && trace.Entries.All(e => e.Before.Phase == SnapshotPhase.Before && e.After.Phase == SnapshotPhase.After), "Bounded trace lost phase or exceeded limits.");
        File.WriteAllLines(traceOutput, new[] { "address,instruction,beforeRax,afterRax,beforeFlags,afterFlags" }.Concat(trace.Entries.Select(e => "0x" + e.Address.ToString("X") + ",\"" + e.Instruction.Replace("\"", "\"\"") + "\",0x" + e.Before.Context.Rax.ToString("X") + ",0x" + e.After.Context.Rax.ToString("X") + ",0x" + e.Before.Context.Rflags.ToString("X") + ",0x" + e.After.Context.Rflags.ToString("X"))));
        Console.WriteLine("Trace=" + traceOutput + "; entries=" + trace.Entries.Count + "; stop=" + trace.StopReason);
        await w.Session.ResumeAsync(); await Timed(command, "Complete traced command");
        var baselineThreads = w.Session.Threads().ToArray();
        var newThreadHit = new TaskCompletionSource<WatchHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = await w.Session.StartWatchAsync(child.Object1, 4, true, hit => { if (!baselineThreads.Contains(hit.Snapshot.ThreadId)) newThreadHit.TrySetResult(hit); });
        await child.Command("thread"); await Timed(newThreadHit.Task, "New thread hardware watch");
        await child.Command("stop"); await w.Session.StopWatchAsync(data.Id);
    }
    private static async Task<PatchDefinition> SaveAndExit(DebugWorkspace w, Child child)
    {
        var module = w.Target.Modules.Single(m => child.Modify >= m.BaseAddress && child.Modify < checked(m.BaseAddress + m.Size));
        var definition = Definition(w, child, child.Modify, 2, "inc dword [" + PointerRegister(w) + "]");
        definition.LocatorKind = PatchLocatorKind.ModuleOffset; definition.ModuleName = module.Name; definition.ImageSha256 = new PatchTargetResolver().Fingerprint(module.Path); definition.Offset = child.Modify - module.BaseAddress;
        w.Repository.Upsert(definition);
        byte[] archive;
        using (var stream = new MemoryStream()) { new ReClassNetFile(w.Project).Save(stream, new NullLogger()); archive = stream.ToArray(); }
        using (var loadedProject = new ReClassNetProject())
        {
            using (var stream = new MemoryStream(archive)) new ReClassNetFile(loadedProject).Load(stream, new NullLogger());
            var loaded = new PatchRepository(loadedProject);
            Check(loaded.Definitions.Count == 1 && w.Manager.ActivePatches.Count == 0, "Archive reload applied process code.");
            definition = loaded.Definitions.Single();
        }
        var applied = await w.Planner.PreviewAsync(definition, w.Target);
        Check((await w.Manager.ApplyAsync(applied)).Success, "Exit case could not activate a fixture patch.");
        await child.Exit(); await Until(() => w.Session.State == DebugSessionState.Exited, "Native target-exit event");
        w.Manager.InvalidateTarget(); Check(w.Manager.ActivePatches.Count == 0 && w.Manager.Preparation == null, "Target exit retained transient live records.");
        return definition;
    }
    private static async Task RestartAndResolve(CoreFunctionsManager core, string executable, PatchDefinition saved)
    {
        using (var child = new Child(executable))
        using (var process = new RemoteProcess(core))
        using (var project = new ReClassNetProject())
        {
            await child.ReadIdentity(); process.Open(new ProcessInfo(new IntPtr(child.Pid), Path.GetFileName(executable), executable)); await process.UpdateProcessInformationsAsync();
            var w = new DebugWorkspace(process, project);
            Exception primaryFailure = null;
            try
            {
                child.NativeWaitOwned = true;
                await Timed(w.Session.AttachAsync(), "Reattach restarted fixture");
                var resolver = new PatchTargetResolver();
                var resolved = resolver.Resolve(saved, w.Target, CancellationToken.None);
                Check(resolved.Status == PatchResolutionStatus.Resolved && resolved.Address == child.Modify && w.Target.ReadExact(child.Modify, 2).SequenceEqual(saved.ExpectedBytes), "Saved module offset did not resolve with exact identity after restart.");
                Check(w.Manager.ActivePatches.Count == 0, "Restart resolution auto-applied a saved patch.");
                var ambiguous = saved.Clone(); ambiguous.LocatorKind = PatchLocatorKind.ModulePattern; ambiguous.Pattern = "90";
                Check(resolver.Resolve(ambiguous, w.Target, CancellationToken.None).Status == PatchResolutionStatus.MultipleMatches, "Ambiguous executable pattern was accepted.");
                var changed = saved.Clone(); changed.ImageSha256 = new string('0', 64);
                Check(resolver.Resolve(changed, w.Target, CancellationToken.None).Status == PatchResolutionStatus.IdentityMismatch, "Changed image identity was accepted.");
                await child.Exit(); await Until(() => w.Session.State == DebugSessionState.Exited, "Restarted target exit"); w.Manager.InvalidateTarget();
            }
            catch (Exception error)
            {
                primaryFailure = error;
                Console.Error.WriteLine("PRIMARY restart-resolution failure: " + error);
                throw;
            }
            finally
            {
                try { w.Dispose(); child.NativeWaitOwned = false; }
                catch (Exception cleanup) when (primaryFailure != null)
                {
                    Console.Error.WriteLine("CLEANUP failure after primary restart-resolution error: " + cleanup);
                }
            }
        }
    }
    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly BlockingCollection<string> output = new BlockingCollection<string>();
        private readonly TaskCompletionSource<bool> stdoutClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool NativeWaitOwned { get; set; }
        public int Pid { get; private set; }
        public ulong Object1, Object2, Modify, Hook, Bias;
        public Child(string executable)
        {
            string launched = executable;
            string arguments = "";
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                launched = Path.Combine(Path.GetDirectoryName(executable), "debugger-launcher");
                if (!File.Exists(launched)) throw new FileNotFoundException("The Linux validation launcher must be next to the controlled fixture to avoid Mono consuming ptrace wait statuses.", launched);
                arguments = "\"" + executable.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }
            process = new Process { StartInfo = new ProcessStartInfo(launched, arguments) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) output.Add(e.Data); else stdoutClosed.TrySetResult(true); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) Console.Error.WriteLine("Target: " + e.Data); };
            Check(process.Start(), "Cannot start controlled target."); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        public async Task ReadIdentity()
        {
            var line = await Next(s => s.StartsWith("pid=", StringComparison.Ordinal), "initial fixture identity");
            var items = line.Split(' ').Select(s => s.Split('=')).ToDictionary(p => p[0], p => p[1]);
            Pid = int.Parse(items["pid"], CultureInfo.InvariantCulture); Object1 = Address(items["object1"]); Object2 = Address(items["object2"]); Modify = Address(items["modify"]); Hook = Address(items["hook"]); Bias = Address(items["bias"]);
            Console.WriteLine("Target " + line);
        }
        private static ulong Address(string text) => ulong.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.Substring(2) : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        private Task<string> Next(Func<string, bool> matches, string operation)
        {
            return Task.Run(() =>
            {
                var timer = Stopwatch.StartNew(); string line;
                while (timer.ElapsedMilliseconds < Timeout)
                {
                    if (output.TryTake(out line, 100) && matches(line)) return line;
                    if (stdoutClosed.Task.IsCompleted && output.Count == 0)
                    {
                        // HasExited/ExitCode can consume Linux ptrace wait statuses owned by the
                        // native debugger. Pipe EOF reports closure without calling managed waitpid.
                        throw new InvalidOperationException("Controlled target stdout closed before producing expected output for " + operation + ". Native session owns process-exit reporting.");
                    }
                }
                throw new TimeoutException("Controlled target output timed out for " + operation + ".");
            });
        }
        public async Task<int[]> Command(string command)
        {
            Console.WriteLine("Target command: " + command);
            process.StandardInput.WriteLine(command); process.StandardInput.Flush();
            var line = await Next(s => s.StartsWith("values=", StringComparison.Ordinal), "command '" + command + "'");
            return line.Substring(7).Split(' ')[0].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        }
        public async Task Exit()
        {
            Console.WriteLine("Target command: exit");
            process.StandardInput.WriteLine("exit"); process.StandardInput.Flush();
            await Timed(stdoutClosed.Task, "Controlled target stdout EOF");
            // Attached callers next await DebugSession.Exited. No managed process waiter runs
            // concurrently with the debugger's per-thread waitpid loop.
        }
        public void Dispose()
        {
            // This child belongs exclusively to the validation executable, never a user-selected target.
            if (!stdoutClosed.Task.IsCompleted)
            {
                try { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); stdoutClosed.Task.Wait(1000); } catch { }
            }
            if (NativeWaitOwned)
            {
                Console.Error.WriteLine("Fixture cleanup deferred: native debugger still owns process wait statuses after failed detach.");
                return;
            }
            process.Dispose();
        }
    }
}
