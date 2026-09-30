using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReClassNET.AssemblyEditing;

namespace ReClassNET.Patching
{
    public enum PatchMode { InPlace, Hook }
    public enum HookSemanticMode { ReplaceSelection, InsertBefore, InsertAfter }
    public enum PatchSourceKind { Assembly, Bytes }
    public enum PatchLocatorKind { SessionAddress, ModuleOffset, ModulePattern }
    public enum BoundarySource { Uncertain, Execution, VerifiedChain, ExplicitOrigin }
    public enum PatchConflictChoice { Cancel, ForceRestore, Abandon }
    public enum PatchStatus { Draft, Previewed, Prepared, Applying, Active, Inactive, Conflict, Unsupported, Failed, RecoveryRequired, TargetExited, Cancelled }

    public sealed class PatchDefinition
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Revision { get; set; } = 1;
        public string Name { get; set; } = "Patch";
        public PatchMode Mode { get; set; }
        public HookSemanticMode HookMode { get; set; }
        public PatchSourceKind SourceKind { get; set; }
        public string Platform { get; set; }
        public string Architecture { get; set; } = "x64";
        public string ModuleName { get; set; }
        public string ImageSha256 { get; set; }
        public PatchLocatorKind LocatorKind { get; set; }
        public ulong Offset { get; set; }
        public string Pattern { get; set; }
        public long EntryOffset { get; set; }
        public ulong SessionAddress { get; set; }
        public Guid SessionId { get; set; }
        public int SelectionLength { get; set; }
        public byte[] ExpectedBytes { get; set; }
        public string Assembly { get; set; }
        public byte[] ReplacementBytes { get; set; }
        public string Notes { get; set; }
        public BoundarySource Boundary { get; set; }
        public PatchDefinition Clone()
        {
            var copy = (PatchDefinition)MemberwiseClone();
            copy.ExpectedBytes = ExpectedBytes == null ? null : (byte[])ExpectedBytes.Clone();
            copy.ReplacementBytes = ReplacementBytes == null ? null : (byte[])ReplacementBytes.Clone();
            return copy;
        }
    }

    public sealed class PatchRange
    {
        public ulong Address { get; set; }
        public ulong Length { get; set; }
        public ulong End => checked(Address + Length);
        public bool Contains(ulong value) => value >= Address && value < End;
        public bool Overlaps(ulong address, int length) => address < End && Address < checked(address + (ulong)length);
    }
    public sealed class PatchModule
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public string InstanceId { get; set; }
        public ulong BaseAddress { get; set; }
        public ulong Size { get; set; }
        public IReadOnlyList<PatchRange> ExecutableRanges { get; set; } = new PatchRange[0];
    }
    public sealed class PatchAllocation
    {
        public ulong Address { get; set; }
        public int Size { get; set; }
        public bool Published { get; internal set; }
        public bool Retired { get; internal set; }
    }
    public sealed class PatchWriteResult
    {
        public bool Success { get; set; }
        public bool RecoveryRequired { get; set; }
        public string Error { get; set; }
        public int NativeError { get; set; }
    }
    public interface IPatchStopLease : IDisposable
    {
        void KeepStopped(string reason);
        void RecoveryCompleted();
    }
    // Implementations marshal these operations through the owning DebugSession worker.
    // WriteCode must restore every original protection and synchronize instruction execution.
    public interface IPatchTarget
    {
        Guid SessionId { get; }
        string ProviderIdentity { get; }
        string Platform { get; }
        bool IsAlive { get; }
        bool SupportsCodeTransactions { get; }
        bool SupportsAllocation { get; }
        IReadOnlyList<PatchModule> Modules { get; }
        IReadOnlyList<ulong> StoppedInstructionPointers { get; }
        IReadOnlyList<ulong> KnownIncomingTargets { get; }
        Task<IPatchStopLease> StopAsync(CancellationToken cancellation);
        byte[] ReadExact(ulong address, int length);
        PatchWriteResult WriteCode(ulong address, byte[] bytes);
        PatchAllocation Allocate(ulong nearAddress, int size);
        PatchWriteResult ProtectExecutable(PatchAllocation allocation);
        void Free(PatchAllocation allocation);
        bool IsModuleCurrent(PatchModule module);
        string FindExternalOverlap(ulong address, int length);
    }
    public sealed class PatchPreview
    {
        public PatchDefinition Definition { get; internal set; }
        public Guid SessionId { get; internal set; }
        public string ProviderIdentity { get; internal set; }
        public PatchModule Module { get; internal set; }
        public ulong Address { get; internal set; }
        public byte[] OriginalBytes { get; internal set; }
        public byte[] ReplacementBytes { get; internal set; }
        public int PaddingLength { get; internal set; }
        public IReadOnlyList<InstructionRecord> Instructions { get; internal set; }
        public PatchStatus Status { get; internal set; }
        public string Message { get; internal set; }
        public bool CanApply => Status == PatchStatus.Previewed && Definition.Mode == PatchMode.InPlace;
    }
    public sealed class PreparedHook
    {
        public Guid Id { get; } = Guid.NewGuid();
        public PatchPreview Preview { get; internal set; }
        public PatchAllocation Allocation { get; internal set; }
        public byte[] Code { get; internal set; }
        public ulong UserBodyOrigin { get; internal set; }
        public byte[] UserBodyBytes { get; internal set; }
        public IReadOnlyList<InstructionRecord> UserBodyInstructions { get; internal set; }
        public byte[] EntryBytes { get; internal set; }
        public ulong ReturnAddress { get; internal set; }
        public int JumpLength { get; internal set; }
        public IReadOnlyList<InstructionRecord> DisplacedInstructions { get; internal set; }
        public IReadOnlyList<RelocationResult> Relocations { get; internal set; }
        public IReadOnlyList<HookInstructionMapping> InstructionMappings { get; internal set; }
        public bool Published => Allocation != null && Allocation.Published;
    }
    public sealed class HookInstructionMapping
    {
        public ulong OriginalAddress { get; internal set; }
        public ulong? RelocatedAddress { get; internal set; }
        public bool Rewritten => !RelocatedAddress.HasValue;
    }
    internal sealed class PatchPreparationCleanupException : Exception
    {
        public PatchAllocation Allocation { get; }
        public PatchPreparationCleanupException(PatchAllocation allocation, string message, Exception inner) : base(message, inner) { Allocation = allocation; }
    }
    public sealed class ActivePatch
    {
        public Guid Id { get; internal set; }
        public PatchPreview Preview { get; internal set; }
        public byte[] InstalledBytes { get; internal set; }
        public PatchAllocation Allocation { get; internal set; }
        public PatchStatus Status { get; internal set; }
        public string Message { get; internal set; }
    }
    public sealed class PatchResult
    {
        public PatchStatus Status { get; internal set; }
        public string Message { get; internal set; }
        public bool Recovered { get; internal set; }
        public ActivePatch Patch { get; internal set; }
        public IReadOnlyList<PatchResult> Failures { get; internal set; } = new PatchResult[0];
        public bool Success => Status == PatchStatus.Active || Status == PatchStatus.Inactive || Status == PatchStatus.TargetExited;
        internal static PatchResult Fail(PatchStatus status, string message) => new PatchResult { Status = status, Message = message };
    }
    internal static class PatchBytes
    {
        public static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        public static ulong AddSigned(ulong address, long offset)
        {
            return offset >= 0 ? checked(address + (ulong)offset) : checked(address - ((ulong)(-(offset + 1)) + 1));
        }
    }
}
