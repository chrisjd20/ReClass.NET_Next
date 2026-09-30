using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ReClassNET.AssemblyEditing;
using ReClassNET.Core;
using ReClassNET.Debugger;
using ReClassNET.Memory;
using ReClassNET.MemoryScanner;
using ReClassNET.Patching;

// Focused regressions for the ReClass feature fixes. Synthetic memory only: no native
// debugger, target process, NASM or GUI is required.
internal static class FixChecks
{
    private static readonly InstructionService Instructions = new InstructionService();
    private static readonly PatchPlanner Planner = new PatchPlanner(Instructions, new AssemblyService(Instructions));
    private static int Main()
    {
        try
        {
            Patterns(); Console.WriteLine("PASS pattern scanner end-of-buffer match and masked pattern round trip");
            Conditions(); Console.WriteLine("PASS signed and unsigned condition division/modulo");
            RestoreAll().GetAwaiter().GetResult(); Console.WriteLine("PASS restore-all continues past conflicts; force restore and abandon");
            Addresses(); Console.WriteLine("PASS module+offset code address parsing");
            Errors(); Console.WriteLine("PASS Linux errno descriptions and ptrace_scope hint");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Patterns()
    {
        var pattern = BytePattern.Parse("AA BB CC");
        Check(PatternScanner.FindPattern(pattern, new byte[] { 0xAA, 0xBB, 0xCC }) == 0, "A pattern filling the whole buffer was missed.");
        Check(PatternScanner.FindPattern(pattern, new byte[] { 0x00, 0x11, 0xAA, 0xBB, 0xCC }) == 2, "A pattern ending at the last byte was missed.");
        Check(PatternScanner.FindPattern(pattern, new byte[] { 0xAA, 0xBB }) == -1, "A buffer shorter than the pattern matched.");
        Check(PatternScanner.FindPattern(pattern, new byte[] { 0xAA, 0xBB, 0xCD }) == -1, "A mismatch was reported as a match.");
        var masked = BytePattern.From(new[] { Tuple.Create((byte)0x48, false), Tuple.Create((byte)0x8B, false), Tuple.Create((byte)0x05, false), Tuple.Create((byte)0x12, true), Tuple.Create((byte)0x34, true) });
        var text = masked.ToString(); var parsed = BytePattern.Parse(text);
        Check(parsed.Length == 5 && parsed.HasWildcards && text.Contains("??"), "Masked pattern text did not round trip: " + text);
        Check(PatternScanner.FindPattern(parsed, new byte[] { 0x90, 0x48, 0x8B, 0x05, 0xFF, 0xEE }) == 1, "Wildcard bytes at the buffer end did not match.");
    }
    private static bool Eval(string condition, ulong rax = 0)
    {
        var registers = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { { "rax", rax } };
        return WatchCondition.Parse(condition).Evaluate(registers, 1, 1, (a, n) => new byte[n]);
    }
    private static void Conditions()
    {
        ulong minusTen = unchecked((ulong)-10L);
        Check(Eval("rax / 2 == 5", 10) && Eval("rax % 4 == 2", 10), "Unsigned division/modulo changed.");
        Check(Eval("signed64(rax) / 3 == 0 - 3", minusTen), "Signed 64-bit division did not truncate toward zero.");
        Check(Eval("signed64(rax) % 3 == 0 - 1", minusTen), "Signed 64-bit modulo lost its sign.");
        Check(Eval("signed32(eax) / 2 == 0 - 5", 0xFFFFFFF6), "Signed 32-bit division was unsigned.");
        Check(Eval("signed32(eax) / 2 < 0", 0xFFFFFFF6), "Signed quotient did not compare as negative.");
        Check(!Eval("rax / 2 == 0 - 5", minusTen), "Unsigned division became signed.");
        Check(Eval("signed64(0x8000000000000000) / signed64(0xFFFFFFFFFFFFFFFF) == 0x8000000000000000") && Eval("signed64(0x8000000000000000) % signed64(0xFFFFFFFFFFFFFFFF) == 0"), "Minimum value divided by -1 did not wrap.");
        bool threw = false; try { Eval("signed64(rax) / 0", 1); } catch (DivideByZeroException) { threw = true; }
        Check(threw, "Division by zero did not stop the condition.");
    }
    private static PatchDefinition Definition(FakeTarget target, ulong address, string name)
    {
        return new PatchDefinition { Name = name, SourceKind = PatchSourceKind.Bytes, LocatorKind = PatchLocatorKind.SessionAddress, SessionId = target.SessionId,
            SessionAddress = address, Platform = "Windows", SelectionLength = 2, ExpectedBytes = target.ReadExact(address, 2),
            ReplacementBytes = new byte[] { 0xFF, 0x00 }, Boundary = BoundarySource.ExplicitOrigin };
    }
    private static async Task<PatchPreview> Apply(PatchManager manager, FakeTarget target, ulong address, string name)
    {
        var preview = await Planner.PreviewAsync(Definition(target, address, name), target);
        Check(preview.CanApply, "Preview failed: " + preview.Message);
        Check((await manager.ApplyAsync(preview)).Success, "Apply failed.");
        return preview;
    }
    private static async Task RestoreAll()
    {
        var target = new FakeTarget(); var manager = new PatchManager(target);
        await Apply(manager, target, 0x1000, "First"); await Apply(manager, target, 0x1010, "Second");
        target.Put(0x1000, new byte[] { 0xCC, 0xCC });
        var result = await manager.RestoreAllAsync();
        Check(!result.Success && result.Status == PatchStatus.Conflict && result.Failures.Count == 1 && result.Message.Contains("First"), "Restore-all did not report the conflict: " + result.Message);
        Check(target.ReadExact(0x1010, 2).SequenceEqual(new byte[] { 0xFF, 0x08 }), "Restore-all stopped before restoring the second patch.");
        Check(manager.ActivePatches.Count == 1 && manager.ActivePatches[0].Status == PatchStatus.Conflict, "The conflicting patch lost its ownership record.");
        Check(target.ReadExact(0x1000, 2).SequenceEqual(new byte[] { 0xCC, 0xCC }), "A conflicting span was overwritten without force.");
        Check((await manager.ForceRestoreAllAsync()).Success && manager.ActivePatches.Count == 0 && target.ReadExact(0x1000, 2).SequenceEqual(new byte[] { 0xFF, 0x08 }), "Force restore did not write the originals.");
        await Apply(manager, target, 0x1000, "Third");
        target.Put(0x1000, new byte[] { 0x90, 0x90 }); int writes = target.Writes;
        var abandoned = await manager.AbandonAllAsync();
        Check(abandoned.Success && manager.ActivePatches.Count == 0 && target.Writes == writes && target.ReadExact(0x1000, 2).SequenceEqual(new byte[] { 0x90, 0x90 }), "Abandon wrote bytes or kept ownership.");
        Check((await manager.RestoreAllAsync()).Success, "Restore-all after abandon still failed.");
    }
    private static void Addresses()
    {
        var modules = new[] { new ReClassNET.Memory.Module { Name = "game.exe", Start = new IntPtr(0x140000000), End = new IntPtr(0x140100000), Size = new IntPtr(0x100000) }, new ReClassNET.Memory.Module { Name = "libgame.so", Start = new IntPtr(0x7F0000000000) } };
        Func<string, ulong?> parse = text => { ulong value; string error; return DebugWorkspace.TryParseCodeAddress(text, modules, out value, out error) ? value : (ulong?)null; };
        Check(parse("game.exe+0x10") == 0x140000010 && parse("GAME.EXE+10") == 0x140000010 && parse("game+0x1A") == 0x14000001A, "Module+offset forms failed.");
        Check(parse("libgame.so+0x20") == 0x7F0000000020 && parse("7FF600001234") == 0x7FF600001234 && parse("0x1234") == 0x1234, "Raw hex or shared-object forms failed.");
        Check(parse("missing.dll+0x10") == null && parse("game.exe+xyz") == null && parse("") == null, "Invalid addresses were accepted.");
    }
    private static void Errors()
    {
        var method = typeof(NativeDebugException).GetMethod("LinuxError", BindingFlags.Static | BindingFlags.NonPublic);
        var attach = (string)method.Invoke(null, new object[] { AdvancedOperation.Attach, 1U });
        Check(attach.Contains("EPERM") && attach.Contains("ptrace_scope"), "EPERM attach failure lacks a ptrace_scope hint: " + attach);
        var write = (string)method.Invoke(null, new object[] { AdvancedOperation.WriteCode, 14U });
        Check(write.Contains("EFAULT") && !write.Contains("ptrace_scope"), "Non-attach errno text is wrong: " + write);
    }
    private sealed class FakeTarget : IPatchTarget
    {
        private readonly byte[] memory = Enumerable.Repeat((byte)0x90, 0x100).ToArray();
        public int Writes;
        public FakeTarget() { Put(0x1000, new byte[] { 0xFF, 0x08 }); Put(0x1010, new byte[] { 0xFF, 0x08 }); }
        public Guid SessionId { get; } = Guid.NewGuid(); public string ProviderIdentity => "fix-fake"; public string Platform => "Windows";
        public bool IsAlive => true; public bool SupportsCodeTransactions => true; public bool SupportsAllocation => false;
        public IReadOnlyList<PatchModule> Modules => new PatchModule[0];
        public IReadOnlyList<ulong> StoppedInstructionPointers => new ulong[0]; public IReadOnlyList<ulong> KnownIncomingTargets => new ulong[0];
        public Task<IPatchStopLease> StopAsync(CancellationToken cancellation) => Task.FromResult<IPatchStopLease>(new Lease());
        private sealed class Lease : IPatchStopLease { public void KeepStopped(string reason) { } public void RecoveryCompleted() { } public void Dispose() { } }
        public byte[] ReadExact(ulong address, int length) { Check(address >= 0x1000 && address + (ulong)length <= 0x1100, "Unmapped fake address."); return memory.Skip((int)(address - 0x1000)).Take(length).ToArray(); }
        public void Put(ulong address, byte[] bytes) => Buffer.BlockCopy(bytes, 0, memory, (int)(address - 0x1000), bytes.Length);
        public PatchWriteResult WriteCode(ulong address, byte[] bytes) { Writes++; Put(address, bytes); return new PatchWriteResult { Success = true }; }
        public PatchAllocation Allocate(ulong nearAddress, int size) => throw new NotSupportedException();
        public PatchWriteResult ProtectExecutable(PatchAllocation allocation) => throw new NotSupportedException();
        public void Free(PatchAllocation allocation) => throw new NotSupportedException();
        public bool IsModuleCurrent(PatchModule module) => true;
        public string FindExternalOverlap(ulong address, int length) => null;
    }
}
