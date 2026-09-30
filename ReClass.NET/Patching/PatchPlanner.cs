using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iced.Intel;
using ReClassNET.AssemblyEditing;

namespace ReClassNET.Patching
{
    public sealed class PatchPlanner
    {
        public const int HookReservationSize = 128 * 1024;
        public const int MaximumBodySize = 64 * 1024;
        private readonly InstructionService instructions;
        private readonly AssemblyService assembler;
        private readonly PatchTargetResolver resolver;
        public PatchPlanner(InstructionService instructions, AssemblyService assembler, PatchTargetResolver resolver = null)
        {
            this.instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
            this.assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
            this.resolver = resolver ?? new PatchTargetResolver();
        }
        public async Task<PatchPreview> PreviewAsync(PatchDefinition definition, IPatchTarget target, CancellationToken cancellation = default(CancellationToken))
        {
            var preview = new PatchPreview { Definition = definition.Clone(), SessionId = target.SessionId, ProviderIdentity = target.ProviderIdentity, Status = PatchStatus.Draft };
            definition = preview.Definition;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!target.IsAlive) return Reject(preview, PatchStatus.TargetExited, "The target exited.");
                if (!target.SupportsCodeTransactions) return Reject(preview, PatchStatus.Unsupported, "The selected provider cannot perform coordinated code transactions.");
                if (definition.Boundary == BoundarySource.Uncertain) return Reject(preview, PatchStatus.Unsupported, "An established instruction boundary is required.");
                if (definition.SelectionLength <= 0 || definition.SelectionLength > MaximumBodySize) return Reject(preview, PatchStatus.Unsupported, "Select 1 to 65536 bytes of complete instructions.");
                var resolved = resolver.Resolve(preview.Definition, target, cancellation);
                if (resolved.Status != PatchResolutionStatus.Resolved) return Reject(preview, PatchStatus.Conflict, resolved.Status + ": " + resolved.Message);
                preview.Address = resolved.Address;
                preview.Module = resolved.Module;
                checked { var end = preview.Address + (ulong)definition.SelectionLength; }
                preview.OriginalBytes = Exact(target, preview.Address, definition.SelectionLength);
                if (definition.ExpectedBytes != null && !PatchBytes.Equal(definition.ExpectedBytes, preview.OriginalBytes)) return Reject(preview, PatchStatus.Conflict, "Original bytes differ from the saved selection.");
                preview.Definition.ExpectedBytes = (byte[])preview.OriginalBytes.Clone();
                var decoded = instructions.Decode(preview.OriginalBytes, preview.Address);
                if (!decoded.Success || decoded.BytesConsumed != preview.OriginalBytes.Length) return Reject(preview, PatchStatus.Unsupported, "Selection does not end at a valid instruction boundary. " + decoded.Error);
                preview.Instructions = decoded.Instructions;
                var replacement = await Assemble(preview.Definition, preview.Address, cancellation).ConfigureAwait(false);
                if (replacement.Length > MaximumBodySize) return Reject(preview, PatchStatus.Unsupported, "Replacement exceeds 64 KiB.");
                var replacementDecoded = instructions.Decode(replacement, preview.Address);
                if (replacement.Length != 0 && !replacementDecoded.Success) return Reject(preview, PatchStatus.Unsupported, "Replacement contains an invalid or incomplete instruction. " + replacementDecoded.Error);
                preview.ReplacementBytes = replacement;
                if (definition.Mode == PatchMode.InPlace)
                {
                    if (replacement.Length > preview.OriginalBytes.Length) return Reject(preview, PatchStatus.Unsupported, "Replacement is longer than the selection; select a larger whole span or create a hook.");
                    preview.PaddingLength = preview.OriginalBytes.Length - replacement.Length;
                    var padded = Enumerable.Repeat((byte)0x90, preview.OriginalBytes.Length).ToArray();
                    Buffer.BlockCopy(replacement, 0, padded, 0, replacement.Length);
                    preview.ReplacementBytes = padded;
                }
                else if (!target.SupportsAllocation) return Reject(preview, PatchStatus.Unsupported, "The selected provider cannot allocate and protect hook memory.");
                preview.Status = PatchStatus.Previewed;
                return preview;
            }
            catch (OperationCanceledException) { return Reject(preview, PatchStatus.Cancelled, "Preview cancelled."); }
            catch (Exception e) { return Reject(preview, PatchStatus.Failed, e.Message); }
        }
        public Task<PreparedHook> PrepareHookAsync(PatchPreview preview, IPatchTarget target, PatchManager manager, CancellationToken cancellation = default(CancellationToken))
        {
            return manager.PrepareAsync(preview, () => PrepareCore(preview, target, cancellation), cancellation);
        }
        private async Task<PreparedHook> PrepareCore(PatchPreview source, IPatchTarget target, CancellationToken cancellation)
        {
            if (source.Status != PatchStatus.Previewed || source.Definition.Mode != PatchMode.Hook) throw new InvalidOperationException("A valid hook preview is required.");
            ValidateIdentity(source, target);
            PatchAllocation allocation = null;
            using (var stop = await target.StopAsync(cancellation).ConfigureAwait(false))
            {
                try
                {
                    ValidateIdentity(source, target);
                    if (!PatchBytes.Equal(source.OriginalBytes, Exact(target, source.Address, source.OriginalBytes.Length))) throw new InvalidOperationException("The preview is stale.");
                    allocation = target.Allocate(source.Address, HookReservationSize);
                    if (allocation == null || allocation.Size != HookReservationSize || allocation.Address == 0 || (allocation.Address & 4095) != 0) throw new IOException("Cannot reserve a page-aligned 128 KiB writable hook allocation.");
                    var jump = Jump(source.Address, allocation.Address);
                    var displaced = source.Instructions.ToList();
                    int length = source.OriginalBytes.Length;
                    while (length < jump.Length)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var record = DecodeOne(target, checked(source.Address + (ulong)length));
                        displaced.Add(record);
                        length = checked(length + record.Length);
                    }
                    var originals = Exact(target, source.Address, length);
                    if (!PatchBytes.Equal(originals.Take(source.OriginalBytes.Length).ToArray(), source.OriginalBytes)) throw new InvalidOperationException("Original bytes changed during preparation.");
                    var end = checked(source.Address + (ulong)length);
                    if (target.KnownIncomingTargets.Any(a => a > source.Address && a < end)) throw new InvalidOperationException("A known branch, symbol, or execution entry targets the overwrite interior.");
                    foreach (var record in displaced) ValidateDisplaced(record);
                    if (source.Definition.HookMode != HookSemanticMode.ReplaceSelection && source.Instructions.Any(i => i.FlowControl != FlowControl.Next && i.FlowControl != FlowControl.Call)) throw new InvalidOperationException("Insertion requires a straight-line selected span with fallthrough.");
                    var selectedEnd = checked(source.Address + (ulong)source.OriginalBytes.Length);
                    var preserved = source.Definition.HookMode == HookSemanticMode.ReplaceSelection ? displaced.Where(i => i.Address >= selectedEnd).ToList() : displaced;
                    foreach (var record in preserved)
                    {
                        var ins = record.Instruction;
                        if (ins.Op0Kind == OpKind.NearBranch16 || ins.Op0Kind == OpKind.NearBranch32 || ins.Op0Kind == OpKind.NearBranch64)
                        {
                            ulong branch = ins.NearBranchTarget;
                            if (branch >= source.Address && branch < end && !displaced.Any(i => i.Address == branch)) throw new InvalidOperationException("A branch targets the middle of a displaced instruction.");
                            if (source.Definition.HookMode == HookSemanticMode.ReplaceSelection && branch > source.Address && branch < selectedEnd) throw new InvalidOperationException("A relocated branch targets intentionally replaced instructions.");
                        }
                    }
                    var preparedPreview = new PatchPreview
                    {
                        Definition = source.Definition.Clone(), SessionId = source.SessionId, ProviderIdentity = source.ProviderIdentity,
                        Module = source.Module, Address = source.Address, OriginalBytes = originals, Instructions = source.Instructions,
                        ReplacementBytes = source.ReplacementBytes, Status = PatchStatus.Prepared
                    };
                    var suffix = displaced.Where(i => i.Address >= selectedEnd).ToList();
                    var prefix = source.Definition.HookMode == HookSemanticMode.InsertAfter ? source.Instructions.ToList() : new List<InstructionRecord>();
                    var afterBody = source.Definition.HookMode == HookSemanticMode.InsertBefore ? displaced : suffix;
                    // Encode all preserved segments together, so cross-segment branches retain their mappings.
                    int prefixLength = prefix.Sum(i => i.Length);
                    byte[] body = null, code = null;
                    var relocations = new List<RelocationResult>();
                    var mappings = new List<HookInstructionMapping>();
                    bool stable = false;
                    for (int attempt = 0; attempt < 16; attempt++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ulong bodyOrigin = checked(allocation.Address + (ulong)prefixLength);
                        body = await Assemble(source.Definition, bodyOrigin, cancellation).ConfigureAwait(false);
                        if (body.Length > MaximumBodySize) throw new InvalidOperationException("User hook output exceeds 64 KiB.");
                        var bodyDecode = instructions.Decode(body, bodyOrigin);
                        if (body.Length != 0 && !bodyDecode.Success) throw new InvalidOperationException("Hook body is not completely decoded.");
                        ValidateBody(bodyDecode.Instructions, bodyOrigin, body.Length);
                        var layout = EncodeLayout(prefix, body, afterBody, allocation.Address, source.Address, end, source.Definition.HookMode == HookSemanticMode.ReplaceSelection);
                        if (layout.Item2 != prefixLength) { prefixLength = layout.Item2; continue; }
                        code = layout.Item1;
                        relocations = layout.Item3.ToList();
                        mappings = new List<HookInstructionMapping>();
                        for (int block = 0; block < relocations.Count; block++)
                        {
                            var relocation = relocations[block];
                            ulong blockOrigin = prefix.Count != 0 && block == 0 ? allocation.Address : checked(bodyOrigin + (ulong)body.Length);
                            for (int instruction = 0; instruction < relocation.OriginalAddresses.Length; instruction++)
                            {
                                uint offset = relocation.NewInstructionOffsets[instruction];
                                mappings.Add(new HookInstructionMapping { OriginalAddress = relocation.OriginalAddresses[instruction], RelocatedAddress = offset == uint.MaxValue ? (ulong?)null : checked(blockOrigin + offset) });
                            }
                        }
                        stable = true;
                        break;
                    }
                    if (!stable) throw new InvalidOperationException("Hook layout did not stabilize at its final assembly origin.");
                    if (code.Length > allocation.Size) throw new InvalidOperationException("Generated hook exceeds its 128 KiB reservation.");
                    var entry = Enumerable.Repeat((byte)0x90, length).ToArray();
                    Buffer.BlockCopy(jump, 0, entry, 0, jump.Length);
                    cancellation.ThrowIfCancellationRequested();
                    var write = target.WriteCode(allocation.Address, code);
                    if (!write.Success)
                    {
                        if (write.RecoveryRequired) stop.KeepStopped(write.Error);
                        throw new IOException("Hook code write failed: " + write.Error);
                    }
                    if (!PatchBytes.Equal(code, Exact(target, allocation.Address, code.Length))) throw new IOException("Hook code readback differs.");
                    var protect = target.ProtectExecutable(allocation);
                    if (!protect.Success)
                    {
                        if (protect.RecoveryRequired) stop.KeepStopped(protect.Error);
                        throw new IOException("Cannot make hook executable/readable: " + protect.Error);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    ValidateIdentity(source, target);
                    ulong finalBodyOrigin = checked(allocation.Address + (ulong)prefixLength);
                    return new PreparedHook { Preview = preparedPreview, Allocation = allocation, Code = code, UserBodyOrigin = finalBodyOrigin, UserBodyBytes = body, UserBodyInstructions = instructions.Decode(body, finalBodyOrigin).Instructions, EntryBytes = entry, ReturnAddress = end, JumpLength = jump.Length, DisplacedInstructions = displaced, Relocations = relocations, InstructionMappings = mappings };
                }
                catch (Exception preparationError)
                {
                    if (allocation != null && !allocation.Published && target.IsAlive)
                    {
                        try { target.Free(allocation); }
                        catch (Exception e)
                        {
                            stop.KeepStopped("Unpublished hook cleanup failed: " + e.Message);
                            throw new PatchPreparationCleanupException(allocation, preparationError.Message + "; unpublished hook cleanup failed: " + e.Message, e);
                        }
                    }
                    throw;
                }
            }
        }
        private Tuple<byte[], int, IReadOnlyList<RelocationResult>> EncodeLayout(List<InstructionRecord> prefix, byte[] body, List<InstructionRecord> suffix, ulong origin, ulong selectedEntry, ulong returnAddress, bool replace)
        {
            // Use a synthetic marker at the replaced entry so branches to that entry map to the body.
            var prefixWriter = new ByteWriter();
            var suffixWriter = new ByteWriter();
            var p = prefix.Select(i => i.Instruction).ToList();
            var s = suffix.Select(i => i.Instruction).ToList();
            int pLength = p.Sum(i => i.Length);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                ulong bodyOrigin = checked(origin + (ulong)pLength);
                var blocks = new List<InstructionBlock>();
                prefixWriter = new ByteWriter(); suffixWriter = new ByteWriter();
                if (p.Count != 0)
                {
                    var withBridge = p.ToList();
                    var bridge = Instruction.CreateBranch(Code.Jmp_rel32_64, bodyOrigin);
                    bridge.IP = ulong.MaxValue - 32;
                    withBridge.Add(bridge);
                    blocks.Add(new InstructionBlock(prefixWriter, withBridge, origin));
                }
                // Explicit terminal jumps keep BlockEncoder pointer tables off the fallthrough path.
                {
                    var adjusted = s.Select(ins =>
                    {
                        if (replace && (ins.Op0Kind == OpKind.NearBranch16 || ins.Op0Kind == OpKind.NearBranch32 || ins.Op0Kind == OpKind.NearBranch64) && ins.NearBranchTarget == selectedEntry) ins.NearBranch64 = bodyOrigin;
                        return ins;
                    }).ToList();
                    var continuation = Instruction.CreateBranch(Code.Jmp_rel32_64, returnAddress);
                    continuation.IP = ulong.MaxValue - 16;
                    adjusted.Add(continuation);
                    blocks.Add(new InstructionBlock(suffixWriter, adjusted, checked(bodyOrigin + (ulong)body.Length)));
                }
                BlockEncoderResult[] results = null;
                string error;
                if (blocks.Count != 0 && !BlockEncoder.TryEncode(64, blocks.ToArray(), out error, out results, BlockEncoderOptions.ReturnNewInstructionOffsets)) throw new InvalidOperationException("Displaced relocation failed: " + error);
                int actualPrefixLength = prefixWriter.Bytes.Count;
                if (actualPrefixLength != pLength) { pLength = actualPrefixLength; continue; }
                var relocations = new List<RelocationResult>();
                int resultIndex = 0;
                if (p.Count != 0)
                {
                    relocations.Add(new RelocationResult { Success = true, Bytes = prefixWriter.Bytes.ToArray(), OriginalAddresses = prefix.Select(i => i.Address).ToArray(), NewInstructionOffsets = results[resultIndex++].NewInstructionOffsets.Take(p.Count).ToArray() });
                }
                relocations.Add(new RelocationResult { Success = true, Bytes = suffixWriter.Bytes.ToArray(), OriginalAddresses = suffix.Select(i => i.Address).ToArray(), NewInstructionOffsets = results[resultIndex].NewInstructionOffsets.Take(s.Count).ToArray() });
                return Tuple.Create(prefixWriter.Bytes.Concat(body).Concat(suffixWriter.Bytes).ToArray(), pLength, (IReadOnlyList<RelocationResult>)relocations);
            }
            throw new InvalidOperationException("Displaced layout did not stabilize.");
        }
        private sealed class ByteWriter : CodeWriter
        {
            public List<byte> Bytes { get; } = new List<byte>();
            public override void WriteByte(byte value) { Bytes.Add(value); }
        }
        private InstructionRecord DecodeOne(IPatchTarget target, ulong address)
        {
            for (int n = 1; n <= 15; n++)
            {
                var bytes = Exact(target, address, n);
                var result = instructions.Decode(bytes, address);
                if (result.Success && result.Instructions.Count == 1 && result.Instructions[0].Length == n) return result.Instructions[0];
            }
            throw new InvalidOperationException("The additional hook suffix has an invalid instruction.");
        }
        private async Task<byte[]> Assemble(PatchDefinition definition, ulong origin, CancellationToken cancellation)
        {
            if (definition.SourceKind == PatchSourceKind.Bytes) return definition.ReplacementBytes == null ? new byte[0] : (byte[])definition.ReplacementBytes.Clone();
            var result = await assembler.AssembleAsync(definition.Assembly ?? "", origin, cancellation).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException("Assembly failed: " + string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
            return result.Bytes;
        }
        internal static void ValidateIdentity(PatchPreview preview, IPatchTarget target)
        {
            if (!target.IsAlive) throw new InvalidOperationException("The target exited.");
            if (preview.SessionId != target.SessionId || preview.ProviderIdentity != target.ProviderIdentity) throw new InvalidOperationException("Provider or target session changed; create a new preview.");
            if (preview.Module != null && !target.IsModuleCurrent(preview.Module)) throw new InvalidOperationException("The preview's module instance was unloaded or replaced.");
        }
        private static void ValidateDisplaced(InstructionRecord record)
        {
            var flow = record.FlowControl;
            var mnemonic = record.Instruction.Mnemonic.ToString();
            if (flow == FlowControl.Return || flow == FlowControl.IndirectBranch || flow == FlowControl.IndirectCall || flow == FlowControl.Interrupt || flow == FlowControl.Exception || flow == FlowControl.XbeginXabortXend || mnemonic.StartsWith("Xbegin", StringComparison.OrdinalIgnoreCase) || mnemonic == "Xend" || mnemonic == "Xabort") throw new InvalidOperationException("Unsupported displaced instruction: " + record.Text);
            if (mnemonic.StartsWith("Sys", StringComparison.OrdinalIgnoreCase) || mnemonic.StartsWith("Iret", StringComparison.OrdinalIgnoreCase) || mnemonic.StartsWith("Fstenv", StringComparison.OrdinalIgnoreCase) || mnemonic.StartsWith("Fnstenv", StringComparison.OrdinalIgnoreCase) || mnemonic.StartsWith("Fsave", StringComparison.OrdinalIgnoreCase) || mnemonic.StartsWith("Fnsave", StringComparison.OrdinalIgnoreCase) || mnemonic.StartsWith("Fxsave", StringComparison.OrdinalIgnoreCase) || mnemonic.StartsWith("Xsave", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Instruction saves or depends on execution-position/system state that automatic relocation does not preserve: " + record.Text);
            if (flow == FlowControl.Call && record.Instruction.NearBranchTarget == checked(record.Address + (ulong)record.Length)) throw new InvalidOperationException("Position-discovery call cannot be relocated automatically.");
        }
        private static void ValidateBody(IReadOnlyList<InstructionRecord> body, ulong origin, int length)
        {
            ulong end = checked(origin + (ulong)length);
            foreach (var record in body)
            {
                ValidateDisplaced(record);
                if (record.FlowControl == FlowControl.UnconditionalBranch || record.FlowControl == FlowControl.ConditionalBranch)
                {
                    ulong dest = record.Instruction.NearBranchTarget;
                    if (dest < origin || dest > end || (dest != end && !body.Any(i => i.Address == dest))) throw new InvalidOperationException("Hook body branch must target a body instruction or its generated continuation.");
                    if (dest <= record.Address) throw new InvalidOperationException("Automatic hook bodies require forward control flow that reaches the continuation.");
                }
            }
        }
        internal static byte[] Jump(ulong from, ulong to)
        {
            ulong next = checked(from + 5);
            if ((to >= next && to - next <= int.MaxValue) || (to < next && next - to <= 2147483648UL))
            {
                long delta = to >= next ? (long)(to - next) : -(long)(next - to);
                return new byte[] { 0xE9 }.Concat(BitConverter.GetBytes((int)delta)).ToArray();
            }
            return new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }.Concat(BitConverter.GetBytes(to)).ToArray();
        }
        internal static byte[] Exact(IPatchTarget target, ulong address, int length)
        {
            checked { var end = address + (ulong)length; }
            var bytes = target.ReadExact(address, length);
            if (bytes == null || bytes.Length != length) throw new IOException("Cannot read the complete instruction span.");
            return bytes;
        }
        private static PatchPreview Reject(PatchPreview preview, PatchStatus status, string message)
        {
            preview.Status = status;
            preview.Message = message;
            return preview;
        }
    }
}
