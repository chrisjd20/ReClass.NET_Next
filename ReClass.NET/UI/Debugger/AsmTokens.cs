using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Text.RegularExpressions;
using Iced.Intel;
using ReClassNET.AssemblyEditing;

namespace ReClassNET.UI.Debugger
{
	public enum AsmTokenKind { Mnemonic, Register, Number, Keyword, Punctuation, Text }

	public struct AsmToken
	{
		public string Text;
		public AsmTokenKind Kind;

		public AsmToken(string text, AsmTokenKind kind) { Text = text; Kind = kind; }

		public override string ToString() => Kind + ":" + Text;
	}

	/// <summary>Splits instructions into coloured tokens, formatted exactly like ReClass prints them elsewhere.</summary>
	public static class AsmTokens
	{
		private sealed class Collector : FormatterOutput
		{
			public readonly List<AsmToken> Tokens = new List<AsmToken>();

			public override void Write(string text, FormatterTextKind kind) => Tokens.Add(new AsmToken(text, Map(kind)));
		}

		public static AsmTokenKind Map(FormatterTextKind kind)
		{
			switch (kind)
			{
				case FormatterTextKind.Mnemonic:
				case FormatterTextKind.Prefix:
					return AsmTokenKind.Mnemonic;
				case FormatterTextKind.Register:
					return AsmTokenKind.Register;
				case FormatterTextKind.Number:
				case FormatterTextKind.LabelAddress:
				case FormatterTextKind.FunctionAddress:
					return AsmTokenKind.Number;
				case FormatterTextKind.Keyword:
				case FormatterTextKind.Directive:
				case FormatterTextKind.Decorator:
					return AsmTokenKind.Keyword;
				case FormatterTextKind.Punctuation:
				case FormatterTextKind.Operator:
					return AsmTokenKind.Punctuation;
				default:
					return AsmTokenKind.Text;
			}
		}

		public static List<AsmToken> Tokenize(InstructionRecord record)
		{
			if (record == null) return new List<AsmToken>();
			if (record.Instruction.IsInvalid) return Tokenize(record.Text);
			var output = new Collector();
			InstructionService.CreateFormatter().Format(record.Instruction, output);
			return output.Tokens;
		}

		private static readonly Regex Lexer = new Regex(
			@"(?<space>\s+)|(?<number>(?:0x[0-9a-fA-F]+|[0-9][0-9a-fA-F]*h?)\b)|(?<keyword>\b(?:byte|word|dword|qword|tword|oword|yword|zword|ptr|short|near|far|rel)\b)|(?<register>\b(?:[re]?[abcd]x|[abcd][lh]|[re]?(?:si|di|bp|sp|ip)|(?:si|di|bp|sp)l|r(?:[89]|1[0-5])[dwb]?|[xyz]mm(?:[0-9]|[12][0-9]|3[01])|[c-gs]s|rflags|eflags)\b)|(?<punct>[\[\]\+\-\*,:\(\)])|(?<word>[A-Za-z_.][\w.]*)|(?<other>.)",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		/// <summary>Best-effort colouring of free text such as a line typed into the Assembly pane.</summary>
		public static List<AsmToken> Tokenize(string text)
		{
			var tokens = new List<AsmToken>();
			bool first = true;
			foreach (Match match in Lexer.Matches(text ?? ""))
			{
				AsmTokenKind kind;
				if (match.Groups["space"].Success) kind = AsmTokenKind.Text;
				else if (match.Groups["number"].Success) kind = AsmTokenKind.Number;
				else if (match.Groups["keyword"].Success) kind = AsmTokenKind.Keyword;
				else if (match.Groups["register"].Success) kind = AsmTokenKind.Register;
				else if (match.Groups["punct"].Success) kind = AsmTokenKind.Punctuation;
				else if (match.Groups["word"].Success) kind = first ? AsmTokenKind.Mnemonic : AsmTokenKind.Text;
				else kind = AsmTokenKind.Text;
				if (!match.Groups["space"].Success) first = false;
				tokens.Add(new AsmToken(match.Value, kind));
			}
			return tokens;
		}

		public static Color ColorOf(AsmTokenKind kind)
		{
			switch (kind)
			{
				case AsmTokenKind.Mnemonic: return DebuggerTheme.AsmMnemonic;
				case AsmTokenKind.Register: return DebuggerTheme.AsmRegister;
				case AsmTokenKind.Number: return DebuggerTheme.AsmNumber;
				case AsmTokenKind.Keyword: return DebuggerTheme.AsmKeyword;
				case AsmTokenKind.Punctuation: return DebuggerTheme.AsmPunctuation;
				default: return DebuggerTheme.AsmText;
			}
		}

		/// <summary>
		/// X offsets of each token. Each comes from measuring the whole line up to that token, because GDI adds a
		/// little overhang to every separately measured string; summing per-token widths spreads "[rax]" apart.
		/// </summary>
		public static int[] Offsets(IList<AsmToken> tokens, Func<string, int> measure)
		{
			var offsets = new int[tokens.Count + 1];
			int anchor = measure("x");
			var prefix = new StringBuilder();
			for (int i = 0; i < tokens.Count; ++i)
			{
				prefix.Append(tokens[i].Text);
				// "x" after the prefix keeps trailing spaces measured; its own width is subtracted again.
				offsets[i + 1] = Math.Max(offsets[i], measure(prefix + "x") - anchor);
			}
			return offsets;
		}

		/// <summary>Draws tokens left to right on one line. Returns the x after the last token.</summary>
		public static int Draw(Graphics g, IEnumerable<AsmToken> tokens, Font font, int x, int y, int maxRight = int.MaxValue, float dim = 0f)
		{
			var list = tokens as IList<AsmToken> ?? tokens.ToList();
			var offsets = Offsets(list, text => DebuggerTheme.Measure(text, font).Width);
			for (int i = 0; i < list.Count; ++i)
			{
				var token = list[i];
				if (x + offsets[i + 1] > maxRight) { DebuggerTheme.DrawText(g, "…", font, DebuggerTheme.Muted, x + offsets[i], y); return x + offsets[i]; }
				if (token.Text.Trim().Length == 0) continue;
				var color = ColorOf(token.Kind);
				if (dim > 0) color = DebuggerTheme.Mix(color, DebuggerTheme.Faint, dim);
				DebuggerTheme.DrawText(g, token.Text, font, color, x + offsets[i], y);
			}
			return x + offsets[list.Count];
		}
	}
}
