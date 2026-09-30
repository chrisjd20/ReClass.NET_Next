using System;
using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace ReClassNET.AssemblyEditing
{
	public enum InstructionBoundaryStatus { FromSuppliedOrigin, Invalid, Incomplete }

	public sealed class InstructionRecord
	{
		public ulong Address { get; internal set; }
		public byte[] Bytes { get; internal set; }
		public int Length => Bytes.Length;
		public Instruction Instruction { get; internal set; }
		public string Text { get; internal set; }
		public bool IsValid => !Instruction.IsInvalid;
		public FlowControl FlowControl => Instruction.FlowControl;
		// This means decoding began at the caller's origin, not that a backward search proved a boundary.
		public InstructionBoundaryStatus BoundaryStatus { get; internal set; }
		public IReadOnlyList<InstructionOperand> Operands { get; internal set; }
		public IReadOnlyList<UsedRegister> UsedRegisters { get; internal set; }
		public IReadOnlyList<UsedMemory> UsedMemory { get; internal set; }
	}

	public sealed class InstructionOperand
	{
		public int Index { get; internal set; }
		public OpKind Kind { get; internal set; }
		public OpAccess Access { get; internal set; }
		public string Text { get; internal set; }
		public int WidthBits { get; internal set; }
	}

	public sealed class DecodeResult
	{
		public IReadOnlyList<InstructionRecord> Instructions { get; internal set; } = new InstructionRecord[0];
		public bool Success { get; internal set; }
		public string Error { get; internal set; }
		public int BytesConsumed { get; internal set; }
	}

	public sealed class ResolvedMemoryOperand
	{
		public UsedMemory Memory { get; internal set; }
		public bool Available { get; internal set; }
		public ulong Address { get; internal set; }
		public int WidthBytes => Memory.MemorySize.GetSize();
		public string Reason { get; internal set; }
	}

	public sealed class InstructionExplanation
	{
		public string Text { get; internal set; }
		public bool HasTemplate { get; internal set; }
		public IReadOnlyList<ResolvedMemoryOperand> MemoryOperands { get; internal set; }
		public IReadOnlyList<UsedRegister> Registers { get; internal set; }
		public RflagsBits FlagsRead { get; internal set; }
		public RflagsBits FlagsModified { get; internal set; }
	}

	public sealed class RelocationResult
	{
		public bool Success { get; internal set; }
		public string Error { get; internal set; }
		public byte[] Bytes { get; internal set; } = new byte[0];
		public uint[] NewInstructionOffsets { get; internal set; } = new uint[0];
		public ulong[] OriginalAddresses { get; internal set; } = new ulong[0];
	}

	public sealed class InstructionService
	{
		public const int MaxBytes = 65536;

		private static NasmFormatter CreateFormatter()
		{
			var formatter = new NasmFormatter();
			formatter.Options.FirstOperandCharIndex = 0;
			formatter.Options.HexPrefix = "0x";
			formatter.Options.HexSuffix = null;
			formatter.Options.RipRelativeAddresses = false;
			formatter.Options.UsePseudoOps = false;
			return formatter;
		}

		public DecodeResult Decode(byte[] bytes, ulong origin, int bitness = 64)
		{
			if (bytes == null) throw new ArgumentNullException(nameof(bytes));
			if (bitness != 64) throw new ArgumentOutOfRangeException(nameof(bitness), "Only x64 targets are supported.");
			var result = new DecodeResult();
			if (bytes.Length > MaxBytes) { result.Error = "Instruction input exceeds 64 KiB."; return result; }
			if ((ulong)bytes.Length > ulong.MaxValue - origin) { result.Error = "Instruction address range overflows."; return result; }
			var records = new List<InstructionRecord>();
			var reader = new ByteArrayCodeReader(bytes);
			var decoder = Decoder.Create(bitness, reader);
			decoder.IP = origin;
			var formatter = CreateFormatter();
			var output = new StringOutput();
			var factory = new InstructionInfoFactory();
			int offset = 0;
			while (offset < bytes.Length)
			{
				var instruction = decoder.Decode();
				int length = instruction.Length;
				if (length <= 0 || length > bytes.Length - offset) { result.Error = "Incomplete instruction at end of input."; break; }
				var exact = new byte[length];
				Buffer.BlockCopy(bytes, offset, exact, 0, length);
				var info = factory.GetInfo(instruction);
				var operands = new List<InstructionOperand>();
				formatter.Format(instruction, output);
				string text = output.ToStringAndReset();
				for (int i = 0; i < instruction.OpCount; ++i)
				{
					var kind = instruction.GetOpKind(i);
					int formattedIndex = formatter.GetFormatterOperand(instruction, i);
					string operandText;
					if (formattedIndex >= 0) { formatter.FormatOperand(instruction, output, formattedIndex); operandText = output.ToStringAndReset(); }
					else operandText = kind == OpKind.Register ? instruction.GetOpRegister(i).ToString().ToLowerInvariant() : "implicit " + kind;
					int width = kind == OpKind.Register ? instruction.GetOpRegister(i).GetSize() * 8 : kind.ToString().StartsWith("Memory", StringComparison.Ordinal) ? instruction.MemorySize.GetSize() * 8 : 0;
					operands.Add(new InstructionOperand { Index = i, Kind = kind, Access = info.GetOpAccess(i), Text = operandText, WidthBits = width });
				}
				var status = !instruction.IsInvalid ? InstructionBoundaryStatus.FromSuppliedOrigin : decoder.LastError == DecoderError.NoMoreBytes ? InstructionBoundaryStatus.Incomplete : InstructionBoundaryStatus.Invalid;
				records.Add(new InstructionRecord { Address = origin + (ulong)offset, Bytes = exact, Instruction = instruction, Text = text,
					BoundaryStatus = status, Operands = operands.AsReadOnly(), UsedRegisters = info.GetUsedRegisters().ToArray(), UsedMemory = info.GetUsedMemory().ToArray() });
				offset += length;
				if (instruction.IsInvalid) { result.Error = status == InstructionBoundaryStatus.Incomplete ? "Incomplete instruction at end of input." : "Invalid instruction at 0x" + instruction.IP.ToString("X") + "."; break; }
			}
			result.Instructions = records.AsReadOnly();
			result.BytesConsumed = offset;
			result.Success = result.Error == null && offset == bytes.Length;
			return result;
		}

		public IReadOnlyList<ResolvedMemoryOperand> ResolveMemoryAddresses(InstructionRecord record, IDictionary<string, ulong> registers,
			ulong? fsBase = null, ulong? gsBase = null, bool beforeInstruction = true)
		{
			if (record == null) throw new ArgumentNullException(nameof(record));
			var resolved = new List<ResolvedMemoryOperand>();
			var values = registers == null ? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, ulong>(registers, StringComparer.OrdinalIgnoreCase);
			var provider = new RegisterValues(values, fsBase, gsBase);
			bool complex = record.Instruction.IsStringInstruction || record.Instruction.IsVsib || record.Instruction.IsSaveRestoreInstruction || record.Instruction.Mnemonic == Mnemonic.Enter;
			if (complex && record.UsedMemory.Count == 0) {
				resolved.Add(new ResolvedMemoryOperand { Reason = "String, vector-indexed, state-save and complex multi-address operands are unavailable." });
				return resolved.AsReadOnly();
			}
			foreach (var memory in record.UsedMemory)
			{
				var item = new ResolvedMemoryOperand { Memory = memory };
				if (!beforeInstruction) item.Reason = "Address requires a register snapshot from before execution.";
				else if (complex) item.Reason = "String, vector-indexed, state-save and complex multi-address operands are unavailable.";
				else if (memory.VsibSize != 0) item.Reason = "Vector-indexed memory addresses are unavailable.";
				else if (record.Instruction.HasRepPrefix || record.Instruction.HasRepnePrefix) item.Reason = "Repeated instructions may access multiple addresses; one scalar address is insufficient.";
				else if (memory.Access == OpAccess.NoMemAccess) item.Reason = "This operand computes an address without accessing memory.";
				else
				{
					ulong address;
					item.Available = memory.TryGetVirtualAddress(0, provider, out address);
					item.Address = address;
					if (!item.Available) item.Reason = "A required register or FS/GS base is unavailable.";
					else if (memory.MemorySize.GetSize() > 0 && (ulong)memory.MemorySize.GetSize() - 1 > ulong.MaxValue - address) { item.Available = false; item.Reason = "Operand address range overflows."; }
				}
				resolved.Add(item);
			}
			return resolved.AsReadOnly();
		}

		public InstructionExplanation Explain(InstructionRecord record, IDictionary<string, ulong> registers = null,
			IDictionary<ulong, string> userLabels = null, ulong? fsBase = null, ulong? gsBase = null, bool beforeInstruction = true)
		{
			if (record == null) throw new ArgumentNullException(nameof(record));
			var instruction = record.Instruction;
			string destination = DescribeOperand(record, 0), source = DescribeOperand(record, 1), text;
			bool template = true;
			switch (instruction.Mnemonic)
			{
				case Mnemonic.Mov: text = "Copy " + source + " into " + destination + "."; break;
				case Mnemonic.Lea: text = "Calculate the effective address " + (record.Operands.Count > 1 ? record.Operands[1].Text : "of the source") + " and store it in " + destination + "; memory is not read."; break;
				case Mnemonic.Inc: text = "Add 1 to " + destination + "."; break;
				case Mnemonic.Dec: text = "Subtract 1 from " + destination + "."; break;
				case Mnemonic.Add: text = "Add " + source + " to " + destination + "."; break;
				case Mnemonic.Sub: text = "Subtract " + source + " from " + destination + "."; break;
				case Mnemonic.Cmp: text = "Compare " + destination + " with " + source + " by subtraction; update flags without storing the result."; break;
				case Mnemonic.Test: text = "Test the bitwise AND of " + destination + " and " + source + "; update flags without storing the result."; break;
				case Mnemonic.Nop: text = "Continue to the next instruction without changing ordinary registers or flags."; break;
				default:
					if (instruction.FlowControl == FlowControl.ConditionalBranch) text = "Branch to " + destination + " when the " + instruction.Mnemonic.ToString().ToLowerInvariant() + " condition is satisfied; otherwise continue to the next instruction.";
					else if (instruction.Mnemonic == Mnemonic.Jmp) text = "Jump to " + destination + ".";
					else if (instruction.Mnemonic == Mnemonic.Call) text = "Push the return address onto the stack and call " + destination + ".";
					else if (instruction.Mnemonic == Mnemonic.Ret) text = "Pop the return address from the stack and return to it; adjust the stack pointer by " + instruction.StackPointerIncrement + " bytes.";
					else { text = "Plain-language explanation unavailable."; template = false; }
					break;
			}
			if (!record.IsValid) { text = "Instruction bytes are invalid or incomplete."; template = false; }
			if (instruction.RflagsRead != RflagsBits.None) text += " Flags read: " + instruction.RflagsRead + ".";
			if (instruction.RflagsModified != RflagsBits.None) text += " Flags modified: " + instruction.RflagsModified + ".";
			var memory = ResolveMemoryAddresses(record, registers, fsBase, gsBase, beforeInstruction);
			foreach (var operand in memory)
			{
				if (operand.Available) {
					text += " Captured address: 0x" + operand.Address.ToString("X") + ".";
					string label;
					if (userLabels != null && userLabels.TryGetValue(operand.Address, out label)) text += " This corresponds to your label \"" + label + "\".";
				}
			}
			return new InstructionExplanation { Text = text, HasTemplate = template, MemoryOperands = memory, Registers = record.UsedRegisters, FlagsRead = instruction.RflagsRead, FlagsModified = instruction.RflagsModified };
		}

		private static string DescribeOperand(InstructionRecord record, int index)
		{
			if (index >= record.Operands.Count) return "the operand";
			var operand = record.Operands[index];
			if (operand.Kind == OpKind.Memory) return "the " + operand.WidthBits + "-bit value stored at " + operand.Text;
			if (operand.Kind == OpKind.Register) return "the " + operand.WidthBits + "-bit register " + operand.Text.ToUpperInvariant();
			return operand.Text;
		}

		public RelocationResult Relocate(IEnumerable<InstructionRecord> records, ulong origin, ulong? returnAddress = null)
		{
			if (records == null) throw new ArgumentNullException(nameof(records));
			var list = records.ToList();
			var result = new RelocationResult { OriginalAddresses = list.Select(r => r.Address).ToArray() };
			if (list.Any(r => !r.IsValid)) { result.Error = "Invalid or incomplete instructions cannot be relocated."; return result; }
			if (list.Sum(r => (long)r.Length) > MaxBytes) { result.Error = "Relocation exceeds 64 KiB."; return result; }
			var instructions = list.Select(r => r.Instruction).ToList();
			if (returnAddress.HasValue) instructions.Add(Instruction.CreateBranch(Code.Jmp_rel32_64, returnAddress.Value));
			var writer = new BoundedCodeWriter();
			try
			{
				string error;
				BlockEncoderResult encoded;
				if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, origin), out error, out encoded, BlockEncoderOptions.ReturnNewInstructionOffsets | BlockEncoderOptions.ReturnRelocInfos)) { result.Error = error; return result; }
				result.Bytes = writer.Bytes.ToArray();
				if ((ulong)result.Bytes.Length > ulong.MaxValue - origin) { result.Error = "Relocated address range overflows."; result.Bytes = new byte[0]; return result; }
				result.NewInstructionOffsets = encoded.NewInstructionOffsets.Take(list.Count).ToArray();
				result.Success = true;
			}
			catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is OverflowException) { result.Error = ex.Message; }
			return result;
		}

		private sealed class BoundedCodeWriter : CodeWriter
		{
			public readonly List<byte> Bytes = new List<byte>();
			public override void WriteByte(byte value) { if (Bytes.Count >= MaxBytes) throw new InvalidOperationException("Relocation exceeds 64 KiB."); Bytes.Add(value); }
		}

		private sealed class RegisterValues : IVATryGetRegisterValueProvider
		{
			private readonly IDictionary<string, ulong> registers;
			private readonly ulong? fsBase, gsBase;
			public RegisterValues(IDictionary<string, ulong> registers, ulong? fsBase, ulong? gsBase) { this.registers = registers; this.fsBase = fsBase; this.gsBase = gsBase; }
			public bool TryGetRegisterValue(Register register, int elementIndex, int elementSize, out ulong value)
			{
				value = 0;
				if (elementSize != 0) return false;
				if (register == Register.FS || register == Register.GS) {
					var segment = register == Register.FS ? fsBase : gsBase;
					if (segment.HasValue) { value = segment.Value; return true; }
					return registers.TryGetValue(register == Register.FS ? "fsbase" : "gsbase", out value);
				}
				if (register == Register.None || register == Register.CS || register == Register.DS || register == Register.ES || register == Register.SS) return true;
				bool direct = registers.TryGetValue(register.ToString(), out value);
				if (!direct && !registers.TryGetValue(register.GetFullRegister().ToString(), out value)) return false;
				int size = register.GetSize();
				if (!direct && (register == Register.AH || register == Register.BH || register == Register.CH || register == Register.DH)) value >>= 8;
				if (size > 0 && size < 8) value &= (1UL << (size * 8)) - 1;
				return true;
			}
		}
	}
}
