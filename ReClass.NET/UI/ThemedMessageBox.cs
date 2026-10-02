using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ReClassNET.Controls.Debugger;
using ReClassNET.UI.Debugger;

namespace ReClassNET.UI
{
	/// <summary>
	/// Drop-in replacement for <see cref="MessageBox.Show(string)"/>. The classic Light theme uses the native message box;
	/// Dark shows a themed dialog with the same buttons, icon, default button and result.
	/// </summary>
	public static class ThemedMessageBox
	{
		public static DialogResult Show(string text) => Show(null, text, "", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
		public static DialogResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) => Show(null, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) => Show(null, text, caption, buttons, icon, defaultButton);
		public static DialogResult Show(IWin32Window owner, string text) => Show(owner, text, "", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
		public static DialogResult Show(IWin32Window owner, string text, string caption) => Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
		public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) => Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
		public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);

		public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
		{
			if (!AppTheme.Current.IsDark)
				return owner == null ? MessageBox.Show(text, caption, buttons, icon, defaultButton) : MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
			using (var dialog = Build(text, caption, buttons, icon, defaultButton, null))
				return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
		}

		/// <summary>An error dialog with the exception's full details one click away.</summary>
		public static void ShowException(IWin32Window owner, Exception exception)
		{
			using (var dialog = Build(exception.Message, Constants.ApplicationName + " - Error", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, exception.ToString()))
				if (owner == null) dialog.ShowDialog(); else dialog.ShowDialog(owner);
		}

		private static Form Build(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, string details)
		{
			var p = AppTheme.Current;
			var dialog = new Form
			{
				Text = string.IsNullOrEmpty(caption) ? Constants.ApplicationName : caption,
				FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
				StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
				BackColor = p.Background, ForeColor = p.Text, Font = DebuggerTheme.UiFont, Padding = new Padding(DpiUtil.ScaleIntX(16))
			};
			var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = p.Background, Dock = DockStyle.Fill };
			var image = IconOf(icon);
			if (image != null)
				layout.Controls.Add(new PictureBox { Image = image.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, DpiUtil.ScaleIntX(14), 0) }, 0, 0);
			var label = new Label { Text = text ?? "", AutoSize = true, MaximumSize = new Size(DpiUtil.ScaleIntX(520), 0), ForeColor = p.Text, Margin = new Padding(0, DpiUtil.ScaleIntY(6), 0, DpiUtil.ScaleIntY(14)) };
			layout.Controls.Add(label, image == null ? 0 : 1, 0);
			if (image == null) layout.SetColumnSpan(label, 2);
			TextBox detailBox = null;
			if (details != null)
			{
				detailBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = details, Visible = false, Size = new Size(DpiUtil.ScaleIntX(560), DpiUtil.ScaleIntY(220)), BackColor = p.Panel, ForeColor = p.Text, BorderStyle = BorderStyle.FixedSingle, Font = DebuggerTheme.MonoSmall };
				layout.Controls.Add(detailBox, 0, 1); layout.SetColumnSpan(detailBox, 2);
			}
			var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = p.Background, Margin = Padding.Empty };
			var choices = Choices(buttons);
			var created = new List<DarkButton>();
			for (int i = choices.Count - 1; i >= 0; --i)
			{
				var button = new DarkButton(choices[i].Key, null, i == 0 ? DarkButtonStyle.Primary : DarkButtonStyle.Secondary) { DialogResult = choices[i].Value, MinimumSize = new Size(DpiUtil.ScaleIntX(84), 0) };
				row.Controls.Add(button); created.Insert(0, button);
			}
			if (detailBox != null)
			{
				var toggle = new DarkButton("Details", null, DarkButtonStyle.Ghost);
				toggle.Click += (s, e) => { detailBox.Visible = !detailBox.Visible; toggle.Text = detailBox.Visible ? "Hide details" : "Details"; };
				row.Controls.Add(toggle);
			}
			layout.Controls.Add(row, 0, 2); layout.SetColumnSpan(row, 2);
			dialog.Controls.Add(layout);
			int index = defaultButton == MessageBoxDefaultButton.Button3 ? 2 : defaultButton == MessageBoxDefaultButton.Button2 ? 1 : 0;
			var focus = created[Math.Min(index, created.Count - 1)];
			dialog.AcceptButton = focus;
			var cancel = created.Find(b => b.DialogResult == DialogResult.Cancel) ?? (created.Count == 1 ? created[0] : created.Find(b => b.DialogResult == DialogResult.No));
			if (cancel != null) dialog.CancelButton = cancel;
			dialog.Shown += (s, e) => focus.Focus();
			if (icon == MessageBoxIcon.Error || icon == MessageBoxIcon.Warning) dialog.Shown += (s, e) => System.Media.SystemSounds.Asterisk.Play();
			AppTheme.SetChrome(dialog);
			return dialog;
		}

		private static Icon IconOf(MessageBoxIcon icon)
		{
			switch (icon)
			{
				case MessageBoxIcon.Error: return SystemIcons.Error;
				case MessageBoxIcon.Warning: return SystemIcons.Warning;
				case MessageBoxIcon.Question: return SystemIcons.Question;
				case MessageBoxIcon.Information: return SystemIcons.Information;
				default: return null;
			}
		}

		private static List<KeyValuePair<string, DialogResult>> Choices(MessageBoxButtons buttons)
		{
			KeyValuePair<string, DialogResult> C(string text, DialogResult result) => new KeyValuePair<string, DialogResult>(text, result);
			switch (buttons)
			{
				case MessageBoxButtons.OKCancel: return new List<KeyValuePair<string, DialogResult>> { C("OK", DialogResult.OK), C("Cancel", DialogResult.Cancel) };
				case MessageBoxButtons.YesNo: return new List<KeyValuePair<string, DialogResult>> { C("Yes", DialogResult.Yes), C("No", DialogResult.No) };
				case MessageBoxButtons.YesNoCancel: return new List<KeyValuePair<string, DialogResult>> { C("Yes", DialogResult.Yes), C("No", DialogResult.No), C("Cancel", DialogResult.Cancel) };
				case MessageBoxButtons.RetryCancel: return new List<KeyValuePair<string, DialogResult>> { C("Retry", DialogResult.Retry), C("Cancel", DialogResult.Cancel) };
				case MessageBoxButtons.AbortRetryIgnore: return new List<KeyValuePair<string, DialogResult>> { C("Abort", DialogResult.Abort), C("Retry", DialogResult.Retry), C("Ignore", DialogResult.Ignore) };
				default: return new List<KeyValuePair<string, DialogResult>> { C("OK", DialogResult.OK) };
			}
		}
	}
}
