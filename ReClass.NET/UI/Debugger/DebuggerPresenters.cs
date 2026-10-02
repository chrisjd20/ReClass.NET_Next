using System;
using System.Collections.Generic;
using System.Linq;

namespace ReClassNET.UI.Debugger
{
	// Window-independent logic behind the debugger windows' guidance and visuals, kept pure so it can be unit tested.

	public enum HitKind { Candidate, Confirmed, Unlikely, Observed, Attempted, Unavailable }

	public static class HitKinds
	{
		/// <summary>Classifies a watch row from its attribution text.</summary>
		public static HitKind From(string status, bool confirmed)
		{
			if (confirmed) return HitKind.Confirmed;
			var text = status ?? "";
			if (text.StartsWith("Unlikely", StringComparison.Ordinal)) return HitKind.Unlikely;
			if (text.StartsWith("Observed", StringComparison.Ordinal)) return HitKind.Observed;
			if (text.StartsWith("Attempted", StringComparison.Ordinal)) return HitKind.Attempted;
			if (text.StartsWith("Writer unavailable", StringComparison.Ordinal) || text.StartsWith("Instruction has no memory", StringComparison.Ordinal) || text.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0) return HitKind.Unavailable;
			return HitKind.Candidate;
		}

		public static string Label(HitKind kind)
		{
			switch (kind)
			{
				case HitKind.Confirmed: return "CONFIRMED";
				case HitKind.Unlikely: return "UNLIKELY";
				case HitKind.Observed: return "OBSERVED";
				case HitKind.Attempted: return "ATTEMPTED";
				case HitKind.Unavailable: return "UNAVAILABLE";
				default: return "CANDIDATE";
			}
		}

		public static Severity Tone(HitKind kind)
		{
			switch (kind)
			{
				case HitKind.Confirmed: return Severity.Success;
				case HitKind.Observed: return Severity.Info;
				case HitKind.Unlikely:
				case HitKind.Unavailable: return Severity.Neutral;
				default: return Severity.Attention;
			}
		}
	}

	public sealed class CoachAdvice
	{
		public Severity Severity;
		public string Message;
		/// <summary>The label of the button the player should press next, or null.</summary>
		public string Button;

		public CoachAdvice(Severity severity, string message, string button = null) { Severity = severity; Message = message; Button = button; }
	}

	public sealed class WatchCoachState
	{
		public bool Execution, WriteOnly, Collecting, ConfirmPending, Paused, Detached;
		public int Rows, Confirmed, Unlikely, Candidates;
		public HitKind? Selected;
	}

	/// <summary>Tells a beginner what to do next in the Find writes/accesses window.</summary>
	public static class WatchCoach
	{
		public static CoachAdvice For(WatchCoachState s)
		{
			if (s.Paused) return new CoachAdvice(Severity.Attention, "The game is paused. Step into (F11) runs one instruction and shows the registers; Resume (F5) lets it continue.", "Resume (F5)");
			if (s.Execution)
			{
				if (s.Rows == 0) return s.Collecting
					? new CoachAdvice(Severity.Info, "Waiting for this instruction to run… do the action in the game that uses it.")
					: new CoachAdvice(Severity.Neutral, "Collection stopped. Start watches the instruction again.", "Start");
				return new CoachAdvice(Severity.Info, "Each row is an address this instruction touched. Select one to see its registers; Follow data opens it in a class.");
			}
			string verb = s.WriteOnly ? "write" : "access";
			if (s.Rows == 0) return s.Collecting
				? new CoachAdvice(Severity.Info, "Waiting for the first " + verb + "… do something in the game that " + (s.WriteOnly ? "changes" : "uses") + " this value.")
				: new CoachAdvice(Severity.Neutral, "Collection stopped. Start watches the value again.", "Start");
			if (s.Confirmed > 0)
			{
				if (s.Selected == HitKind.Confirmed) return new CoachAdvice(Severity.Success, "Confirmed! This is the exact instruction. Inspect / edit opens it in the instruction editor.", "Inspect / edit");
				return new CoachAdvice(Severity.Success, "An instruction is confirmed. Select the CONFIRMED row, then Inspect / edit.");
			}
			if (s.ConfirmPending) return new CoachAdvice(Severity.Attention, "Waiting for that instruction to run again… repeat the action in the game. A real instruction turns CONFIRMED.");
			if (s.Selected == HitKind.Unlikely) return new CoachAdvice(Severity.Attention, "This row is marked Unlikely: its memory operand doesn't touch the watched data. Pick another row.");
			int possible = s.Candidates > 0 ? s.Candidates : s.Rows - s.Unlikely;
			if (s.Selected == HitKind.Candidate) return new CoachAdvice(Severity.Info, "Good pick. Confirm next execution checks that this really is the instruction.", "Confirm next execution");
			return new CoachAdvice(Severity.Info, (possible == 1 ? "1 instruction" : possible + " possible instructions") + " found. Select the one that isn't marked Unlikely, then Confirm next execution.");
		}
	}

	public enum PatchStep { Inspect, Edit, Preview, Apply, Restore, Done }

	public sealed class PatchStepState
	{
		public bool OriginalsLoaded, Edited, PreviewCurrent, Active, Restored, HookMode;
	}

	/// <summary>Where the instruction editor's Inspect → Edit → Preview → Apply → Restore flow stands.</summary>
	public static class PatchSteps
	{
		public static readonly string[] Names = { "Inspect", "Edit", "Preview", "Apply", "Restore" };

		public static PatchStep Current(PatchStepState s)
		{
			if (s.Active) return PatchStep.Restore;
			if (s.Restored) return PatchStep.Done;
			if (!s.OriginalsLoaded) return PatchStep.Inspect;
			if (!s.Edited) return PatchStep.Edit;
			if (!s.PreviewCurrent) return PatchStep.Preview;
			return PatchStep.Apply;
		}

		public static string Hint(PatchStepState s)
		{
			switch (Current(s))
			{
				case PatchStep.Inspect: return "Load selection reads the original instruction bytes at this address.";
				case PatchStep.Edit: return "Change the assembly or the hex bytes, or use NOP selection to make the instruction do nothing.";
				case PatchStep.Preview: return s.HookMode
					? "Prepare hook (or Preview) shows exactly what will be written. Nothing changes in the game yet."
					: "Preview shows exactly which bytes will change. Nothing changes in the game yet.";
				case PatchStep.Apply: return "Apply writes the change into the running game.";
				case PatchStep.Restore: return "Patch active. Try it in the game, then Restore original puts the original bytes back.";
				default: return "Original bytes restored. Edit again, or close the editor.";
			}
		}
	}

	public enum ByteCellKind { Same, Changed, Added, Removed, Padding }

	public struct ByteCell
	{
		public byte? Original, Replacement;
		public ByteCellKind Kind;
	}

	public static class ByteDiff
	{
		/// <summary>Aligns the original and replacement bytes column by column. The last <paramref name="padding"/> replacement bytes are NOP padding.</summary>
		public static List<ByteCell> Build(byte[] original, byte[] replacement, int padding)
		{
			original = original ?? new byte[0];
			replacement = replacement ?? new byte[0];
			int padStart = Math.Max(0, replacement.Length - Math.Max(0, padding));
			var cells = new List<ByteCell>();
			for (int i = 0; i < Math.Max(original.Length, replacement.Length); ++i)
			{
				var cell = new ByteCell
				{
					Original = i < original.Length ? original[i] : (byte?)null,
					Replacement = i < replacement.Length ? replacement[i] : (byte?)null
				};
				if (cell.Replacement == null) cell.Kind = ByteCellKind.Removed;
				else if (cell.Original == null) cell.Kind = ByteCellKind.Added;
				else if (i >= padStart && padding > 0) cell.Kind = ByteCellKind.Padding;
				else cell.Kind = cell.Original == cell.Replacement ? ByteCellKind.Same : ByteCellKind.Changed;
				cells.Add(cell);
			}
			return cells;
		}
	}

	public sealed class RegisterView
	{
		public string Name;
		public ulong Value;
		public bool Used, PointsAtWatched, Changed, IsInstructionPointer;
	}

	public static class RegisterHighlights
	{
		private static readonly string[] Order = { "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "rip", "rflags" };

		/// <summary>
		/// Orders registers the usual way and marks the interesting ones: used by the instruction, pointing into the
		/// watched data, or changed since the previous snapshot.
		/// </summary>
		public static List<RegisterView> Build(IDictionary<string, ulong> registers, IEnumerable<string> used, ulong watchedStart, int watchedLength, IDictionary<string, ulong> previous)
		{
			var result = new List<RegisterView>();
			if (registers == null) return result;
			var usedSet = new HashSet<string>((used ?? Enumerable.Empty<string>()).Select(n => n.ToLowerInvariant()));
			var keys = registers.Keys.ToList();
			var ordered = Order.Where(o => keys.Any(k => string.Equals(k, o, StringComparison.OrdinalIgnoreCase)))
				.Select(o => keys.First(k => string.Equals(k, o, StringComparison.OrdinalIgnoreCase)))
				.Concat(keys.Where(k => !Order.Contains(k.ToLowerInvariant())).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
			foreach (var key in ordered)
			{
				var value = registers[key];
				var name = key.ToLowerInvariant();
				ulong before;
				result.Add(new RegisterView
				{
					Name = name,
					Value = value,
					Used = usedSet.Contains(name),
					IsInstructionPointer = name == "rip",
					PointsAtWatched = watchedLength > 0 && watchedStart != 0 && value >= watchedStart && value - watchedStart < (ulong)watchedLength,
					Changed = previous != null && previous.TryGetValue(key, out before) && before != value
				});
			}
			return result;
		}
	}

	public enum SizeFit { Unknown, Exact, Padded, TooLong }

	public static class PatchSize
	{
		public static SizeFit Classify(int original, int replacement)
		{
			if (original <= 0 || replacement < 0) return SizeFit.Unknown;
			if (replacement == original) return SizeFit.Exact;
			return replacement < original ? SizeFit.Padded : SizeFit.TooLong;
		}

		public static string Describe(int original, int replacement)
		{
			switch (Classify(original, replacement))
			{
				case SizeFit.Exact: return replacement + " / " + original + " bytes: fits exactly in place.";
				case SizeFit.Padded:
					int pad = original - replacement;
					return replacement + " / " + original + " bytes: fits, plus " + pad + " NOP" + (pad == 1 ? "" : "s") + " of padding.";
				case SizeFit.TooLong: return replacement + " / " + original + " bytes: too long to patch in place. Click here to switch Patch mode to Hook.";
				default: return "Load a selection to compare sizes.";
			}
		}
	}
}
