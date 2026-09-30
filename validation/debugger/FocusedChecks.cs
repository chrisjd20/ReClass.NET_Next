using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ReClassNET.AssemblyEditing;
using ReClassNET.Debugger;
using ReClassNET.Patching;
using ReClassNET.Project;

internal static class FocusedChecks
{
    private static readonly InstructionService Instructions = new InstructionService();
    private static readonly AssemblyService Assembler = new AssemblyService(Instructions);
    private static readonly PatchPlanner Planner = new PatchPlanner(Instructions, Assembler);
    private static readonly string[] GroupNames = { "conversion", "transactions", "hooks", "conditions", "persistence" };
    private static int Main(string[] args)
    {
        try
        {
            var groups = args.Length == 0 ? GroupNames : args.Select(a => a.ToLowerInvariant()).Distinct().ToArray();
            if (groups.Any(group => !GroupNames.Contains(group))) throw new ArgumentException("Usage: FocusedChecks [conversion] [transactions] [hooks] [conditions] [persistence]");
            Run(groups).GetAwaiter().GetResult();
            Console.WriteLine("PASS focused logic groups: " + string.Join(", ", groups)); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
    private static async Task Run(IEnumerable<string> groups)
    {
        foreach (var group in groups)
        {
            switch (group)
            {
                case "conversion": await Conversion(); Console.WriteLine("PASS conversion and boundaries"); break;
                case "transactions": await Transactions(); Console.WriteLine("PASS transactions and failed-write recovery"); break;
                case "hooks": await Hooks(); Console.WriteLine("PASS hook relocation, modes, and allocation lifetime"); break;
                case "conditions": Conditions(); Console.WriteLine("PASS shared operand addresses and bounded conditions"); break;
                case "persistence": Persistence(); Console.WriteLine("PASS inactive persistence and unique resolution"); break;
            }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static PatchDefinition Definition(FakeTarget target, int length = 2, byte[] replacement = null)
    {
        return new PatchDefinition { Name = "Ammo", SourceKind = PatchSourceKind.Bytes, LocatorKind = PatchLocatorKind.SessionAddress, SessionId = target.SessionId,
            SessionAddress = 0x1000, Platform = "Windows", SelectionLength = length, ExpectedBytes = target.ReadExact(0x1000, length),
            ReplacementBytes = replacement ?? new byte[] { 0xFF, 0x00 }, Boundary = BoundarySource.ExplicitOrigin };
    }
    private static async Task Conversion()
    {
        var dec = Instructions.Decode(new byte[] { 0xFF, 0x08 }, 0x1000);
        Check(dec.Success && dec.Instructions.Single().Text.Contains("dec"), "Known decrement decode failed.");
        var inc = await Assembler.AssembleAsync("inc dword [rax]", 0x1000);
        Check(inc.Success && inc.Bytes.SequenceEqual(new byte[] { 0xFF, 0x00 }), "Known increment assembly failed. " + string.Join("; ", inc.Diagnostics.Select(d => d.ToString())));
        var branch = await Assembler.AssembleAsync("jmp 0x1200", 0x1000);
        Check(branch.Success && Instructions.Decode(branch.Bytes, 0x1000).Instructions[0].Instruction.NearBranchTarget == 0x1200, "Branch origin was lost. " + string.Join("; ", branch.Diagnostics.Select(d => d.ToString())));
        Check(!Instructions.Decode(new byte[] { 0x0F }, 0x1000).Success, "Incomplete bytes were accepted.");
        var target = new FakeTarget();
        var shorter = await Planner.PreviewAsync(Definition(target, 2, new byte[] { 0x90 }), target);
        Check(shorter.CanApply && shorter.PaddingLength == 1 && shorter.ReplacementBytes.SequenceEqual(new byte[] { 0x90, 0x90 }), "Short edit was not visibly NOP padded.");
        var longer = await Planner.PreviewAsync(Definition(target, 2, new byte[] { 0x90, 0x90, 0x90 }), target);
        Check(!longer.CanApply && target.Writes == 0, "Oversized in-place preview wrote code or enabled Apply.");
        var uncertain = Definition(target); uncertain.Boundary = BoundarySource.Uncertain;
        Check(!(await Planner.PreviewAsync(uncertain, target)).CanApply, "Uncertain boundary became a patch site.");
    }
    private static async Task Transactions()
    {
        var target = new FakeTarget(); var manager = new PatchManager(target);
        var preview = await Planner.PreviewAsync(Definition(target), target);
        Check((await manager.ApplyAsync(preview)).Success, "Apply failed.");
        var overlapping = await Planner.PreviewAsync(Definition(target, 2, new byte[] { 0x90 }), target);
        Check((await manager.ApplyAsync(overlapping)).Status == PatchStatus.Conflict, "Active intervals stacked.");
        Check((await manager.RestoreAsync(preview.Definition.Id)).Success && target.ReadExact(0x1000, 2).SequenceEqual(new byte[] { 0xFF, 0x08 }), "Restore failed.");
        preview = await Planner.PreviewAsync(Definition(target), target); target.Put(0x1000, new byte[] { 0x90, 0x90 });
        int writes = target.Writes;
        Check((await manager.ApplyAsync(preview)).Status == PatchStatus.Conflict && target.Writes == writes, "Stale originals were overwritten.");
        target.Put(0x1000, new byte[] { 0xFF, 0x08 });
        preview = await Planner.PreviewAsync(Definition(target, 2, new byte[] { 0x90, 0x90 }), target); target.FailWrites = 1;
        var recovered = await manager.ApplyAsync(preview);
        Check(!recovered.Success && recovered.Recovered && target.ReadExact(0x1000, 2).SequenceEqual(new byte[] { 0xFF, 0x08 }) && !target.KeptStopped, "Partial write did not recover before releasing stop.");
        target.FailWrites = 2;
        var failed = await manager.ApplyAsync(preview);
        Check(failed.Status == PatchStatus.RecoveryRequired && target.KeptStopped && manager.ActivePatches.Count == 1, "Failed rollback lost ownership or resumed unknown code.");
        Check((await manager.RestoreAsync(preview.Definition.Id)).Success && !target.KeptStopped, "Explicit recovery did not restore and clear recovery stop.");
        var paused = await Planner.PreviewAsync(Definition(target), target); target.Ips.Add(0x1001);
        Check((await manager.ApplyAsync(paused)).Status == PatchStatus.Conflict, "A thread inside the overwrite was ignored.");
        target.Ips.Clear(); target.ExternalOverlap = "software breakpoint";
        Check((await manager.ApplyAsync(paused)).Status == PatchStatus.Conflict, "Software breakpoint ownership was ignored.");
    }
    private static async Task Hooks()
    {
        var target = new FakeTarget(); var manager = new PatchManager(target);
        // Select only the two NOPs; the six-byte RIP-relative load is extra displacement.
        target.Put(0x1000, new byte[] { 0x90, 0x90, 0x8B, 0x05, 0xF8, 0x07, 0, 0 });
        var definition = Definition(target); definition.Mode = PatchMode.Hook; definition.SourceKind = PatchSourceKind.Assembly; definition.Assembly = "inc dword [rax]";
        var preview = await Planner.PreviewAsync(definition, target);
        var hook = await Planner.PrepareHookAsync(preview, target, manager);
        Check(hook.JumpLength == 5 && hook.Preview.OriginalBytes.Length == 8 && hook.ReturnAddress == 0x1008 && target.RxTransitions == 1, "Near hook displaced wrong boundaries or did not become RX.");
        var suffix = hook.Relocations.Last(); var map = hook.InstructionMappings.Single(m => m.OriginalAddress == 0x1002);
        Check(map.RelocatedAddress.HasValue && Instructions.Decode(target.ReadExact(map.RelocatedAddress.Value, 6), map.RelocatedAddress.Value).Instructions[0].Instruction.IPRelativeMemoryAddress == 0x1800, "RIP-relative load changed its absolute destination.");
        Check((await manager.ApplyAsync(hook)).Success && (await manager.RestoreAsync(definition.Id)).Success, "Hook publication/restoration failed.");
        Check(hook.Allocation.Published && hook.Allocation.Retired && target.Frees == 0 && manager.PublishedBytes == 131072, "Published allocation was freed or unaccounted.");
        definition.HookMode = HookSemanticMode.InsertAfter;
        preview = await Planner.PreviewAsync(definition, target);
        hook = await Planner.PrepareHookAsync(preview, target, manager);
        Check(hook.InstructionMappings.Any(m => m.OriginalAddress == 0x1000), "Insert-after lost selected originals.");
        Check((await manager.CancelPreparationAsync(hook.Id)).Success && target.Frees == 1, "Unpublished allocation was not freed.");
        target.Put(0x1000, Enumerable.Repeat((byte)0x90, 32).ToArray()); target.NextAllocation = 0x10000000000;
        definition = Definition(target); definition.Mode = PatchMode.Hook;
        hook = await Planner.PrepareHookAsync(await Planner.PreviewAsync(definition, target), target, manager);
        Check(hook.JumpLength == 14 && hook.EntryBytes[0] == 0xFF && hook.EntryBytes[1] == 0x25, "Far jump did not use the non-clobbering 14-byte encoding.");
        await manager.CancelPreparationAsync(hook.Id);
        target.Put(0x1000, new byte[] { 0x90, 0x90, 0xC3 }); target.NextAllocation = 0x200000;
        definition = Definition(target); definition.Mode = PatchMode.Hook;
        bool rejected = false;
        try { await Planner.PrepareHookAsync(await Planner.PreviewAsync(definition, target), target, manager); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Displaced return was accepted.");
    }
    private static void Conditions()
    {
        var registers = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { { "rax", 0x3000 }, { "rflags", 0x40 } };
        var instruction = Instructions.Decode(new byte[] { 0xFF, 0x08 }, 0x1000).Instructions[0];
        Check(Instructions.ResolveMemoryAddresses(instruction, registers).Single().Address == 0x3000, "Before operand address was lost.");
        registers["rax"] = 0x4000;
        Check(Instructions.ResolveMemoryAddresses(instruction, registers).Single().Address == 0x4000, "Shared instruction was assumed to access only one object.");
        int reads = 0;
        Func<ulong, int, byte[]> read = (address, length) => { reads++; return new byte[length]; };
        Check(WatchCondition.Parse("0 && mem64(0x10)").Evaluate(registers, 1, 1, read) == false && reads == 0, "Condition did not short circuit.");
        Check(WatchCondition.Parse("eax == 0x4000 && zf && signed32(0xffffffff) < 0").Evaluate(registers, 1, 1, read), "Subregister, flag, or signed comparison failed.");
        bool limited = false;
        try { WatchCondition.Parse(string.Join(" + ", Enumerable.Repeat("mem8(0)", 17))).Evaluate(registers, 1, 1, read); } catch (InvalidOperationException) { limited = true; }
        Check(limited, "Condition memory reads were unbounded.");
    }
    private static void Persistence()
    {
        var target = new FakeTarget(); var definition = Definition(target); int dirtied = 0;
        using (var project = new ReClassNetProject())
        {
            var repository = new PatchRepository(project, () => dirtied++); repository.Upsert(definition);
            var loaded = new PatchRepository(project);
            Check(loaded.Definitions.Count == 1 && loaded.Definitions[0].SessionId == Guid.Empty && target.Writes == 0 && dirtied == 1, "Loading definitions touched the process or persisted live identity.");
            var opaque = new XElement("patches", new XAttribute("version", "2"), new XElement("future", "keep"));
            project.CustomData.SetXElement(PatchRepository.CustomDataKey, opaque); string original = project.CustomData.GetString(PatchRepository.CustomDataKey);
            loaded.Load(); loaded.Save();
            Check(loaded.IsReadOnly && project.CustomData.GetString(PatchRepository.CustomDataKey) == original, "Unknown schema was overwritten.");
        }
        var resolver = new PatchTargetResolver();
        definition.LocatorKind = PatchLocatorKind.ModulePattern; definition.ModuleName = "fixture.dll"; definition.ImageSha256 = resolver.Fingerprint(typeof(PatchPlanner).Assembly.Location); definition.Pattern = "AB CD EF";
        target.Put(0x1000 + 65535, new byte[] { 0xAB, 0xCD, 0xEF });
        var resolved = resolver.Resolve(definition, target, CancellationToken.None);
        Check(resolved.Status == PatchResolutionStatus.Resolved && resolved.Address == 0x1000 + 65535, "Cross-chunk unique pattern was lost.");
        target.Put(0x1000 + (ulong)target.Memory.Length - 3, new byte[] { 0xAB, 0xCD, 0xEF });
        Check(resolver.Resolve(definition, target, CancellationToken.None).Status == PatchResolutionStatus.MultipleMatches, "The final candidate was excluded or ambiguity ignored.");
        target.Put(0x1000 + (ulong)target.Memory.Length - 3, new byte[] { 0x90, 0x90, 0x90 }); target.FailReadAt = 0x1000 + 131072;
        Check(resolver.Resolve(definition, target, CancellationToken.None).Status == PatchResolutionStatus.IncompleteScan, "An incomplete scan established uniqueness.");
        definition.ImageSha256 = new string('0', 64);
        Check(resolver.Resolve(definition, target, CancellationToken.None).Status == PatchResolutionStatus.IdentityMismatch, "A changed module identity was accepted.");
    }
    private sealed class FakeTarget : IPatchTarget
    {
        public readonly byte[] Memory = Enumerable.Repeat((byte)0x90, 131080).ToArray();
        private readonly Dictionary<ulong, byte[]> allocations = new Dictionary<ulong, byte[]>();
        public readonly List<ulong> Ips = new List<ulong>();
        public int Writes, FailWrites, Frees, RxTransitions; public bool KeptStopped; public string ExternalOverlap; public ulong? FailReadAt;
        public ulong NextAllocation = 0x200000;
        public FakeTarget() { Put(0x1000, new byte[] { 0xFF, 0x08 }); }
        public Guid SessionId { get; } = Guid.NewGuid(); public string ProviderIdentity => "focused-fake"; public string Platform => "Windows";
        public bool IsAlive => true; public bool SupportsCodeTransactions => true; public bool SupportsAllocation => true;
        public IReadOnlyList<ulong> StoppedInstructionPointers => Ips; public IReadOnlyList<ulong> KnownIncomingTargets => new ulong[0];
        public IReadOnlyList<PatchModule> Modules => new[] { new PatchModule { Name = "fixture.dll", Path = typeof(PatchPlanner).Assembly.Location, BaseAddress = 0x1000, Size = (ulong)Memory.Length, InstanceId = SessionId.ToString(), ExecutableRanges = new[] { new PatchRange { Address = 0x1000, Length = (ulong)Memory.Length } } } };
        public bool IsModuleCurrent(PatchModule module) => module == null || module.InstanceId == SessionId.ToString();
        public string FindExternalOverlap(ulong address, int length) => ExternalOverlap;
        public Task<IPatchStopLease> StopAsync(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult<IPatchStopLease>(new Lease(this)); }
        private sealed class Lease : IPatchStopLease { private readonly FakeTarget target; public Lease(FakeTarget target) { this.target = target; } public void KeepStopped(string reason) { target.KeptStopped = true; } public void RecoveryCompleted() { target.KeptStopped = false; } public void Dispose() { } }
        public byte[] ReadExact(ulong address, int length)
        {
            if (FailReadAt.HasValue && address >= FailReadAt.Value) throw new IOException("Controlled unreadable region.");
            var location = Locate(address, length); var bytes = new byte[length]; Buffer.BlockCopy(location.Item1, location.Item2, bytes, 0, length); return bytes;
        }
        public void Put(ulong address, byte[] bytes) { var location = Locate(address, bytes.Length); Buffer.BlockCopy(bytes, 0, location.Item1, location.Item2, bytes.Length); }
        private Tuple<byte[], int> Locate(ulong address, int length)
        {
            if (address >= 0x1000 && address - 0x1000 + (ulong)length <= (ulong)Memory.Length) return Tuple.Create(Memory, (int)(address - 0x1000));
            foreach (var allocation in allocations) if (address >= allocation.Key && address - allocation.Key + (ulong)length <= (ulong)allocation.Value.Length) return Tuple.Create(allocation.Value, (int)(address - allocation.Key));
            throw new IOException("Unmapped fake address.");
        }
        public PatchWriteResult WriteCode(ulong address, byte[] bytes)
        {
            Writes++;
            if (FailWrites > 0) { FailWrites--; if (bytes.Length > 0) Put(address, bytes.Take(1).ToArray()); return new PatchWriteResult { Error = "Controlled partial write." }; }
            Put(address, bytes); return new PatchWriteResult { Success = true };
        }
        public PatchAllocation Allocate(ulong nearAddress, int size)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            ulong address = Align(NextAllocation);
            while (true)
            {
                ulong end = checked(address + (ulong)size);
                ulong memoryEnd = checked(0x1000UL + (ulong)Memory.Length);
                if (address < memoryEnd && 0x1000UL < end) { address = Align(memoryEnd); continue; }
                var overlapping = allocations.Where(a => address < checked(a.Key + (ulong)a.Value.Length) && a.Key < end).OrderBy(a => a.Key).FirstOrDefault();
                if (overlapping.Value == null) break;
                // Published and retired mappings remain in the ledger, even when a scenario
                // asks to search again from an earlier near-allocation address.
                address = Align(checked(overlapping.Key + (ulong)overlapping.Value.Length));
            }
            allocations.Add(address, new byte[size]);
            NextAllocation = Align(checked(address + (ulong)size));
            return new PatchAllocation { Address = address, Size = size };
        }
        private static ulong Align(ulong address) => checked(address + 4095UL) & ~4095UL;
        public PatchWriteResult ProtectExecutable(PatchAllocation allocation) { RxTransitions++; return new PatchWriteResult { Success = true }; }
        public void Free(PatchAllocation allocation) { Check(!allocation.Published, "Attempted to free published memory."); Check(allocations.Remove(allocation.Address), "Attempted to free an unknown or already released allocation."); Frees++; }
    }
}
