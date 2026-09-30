using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReClassNET.AssemblyEditing;
using ReClassNET.Debugger;
using ReClassNET.Patching;
using ReClassNET.UI;

namespace ReClassNET.Forms
{
	public sealed class AssemblyEditorForm : IconForm
	{
		private readonly DebugWorkspace workspace;
		private readonly PatchRepository repository;
		private readonly RegisterSnapshot snapshot;
		private PatchDefinition definition;
		private readonly TextBox addressBox = new TextBox { Width = 160 };
		private readonly TextBox nameBox = new TextBox { Width = 180 };
		private readonly NumericUpDown lengthBox = new NumericUpDown { Minimum = 1, Maximum = 65536, Width = 80 };
		private readonly ComboBox modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
		private readonly ComboBox semanticBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
		private readonly ComboBox locatorBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
		private readonly TextBox patternBox = new TextBox { Width = 310 };
		private readonly TextBox entryOffsetBox = new TextBox { Width = 75, Text = "0" };
		private readonly Label moduleLabel = new Label { AutoSize = true };
		private readonly TextBox originalBox = MakeCodeBox(true);
		private readonly TextBox assemblyBox = MakeCodeBox(false);
		private readonly TextBox hexBox = MakeCodeBox(false);
		private readonly TextBox previewBox = MakeCodeBox(true);
		private readonly Label status = new Label { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(4), ForeColor = Color.DarkSlateGray };
		private readonly Button loadButton = new Button { Text = "Load selection", AutoSize = true };
		private readonly Button previewButton = new Button { Text = "Preview", AutoSize = true };
		private readonly Button applyButton = new Button { Text = "Apply", AutoSize = true };
		private readonly Button prepareButton = new Button { Text = "Prepare hook", AutoSize = true };
		private readonly Button nopButton = new Button { Text = "NOP selection", AutoSize = true };
		private readonly Button restoreButton = new Button { Text = "Restore original", AutoSize = true };
		private readonly Button saveButton = new Button { Text = "Save definition", AutoSize = true };
		private readonly Button cancelButton = new Button { Text = "Cancel preparation", AutoSize = true };
		private readonly Button followRegisterButton = new Button { Text = "Follow captured register", AutoSize = true };
		private readonly Button followOperandButton = new Button { Text = "Follow captured operand", AutoSize = true };
		private readonly Button reverseButton = new Button { Text = "Find accessed addresses", AutoSize = true };
		private readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 300 };
		private CancellationTokenSource conversion;
		private CancellationTokenSource operation;
		private PatchPreview preview;
		private PreparedHook prepared;
		private PatchSourceKind authoritative;
		private byte[] originals;
		private ulong loadedAddress;
		private int loadedLength;
		private long editVersion;
		private bool syncing, busy, closingAfterCleanup, closeRequested;

		public AssemblyEditorForm(DebugWorkspace workspace, ulong address, BoundarySource boundary = BoundarySource.ExplicitOrigin, RegisterSnapshot snapshot = null, PatchDefinition definition = null)
		{
			this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
			repository = workspace.Repository;
			this.snapshot = snapshot;
			this.definition = definition?.Clone() ?? new PatchDefinition { Boundary = boundary, SessionId = workspace.Target.SessionId, SessionAddress = address, Platform = workspace.Target.Platform };
			authoritative = this.definition.SourceKind;
			Text = "Instruction inspector and assembly editor";
			MinimumSize = new Size(950, 700); Size = new Size(1150, 880); StartPosition = FormStartPosition.CenterParent;
			modeBox.Items.AddRange(new object[] { PatchMode.InPlace, PatchMode.Hook });
			semanticBox.Items.AddRange(new object[] { HookSemanticMode.ReplaceSelection, HookSemanticMode.InsertBefore, HookSemanticMode.InsertAfter });
			locatorBox.Items.AddRange(new object[] { PatchLocatorKind.SessionAddress, PatchLocatorKind.ModuleOffset, PatchLocatorKind.ModulePattern });
			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(8) };
			layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
			layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 37));
			layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
			var top = Flow(); top.Controls.AddRange(new Control[] { Caption("Code address (hex)"), addressBox, Caption("Selected bytes"), lengthBox, loadButton, Caption("Name"), nameBox });
			layout.Controls.Add(top, 0, 0); layout.Controls.Add(Group("Original selection, operands and captured registers", originalBox), 0, 1);
			var editors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
			editors.Controls.Add(Group("Assembly (NASM, 64-bit)", assemblyBox), 0, 0); editors.Controls.Add(Group("Replacement hex bytes", hexBox), 1, 0); layout.Controls.Add(editors, 0, 2);
			var modes = Flow(); modes.Controls.AddRange(new Control[] { Caption("Patch mode"), modeBox, Caption("Hook semantics"), semanticBox, prepareButton, cancelButton }); layout.Controls.Add(modes, 0, 3);
			var locators = Flow(); locators.Controls.AddRange(new Control[] { Caption("Saved locator"), locatorBox, Caption("Pattern"), patternBox, Caption("Entry offset"), entryOffsetBox, moduleLabel }); layout.Controls.Add(locators, 0, 4);
			layout.Controls.Add(Group("Change preview", previewBox), 0, 5);
			var actions = Flow(); actions.Controls.AddRange(new Control[] { previewButton, applyButton, nopButton, restoreButton, saveButton, followRegisterButton, followOperandButton, reverseButton }); layout.Controls.Add(actions, 0, 6); layout.Controls.Add(status, 0, 7); Controls.Add(layout);
			syncing = true;
			addressBox.Text = address.ToString("X16"); nameBox.Text = this.definition.Name; lengthBox.Value = Math.Max(1, Math.Min(65536, this.definition.SelectionLength));
			modeBox.SelectedItem = this.definition.Mode; semanticBox.SelectedItem = this.definition.HookMode; locatorBox.SelectedItem = this.definition.LocatorKind;
			patternBox.Text = this.definition.Pattern ?? ""; entryOffsetBox.Text = this.definition.EntryOffset.ToString(CultureInfo.InvariantCulture);
			assemblyBox.Text = this.definition.Assembly ?? ""; hexBox.Text = AssemblyService.FormatHex(this.definition.ReplacementBytes ?? new byte[0]); syncing = false;
			assemblyBox.TextChanged += (s, e) => SourceEdited(PatchSourceKind.Assembly);
			hexBox.TextChanged += (s, e) => SourceEdited(PatchSourceKind.Bytes);
			addressBox.TextChanged += (s, e) => SelectionEdited(); lengthBox.ValueChanged += (s, e) => SelectionEdited();
			nameBox.TextChanged += (s, e) => DefinitionEdited(); modeBox.SelectedIndexChanged += (s, e) => DefinitionEdited(); semanticBox.SelectedIndexChanged += (s, e) => DefinitionEdited();
			locatorBox.SelectedIndexChanged += (s, e) => DefinitionEdited(); patternBox.TextChanged += (s, e) => DefinitionEdited(); entryOffsetBox.TextChanged += (s, e) => DefinitionEdited();
			debounce.Tick += async (s, e) => { debounce.Stop(); await ConvertAsync(); };
			loadButton.Click += async (s, e) => await RunAsync(() => LoadSelectionAsync(false));
			previewButton.Click += async (s, e) => await RunAsync(PreviewAsync);
			prepareButton.Click += async (s, e) => await RunAsync(PrepareAsync);
			applyButton.Click += async (s, e) => await RunAsync(ApplyAsync);
			restoreButton.Click += async (s, e) => await RunAsync(RestoreAsync);
			saveButton.Click += async (s, e) => await RunAsync(SaveAsync);
			cancelButton.Click += async (s, e) => await RunAsync(CancelPreparationAsync);
			followRegisterButton.Click += async (s, e) => await RunAsync(FollowRegisterAsync);
			followOperandButton.Click += async (s, e) => await RunAsync(FollowOperandAsync);
			reverseButton.Click += async (s, e) => await RunAsync(async () => { await CancelPreparationAsync(); new WatchFinderForm(workspace, Address(), 1, false, true).Show(this); });
			nopButton.Click += (s, e) => { if (originals == null) return; modeBox.SelectedItem = PatchMode.InPlace; hexBox.Text = AssemblyService.FormatHex(Enumerable.Repeat((byte)0x90, originals.Length).ToArray()); authoritative = PatchSourceKind.Bytes; };
			workspace.Manager.Changed += ManagerChanged; workspace.Session.StateChanged += SessionChanged;
			Shown += async (s, e) => await RunAsync(() => LoadSelectionAsync(true));
			FormClosing += EditorClosing; FormClosed += EditorClosed;
			UpdateButtons();
		}

		private static TextBox MakeCodeBox(bool readOnly) => new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, ReadOnly = readOnly, AcceptsTab = !readOnly, MaxLength = 262144, Font = new Font(FontFamily.GenericMonospace, 10) };
		private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(4, 8, 4, 0) };
		private static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
		private static GroupBox Group(string text, Control content) { var group = new GroupBox { Text = text, Dock = DockStyle.Fill, Padding = new Padding(6) }; group.Controls.Add(content); return group; }
		private ulong Address() { string text = addressBox.Text.Trim(); if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2); ulong address; if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)) throw new FormatException("Enter a valid 64-bit hexadecimal code address."); return address; }
		private void SelectionEdited() { if (syncing) return; DefinitionEdited(); status.Text = "Selection changed. Load selection to capture complete original instructions."; }
		private void SourceEdited(PatchSourceKind kind) { if (syncing) return; authoritative = kind; DefinitionEdited(); conversion?.Cancel(); debounce.Stop(); debounce.Start(); }
		private void DefinitionEdited()
		{
			if (syncing) return; ++editVersion; preview = null; previewBox.Clear();
			if (prepared != null) { var pending = prepared; prepared = null; _ = ReleaseReservationAsync(pending.Id); }
			UpdateButtons();
		}
		private async Task ReleaseReservationAsync(Guid id)
		{
			try { var result = await workspace.Manager.CancelPreparationAsync(id); if (!IsDisposed && !result.Success) status.Text = result.Message; }
			catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
		}
		private async Task ConvertAsync()
		{
			conversion?.Cancel(); conversion?.Dispose(); conversion = new CancellationTokenSource(); var token = conversion.Token; long version = editVersion;
			try
			{
				ulong origin = Address();
				if (authoritative == PatchSourceKind.Assembly)
				{
					var result = await workspace.Assembler.AssembleAsync(assemblyBox.Text, origin, token);
					if (IsDisposed || token.IsCancellationRequested || version != editVersion) return;
					if (!result.Success) { status.Text = string.Join("; ", result.Diagnostics.Select(d => d.ToString())); return; }
					syncing = true; hexBox.Text = AssemblyService.FormatHex(result.Bytes); syncing = false;
					status.Text = "Assembly produced " + result.Bytes.Length + " bytes at 0x" + origin.ToString("X") + ". Preview before applying.";
				}
				else
				{
					var bytes = AssemblyService.ParseHex(hexBox.Text); var decode = workspace.Instructions.Decode(bytes, origin);
					if (!decode.Success) throw new FormatException(decode.Error);
					syncing = true; assemblyBox.Text = string.Join(Environment.NewLine, decode.Instructions.Select(i => i.Text)); syncing = false;
					status.Text = "Hex is authoritative: " + bytes.Length + " replacement bytes. Preview before applying.";
				}
			}
			catch (Exception ex) { if (!IsDisposed && version == editVersion) status.Text = ex.Message; }
			finally { syncing = false; }
		}

		private async Task LoadSelectionAsync(bool initial)
		{
			await CancelPreparationAsync();
			ulong address = Address(); int length = (int)lengthBox.Value;
			var saved = initial && definition.ExpectedBytes != null && definition.ExpectedBytes.Length > 0;
			var data = await Task.Run(() => {
				if (initial && !saved && definition.SelectionLength == 0) {
					for (int n = 1; n <= 15; ++n) { var candidate = workspace.Target.ReadExact(address, n); var decode = workspace.Instructions.Decode(candidate, address); if (decode.Success && decode.Instructions.Count == 1) return candidate; }
					throw new InvalidOperationException("Cannot decode a complete instruction at this origin.");
				}
				return workspace.Target.ReadExact(address, saved ? definition.ExpectedBytes.Length : length);
			});
			originals = saved ? (byte[])definition.ExpectedBytes.Clone() : data;
			loadedAddress = address; loadedLength = originals.Length;
			var decoded = workspace.Instructions.Decode(originals, address);
			if (!decoded.Success) { originals = null; throw new InvalidOperationException("Selection must contain only complete instructions. " + decoded.Error); }
			syncing = true; lengthBox.Value = loadedLength;
			if (!saved) { definition.ExpectedBytes = (byte[])originals.Clone(); definition.SelectionLength = loadedLength; definition.SessionAddress = address; definition.SessionId = workspace.Target.SessionId; if (!initial) definition.Boundary = BoundarySource.ExplicitOrigin; }
			if (initial && string.IsNullOrWhiteSpace(assemblyBox.Text) && string.IsNullOrWhiteSpace(hexBox.Text)) {
				assemblyBox.Text = string.Join(Environment.NewLine, decoded.Instructions.Select(i => i.Text));
				hexBox.Text = AssemblyService.FormatHex(originals); authoritative = PatchSourceKind.Bytes;
			}
			if (string.IsNullOrWhiteSpace(patternBox.Text)) patternBox.Text = AssemblyService.FormatHex(originals);
			syncing = false;
			var text = new StringBuilder();
			var labels = workspace.Process.NamedAddresses.ToDictionary(pair => unchecked((ulong)pair.Key.ToInt64()), pair => pair.Value);
			if (saved && !originals.SequenceEqual(data)) {
				text.AppendLine("Saved original bytes are shown below. Current bytes differ; Preview will reject a stale original.");
				text.AppendLine("Current bytes: " + AssemblyService.FormatHex(data));
			}
			foreach (var record in decoded.Instructions) { text.AppendLine(record.Address.ToString("X16") + "  " + AssemblyService.FormatHex(record.Bytes) + "  " + record.Text); text.AppendLine(workspace.Instructions.Explain(record, snapshot?.Registers, labels, null, null, snapshot != null && snapshot.Phase == SnapshotPhase.Before && snapshot.Context.Rip == record.Address).Text); }
			if (snapshot != null) { text.AppendLine("Captured thread " + snapshot.ThreadId + ", " + snapshot.Timestamp.ToLocalTime().ToString("G") + ", phase " + snapshot.Phase); foreach (var register in snapshot.Registers) text.Append(register.Key.ToUpperInvariant() + "=" + register.Value.ToString("X16") + "  "); text.AppendLine(); }
			text.AppendLine("Boundary: " + definition.Boundary + ". The code address is separate from any watched data address.");
			originalBox.Text = text.ToString();
			var module = await Task.Run(() => workspace.Target.Modules.SingleOrDefault(m => address >= m.BaseAddress && address - m.BaseAddress < m.Size));
			moduleLabel.Text = module == null ? "No module at this address; session-only draft." : "Module: " + module.Name + ", offset 0x" + (address - module.BaseAddress).ToString("X") + ", SHA-256 " + (module.Sha256 ?? "unavailable");
			status.Text = saved && !originals.SequenceEqual(data) ? "Conflict: current bytes differ from the saved originals. Restore or choose an explicit new selection." : "Original bytes captured. Choose Preview to inspect a change.";
			preview = null; prepared = null; ++editVersion; UpdateButtons();
			await ConvertAsync();
		}

		private PatchDefinition BuildDefinition()
		{
			if (!ReferenceEquals(repository, workspace.Repository)) throw new InvalidOperationException("The project changed. Open a new editor for the current project.");
			if (originals == null || Address() != loadedAddress || (int)lengthBox.Value != loadedLength) throw new InvalidOperationException("Load the selected original instruction span first.");
			var result = definition.Clone(); result.Name = nameBox.Text.Trim(); if (result.Name.Length == 0) result.Name = "Patch";
			result.Platform = workspace.Target.Platform; result.Architecture = "x64"; result.SessionAddress = loadedAddress; result.SessionId = workspace.Target.SessionId;
			result.SelectionLength = loadedLength; result.ExpectedBytes = (byte[])originals.Clone(); result.Mode = (PatchMode)modeBox.SelectedItem; result.HookMode = (HookSemanticMode)semanticBox.SelectedItem;
			result.SourceKind = authoritative; result.Assembly = assemblyBox.Text; result.ReplacementBytes = authoritative == PatchSourceKind.Bytes ? AssemblyService.ParseHex(hexBox.Text) : null;
			result.LocatorKind = (PatchLocatorKind)locatorBox.SelectedItem;
			if (result.LocatorKind != PatchLocatorKind.SessionAddress)
			{
				var module = workspace.Target.Modules.SingleOrDefault(m => loadedAddress >= m.BaseAddress && loadedAddress - m.BaseAddress < m.Size);
				if (module == null || string.IsNullOrEmpty(module.Sha256)) throw new InvalidOperationException("A loaded module with an available image SHA-256 is required.");
				if (!string.IsNullOrEmpty(definition.ImageSha256) && !string.Equals(definition.ImageSha256, module.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Module image differs from the saved definition.");
				result.ModuleName = module.Name; result.ImageSha256 = module.Sha256; result.Offset = loadedAddress - module.BaseAddress;
				result.Pattern = patternBox.Text.Trim(); long entry; if (!long.TryParse(entryOffsetBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out entry)) throw new FormatException("Pattern entry offset must be a signed decimal integer."); result.EntryOffset = entry;
			}
			return result;
		}
		private async Task PreviewAsync()
		{
			await CancelPreparationAsync(); var candidate = BuildDefinition();
			preview = await workspace.Planner.PreviewAsync(candidate, workspace.Target, operation.Token);
			RenderPreview(); status.Text = preview.Message ?? "Preview ready. Original " + preview.OriginalBytes.Length + " bytes, installed " + preview.ReplacementBytes.Length + " bytes, NOP padding " + preview.PaddingLength + " bytes.";
		}
		private async Task PrepareAsync()
		{
			if ((PatchMode)modeBox.SelectedItem != PatchMode.Hook) throw new InvalidOperationException("Choose Hook mode first.");
			await PreviewAsync(); if (preview.Status != PatchStatus.Previewed) return;
			prepared = await workspace.Planner.PrepareHookAsync(preview, workspace.Target, workspace.Manager, operation.Token);
			RenderPreview(); status.Text = "Hook reserved and prepared at its final origin. Review the expanded overwrite span and code before Apply.";
		}
		private void RenderPreview()
		{
			var text = new StringBuilder(); if (preview == null) { previewBox.Clear(); return; }
			text.AppendLine("Status: " + preview.Status + " " + preview.Message);
			if (preview.OriginalBytes != null) { text.AppendLine("Code address 0x" + preview.Address.ToString("X") + "; selected original " + preview.OriginalBytes.Length + " bytes"); text.AppendLine("Original: " + AssemblyService.FormatHex(preview.OriginalBytes)); }
			if (preview.ReplacementBytes != null) { text.AppendLine("Replacement preview: " + AssemblyService.FormatHex(preview.ReplacementBytes)); text.AppendLine("NOP padding: " + preview.PaddingLength + " bytes"); }
			if (prepared != null) {
				text.AppendLine("Hook semantics: " + prepared.Preview.Definition.HookMode + "; final allocation 0x" + prepared.Allocation.Address.ToString("X"));
				text.AppendLine("Final user body origin: 0x" + prepared.UserBodyOrigin.ToString("X") + "; " + prepared.UserBodyBytes.Length + " bytes");
				foreach (var instruction in prepared.UserBodyInstructions) text.AppendLine(instruction.Address.ToString("X16") + " " + AssemblyService.FormatHex(instruction.Bytes) + " " + instruction.Text);
				text.AppendLine("Entry overwrite: " + prepared.EntryBytes.Length + " bytes; jump " + prepared.JumpLength + " bytes; return 0x" + prepared.ReturnAddress.ToString("X"));
				text.AppendLine("Entry bytes: " + AssemblyService.FormatHex(prepared.EntryBytes));
				text.AppendLine("Complete displaced instructions (extra instructions are preserved):");
				foreach (var instruction in prepared.DisplacedInstructions) text.AppendLine(instruction.Address.ToString("X16") + " " + instruction.Text);
				text.AppendLine("Relocated instruction mapping:");
				foreach (var mapping in prepared.InstructionMappings) text.AppendLine("0x" + mapping.OriginalAddress.ToString("X") + " -> " + (mapping.RelocatedAddress.HasValue ? "0x" + mapping.RelocatedAddress.Value.ToString("X") : "rewritten sequence"));
				text.AppendLine("Final hook code: " + prepared.Code.Length + " bytes (first 4096 shown)"); text.AppendLine(AssemblyService.FormatHex(prepared.Code.Take(4096).ToArray()));
				text.AppendLine("After publication, hook memory is retained until target exit, including after Restore.");
			}
			previewBox.Text = text.ToString();
		}
		private async Task ApplyAsync()
		{
			PatchResult result; if (prepared != null) result = await workspace.Manager.ApplyAsync(prepared, operation.Token); else if (preview?.CanApply == true) result = await workspace.Manager.ApplyAsync(preview, operation.Token); else throw new InvalidOperationException("A current valid preview or prepared hook is required.");
			status.Text = result.Message; if (result.Status == PatchStatus.Active && result.Patch != null) { definition = result.Patch.Preview.Definition.Clone(); prepared = null; preview = null; }
		}
		private async Task RestoreAsync() { await CancelPreparationAsync(); var result = await workspace.Manager.RestoreAsync(definition.Id, operation.Token); status.Text = result.Message; preview = null; }
		private async Task CancelPreparationAsync() { if (prepared == null) return; var result = await workspace.Manager.CancelPreparationAsync(prepared.Id); if (!result.Success) throw new InvalidOperationException(result.Message); prepared = null; preview = null; status.Text = result.Message; }
		private async Task SaveAsync()
		{
			if (repository.IsReadOnly) throw new InvalidOperationException(repository.Message);
			var candidate = BuildDefinition();
			if (candidate.SourceKind == PatchSourceKind.Assembly) {
				var assembled = await workspace.Assembler.AssembleAsync(candidate.Assembly, loadedAddress, operation.Token);
				if (!assembled.Success) throw new InvalidOperationException("Cannot save invalid assembly: " + string.Join("; ", assembled.Diagnostics.Select(d => d.ToString())));
			}
			else {
				var decoded = workspace.Instructions.Decode(candidate.ReplacementBytes, loadedAddress);
				if (!decoded.Success) throw new InvalidOperationException("Cannot save incomplete replacement instructions: " + decoded.Error);
			}
			var resolved = await Task.Run(() => new PatchTargetResolver().Resolve(candidate, workspace.Target, operation.Token));
			if (resolved.Status != PatchResolutionStatus.Resolved || resolved.Address != loadedAddress) throw new InvalidOperationException("Saved locator does not resolve uniquely to this selection: " + resolved.Status + " " + resolved.Message);
			var observed = await Task.Run(() => workspace.Target.ReadExact(loadedAddress, loadedLength));
			var active = workspace.Manager.ActivePatches.FirstOrDefault(p => p.Id == candidate.Id);
			if (!observed.SequenceEqual(originals) && (active == null || !observed.SequenceEqual(active.InstalledBytes))) throw new InvalidOperationException("Original or installed bytes changed; the definition was not saved.");
			repository.Upsert(candidate); definition = repository.Definitions.Single(d => d.Id == candidate.Id); DefinitionEdited();
			status.Text = candidate.LocatorKind == PatchLocatorKind.SessionAddress ? "Saved inactive session-only draft. After restart, select an address and load a new selection to resolve it." : "Saved definition in the current project. Loading a project keeps patches inactive; save the project file to persist it.";
		}
		private async Task FollowPointerAsync(ulong pointer)
		{
			if (snapshot == null || snapshot.SessionId != workspace.Session.Id) throw new InvalidOperationException("The captured register snapshot belongs to a different process session.");
			if (pointer == 0) throw new InvalidOperationException("The captured address is zero.");
			await Task.Run(() => workspace.Target.ReadExact(pointer, 1));
			LinkedWindowFeatures.CreateClassAtAddress(new IntPtr(unchecked((long)pointer)), true);
			status.Text = "Following captured address 0x" + pointer.ToString("X") + ". The displayed memory is current and may differ from the captured event.";
		}
		private async Task FollowRegisterAsync()
		{
			if (snapshot == null || snapshot.Registers.Count == 0) throw new InvalidOperationException("No captured registers are available.");
			var registers = snapshot.Registers.ToArray();
			int index = ChooseCaptured("Follow captured register (current memory)", registers.Select(r => r.Key.ToUpperInvariant() + " = 0x" + r.Value.ToString("X16")).ToArray());
			if (index >= 0) await FollowPointerAsync(registers[index].Value);
		}
		private async Task FollowOperandAsync()
		{
			if (snapshot == null || originals == null) throw new InvalidOperationException("A captured Before snapshot and loaded instruction are required.");
			var instruction = workspace.Instructions.Decode(originals, loadedAddress).Instructions.First();
			var operands = workspace.Instructions.ResolveMemoryAddresses(instruction, snapshot.Registers, beforeInstruction: snapshot.Phase == SnapshotPhase.Before && snapshot.Context.Rip == loadedAddress);
			var available = operands.Where(o => o.Available).ToArray();
			if (available.Length == 0) throw new InvalidOperationException("Operand address unavailable. " + string.Join("; ", operands.Select(o => o.Reason)));
			int index = ChooseCaptured("Follow captured operand (current memory)", available.Select(o => "0x" + o.Address.ToString("X16") + " / " + o.WidthBytes + " bytes / " + o.Memory.Access).ToArray());
			if (index >= 0) await FollowPointerAsync(available[index].Address);
		}
		private int ChooseCaptured(string title, string[] choices)
		{
			using (var dialog = new Form { Text = title, Size = new Size(530, 160), StartPosition = FormStartPosition.CenterParent }) {
				var choicesBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList }; choicesBox.Items.AddRange(choices); choicesBox.SelectedIndex = 0;
				var follow = new Button { Text = "Follow current memory", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK }; dialog.Controls.Add(choicesBox); dialog.Controls.Add(follow); dialog.AcceptButton = follow;
				return dialog.ShowDialog(this) == DialogResult.OK ? choicesBox.SelectedIndex : -1;
			}
		}
		private async Task RunAsync(Func<Task> action)
		{
			if (busy || IsDisposed) return; if (!ReferenceEquals(repository, workspace.Repository)) { status.Text = "The project changed. Open a new editor for the current project."; return; } busy = true; debounce.Stop(); conversion?.Cancel(); operation = new CancellationTokenSource(); UpdateButtons();
			try { await action(); } catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Operation cancelled."; } catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
			finally { operation.Dispose(); operation = null; busy = false; if (!IsDisposed) { UpdateButtons(); if (closeRequested) { closeRequested = false; BeginInvoke(new Action(Close)); } } }
		}
		private void UpdateButtons()
		{
			if (!busy && prepared != null && !ReferenceEquals(workspace.Manager.Preparation, prepared)) {
				prepared = null; preview = null; status.Text = "Hook preparation is no longer current. Prepare it again before applying.";
			}
			bool editable = !busy && workspace.Target.IsAlive;
			addressBox.Enabled = lengthBox.Enabled = nameBox.Enabled = assemblyBox.Enabled = hexBox.Enabled = modeBox.Enabled = semanticBox.Enabled = locatorBox.Enabled = patternBox.Enabled = entryOffsetBox.Enabled = editable;
			loadButton.Enabled = previewButton.Enabled = editable; nopButton.Enabled = editable && originals != null;
			prepareButton.Enabled = editable && (PatchMode)modeBox.SelectedItem == PatchMode.Hook && workspace.Target.SupportsAllocation;
			applyButton.Enabled = editable && (prepared != null || preview?.CanApply == true);
			restoreButton.Enabled = !busy && workspace.Manager.ActivePatches.Any(p => p.Id == definition.Id);
			saveButton.Enabled = editable && originals != null && !repository.IsReadOnly;
			cancelButton.Enabled = !busy && prepared != null;
			followRegisterButton.Enabled = editable && snapshot != null && snapshot.Registers.Count != 0;
			followOperandButton.Enabled = editable && originals != null && snapshot != null;
			reverseButton.Enabled = editable && originals != null;
			patternBox.Enabled = editable && (PatchLocatorKind)locatorBox.SelectedItem == PatchLocatorKind.ModulePattern; entryOffsetBox.Enabled = patternBox.Enabled;
		}
		private void ManagerChanged(object sender, EventArgs e) => QueueUpdate();
		private void SessionChanged(DebugSessionState state) => QueueUpdate();
		private void QueueUpdate() { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(new Action(() => { if (!IsDisposed) UpdateButtons(); })); } catch (InvalidOperationException) { } }
		private async void EditorClosing(object sender, FormClosingEventArgs e)
		{
			if (closingAfterCleanup) return;
			if (busy) { e.Cancel = true; closeRequested = true; operation?.Cancel(); status.Text = "Cancelling the operation and completing cleanup before closing."; return; }
			conversion?.Cancel(); debounce.Stop();
			if (prepared == null) return;
			e.Cancel = true; await RunAsync(async () => { await CancelPreparationAsync(); closingAfterCleanup = true; }); if (closingAfterCleanup) Close();
		}
		private void EditorClosed(object sender, FormClosedEventArgs e) { workspace.Manager.Changed -= ManagerChanged; workspace.Session.StateChanged -= SessionChanged; conversion?.Cancel(); conversion?.Dispose(); debounce.Dispose(); GlobalWindowManager.RemoveWindow(this); }
		protected override void OnLoad(EventArgs e) { base.OnLoad(e); GlobalWindowManager.AddWindow(this); }
	}
}
