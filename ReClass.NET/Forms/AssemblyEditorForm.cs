using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReClassNET.AssemblyEditing;
using ReClassNET.Controls.Debugger;
using ReClassNET.Debugger;
using ReClassNET.Patching;
using ReClassNET.Properties;
using ReClassNET.UI;
using ReClassNET.UI.Debugger;

namespace ReClassNET.Forms
{
	public sealed class AssemblyEditorForm : IconForm
	{
		private readonly DebugWorkspace workspace;
		private readonly PatchRepository repository;
		private readonly RegisterSnapshot snapshot;
		private PatchDefinition definition;
		private readonly TextBox addressBox = new TextBox { Width = DpiUtil.ScaleIntX(170) };
		private readonly TextBox nameBox = new TextBox { Width = DpiUtil.ScaleIntX(180) };
		private readonly NumericUpDown lengthBox = new NumericUpDown { Minimum = 1, Maximum = 65536, Width = DpiUtil.ScaleIntX(80) };
		private readonly SegmentedSelector<PatchMode> modeBox = new SegmentedSelector<PatchMode>()
			.Add(PatchMode.InPlace, "In place", "Overwrite the selected bytes. The new code must fit in the same space.")
			.Add(PatchMode.Hook, "Hook", "Jump out to your code in new memory, then back. Any length.");
		private readonly SegmentedSelector<HookSemanticMode> semanticBox = new SegmentedSelector<HookSemanticMode>()
			.Add(HookSemanticMode.ReplaceSelection, "Replace selection", "Your code runs instead of the original instructions.")
			.Add(HookSemanticMode.InsertBefore, "Insert before", "Your code runs first, then the original instructions.")
			.Add(HookSemanticMode.InsertAfter, "Insert after", "The original instructions run first, then your code.");
		private readonly SegmentedSelector<PatchLocatorKind> locatorBox = new SegmentedSelector<PatchLocatorKind>()
			.Add(PatchLocatorKind.SessionAddress, "Session address", "Saved by absolute address: only valid in this game process.")
			.Add(PatchLocatorKind.ModuleOffset, "Module + offset", "Saved as module + offset: finds the code again after a restart.")
			.Add(PatchLocatorKind.ModulePattern, "Module pattern", "Saved as a unique byte pattern: survives small game updates.");
		private readonly TextBox patternBox = new TextBox { Width = DpiUtil.ScaleIntX(300) };
		private readonly TextBox entryOffsetBox = new TextBox { Width = DpiUtil.ScaleIntX(70), Text = "0" };
		private readonly Label moduleLabel = new Label { AutoSize = true };
		private readonly TextBox originalBox = MakeCodeBox(true);
		private readonly TextBox assemblyBox = MakeCodeBox(false);
		private readonly TextBox hexBox = MakeCodeBox(false);
		private readonly TextBox previewBox = MakeCodeBox(true);
		private readonly StatusLine status = new StatusLine();
		private readonly DebuggerHeader header = new DebuggerHeader();
		private readonly StepRail rail = new StepRail();
		private readonly InstructionListView originalList = new InstructionListView { Dock = DockStyle.Fill };
		private readonly RegisterBoard capturedBoard = new RegisterBoard { Dock = DockStyle.Fill };
		private readonly ByteDiffView diffView = new ByteDiffView { Dock = DockStyle.Fill };
		private readonly NoteLine assemblyNote = new NoteLine(), hexNote = new NoteLine();
		private readonly SizeMeter sizeMeter = new SizeMeter { Dock = DockStyle.Fill };
		private readonly Label modeHelp = Help(), semanticHelp = Help(), locatorHelp = Help();
		private readonly ToolTip tips = new ToolTip();
		private readonly DarkButton loadButton = new DarkButton("Load selection", Resources.B16x16_Arrow_Refresh);
		private readonly DarkButton previewButton = new DarkButton("Preview", Resources.B16x16_Magnifier);
		private readonly DarkButton applyButton = new DarkButton("Apply", Resources.B16x16_Accept, DarkButtonStyle.Primary);
		private readonly DarkButton prepareButton = new DarkButton("Prepare hook", Resources.B16x16_Cogs);
		private readonly DarkButton nopButton = new DarkButton("NOP selection", Resources.B16x16_Button_Remove);
		private readonly DarkButton restoreButton = new DarkButton("Restore original", Resources.B16x16_Undo, DarkButtonStyle.Danger);
		private readonly DarkButton saveButton = new DarkButton("Save definition", Resources.B16x16_Save);
		private readonly DarkButton cancelButton = new DarkButton("Cancel preparation", Resources.B16x16_Button_Delete, DarkButtonStyle.Ghost);
		private readonly DarkButton followRegisterButton = new DarkButton("Follow captured register", Resources.B16x16_Pointer_Type, DarkButtonStyle.Ghost);
		private readonly DarkButton followOperandButton = new DarkButton("Follow captured operand", Resources.B16x16_Right_Button, DarkButtonStyle.Ghost);
		private readonly DarkButton reverseButton = new DarkButton("Find accessed addresses", Resources.B16x16_Magnifier_Arrow, DarkButtonStyle.Ghost);
		private readonly Panel patternFields = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
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
		private bool syncing, busy, closingAfterCleanup, closeRequested, restored;

		public AssemblyEditorForm(DebugWorkspace workspace, ulong address, BoundarySource boundary = BoundarySource.ExplicitOrigin, RegisterSnapshot snapshot = null, PatchDefinition definition = null)
		{
			this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
			repository = workspace.Repository;
			this.snapshot = snapshot;
			this.definition = definition?.Clone() ?? new PatchDefinition { Boundary = boundary, SessionId = workspace.Target.SessionId, SessionAddress = address, Platform = workspace.Target.Platform };
			authoritative = this.definition.SourceKind;
			Text = "Instruction inspector and assembly editor";
			MinimumSize = new Size(DpiUtil.ScaleIntX(1000), DpiUtil.ScaleIntY(760)); Size = new Size(DpiUtil.ScaleIntX(1260), DpiUtil.ScaleIntY(980)); StartPosition = FormStartPosition.CenterParent;
			BackColor = DebuggerTheme.Background; ForeColor = DebuggerTheme.Text; Font = DebuggerTheme.UiFont;

			header.Icon = Resources.B32x32_Page_Code; header.Title = "Instruction editor";
			header.Subtitle = address == 0 ? "Enter a code address, then Load selection." : "0x" + address.ToString("X");

			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(DpiUtil.ScaleIntX(8), DpiUtil.ScaleIntY(4), DpiUtil.ScaleIntX(8), DpiUtil.ScaleIntY(4)), BackColor = DebuggerTheme.Background };
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 31));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiUtil.ScaleIntY(34)));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

			var top = Flow(); top.Controls.AddRange(new Control[] { Caption("Code address (hex or module+offset)"), addressBox, Caption("Selected bytes"), lengthBox, loadButton, Caption("Name"), nameBox });
			layout.Controls.Add(top, 0, 0);

			var originalCard = new Card("Original selection, operands and captured registers");
			var registersToggle = new ToggleChip("Registers", snapshot != null); var rawToggle = new ToggleChip("Raw");
			originalCard.HeaderRight.Controls.Add(registersToggle); originalCard.HeaderRight.Controls.Add(rawToggle);
			var capturedHost = new Panel { Dock = DockStyle.Right, Width = DpiUtil.ScaleIntX(440), Padding = new Padding(DpiUtil.ScaleIntX(8), 0, 0, 0), BackColor = DebuggerTheme.Panel, Visible = snapshot != null };
			capturedHost.Controls.Add(capturedBoard);
			originalBox.Visible = false;
			originalCard.Body.Controls.Add(originalList); originalCard.Body.Controls.Add(originalBox); originalCard.Body.Controls.Add(capturedHost);
			registersToggle.CheckedChanged += (s, e) => capturedHost.Visible = registersToggle.Checked;
			rawToggle.CheckedChanged += (s, e) => { originalBox.Visible = rawToggle.Checked; originalList.Visible = !rawToggle.Checked; if (rawToggle.Checked) originalBox.BringToFront(); };
			originalList.EmptyText = "Load selection decodes the original bytes here, one instruction per line, each explained in plain English.";
			capturedBoard.EmptyText = "No registers were captured for this instruction.";
			capturedBoard.FollowRequested += register => _ = RunAsync(() => FollowPointerAsync(register.Value));
			layout.Controls.Add(originalCard, 0, 1);

			var editors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = DebuggerTheme.Background };
			editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
			editors.Controls.Add(EditorCard("Assembly (NASM, 64-bit)", assemblyBox, assemblyNote), 0, 0); editors.Controls.Add(EditorCard("Replacement hex bytes", hexBox, hexNote), 1, 0);
			layout.Controls.Add(editors, 0, 2);
			sizeMeter.Margin = new Padding(DpiUtil.ScaleIntX(6), 0, DpiUtil.ScaleIntX(6), 0);
			sizeMeter.SwitchToHookRequested += (s, e) => { if (modeBox.Enabled) modeBox.SelectedValue = PatchMode.Hook; };
			layout.Controls.Add(sizeMeter, 0, 3);

			var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = DebuggerTheme.Background, Margin = Padding.Empty };
			options.Controls.Add(OptionGroup("Patch mode", modeHelp, modeBox));
			options.Controls.Add(OptionGroup("Hook semantics", semanticHelp, semanticBox));
			patternFields.Controls.AddRange(new Control[] { Caption("Pattern"), patternBox, Caption("Entry offset"), entryOffsetBox });
			options.Controls.Add(OptionGroup("Saved locator", locatorHelp, locatorBox, patternFields));
			moduleLabel.ForeColor = DebuggerTheme.Muted; moduleLabel.Margin = new Padding(DpiUtil.ScaleIntX(10), DpiUtil.ScaleIntY(4), 0, DpiUtil.ScaleIntY(4));
			options.Controls.Add(moduleLabel); options.SetFlowBreak(options.Controls[options.Controls.Count - 2], true);
			layout.Controls.Add(options, 0, 4);

			var previewCard = new Card("Change preview");
			var detailsToggle = new ToggleChip("Details"); previewCard.HeaderRight.Controls.Add(detailsToggle);
			previewBox.Visible = false;
			previewCard.Body.Controls.Add(diffView); previewCard.Body.Controls.Add(previewBox);
			detailsToggle.CheckedChanged += (s, e) => { previewBox.Visible = detailsToggle.Checked; diffView.Visible = !detailsToggle.Checked; if (detailsToggle.Checked) previewBox.BringToFront(); };
			layout.Controls.Add(previewCard, 0, 5);

			var actions = Flow(); actions.Controls.AddRange(new Control[] { previewButton, prepareButton, cancelButton, applyButton, restoreButton, nopButton, saveButton, Spacer(), followRegisterButton, followOperandButton, reverseButton });
			layout.Controls.Add(actions, 0, 6);
			Controls.Add(layout); Controls.Add(status); Controls.Add(rail); Controls.Add(header);
			DebuggerTheme.Style(layout);
			foreach (var box in new[] { originalBox, assemblyBox, hexBox, previewBox }) { box.BorderStyle = BorderStyle.None; box.BackColor = box.ReadOnly ? DebuggerTheme.Panel : DebuggerTheme.Raised; box.ForeColor = DebuggerTheme.Text; }
			addressBox.Font = patternBox.Font = entryOffsetBox.Font = DebuggerTheme.Mono;
			Tip(loadButton, "Read the original bytes at this address and decode them."); Tip(previewButton, "Show exactly which bytes would change. Nothing is written yet.");
			Tip(applyButton, "Write the previewed change into the running game."); Tip(restoreButton, "Put the original bytes back.");
			Tip(nopButton, "Replace the whole selection with NOP (do nothing) instructions."); Tip(saveButton, "Store this patch in the project so it can be applied again later.");
			Tip(prepareButton, "Reserve hook memory and build the final hook code, so you can review it before Apply."); Tip(cancelButton, "Release a prepared hook without applying it.");
			Tip(followRegisterButton, "Open the memory a captured register points at, as a class."); Tip(followOperandButton, "Open the memory this instruction's operand pointed at, as a class.");
			Tip(reverseButton, "Watch this instruction and list every address it touches.");
			DebuggerTheme.UseDarkChrome(this);

			syncing = true;
			addressBox.Text = address == 0 ? "" : address.ToString("X16"); nameBox.Text = this.definition.Name; lengthBox.Value = Math.Max(1, Math.Min(65536, this.definition.SelectionLength));
			modeBox.SelectedValue = this.definition.Mode; semanticBox.SelectedValue = this.definition.HookMode; locatorBox.SelectedValue = this.definition.LocatorKind;
			patternBox.Text = this.definition.Pattern ?? ""; entryOffsetBox.Text = this.definition.EntryOffset.ToString(CultureInfo.InvariantCulture);
			assemblyBox.Text = this.definition.Assembly ?? ""; hexBox.Text = AssemblyService.FormatHex(this.definition.ReplacementBytes ?? new byte[0]); syncing = false;
			assemblyBox.TextChanged += (s, e) => SourceEdited(PatchSourceKind.Assembly);
			hexBox.TextChanged += (s, e) => { SourceEdited(PatchSourceKind.Bytes); UpdateSizeMeter(); };
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
			nopButton.Click += (s, e) => { if (originals == null) return; modeBox.SelectedValue = PatchMode.InPlace; hexBox.Text = AssemblyService.FormatHex(Enumerable.Repeat((byte)0x90, originals.Length).ToArray()); authoritative = PatchSourceKind.Bytes; };
			workspace.Manager.Changed += ManagerChanged; workspace.Session.StateChanged += SessionChanged;
			Shown += async (s, e) =>
			{
				await Task.Yield();
				// Mono resets multiline TextBox heights when their native handles are
				// created. Reapply docking after showing so all code panes stay readable.
				foreach (var box in new[] { originalBox, assemblyBox, hexBox, previewBox }) box.Parent.PerformLayout();
				if (addressBox.Text.Length == 0) { status.Text = "New patch: enter a code address (hex, or module+offset such as game.exe+0x1234), set Selected bytes, then choose Load selection."; return; }
				await RunAsync(() => LoadSelectionAsync(true));
			};
			FormClosing += EditorClosing; FormClosed += EditorClosed;
			UpdateButtons();
		}

		private static TextBox MakeCodeBox(bool readOnly) => new TextBox { AutoSize = false, Multiline = true, ScrollBars = readOnly ? ScrollBars.Both : ScrollBars.Vertical, WordWrap = false, ReadOnly = readOnly, AcceptsTab = !readOnly, MaxLength = 262144, Font = DebuggerTheme.Mono, Dock = DockStyle.Fill };
		private static Label Caption(string text) => new Label { Text = text, AutoSize = true, ForeColor = DebuggerTheme.Muted, Margin = new Padding(DpiUtil.ScaleIntX(6), DpiUtil.ScaleIntY(9), DpiUtil.ScaleIntX(2), 0) };
		private static Label Help() => new Label { AutoSize = true, ForeColor = DebuggerTheme.Muted, MaximumSize = new Size(DpiUtil.ScaleIntX(320), 0), Padding = new Padding(0, 0, DpiUtil.ScaleIntX(8), 0), Margin = new Padding(DpiUtil.ScaleIntX(4), DpiUtil.ScaleIntY(4), 0, 0) };
		private static Control Spacer() => new Panel { Width = DpiUtil.ScaleIntX(24), Height = 1, Margin = Padding.Empty };
		private static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = DebuggerTheme.Background, Margin = new Padding(0, DpiUtil.ScaleIntY(2), 0, DpiUtil.ScaleIntY(2)) };
		private void Tip(Control control, string text) => tips.SetToolTip(control, text);
		private static Card EditorCard(string title, TextBox box, NoteLine note)
		{
			var card = new Card(title);
			var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = DebuggerTheme.Border };
			frame.Controls.Add(box); card.Body.Controls.Add(frame); card.Body.Controls.Add(note);
			return card;
		}
		private static ToolGroup OptionGroup(string caption, Label help, params Control[] row)
		{
			var group = new ToolGroup(caption) { FlowDirection = FlowDirection.TopDown };
			var line = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = DebuggerTheme.Panel, Margin = Padding.Empty };
			line.Controls.AddRange(row);
			group.Controls.Add(line); group.Controls.Add(help);
			return group;
		}
		private ulong Address() { ulong address; string error; if (!DebugWorkspace.TryParseCodeAddress(addressBox.Text, workspace.Process.Modules, out address, out error)) throw new FormatException(error); return address; }
		private PatchMode SelectedMode => modeBox.SelectedValue;
		private PatchLocatorKind SelectedLocator => locatorBox.SelectedValue;
		// Restore acts on this definition's live patch, or on whichever live patch owns the loaded selection.
		private ActivePatch RestoreTarget()
		{
			var patches = workspace.Manager.ActivePatches;
			return patches.FirstOrDefault(p => p.Id == definition.Id) ?? (originals == null ? null : patches.FirstOrDefault(p => new PatchRange { Address = p.Preview.Address, Length = (ulong)p.InstalledBytes.Length }.Overlaps(loadedAddress, loadedLength)));
		}
		private string DefaultPattern()
		{
			try
			{
				var masked = ReClassNET.MemoryScanner.PatternScanner.CreatePatternFromCode(workspace.Process, (byte[])originals.Clone());
				if (masked.Length == originals.Length) return masked.ToString();
			}
			catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Masked pattern unavailable: " + ex.Message); }
			return AssemblyService.FormatHex(originals);
		}
		private void SelectionEdited() { if (syncing) return; DefinitionEdited(); status.Text = "Selection changed. Load selection to capture complete original instructions."; }
		private void SourceEdited(PatchSourceKind kind) { if (syncing) return; authoritative = kind; DefinitionEdited(); conversion?.Cancel(); debounce.Stop(); debounce.Start(); }
		private void DefinitionEdited()
		{
			if (syncing) return; ++editVersion; preview = null; previewBox.Clear(); diffView.Clear(); restored = false;
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
					assemblyNote.Show(Severity.Neutral, "Assembling…");
					var result = await workspace.Assembler.AssembleAsync(assemblyBox.Text, origin, token);
					if (IsDisposed || token.IsCancellationRequested || version != editVersion) return;
					if (!result.Success) { status.Text = string.Join("; ", result.Diagnostics.Select(d => d.ToString())); assemblyNote.Show(Severity.Danger, result.Diagnostics.Count > 0 ? result.Diagnostics[0].ToString() : "NASM could not assemble this."); return; }
					syncing = true; hexBox.Text = AssemblyService.FormatHex(result.Bytes); syncing = false;
					status.Text = "Assembly produced " + result.Bytes.Length + " bytes at 0x" + origin.ToString("X") + ". Preview before applying.";
					assemblyNote.Show(Severity.Success, "Assembles to " + result.Bytes.Length + " byte" + (result.Bytes.Length == 1 ? "" : "s") + ".");
					hexNote.Show(Severity.Neutral, "Generated from the assembly.");
				}
				else
				{
					var bytes = AssemblyService.ParseHex(hexBox.Text); var decode = workspace.Instructions.Decode(bytes, origin);
					if (!decode.Success) { hexNote.Show(Severity.Danger, decode.Error); throw new FormatException(decode.Error); }
					syncing = true; assemblyBox.Text = string.Join(Environment.NewLine, decode.Instructions.Select(i => i.Text)); syncing = false;
					status.Text = "Hex is authoritative: " + bytes.Length + " replacement bytes. Preview before applying.";
					hexNote.Show(Severity.Success, bytes.Length + " byte" + (bytes.Length == 1 ? "" : "s") + ", " + decode.Instructions.Count + " instruction" + (decode.Instructions.Count == 1 ? "" : "s") + ".");
					assemblyNote.Show(Severity.Neutral, "Decoded from the hex bytes.");
				}
			}
			catch (Exception ex) { if (!IsDisposed && version == editVersion) status.Text = ex.Message; }
			finally { syncing = false; UpdateSizeMeter(); UpdateButtons(); }
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
			if (string.IsNullOrWhiteSpace(patternBox.Text)) patternBox.Text = DefaultPattern();
			syncing = false;
			var text = new StringBuilder();
			var labels = workspace.Process.NamedAddresses.ToDictionary(pair => unchecked((ulong)pair.Key.ToInt64()), pair => pair.Value);
			if (saved && !originals.SequenceEqual(data)) {
				text.AppendLine("Saved original bytes are shown below. Current bytes differ; Preview will reject a stale original.");
				text.AppendLine("Current bytes: " + AssemblyService.FormatHex(data));
			}
			var listed = new List<ListedInstruction>();
			foreach (var record in decoded.Instructions)
			{
				bool atCapture = snapshot != null && snapshot.Phase == SnapshotPhase.Before && snapshot.Context.Rip == record.Address;
				var explanation = workspace.Instructions.Explain(record, snapshot?.Registers, labels, null, null, atCapture).Text;
				text.AppendLine(record.Address.ToString("X16") + "  " + AssemblyService.FormatHex(record.Bytes) + "  " + record.Text); text.AppendLine(explanation);
				var item = new ListedInstruction { Address = record.Address, Bytes = record.Bytes, Tokens = AsmTokens.Tokenize(record), Text = record.Text, Explanation = explanation };
				if (record.Address == address && definition.Boundary == BoundarySource.Execution) { item.Accent = Severity.Success; item.Badges.Add(new ListedBadge("CONFIRMED", Severity.Success)); }
				if (atCapture) item.Badges.Add(new ListedBadge("CAPTURED HERE", Severity.Info));
				listed.Add(item);
			}
			if (snapshot != null) { text.AppendLine("Captured thread " + snapshot.ThreadId + ", " + snapshot.Timestamp.ToLocalTime().ToString("G") + ", phase " + snapshot.Phase); foreach (var register in snapshot.Registers) text.Append(register.Key.ToUpperInvariant() + "=" + register.Value.ToString("X16") + "  "); text.AppendLine(); }
			text.AppendLine("Boundary: " + definition.Boundary + ". The code address is separate from any watched data address.");
			originalBox.Text = text.ToString();
			originalList.SetItems(listed, decoded.Instructions.Count + " INSTRUCTION" + (decoded.Instructions.Count == 1 ? "" : "S") + " · " + loadedLength + " BYTES" + (saved && !originals.SequenceEqual(data) ? " · SAVED ORIGINALS (CURRENT BYTES DIFFER)" : ""));
			if (snapshot != null)
			{
				var used = decoded.Instructions.SelectMany(i => i.UsedRegisters).Where(r => r.Register != Iced.Intel.Register.None).Select(r => Iced.Intel.RegisterExtensions.GetFullRegister(r.Register).ToString().ToLowerInvariant());
				capturedBoard.SetRegisters(RegisterHighlights.Build(snapshot.Registers, used, 0, 0, null), "CAPTURED " + snapshot.Phase.ToString().ToUpperInvariant() + " · THREAD " + snapshot.ThreadId + " · CLICK TO FOLLOW");
			}
			var module = await Task.Run(() => workspace.Target.Modules.SingleOrDefault(m => address >= m.BaseAddress && address - m.BaseAddress < m.Size));
			moduleLabel.Text = module == null ? "No module at this address; session-only draft." : "Module: " + module.Name + ", offset 0x" + (address - module.BaseAddress).ToString("X") + ", SHA-256 " + (module.Sha256 ?? "unavailable");
			header.Subtitle = "0x" + address.ToString("X") + (module == null ? "  ·  not inside a module" : "  ·  " + module.Name + " + 0x" + (address - module.BaseAddress).ToString("X")) + "  ·  " + loadedLength + " byte" + (loadedLength == 1 ? "" : "s") + " selected";
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
			result.SelectionLength = loadedLength; result.ExpectedBytes = (byte[])originals.Clone(); result.Mode = SelectedMode; result.HookMode = semanticBox.SelectedValue;
			result.SourceKind = authoritative; result.Assembly = assemblyBox.Text; result.ReplacementBytes = authoritative == PatchSourceKind.Bytes ? AssemblyService.ParseHex(hexBox.Text) : null;
			result.LocatorKind = SelectedLocator;
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
		private Task PreviewAsync() => PreviewAsync(true);
		private async Task PreviewAsync(bool confirmRelease)
		{
			if (confirmRelease && prepared != null && MessageBox.Show(this, "A hook is prepared and its memory at 0x" + prepared.Allocation.Address.ToString("X") + " is reserved.\n\nPreview again releases this reservation; you will need Prepare hook again before Apply. Continue?", "Release prepared hook?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) { status.Text = "Preview cancelled; the prepared hook is still reserved. Choose Apply to install it."; return; }
			await CancelPreparationAsync(); var candidate = BuildDefinition();
			preview = await workspace.Planner.PreviewAsync(candidate, workspace.Target, operation.Token);
			RenderPreview(); status.Text = preview.Message ?? "Preview ready. Original " + preview.OriginalBytes.Length + " bytes, installed " + preview.ReplacementBytes.Length + " bytes, NOP padding " + preview.PaddingLength + " bytes.";
		}
		private async Task PrepareAsync()
		{
			if (SelectedMode != PatchMode.Hook) throw new InvalidOperationException("Choose Hook mode first.");
			await PreviewAsync(false); if (preview == null || preview.Status != PatchStatus.Previewed) return;
			prepared = await workspace.Planner.PrepareHookAsync(preview, workspace.Target, workspace.Manager, operation.Token);
			RenderPreview(); status.Text = "Hook reserved and prepared at its final origin. Review the expanded overwrite span and code before Apply.";
		}
		private void RenderPreview()
		{
			var text = new StringBuilder(); if (preview == null) { previewBox.Clear(); diffView.Clear(); return; }
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
			ShowPicture();
		}
		// The picture version of the preview: a byte-by-byte diff, or the hook's jump out and back.
		private void ShowPicture()
		{
			if (prepared != null)
			{
				var model = new ByteDiffView.HookModel
				{
					Site = prepared.Preview.Address, Allocation = prepared.Allocation.Address, ReturnAddress = prepared.ReturnAddress, EntryBytes = prepared.EntryBytes,
					Semantics = semanticBox.Options().FirstOrDefault(o => Equals(o.Value, prepared.Preview.Definition.HookMode))?.Label ?? prepared.Preview.Definition.HookMode.ToString(),
					YourCode = prepared.UserBodyInstructions.Select(i => new ListedInstruction { Address = i.Address, Bytes = i.Bytes, Tokens = AsmTokens.Tokenize(i), Text = i.Text }).ToList(),
					Displaced = prepared.DisplacedInstructions.Select(i => new ListedInstruction { Address = i.Address, Bytes = i.Bytes, Tokens = AsmTokens.Tokenize(i), Text = i.Text }).ToList()
				};
				diffView.ShowHook(model, "Hook prepared: the game jumps out at 0x" + prepared.Preview.Address.ToString("X") + ", runs the hook code, and comes back. Nothing is written until you click Apply.");
				return;
			}
			if (preview.OriginalBytes == null) { diffView.Clear(); diffView.EmptyText = preview.Status + ": " + preview.Message; return; }
			var before = workspace.Instructions.Decode(preview.OriginalBytes, preview.Address);
			var replacement = preview.ReplacementBytes ?? new byte[0];
			var after = workspace.Instructions.Decode(replacement, preview.Address);
			string summary = preview.Status == PatchStatus.Previewed
				? (preview.Definition.Mode == PatchMode.Hook ? "Hook preview at 0x" + preview.Address.ToString("X") + ". Click Prepare hook to build the hook code, then Apply." : "At 0x" + preview.Address.ToString("X") + ": " + preview.OriginalBytes.Length + " original bytes become " + replacement.Length + (preview.PaddingLength > 0 ? " (" + preview.PaddingLength + " NOP padding)" : "") + ". Nothing is written until you click Apply.")
				: preview.Status + ": " + preview.Message;
			diffView.ShowInPlace(preview.Address, ByteDiff.Build(preview.OriginalBytes, replacement, preview.PaddingLength),
				before.Success ? before.Instructions.Select(AsmTokens.Tokenize) : new[] { AsmTokens.Tokenize(AssemblyService.FormatHex(preview.OriginalBytes)) },
				after.Success ? after.Instructions.Select(AsmTokens.Tokenize) : new[] { AsmTokens.Tokenize(AssemblyService.FormatHex(replacement)) }, summary);
		}
		private async Task ApplyAsync()
		{
			PatchResult result; if (prepared != null) result = await workspace.Manager.ApplyAsync(prepared, operation.Token); else if (preview?.CanApply == true) result = await workspace.Manager.ApplyAsync(preview, operation.Token); else throw new InvalidOperationException("A current valid preview or prepared hook is required.");
			status.Text = result.Message; if (result.Status == PatchStatus.Active && result.Patch != null) { definition = result.Patch.Preview.Definition.Clone(); prepared = null; preview = null; restored = false; }
		}
		private async Task RestoreAsync()
		{
			var target = RestoreTarget(); if (target == null) throw new InvalidOperationException("No live patch belongs to this definition or overlaps the loaded selection.");
			await CancelPreparationAsync(); var result = await workspace.Manager.RestoreAsync(target.Id, operation.Token);
			if (result.Status == PatchStatus.Conflict && result.Patch != null)
			{
				var choice = PatchManagerForm.AskPatchConflict(this, result.Message);
				if (choice == PatchConflictChoice.ForceRestore) result = await workspace.Manager.ForceRestoreAsync(target.Id, operation.Token);
				else if (choice == PatchConflictChoice.Abandon) result = await workspace.Manager.AbandonAsync(target.Id, operation.Token);
			}
			status.Text = result.Message; preview = null; RenderPreview();
			restored = result.Success;
		}
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
			if (resolved.Status != PatchResolutionStatus.Resolved || resolved.Address != loadedAddress) throw new InvalidOperationException("Saved locator does not resolve uniquely to this selection: " + resolved.Status + " " + resolved.Message + (resolved.Status == PatchResolutionStatus.MultipleMatches ? " Lengthen the pattern with following bytes, replace fewer bytes with ??, or choose Module + offset." : ""));
			var observed = await Task.Run(() => workspace.Target.ReadExact(loadedAddress, loadedLength));
			var active = workspace.Manager.ActivePatches.FirstOrDefault(p => p.Id == candidate.Id);
			if (!observed.SequenceEqual(originals) && (active == null || !observed.SequenceEqual(active.InstalledBytes))) throw new InvalidOperationException("Original or installed bytes changed; the definition was not saved.");
			repository.Upsert(candidate); definition = repository.Definitions.Single(d => d.Id == candidate.Id);
			// Nothing changed since the preview (edits clear it), so it stays valid for the saved revision.
			if (preview != null) preview.Definition.Revision = definition.Revision;
			if (prepared != null) prepared.Preview.Definition.Revision = definition.Revision;
			UpdateButtons();
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
			using (var dialog = new Form { Text = title, Size = new Size(DpiUtil.ScaleIntX(560), DpiUtil.ScaleIntY(170)), StartPosition = FormStartPosition.CenterParent, Padding = new Padding(DpiUtil.ScaleIntX(10)) }) {
				var choicesBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Font = DebuggerTheme.Mono }; choicesBox.Items.AddRange(choices); choicesBox.SelectedIndex = 0;
				var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = DpiUtil.ScaleIntY(40) };
				var follow = new DarkButton("Follow current memory", null, DarkButtonStyle.Primary) { DialogResult = DialogResult.OK }; var cancel = new DarkButton("Cancel") { DialogResult = DialogResult.Cancel };
				buttons.Controls.Add(cancel); buttons.Controls.Add(follow); dialog.Controls.Add(choicesBox); dialog.Controls.Add(buttons); dialog.AcceptButton = follow; dialog.CancelButton = cancel;
				DebuggerTheme.StyleDialog(dialog);
				return dialog.ShowDialog(this) == DialogResult.OK ? choicesBox.SelectedIndex : -1;
			}
		}
		private async Task RunAsync(Func<Task> action)
		{
			if (busy || IsDisposed) return; if (!ReferenceEquals(repository, workspace.Repository)) { status.Text = "The project changed. Open a new editor for the current project."; return; } busy = true; debounce.Stop(); conversion?.Cancel(); operation = new CancellationTokenSource(); UpdateButtons();
			try { await action(); } catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Operation cancelled."; } catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
			finally { operation.Dispose(); operation = null; busy = false; if (!IsDisposed) { UpdateButtons(); if (closeRequested) { closeRequested = false; BeginInvoke(new Action(Close)); } } }
		}
		private int ReplacementLength() { try { return AssemblyService.ParseHex(hexBox.Text).Length; } catch (Exception) { return -1; } }
		private void UpdateSizeMeter() => sizeMeter.Show(originals?.Length ?? 0, originals == null ? -1 : ReplacementLength(), SelectedMode == PatchMode.Hook);
		private void UpdateButtons()
		{
			if (!busy && prepared != null && !ReferenceEquals(workspace.Manager.Preparation, prepared)) {
				prepared = null; preview = null; status.Text = "Hook preparation is no longer current. Prepare it again before applying.";
			}
			bool editable = !busy && workspace.Target.IsAlive;
			addressBox.Enabled = lengthBox.Enabled = nameBox.Enabled = assemblyBox.Enabled = hexBox.Enabled = modeBox.Enabled = semanticBox.Enabled = locatorBox.Enabled = patternBox.Enabled = entryOffsetBox.Enabled = editable;
			loadButton.Enabled = previewButton.Enabled = editable; nopButton.Enabled = editable && originals != null;
			prepareButton.Enabled = editable && SelectedMode == PatchMode.Hook && workspace.Target.SupportsAllocation;
			applyButton.Enabled = editable && (prepared != null || preview?.CanApply == true);
			var live = RestoreTarget();
			restoreButton.Enabled = !busy && live != null;
			saveButton.Enabled = editable && originals != null && !repository.IsReadOnly;
			cancelButton.Enabled = !busy && prepared != null;
			followRegisterButton.Enabled = editable && snapshot != null && snapshot.Registers.Count != 0;
			followOperandButton.Enabled = editable && originals != null && snapshot != null;
			reverseButton.Enabled = editable && originals != null;
			patternBox.Enabled = editable && SelectedLocator == PatchLocatorKind.ModulePattern; entryOffsetBox.Enabled = patternBox.Enabled;
			// Presentation only: what is visible, what glows, and where the Inspect → Restore flow stands.
			prepareButton.Visible = cancelButton.Visible = SelectedMode == PatchMode.Hook;
			patternFields.Visible = SelectedLocator == PatchLocatorKind.ModulePattern;
			modeHelp.Text = modeBox.SelectedDescription; semanticHelp.Text = SelectedMode == PatchMode.Hook ? semanticBox.SelectedDescription : "Only used in Hook mode. " + semanticBox.SelectedDescription; locatorHelp.Text = locatorBox.SelectedDescription;
			byte[] replacement = null; try { replacement = AssemblyService.ParseHex(hexBox.Text); } catch (Exception) { }
			var state = new PatchStepState { OriginalsLoaded = originals != null, Edited = originals != null && replacement != null && !replacement.SequenceEqual(originals) || SelectedMode == PatchMode.Hook && originals != null, PreviewCurrent = prepared != null || (preview != null && preview.Status == PatchStatus.Previewed && (SelectedMode == PatchMode.InPlace || prepared != null)), Active = live != null, Restored = restored, HookMode = SelectedMode == PatchMode.Hook };
			var step = PatchSteps.Current(state);
			rail.Show(step, PatchSteps.Hint(state));
			loadButton.Glow = step == PatchStep.Inspect && loadButton.Enabled;
			previewButton.Glow = step == PatchStep.Preview && SelectedMode == PatchMode.InPlace && previewButton.Enabled;
			prepareButton.Glow = step == PatchStep.Preview && SelectedMode == PatchMode.Hook && prepareButton.Enabled;
			applyButton.Glow = step == PatchStep.Apply && applyButton.Enabled;
			restoreButton.Glow = step == PatchStep.Restore && restoreButton.Enabled;
			var boundaryPill = definition.Boundary == BoundarySource.Execution ? new DebuggerHeader.HeaderPill { Text = "CONFIRMED BY EXECUTION", Severity = Severity.Success }
				: definition.Boundary == BoundarySource.Uncertain ? new DebuggerHeader.HeaderPill { Text = "BOUNDARY UNCERTAIN", Severity = Severity.Attention }
				: new DebuggerHeader.HeaderPill { Text = "MANUAL ADDRESS", Severity = Severity.Neutral };
			header.SetPills(boundaryPill,
				prepared != null ? new DebuggerHeader.HeaderPill { Text = "HOOK PREPARED", Severity = Severity.Info, Pulse = true } : null,
				live != null ? new DebuggerHeader.HeaderPill { Text = "PATCH ACTIVE", Severity = Severity.Danger, Pulse = true, Solid = true } : null);
			UpdateSizeMeter();
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
		private void EditorClosed(object sender, FormClosedEventArgs e) { workspace.Manager.Changed -= ManagerChanged; workspace.Session.StateChanged -= SessionChanged; conversion?.Cancel(); conversion?.Dispose(); debounce.Dispose(); tips.Dispose(); GlobalWindowManager.RemoveWindow(this); }
		protected override void OnLoad(EventArgs e) { base.OnLoad(e); GlobalWindowManager.AddWindow(this); }
	}
}
