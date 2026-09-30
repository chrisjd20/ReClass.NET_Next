using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReClassNET.Debugger;
using ReClassNET.Patching;
using ReClassNET.UI;

namespace ReClassNET.Forms
{
	public sealed class PatchManagerForm : IconForm
	{
		private readonly DebugWorkspace workspace;
		private readonly PatchRepository repository;
		private readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
		private readonly Label status = new Label { Dock = DockStyle.Fill, Padding = new Padding(5) };
		private readonly Button openButton = new Button { Text = "Open editor", AutoSize = true };
		private readonly Button restoreButton = new Button { Text = "Restore original", AutoSize = true };
		private readonly Button deleteButton = new Button { Text = "Delete definition", AutoSize = true };
		private readonly Button cancelButton = new Button { Text = "Cancel preparation", AutoSize = true };
		private readonly Button restoreAllButton = new Button { Text = "Restore all", AutoSize = true };
		private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1000 };
		private readonly Dictionary<Guid, Tuple<string, string>> resolutions = new Dictionary<Guid, Tuple<string, string>>();
		private bool busy;
		public PatchManagerForm(DebugWorkspace workspace)
		{
			this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace)); repository = workspace.Repository;
			Text = "Patches"; Size = new Size(1060, 520); MinimumSize = new Size(800, 380); StartPosition = FormStartPosition.CenterParent;
			foreach (var column in new[] { Tuple.Create("Name", 150), Tuple.Create("Status", 120), Tuple.Create("Mode", 90), Tuple.Create("Target", 180), Tuple.Create("Selected bytes", 95), Tuple.Create("Details", 320) }) list.Columns.Add(column.Item1, column.Item2);
			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
			layout.Controls.Add(list, 0, 0); var actions = new FlowLayoutPanel { Dock = DockStyle.Fill }; actions.Controls.AddRange(new Control[] { openButton, restoreButton, restoreAllButton, cancelButton, deleteButton }); layout.Controls.Add(actions, 0, 1); layout.Controls.Add(status, 0, 2); Controls.Add(layout);
			list.SelectedIndexChanged += (s, e) => UpdateButtons(); list.DoubleClick += async (s, e) => await RunAsync(OpenEditorAsync);
			openButton.Click += async (s, e) => await RunAsync(OpenEditorAsync); restoreButton.Click += async (s, e) => await RunAsync(RestoreAsync);
			restoreAllButton.Click += async (s, e) => await RunAsync(async () => { var result = await workspace.Manager.RestoreAllAsync(); status.Text = result.Message; });
			cancelButton.Click += async (s, e) => await RunAsync(async () => { var result = await workspace.Manager.CancelPreparationAsync(); status.Text = result.Message; });
			deleteButton.Click += async (s, e) => await RunAsync(DeleteAsync);
			repository.Changed += Changed; workspace.Manager.Changed += Changed; workspace.Session.StateChanged += SessionChanged;
			refresh.Tick += (s, e) => { if (!busy) RefreshRows(); }; refresh.Start();
			FormClosing += (s, e) => { if (busy) { e.Cancel = true; status.Text = "Finishing the current patch operation before closing."; } };
			FormClosed += (s, e) => { refresh.Dispose(); repository.Changed -= Changed; workspace.Manager.Changed -= Changed; workspace.Session.StateChanged -= SessionChanged; GlobalWindowManager.RemoveWindow(this); };
			RefreshRows(); status.Text = repository.Message ?? "Saved definitions load inactive. Open an editor to resolve and preview before applying. Save the project file to persist changes.";
		}
		private Guid? SelectedId => list.SelectedItems.Count == 0 ? (Guid?)null : (Guid)list.SelectedItems[0].Tag;
		private void RefreshRows()
		{
			Guid? selected = SelectedId; var active = workspace.Manager.ActivePatches; var definitions = repository.Definitions.ToList();
			foreach (var patch in active) if (definitions.All(d => d.Id != patch.Id)) definitions.Add(patch.Preview.Definition.Clone());
			list.BeginUpdate(); list.Items.Clear();
			foreach (var definition in definitions)
			{
				var current = active.FirstOrDefault(p => p.Id == definition.Id); var row = new ListViewItem(definition.Name ?? "Patch") { Tag = definition.Id };
				Tuple<string, string> resolution; resolutions.TryGetValue(definition.Id, out resolution);
				row.SubItems.Add(current != null ? (workspace.Target.IsAlive ? current.Status.ToString() : "TargetExited") : resolution?.Item1 ?? "Inactive"); row.SubItems.Add(definition.Mode.ToString());
				row.SubItems.Add(definition.LocatorKind == PatchLocatorKind.SessionAddress ? "Session-only draft" : definition.ModuleName + (definition.LocatorKind == PatchLocatorKind.ModuleOffset ? "+0x" + definition.Offset.ToString("X") : " pattern"));
				row.SubItems.Add(definition.SelectionLength.ToString()); row.SubItems.Add(current?.Message ?? resolution?.Item2 ?? (definition.LocatorKind == PatchLocatorKind.SessionAddress ? "Choose an explicit origin after reopening a project." : "Requires matching image SHA-256 and original bytes."));
				if (current != null) row.ForeColor = current.Status == PatchStatus.Active ? Color.DarkGreen : Color.DarkRed;
				list.Items.Add(row); if (selected == definition.Id) row.Selected = true;
			}
			list.EndUpdate(); UpdateButtons();
		}
		private PatchDefinition SelectedDefinition()
		{
			var id = SelectedId; if (!id.HasValue) throw new InvalidOperationException("Select a patch first.");
			return repository.Definitions.FirstOrDefault(d => d.Id == id.Value) ?? workspace.Manager.ActivePatches.First(p => p.Id == id.Value).Preview.Definition.Clone();
		}
		private async Task OpenEditorAsync()
		{
			var definition = SelectedDefinition(); var active = workspace.Manager.ActivePatches.FirstOrDefault(p => p.Id == definition.Id); ulong address;
			if (active != null) address = active.Preview.Address;
			else
			{
				var resolved = await Task.Run(() => new PatchTargetResolver().Resolve(definition, workspace.Target, CancellationToken.None));
				resolutions[definition.Id] = Tuple.Create(resolved.Status == PatchResolutionStatus.Resolved ? "Resolved inactive" : resolved.Status.ToString(), resolved.Message ?? "Resolved to 0x" + resolved.Address.ToString("X") + "; preview and original-byte checks still required.");
				if (resolved.Status != PatchResolutionStatus.Resolved)
				{
					if (definition.LocatorKind != PatchLocatorKind.SessionAddress) throw new InvalidOperationException(resolved.Status + ": " + resolved.Message);
					address = 0; status.Text = "This session-only draft has no persistent address. Enter a code address and load a selection in the editor.";
				}
				else address = resolved.Address;
			}
			new AssemblyEditorForm(workspace, address, definition.Boundary, null, definition).Show(this);
		}
		private async Task RestoreAsync() { var id = SelectedId; if (!id.HasValue) return; var result = await workspace.Manager.RestoreAsync(id.Value); status.Text = result.Message; }
		private async Task DeleteAsync()
		{
			var id = SelectedId; if (!id.HasValue) return; if (repository.IsReadOnly) throw new InvalidOperationException(repository.Message);
			var pending = workspace.Manager.Preparation;
			if (pending != null && pending.Preview.Definition.Id == id.Value) { var cancelled = await workspace.Manager.CancelPreparationAsync(pending.Id); if (!cancelled.Success) throw new InvalidOperationException(cancelled.Message); }
			var result = await workspace.Manager.DeleteDefinitionAsync(repository, id.Value); if (result.Success) resolutions.Remove(id.Value); status.Text = result.Success ? "Original bytes restored; definition deleted. Save the project file to persist the deletion." : result.Message;
		}
		private async Task RunAsync(Func<Task> action)
		{
			if (busy || IsDisposed) return;
			if (!ReferenceEquals(repository, workspace.Repository)) { status.Text = "The project changed. Open the patch list again."; return; }
			busy = true; UpdateButtons(); try { await action(); } catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; } finally { busy = false; if (!IsDisposed) RefreshRows(); }
		}
		private void UpdateButtons()
		{
			bool selected = SelectedId.HasValue, sameProject = ReferenceEquals(repository, workspace.Repository); bool enabled = !busy && sameProject;
			openButton.Enabled = enabled && selected && workspace.Target.IsAlive;
			restoreButton.Enabled = enabled && selected && workspace.Manager.ActivePatches.Any(p => p.Id == SelectedId.Value);
			restoreAllButton.Enabled = enabled && workspace.Manager.ActivePatches.Count != 0; cancelButton.Enabled = enabled && workspace.Manager.Preparation != null;
			deleteButton.Enabled = enabled && selected && !repository.IsReadOnly; list.Enabled = !busy;
		}
		private void Changed(object sender, EventArgs e) => QueueRefresh();
		private void SessionChanged(DebugSessionState state) => QueueRefresh();
		private void QueueRefresh() { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(new Action(() => { if (!IsDisposed && !busy) RefreshRows(); })); } catch (InvalidOperationException) { } }
		protected override void OnLoad(EventArgs e) { base.OnLoad(e); GlobalWindowManager.AddWindow(this); }
	}
}
