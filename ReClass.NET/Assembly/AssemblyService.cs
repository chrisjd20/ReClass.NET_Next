using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Iced.Intel;

namespace ReClassNET.AssemblyEditing
{
	public enum AssemblyDiagnosticSeverity { Warning, Error }
	public sealed class AssemblyDiagnostic
	{
		public int Line { get; internal set; }
		public int Column { get; internal set; }
		public AssemblyDiagnosticSeverity Severity { get; internal set; }
		public string Message { get; internal set; }
		public override string ToString() => (Line > 0 ? "Line " + Line + ": " : "") + Message;
	}
	public sealed class AssemblyResult
	{
		public ulong Origin { get; internal set; }
		public byte[] Bytes { get; internal set; } = new byte[0];
		public DecodeResult Preview { get; internal set; }
		public IReadOnlyList<AssemblyDiagnostic> Diagnostics { get; internal set; } = new AssemblyDiagnostic[0];
		public bool Success { get; internal set; }
		public bool Cancelled { get; internal set; }
		public bool TimedOut { get; internal set; }
	}

	public sealed class AssemblyService
	{
		public const int MaxBytes = 65536;
		public const int TimeoutMilliseconds = 5000;
		private const int MaxDiagnosticChars = 16384;
		private readonly string executable;
		private readonly InstructionService instructions;
		private static readonly Regex Label = new Regex(@"^[A-Za-z_.?][A-Za-z0-9_.$?]*\s*:\s*", RegexOptions.CultureInvariant);
		private static readonly Regex Token = new Regex(@"^[A-Za-z][A-Za-z0-9]*", RegexOptions.CultureInvariant);
		private static readonly Regex Diagnostic = new Regex(@"^.*?:(\d+)(?::(\d+))?:\s*(warning|error|fatal|panic):\s*(.*)$", RegexOptions.CultureInvariant);
		private static readonly HashSet<string> Mnemonics = MakeMnemonics();
		private static readonly HashSet<string> Prefixes = new HashSet<string>(new[] { "lock", "rep", "repe", "repz", "repne", "repnz", "xacquire", "xrelease", "bnd", "notrack", "o16", "o32", "o64", "a16", "a32", "a64", "vex", "vex2", "vex3", "evex", "rex" }, StringComparer.OrdinalIgnoreCase);

		public AssemblyService(InstructionService instructions = null)
		{
			this.instructions = instructions ?? new InstructionService();
			executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", Environment.OSVersion.Platform == PlatformID.Win32NT ? "nasm.exe" : "nasm");
		}

		private static HashSet<string> MakeMnemonics()
		{
			var names = new HashSet<string>(Enum.GetNames(typeof(Mnemonic)), StringComparer.OrdinalIgnoreCase);
			foreach (var alias in new[] { "jz", "jnz", "jc", "jnc", "jna", "jnae", "jnb", "jnbe", "jnge", "jng", "jnle", "jnl", "jpe", "jpo", "setz", "setnz", "setc", "setnc", "setna", "setnae", "setnb", "setnbe", "setnge", "setng", "setnle", "setnl", "setpe", "setpo", "cmovz", "cmovnz", "cmovc", "cmovnc", "cmovna", "cmovnae", "cmovnb", "cmovnbe", "cmovnge", "cmovng", "cmovnle", "cmovnl", "cmovpe", "cmovpo", "sal", "retn" }) names.Add(alias);
			names.Remove("INVALID");
			foreach (var directive in new[] { "db", "dw", "dd", "dq", "dt", "do", "dy", "dz", "resb", "resw", "resd", "resq", "rest", "reso", "resy", "resz", "incbin", "equ", "times", "align", "alignb", "bits", "org", "section", "segment", "absolute", "default", "global", "extern", "common", "cpu", "struc", "istruc" }) names.Remove(directive);
			return names;
		}

		private static List<AssemblyDiagnostic> Validate(string source)
		{
			var errors = new List<AssemblyDiagnostic>();
			if (source == null || string.IsNullOrWhiteSpace(source)) { errors.Add(Error("Enter at least one instruction.")); return errors; }
			if (source.Length > MaxBytes || Encoding.UTF8.GetByteCount(source) > MaxBytes) { errors.Add(Error("Assembly source exceeds 64 KiB.")); return errors; }
			var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			for (int i = 0; i < lines.Length; ++i)
			{
				string line = lines[i];
				int comment = line.IndexOf(';');
				if (comment >= 0) line = line.Substring(0, comment);
				line = line.Trim();
				if (line.Length == 0) continue;
				if (line.Any(c => (char.IsControl(c) && c != '\t') || c == '%' || c == '\\' || c == '\'' || c == '"' || c == '`')) { errors.Add(Error("Preprocessor syntax, strings and line continuation are not supported.", i + 1)); continue; }
				var label = Label.Match(line);
				while (label.Success) { line = line.Substring(label.Length).TrimStart(); label = Label.Match(line); }
				if (line.Length == 0) continue;
				var token = Token.Match(line);
				int prefixCount = 0;
				while (token.Success && Prefixes.Contains(token.Value) && prefixCount++ < 8) { line = line.Substring(token.Length).TrimStart(); token = Token.Match(line); }
				if (!token.Success || !Mnemonics.Contains(token.Value)) errors.Add(Error("Only instructions, explicit labels, comments and numeric expressions are supported; directives are disabled.", i + 1));
			}
			return errors;
		}

		public async Task<AssemblyResult> AssembleAsync(string source, ulong origin, CancellationToken cancellation = default(CancellationToken))
		{
			var result = new AssemblyResult { Origin = origin };
			var diagnostics = Validate(source);
			result.Diagnostics = diagnostics.AsReadOnly();
			if (cancellation.IsCancellationRequested) { result.Cancelled = true; return result; }
			if (diagnostics.Count != 0) return result;
			if (!File.Exists(executable)) { diagnostics.Add(Error("Bundled NASM 3.02 was not found at " + executable + ". Install the complete application package.")); return result; }
			string directory = Path.Combine(Path.GetTempPath(), "ReClass-Assembly-" + Guid.NewGuid().ToString("N"));
			Process process = null;
			bool processStarted = false;
			bool exited = true;
			try
			{
				Directory.CreateDirectory(directory);
				// Normalize line endings so diagnostic line numbers subtract exactly the two controlled header lines.
				// -a disables the user-level directive macros; use primitive directives in our controlled header.
				File.WriteAllText(Path.Combine(directory, "input.asm"), "[BITS 64]\n[ORG 0x" + origin.ToString("X", CultureInfo.InvariantCulture) + "]\n" + source.Replace("\r\n", "\n").Replace('\r', '\n') + "\n", new UTF8Encoding(false));
				var start = new ProcessStartInfo {
					FileName = executable, WorkingDirectory = directory,
					Arguments = "-a -f bin -o output.bin -w+error --limit-passes 64 --limit-stalled-passes 16 --limit-eval 1024 --limit-lines 65540 input.asm",
					UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
				};
				start.EnvironmentVariables.Remove("NASMENV");
				start.EnvironmentVariables.Remove("NASM");
				process = new Process { StartInfo = start };
				if (!process.Start()) { diagnostics.Add(Error("Bundled assembler could not be started.")); return result; }
				processStarted = true;
				exited = false;
				Task<string> stderr = ReadBoundedAsync(process.StandardError), stdout = ReadBoundedAsync(process.StandardOutput);
				var stopwatch = Stopwatch.StartNew();
				string output = Path.Combine(directory, "output.bin");
				while (!process.HasExited)
				{
					if (cancellation.IsCancellationRequested) { result.Cancelled = true; break; }
					if (stopwatch.ElapsedMilliseconds >= TimeoutMilliseconds) { result.TimedOut = true; diagnostics.Add(Error("Assembly timed out after five seconds.")); break; }
					if (File.Exists(output) && new FileInfo(output).Length > MaxBytes) { diagnostics.Add(Error("Assembled output exceeds 64 KiB.")); break; }
					await Task.Delay(20).ConfigureAwait(false);
				}
				if (!process.HasExited) process.Kill();
				exited = await Task.Run(() => process.WaitForExit(1000)).ConfigureAwait(false);
				if (!exited) { diagnostics.Add(Error("Assembler did not terminate after cancellation; temporary files were retained at " + directory + ".")); return result; }
				var readers = Task.WhenAll(stderr, stdout);
				if (await Task.WhenAny(readers, Task.Delay(1000)).ConfigureAwait(false) != readers) { diagnostics.Add(Error("Assembler diagnostic stream did not close.")); return result; }
				foreach (var stream in await readers.ConfigureAwait(false)) AddDiagnostics(stream, diagnostics);
				if (process.ExitCode != 0) diagnostics.Add(Error("Bundled NASM exited with code " + process.ExitCode + " (0x" + process.ExitCode.ToString("X8", CultureInfo.InvariantCulture) + "). Executable: " + executable));
				if (result.Cancelled || result.TimedOut || diagnostics.Any(d => d.Severity == AssemblyDiagnosticSeverity.Error)) return result;
				if (!File.Exists(output) || new FileInfo(output).Length == 0) { diagnostics.Add(Error("Source did not generate any instructions.")); return result; }
				if (new FileInfo(output).Length > MaxBytes) { diagnostics.Add(Error("Assembled output exceeds 64 KiB.")); return result; }
				result.Bytes = File.ReadAllBytes(output);
				result.Preview = instructions.Decode(result.Bytes, origin);
				if (!result.Preview.Success) { diagnostics.Add(Error(result.Preview.Error)); return result; }
				result.Success = true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { diagnostics.Add(Error("Assembler failed using " + executable + ": " + ex.Message)); }
			finally
			{
				if (process != null)
				{
					try { if (processStarted && !process.HasExited) { process.Kill(); exited = process.WaitForExit(1000); } else exited = true; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { diagnostics.Add(Error("Assembler cleanup failed: " + ex.Message)); }
					process.Dispose();
				}
				if (exited) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { diagnostics.Add(new AssemblyDiagnostic { Severity = AssemblyDiagnosticSeverity.Warning, Message = "Temporary assembler files could not be removed: " + ex.Message }); } }
			}
			return result;
		}

		private static async Task<string> ReadBoundedAsync(StreamReader reader)
		{
			var builder = new StringBuilder();
			var buffer = new char[1024];
			bool truncated = false;
			int count;
			while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
			{
				int keep = Math.Min(count, MaxDiagnosticChars - builder.Length);
				if (keep > 0) builder.Append(buffer, 0, keep);
				if (keep < count) truncated = true;
			}
			if (truncated) builder.Append("\nAssembler diagnostics truncated.\n");
			return builder.ToString();
		}

		private static void AddDiagnostics(string text, List<AssemblyDiagnostic> diagnostics)
		{
			foreach (var line in text.Split('\n'))
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				var match = Diagnostic.Match(line.Trim());
				int sourceLine = 0, column = 0;
				if (match.Success) { int.TryParse(match.Groups[1].Value, out sourceLine); sourceLine = Math.Max(0, sourceLine - 2); int.TryParse(match.Groups[2].Value, out column); }
				diagnostics.Add(new AssemblyDiagnostic { Line = sourceLine, Column = column, Severity = match.Success && match.Groups[3].Value == "warning" ? AssemblyDiagnosticSeverity.Warning : AssemblyDiagnosticSeverity.Error, Message = match.Success ? match.Groups[4].Value : line.Trim() });
			}
		}
		private static AssemblyDiagnostic Error(string message, int line = 0) => new AssemblyDiagnostic { Message = message, Line = line, Severity = AssemblyDiagnosticSeverity.Error };

		public static byte[] ParseHex(string text)
		{
			if (text == null) throw new ArgumentNullException(nameof(text));
			if (text.Length > MaxBytes * 4) throw new FormatException("Hex input exceeds the 64 KiB output limit.");
			var digits = new StringBuilder();
			foreach (char c in text) {
				if (char.IsWhiteSpace(c)) continue;
				if (!Uri.IsHexDigit(c)) throw new FormatException("Hex input contains a non-hexadecimal character.");
				digits.Append(c);
			}
			if (digits.Length == 0 || (digits.Length & 1) != 0) throw new FormatException("Enter complete hexadecimal byte pairs.");
			if (digits.Length / 2 > MaxBytes) throw new FormatException("Hex output exceeds 64 KiB.");
			var bytes = new byte[digits.Length / 2];
			for (int i = 0; i < bytes.Length; ++i) bytes[i] = byte.Parse(digits.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			return bytes;
		}
		public static string FormatHex(byte[] bytes)
		{
			if (bytes == null) throw new ArgumentNullException(nameof(bytes));
			if (bytes.Length > MaxBytes) throw new ArgumentOutOfRangeException(nameof(bytes), "Hex output exceeds 64 KiB.");
			return BitConverter.ToString(bytes).Replace('-', ' ');
		}
	}
}
